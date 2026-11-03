using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Claims;
using ChurchTranslate.Application;
using ChurchTranslate.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;

namespace ChurchTranslate.Api;

public static class BroadcastEndpoints
{
    public static void MapBroadcastEndpoints(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var admin = app.MapGroup("/api/admin/broadcasts").RequireAuthorization();
        admin.MapGet("", async (HttpContext http, AdminService service, BroadcastRuntime runtime) =>
        {
            var user = await ActorAsync(http, service);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(runtime.List().Where(x => user.CanManage(x.ChurchId)));
        });
        admin.MapPost("", async (StartBroadcastRequest request, HttpContext http, AdminService service, BroadcastRuntime runtime) =>
        {
            var user = await ActorAsync(http, service);
            if (user is null) return Results.Unauthorized();
            if (await service.GetChurchAsync(user, request.ChurchId, http.RequestAborted) is null) return Results.Forbid();
            try { return Results.Ok(await runtime.StartAsync(request.ChurchId, request.TargetLanguages, http.RequestAborted)); }
            catch (ArgumentException ex) { return Results.BadRequest(new ProblemResponse("validation", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ProblemResponse("start_failed", ex.Message)); }
        }).AddEndpointFilter(CsrfFilter);
        admin.MapPost("/{id:guid}/stop", async (Guid id, HttpContext http, AdminService service, BroadcastRuntime runtime) =>
        {
            var user = await ActorAsync(http, service);
            var broadcast = runtime.Get(id);
            if (user is null) return Results.Unauthorized();
            if (broadcast is null) return Results.NotFound();
            if (!user.CanManage(broadcast.ChurchId)) return Results.Forbid();
            await runtime.StopAsync(id, http.RequestAborted);
            return Results.NoContent();
        }).AddEndpointFilter(CsrfFilter);

        app.MapGet("/api/public/broadcasts/{id:guid}", (Guid id, BroadcastRuntime runtime) =>
            runtime.Get(id) is { Status: "Live" } response ? Results.Ok(response) : Results.NotFound());
        app.MapGet("/api/public/broadcasts/{id:guid}/{language}/token", (Guid id, string language,
            BroadcastRuntime runtime, ITimeLimitedDataProtector tokenProtector) =>
        {
            var item = runtime.Get(id);
            if (item is null || item.Status != "Live" || !item.TargetLanguages.Contains(language)) return Results.NotFound();
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            return Results.Ok(new ListenTokenResponse(tokenProtector.Protect($"{id:N}:{language}", TimeSpan.FromMinutes(5)), expires));
        });

        app.Map("/ws/ingest/{id:guid}", async (Guid id, HttpContext http, AdminService service,
            BroadcastRuntime runtime, ApiOptions settings) =>
        {
            var item = runtime.Get(id);
            var user = await ActorAsync(http, service);
            if (item is null || item.Status != "Live" || user is null || !user.CanManage(item.ChurchId) ||
                !ValidOrigin(http, settings.AdminOrigin) || !http.WebSockets.IsWebSocketRequest)
            { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (!runtime.TryAcquireIngest(id)) { http.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[9600];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, http.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (received.MessageType != WebSocketMessageType.Binary || !received.EndOfMessage || received.Count != buffer.Length)
                    { await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Expected 200 ms PCM16", CancellationToken.None); break; }
                    await runtime.SendInputAsync(id, buffer, http.RequestAborted);
                }
            }
            catch (InvalidOperationException ex)
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, CancellationToken.None);
            }
            catch (Exception) when (socket.State != WebSocketState.Open || http.RequestAborted.IsCancellationRequested) { }
            finally { await runtime.StopAsync(id, CancellationToken.None); }
        }).RequireAuthorization();

        app.Map("/ws/listen/{id:guid}/{language}", async (Guid id, string language, HttpContext http,
            BroadcastRuntime runtime, ApiOptions settings, ITimeLimitedDataProtector tokenProtector) =>
        {
            var token = http.Request.Query["token"].ToString();
            try
            {
                if (!ValidOrigin(http, settings.ListenerOrigin) ||
                    tokenProtector.Unprotect(token) != $"{id:N}:{language}" || !http.WebSockets.IsWebSocketRequest)
                { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            }
            catch { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var subscription = runtime.Subscribe(id, language);
            if (subscription is null) { http.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            try
            {
                await foreach (var packet in subscription.Value.reader.ReadAllAsync(http.RequestAborted))
                {
                    var bytes = new byte[24 + packet.Payload.Length];
                    bytes[0] = (byte)'C'; bytes[1] = (byte)'T'; bytes[2] = (byte)'0'; bytes[3] = (byte)'1';
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), packet.Sequence);
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), packet.TimestampUtc.ToUnixTimeMilliseconds());
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), packet.SampleRateHz);
                    packet.Payload.CopyTo(bytes.AsMemory(24));
                    await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, http.RequestAborted);
                }
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Broadcast ended", CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally { runtime.Unsubscribe(id, language, subscription.Value.id); }
        });
    }

    private static bool ValidOrigin(HttpContext http, string expected) =>
        string.Equals(http.Request.Headers.Origin, expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static async Task<ChurchTranslate.Domain.ChurchUser?> ActorAsync(HttpContext http, AdminService service) =>
        http.User.FindFirstValue("sub") is { } subject ? await service.FindActiveUserAsync(subject, http.RequestAborted) : null;

    private static async ValueTask<object?> CsrfFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context.HttpContext)
            ? await next(context) : Results.BadRequest(new ProblemResponse("csrf", "A valid request token is required."));
}
