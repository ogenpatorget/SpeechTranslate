using System.Collections.Concurrent;
using System.Threading.Channels;
using ChurchTranslate.Application;
using ChurchTranslate.Contracts;
using ChurchTranslate.Domain;

namespace ChurchTranslate.Api;

public sealed class BroadcastRuntime(
    IChurchRepository churches, IUsageRepository usage, ITranslationProvider provider,
    ApiOptions options, ILogger<BroadcastRuntime> logger)
{
    private readonly ConcurrentDictionary<Guid, ActiveBroadcast> active = new();
    private readonly ConcurrentDictionary<Guid, string> failures = new();
    public string ProviderName => options.TranslationProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        ? "OpenAI · gpt-realtime-translate" : "Demo tone";

    public async Task<BroadcastResponse> StartAsync(Guid churchId, string[] targets, CancellationToken ct)
    {
        var church = await churches.GetAsync(churchId, ct) ?? throw new ArgumentException("Church not found.");
        if (!church.Enabled) throw new InvalidOperationException("Church is disabled.");
        targets = Languages.ValidateTargets(targets);
        if (targets.Any(x => !church.AllowedTargetLanguages.Contains(x)))
            throw new ArgumentException("A selected language is not enabled for this church.");
        var monthStart = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var consumed = (await usage.ListForChurchAsync(churchId, ct))
            .Where(x => x.StartedAtUtc >= monthStart).Sum(x => x.BillableAudioDuration.TotalMinutes);
        if (church.MonthlyTranslationMinuteLimit is { } monthlyLimit && consumed >= monthlyLimit)
            throw new InvalidOperationException("The monthly translation minute limit has been reached.");
        if (active.Values.Count(x => x.Church.Id == churchId) >= church.MaxConcurrentBroadcasts)
            throw new InvalidOperationException("The church has reached its concurrent broadcast limit.");
        var item = new ActiveBroadcast(church, targets, ProviderName);
        if (church.MonthlyTranslationMinuteLimit is { } limit)
            item.MaxInputBytes = (long)Math.Max(0, (limit - consumed) / targets.Length * 60 * 48000);
        if (!active.TryAdd(item.Id, item)) throw new InvalidOperationException("Could not create a broadcast.");
        try
        {
            foreach (var language in targets)
            {
                var session = await provider.StartAsync(new TranslationOptions(church.SourceLanguage, language, 24000, 200), ct);
                item.Sessions[language] = session;
                item.Listeners[language] = new ConcurrentDictionary<Guid, Channel<AudioPacket>>();
                item.Readers.Add(Task.Run(() => ReadProviderAsync(item, language, session)));
            }
            item.StartedAtUtc = DateTimeOffset.UtcNow;
            item.Status = "Live";
            return Describe(item);
        }
        catch
        {
            await StopAsync(item.Id, CancellationToken.None);
            throw;
        }
    }

    public BroadcastResponse? Get(Guid id) => active.TryGetValue(id, out var item) ? Describe(item) : null;
    public IReadOnlyList<BroadcastResponse> List() => active.Values.Select(Describe).ToArray();
    public bool TryAcquireIngest(Guid id) => active.TryGetValue(id, out var item) &&
        Interlocked.CompareExchange(ref item.IngestConnected, 1, 0) == 0;

    public (Guid id, ChannelReader<AudioPacket> reader)? Subscribe(Guid broadcastId, string language)
    {
        if (!active.TryGetValue(broadcastId, out var item) || item.Status != "Live" ||
            !item.Listeners.TryGetValue(language, out var listeners)) return null;
        var channel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(40)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        var id = Guid.NewGuid();
        listeners[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid broadcastId, string language, Guid listenerId)
    {
        if (active.TryGetValue(broadcastId, out var item) && item.Listeners.TryGetValue(language, out var listeners) &&
            listeners.TryRemove(listenerId, out var channel)) channel.Writer.TryComplete();
    }

    public async Task SendInputAsync(Guid id, ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        if (!active.TryGetValue(id, out var item) || item.Status != "Live")
            throw new InvalidOperationException(failures.TryGetValue(id, out var reason) ? reason : "Broadcast is not live.");
        if (pcm16.Length != 9600) throw new ArgumentException("Send 200 ms of mono 24 kHz PCM16 audio.");
        if (Interlocked.Read(ref item.InputBytes) + pcm16.Length > item.MaxInputBytes)
            throw new InvalidOperationException("The monthly translation minute limit has been reached.");
        // A failed language must stop the broadcast rather than leave listeners with a silent channel.
        foreach (var session in item.Sessions.Values) await session.SendAudioAsync(pcm16, ct);
        Interlocked.Add(ref item.InputBytes, pcm16.Length);
    }

    public async Task<bool> StopAsync(Guid id, CancellationToken ct)
    {
        if (!active.TryRemove(id, out var item)) return false;
        item.Status = "Stopping";
        foreach (var session in item.Sessions.Values)
        {
            try { await session.CompleteInputAsync(ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not close translation session"); }
        }
        try { await Task.WhenAll(item.Readers).WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Translation output did not drain cleanly"); }
        item.Cancel.Cancel();
        foreach (var session in item.Sessions.Values) await session.DisposeAsync();
        foreach (var listeners in item.Listeners.Values)
            foreach (var channel in listeners.Values) channel.Writer.TryComplete();
        item.Status = "Stopped";
        var duration = TimeSpan.FromSeconds(Interlocked.Read(ref item.InputBytes) / 48000d);
        if (item.StartedAtUtc is not null) foreach (var language in item.Targets)
        {
            var estimated = options.TranslationProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                ? decimal.Round((decimal)duration.TotalMinutes * options.EstimatedUsdPerAudioMinute, 4) : 0;
            await usage.AddAsync(new UsageRecord(item.Id, item.Church.Id, language,
                item.StartedAtUtc ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, duration,
                null, estimated, "USD", "estimate-2026-11"), CancellationToken.None);
        }
        item.Cancel.Dispose();
        return true;
    }

    private async Task ReadProviderAsync(ActiveBroadcast item, string language, ITranslationSession session)
    {
        try
        {
            await foreach (var chunk in session.ReceiveAsync(item.Cancel.Token))
            {
                if (chunk.Encoding != "pcm16" || chunk.SampleRateHz != 24000) continue;
                var packet = new AudioPacket(chunk.Sequence, chunk.TimestampUtc, chunk.Encoding, chunk.SampleRateHz, chunk.Payload);
                foreach (var listener in item.Listeners[language].Values) listener.Writer.TryWrite(packet);
            }
        }
        catch (OperationCanceledException) when (item.Cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Translation channel {Language} failed for {BroadcastId}", language, item.Id);
            failures[item.Id] = ex.Message;
            if (failures.Count > 100)
                foreach (var oldId in failures.Keys.Take(50)) failures.TryRemove(oldId, out _);
            item.Status = "Failed";
            _ = Task.Run(() => StopAsync(item.Id, CancellationToken.None));
        }
    }

    private static BroadcastResponse Describe(ActiveBroadcast item) => new(item.Id, item.Church.Id, item.Church.Name,
        item.Status, item.Targets, item.Provider, item.StartedAtUtc,
        item.Listeners.Values.Sum(x => x.Count));

    private sealed class ActiveBroadcast(Church church, string[] targets, string provider)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Church Church { get; } = church;
        public string[] Targets { get; } = targets;
        public string Provider { get; } = provider;
        public string Status { get; set; } = "Starting";
        public DateTimeOffset? StartedAtUtc { get; set; }
        public long InputBytes;
        public long MaxInputBytes = long.MaxValue;
        public int IngestConnected;
        public CancellationTokenSource Cancel { get; } = new();
        public Dictionary<string, ITranslationSession> Sessions { get; } = [];
        public Dictionary<string, ConcurrentDictionary<Guid, Channel<AudioPacket>>> Listeners { get; } = [];
        public List<Task> Readers { get; } = [];
    }
}
