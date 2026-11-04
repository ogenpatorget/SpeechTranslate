using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ChurchTranslate.Application;
using ChurchTranslate.Domain;
using ChurchTranslate.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net;

namespace ChurchTranslate.IntegrationTests;

public sealed class ApiAuthorizationTests
{
    [Fact]
    public async Task EphemeralModeSeedsApprovedAccountsWithoutCreatingDataFiles()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "churchtranslate-ephemeral-" + Guid.NewGuid().ToString("N"));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ChurchTranslate:EphemeralMode", "true");
            builder.UseSetting("ChurchTranslate:ApprovedAdminEmails", "approved@example.org");
            builder.UseSetting("ChurchTranslate:BootstrapPlatformAdminEmail", "");
            builder.UseSetting("ChurchTranslate:DataPath", dataPath);
            builder.UseSetting("ChurchTranslate:DevOpenAccess", "false");
        });
        using var client = factory.CreateClient();
        var config = await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config");
        Assert.True(config?.EphemeralMode);
        var approved = await factory.Services.GetRequiredService<IUserRepository>().ListAsync(default);
        Assert.Single(approved);
        Assert.Equal("approved@example.org", approved[0].Email);
        Assert.False(Directory.Exists(dataPath));
    }

    [Fact]
    public async Task DevelopmentPreviewCanUseStudioWithoutGoogleFromNonLoopbackClient()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "churchtranslate-api-" + Guid.NewGuid().ToString("N"));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ChurchTranslate:DataPath", dataPath);
            builder.UseSetting("ChurchTranslate:DevOpenAccess", "true");
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, RemoteStartupFilter>());
        });
        using var client = factory.CreateClient();
        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/auth/me");
        Assert.Equal("PlatformAdmin", me?.Role);
        var churches = await client.GetFromJsonAsync<ChurchResponse[]>("/api/admin/churches");
        Assert.Single(churches!);
        Assert.True((await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config"))!.DevOpenAccess);
        using var listenerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/public/broadcasts/" + Guid.NewGuid());
        listenerRequest.Headers.Add("Origin", "http://127.0.0.1:5302");
        using var listenerResponse = await client.SendAsync(listenerRequest);
        Assert.Equal("http://127.0.0.1:5302", listenerResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task AnonymousApiRequestReturns401WithoutGoogleRedirect()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "churchtranslate-api-" + Guid.NewGuid().ToString("N"));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ChurchTranslate:DataPath", dataPath);
            builder.UseSetting("ChurchTranslate:DevOpenAccess", "false");
            builder.UseSetting("Authentication:Google:ClientId", "");
            builder.UseSetting("Authentication:Google:ClientSecret", "");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False((await client.GetFromJsonAsync<GoogleAuthStatusResponse>("/api/auth/config"))!.Configured);
    }

    [Fact]
    public async Task EndpointsRequireAuthenticationAndEnforceChurchScope()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "churchtranslate-api-" + Guid.NewGuid().ToString("N"));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ChurchTranslate:DataPath", dataPath);
            builder.UseSetting("ChurchTranslate:DevOpenAccess", "false");
            builder.ConfigureTestServices(services => services.AddAuthentication(options =>
            {
                options.DefaultScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { }));
        });
        var churches = factory.Services.GetRequiredService<IChurchRepository>();
        var users = factory.Services.GetRequiredService<IUserRepository>();
        var own = new Church { Name = "Own", Slug = "own" };
        var other = new Church { Name = "Other", Slug = "other" };
        await churches.SaveAsync(own, default);
        await churches.SaveAsync(other, default);
        var actor = new ChurchUser { Email = "admin@example.org", Role = ChurchRole.ChurchAdmin,
            AssignedChurchIds = [own.Id] };
        actor.BindVerifiedGoogleSubject("test-sub");
        await users.SaveAsync(actor, default);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/churches")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Subject", "test-sub");
        var list = await client.GetStringAsync("/api/admin/churches");
        Assert.Contains(own.Id.ToString(), list);
        Assert.DoesNotContain(other.Id.ToString(), list);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/churches/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/churches",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);

        var platform = new ChurchUser { Email = "platform@example.org", Role = ChurchRole.PlatformAdmin };
        platform.BindVerifiedGoogleSubject("platform-sub");
        await users.SaveAsync(platform, default);
        client.DefaultRequestHeaders.Remove("X-Test-Subject");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "platform-sub");
        var token = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!.Token;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        var created = await client.PostAsJsonAsync("/api/admin/churches",
            new ChurchChangeRequest("Third", "third", ["en"], true, 1, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var revoked = await client.PutAsJsonAsync($"/api/admin/users/{actor.Id}",
            new UpdateUserRequest(false, [own.Id]));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Subject");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "test-sub");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/churches")).StatusCode);
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Subject", out var subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject.ToString())], "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    private sealed class RemoteStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                await continuation();
            });
            next(app);
        };
    }
}
