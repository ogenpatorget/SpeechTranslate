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
            BroadcastRuntime runtime, ApiOptions settings, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("ChurchTranslate.Ingest");
            var item = runtime.Get(id);
            var user = await ActorAsync(http, service);
            if (item is null || item.Status != "Live" || user is null || !user.CanManage(item.ChurchId) ||
                !ValidOrigin(http, settings.AdminOrigin) || !http.WebSockets.IsWebSocketRequest)
            { logger.LogWarning("Rejected ingest connection for broadcast {BroadcastId}", id); http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (!runtime.TryAcquireIngest(id)) { logger.LogWarning("Ingest connection already active for broadcast {BroadcastId}", id); http.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[9600];
            var length = 0;
            var receivedInput = false;
            logger.LogInformation("Ingest connected for broadcast {BroadcastId}", id);
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(length), http.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        logger.LogInformation("Ingest closed by client for broadcast {BroadcastId}", id);
                        await socket.CloseOutputAsync(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            socket.CloseStatusDescription, CancellationToken.None);
                        break;
                    }
                    length += received.Count;
                    if (received.MessageType != WebSocketMessageType.Binary ||
                        (received.EndOfMessage && length != buffer.Length) ||
                        (!received.EndOfMessage && length == buffer.Length))
                    {
                        logger.LogWarning("Invalid audio frame for broadcast {BroadcastId}: {Length} bytes", id, length);
                        await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Expected 200 ms PCM16", CancellationToken.None);
                        break;
                    }
                    if (!received.EndOfMessage) continue;
                    await runtime.SendInputAsync(id, buffer, http.RequestAborted);
                    if (!receivedInput)
                    {
                        logger.LogInformation("First audio frame received for broadcast {BroadcastId}", id);
                        receivedInput = true;
                    }
                    length = 0;
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Ingest stopped for broadcast {BroadcastId}", id);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, CancellationToken.None);
            }
            catch (Exception ex) when (socket.State != WebSocketState.Open || http.RequestAborted.IsCancellationRequested)
            { logger.LogInformation("Ingest connection interrupted for broadcast {BroadcastId}: {Reason}", id, ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingest connection failed for broadcast {BroadcastId}", id);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError,
                        "The audio service failed.", CancellationToken.None);
            }
            finally { await runtime.StopAsync(id, CancellationToken.None); }
        }).RequireAuthorization();

        app.Map("/ws/listen/{id:guid}/{language}", async (Guid id, string language, HttpContext http,
            BroadcastRuntime runtime, ApiOptions settings, ITimeLimitedDataProtector tokenProtector, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("ChurchTranslate.ListenerStream");
            var token = http.Request.Query["token"].ToString();
            try
            {
                if (!ValidOrigin(http, settings.ListenerOrigin) ||
                    tokenProtector.Unprotect(token) != $"{id:N}:{language}" || !http.WebSockets.IsWebSocketRequest)
                { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            }
            catch { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var subscription = runtime.Subscribe(id, language);
            if (subscription is null)
            {
                http.Response.StatusCode = runtime.Get(id) is { Status: "Live" } broadcast &&
                    broadcast.TargetLanguages.Contains(language)
                    ? StatusCodes.Status429TooManyRequests : StatusCodes.Status404NotFound;
                return;
            }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("Listener connected for broadcast {BroadcastId} language {Language}", id, language);
            try
            {
                var sent = false;
                await foreach (var packet in subscription.Value.reader.ReadAllAsync(http.RequestAborted))
                {
                    var bytes = new byte[24 + packet.Payload.Length];
                    bytes[0] = (byte)'C'; bytes[1] = (byte)'T'; bytes[2] = (byte)'0'; bytes[3] = (byte)'1';
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), packet.Sequence);
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), packet.TimestampUtc.ToUnixTimeMilliseconds());
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), packet.SampleRateHz);
                    packet.Payload.CopyTo(bytes.AsMemory(24));
                    await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, http.RequestAborted);
                    if (!sent)
                    {
                        logger.LogInformation("First audio packet sent to listener for broadcast {BroadcastId} language {Language}", id, language);
                        sent = true;
                    }
                }
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Broadcast ended", CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                logger.LogInformation("Listener disconnected for broadcast {BroadcastId} language {Language}", id, language);
                runtime.Unsubscribe(id, language, subscription.Value.id);
            }
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
