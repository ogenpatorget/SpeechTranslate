using System.Net.WebSockets;
using ChurchTranslate.Api;
using ChurchTranslate.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChurchTranslate.IntegrationTests;

public sealed class IngestWebSocketTests
{
    [Fact]
    public async Task IngestAcceptsAudioMessageDeliveredInFragments()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ChurchTranslate:EphemeralMode", "true");
            builder.UseSetting("ChurchTranslate:DevOpenAccess", "true");
        });
        using var client = factory.CreateClient();
        var church = (await factory.Services.GetRequiredService<IChurchRepository>().ListAsync(default)).Single();
        var runtime = factory.Services.GetRequiredService<BroadcastRuntime>();
        var broadcast = await runtime.StartAsync(church.Id, ["en"], default);

        var websockets = factory.Server.CreateWebSocketClient();
        websockets.ConfigureRequest = request => request.Headers["Origin"] = "http://127.0.0.1:5301";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await websockets.ConnectAsync(new Uri($"ws://localhost/ws/ingest/{broadcast.Id}"), timeout.Token);
        var frame = new byte[9600];
        await socket.SendAsync(new ArraySegment<byte>(frame, 0, 4800), WebSocketMessageType.Binary, false, timeout.Token);
        await socket.SendAsync(new ArraySegment<byte>(frame, 4800, 4800), WebSocketMessageType.Binary, true, timeout.Token);

        var listener = runtime.Subscribe(broadcast.Id, "en");
        Assert.NotNull(listener);
        await socket.SendAsync(frame, WebSocketMessageType.Binary, true, timeout.Token);
        var packet = await listener.Value.reader.ReadAsync(timeout.Token);
        Assert.Equal(frame.Length, packet.Payload.Length);
        Assert.Equal(WebSocketState.Open, socket.State);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }
}
