using System.Net;
using System.Net.Http.Json;
using ChurchTranslate.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace ChurchTranslate.Admin;

public sealed class AdminApiClient(HttpClient http)
{
    private string listenerOrigin = "http://127.0.0.1:5302";
    public string LoginUrl => new Uri(http.BaseAddress!, "auth/login").ToString();

    public async Task<AuthConfigResponse> AuthConfigAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/auth/config", null, ct);
        await EnsureSuccessAsync(response, ct);
        var config = (await response.Content.ReadFromJsonAsync<AuthConfigResponse>(cancellationToken: ct))!;
        listenerOrigin = config.ListenerOrigin;
        return config;
    }

    public async Task<CurrentUserResponse?> CurrentUserAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/auth/me", null, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<CurrentUserResponse>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ChurchResponse>> ChurchesAsync(CancellationToken ct = default) =>
        await GetListAsync<ChurchResponse>("api/admin/churches", ct);

    public async Task<IReadOnlyList<UserResponse>> UsersAsync(CancellationToken ct = default) =>
        await GetListAsync<UserResponse>("api/admin/users", ct);

    public async Task<IReadOnlyList<BroadcastResponse>> BroadcastsAsync(CancellationToken ct = default) =>
        await GetListAsync<BroadcastResponse>("api/admin/broadcasts", ct);

    public Task<BroadcastResponse> StartBroadcastAsync(Guid churchId, string[] languages, CancellationToken ct = default) =>
        WriteAsync<BroadcastResponse>(HttpMethod.Post, "api/admin/broadcasts", new StartBroadcastRequest(churchId, languages), ct);

    public async Task StopBroadcastAsync(Guid id, CancellationToken ct = default)
    {
        var token = await CsrfAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/admin/broadcasts/{id}/stop");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public string IngestWebSocketUrl(Guid id) => new Uri(http.BaseAddress!, $"ws/ingest/{id}").ToString()
        .Replace("http://", "ws://").Replace("https://", "wss://");

    public string ListenerLink(Guid id) => $"{listenerOrigin}/?broadcast={id}";

    public async Task<ChurchResponse> CreateChurchAsync(ChurchChangeRequest change, CancellationToken ct = default) =>
        await WriteAsync<ChurchResponse>(HttpMethod.Post, "api/admin/churches", change, ct);

    public async Task<ChurchResponse> UpdateChurchAsync(Guid id, ChurchChangeRequest change, CancellationToken ct = default) =>
        await WriteAsync<ChurchResponse>(HttpMethod.Put, $"api/admin/churches/{id}", change, ct);

    public async Task<UserResponse> InviteAsync(InviteUserRequest invite, CancellationToken ct = default) =>
        await WriteAsync<UserResponse>(HttpMethod.Post, "api/admin/users", invite, ct);

    public async Task<UserResponse> UpdateUserAsync(Guid id, UpdateUserRequest change, CancellationToken ct = default) =>
        await WriteAsync<UserResponse>(HttpMethod.Put, $"api/admin/users/{id}", change, ct);

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var token = await CsrfAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T[]>(cancellationToken: ct) ?? [];
    }

    private async Task<T> WriteAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var token = await CsrfAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private async Task<string> CsrfAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/auth/csrf", null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CsrfResponse>(cancellationToken: ct))?.Token
            ?? throw new InvalidOperationException("The request token was unavailable.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return await http.SendAsync(request, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        ProblemResponse? problem = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
            problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(cancellationToken: ct);
        throw new InvalidOperationException(problem?.Message ?? $"Request failed ({(int)response.StatusCode}).");
    }
}
