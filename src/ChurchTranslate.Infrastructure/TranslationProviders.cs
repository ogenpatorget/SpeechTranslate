using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChurchTranslate.Application;

namespace ChurchTranslate.Infrastructure;

// A deterministic signal makes the complete transport testable without spending API credits.
public sealed class MockTranslationProvider : ITranslationProvider
{
    public Task<ITranslationSession> StartAsync(TranslationOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<ITranslationSession>(new MockSession(options.TargetLanguage));

    private sealed class MockSession(string language) : ITranslationSession
    {
        private readonly Channel<TranslatedAudioChunk> output = Channel.CreateBounded<TranslatedAudioChunk>(
            new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private long sequence;
        private double phase;

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
        {
            // Each input frame produces an equally long, clearly synthetic tone.
            var pcm = new byte[audio.Length];
            var frequency = language switch { "ar" => 523.25, "fr" => 659.25, _ => 440.0 };
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var sample = (short)(Math.Sin(phase) * 2200);
                phase += 2 * Math.PI * frequency / 24000;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                pcm[i] = (byte)sample;
                pcm[i + 1] = (byte)(sample >> 8);
            }
            output.Writer.TryWrite(new TranslatedAudioChunk(Interlocked.Increment(ref sequence),
                DateTimeOffset.UtcNow, "pcm16", 24000, pcm));
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<TranslatedAudioChunk> ReceiveAsync(CancellationToken cancellationToken) =>
            output.Reader.ReadAllAsync(cancellationToken);

        public ValueTask CompleteInputAsync(CancellationToken cancellationToken)
        { output.Writer.TryComplete(); return ValueTask.CompletedTask; }

        public ValueTask DisposeAsync() { output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}

public sealed class OpenAiTranslationProvider(string apiKey) : ITranslationProvider
{
    public async Task<ITranslationSession> StartAsync(TranslationOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENAI_API_KEY is required for the OpenAI provider.");
        if (options.SampleRateHz != 24000) throw new ArgumentException("OpenAI translation requires 24 kHz PCM16 input.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        try
        {
            await socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate"), cancellationToken);
            var session = new OpenAiSession(socket);
            await session.SendEventAsync(new { type = "session.update", session = new { audio = new { output = new { language = options.TargetLanguage } } } }, cancellationToken);
            return session;
        }
        catch { socket.Dispose(); throw; }
    }

    private sealed class OpenAiSession(ClientWebSocket socket) : ITranslationSession
    {
        private long sequence;
        private bool closing;
        private readonly SemaphoreSlim sendGate = new(1, 1);

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken) =>
            closing ? ValueTask.CompletedTask : SendEventAsync(new { type = "session.input_audio_buffer.append", audio = Convert.ToBase64String(audio.Span) }, cancellationToken);

        public async ValueTask CompleteInputAsync(CancellationToken cancellationToken)
        {
            if (closing || socket.State != WebSocketState.Open) return;
            closing = true;
            await SendEventAsync(new { type = "session.close" }, cancellationToken);
        }

        public async ValueTask SendEventAsync(object value, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await sendGate.WaitAsync(ct);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
            finally { sendGate.Release(); }
        }

        public async IAsyncEnumerable<TranslatedAudioChunk> ReceiveAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close) yield break;
                if (received.MessageType != WebSocketMessageType.Text) continue;
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;
                message.Position = 0;
                using var json = await JsonDocument.ParseAsync(message, cancellationToken: ct);
                var root = json.RootElement;
                var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                if (type == "session.output_audio.delta" && root.TryGetProperty("delta", out var delta))
                    yield return new TranslatedAudioChunk(Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow,
                        "pcm16", 24000, Convert.FromBase64String(delta.GetString() ?? ""));
                if (type == "error")
                {
                    var code = root.TryGetProperty("error", out var error) &&
                        error.TryGetProperty("code", out var codeNode) ? codeNode.GetString() : null;
                    throw new InvalidOperationException(code switch
                    {
                        "credit_balance_exhausted" => "OpenAI API credits are exhausted. Add credits in OpenAI Platform billing.",
                        "invalid_api_key" => "OpenAI rejected the API key.",
                        _ => $"OpenAI translation failed ({code ?? "unknown error"})."
                    });
                }
                if (type == "session.closed") yield break;
                message.SetLength(0);
            }
        }

        public ValueTask DisposeAsync() { socket.Dispose(); sendGate.Dispose(); return ValueTask.CompletedTask; }
    }
}
