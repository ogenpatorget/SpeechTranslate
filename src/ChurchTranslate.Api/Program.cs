using System.Security.Claims;
using ChurchTranslate.Api;
using ChurchTranslate.Application;
using ChurchTranslate.Contracts;
using ChurchTranslate.Domain;
using ChurchTranslate.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("ChurchTranslate").Get<ApiOptions>() ?? new ApiOptions();
var publicAdminUrl = string.IsNullOrWhiteSpace(settings.PublicAdminUrl) ? settings.AdminOrigin : settings.PublicAdminUrl;
var publicListenerUrl = string.IsNullOrWhiteSpace(settings.PublicListenerUrl) ? settings.ListenerOrigin : settings.PublicListenerUrl;
var approvedAdminEmails = settings.ApprovedAdminEmails.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .Concat(string.IsNullOrWhiteSpace(settings.BootstrapPlatformAdminEmail)
        ? Enumerable.Empty<string>() : [settings.BootstrapPlatformAdminEmail.Trim()])
    .Select(x => x.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
if (settings.DevOpenAccess && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("DevOpenAccess is available only in Development.");
if (settings.EphemeralMode && !settings.DevOpenAccess && approvedAdminEmails.Length == 0)
    throw new InvalidOperationException("EphemeralMode requires at least one approved administrator email.");
if (!settings.TranslationProvider.Equals("Mock", StringComparison.OrdinalIgnoreCase) &&
    !settings.TranslationProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("TranslationProvider must be Mock or OpenAI.");
builder.Services.AddSingleton(settings);
if (settings.EphemeralMode) builder.Services.AddChurchTranslateEphemeralFoundation();
else
{
    var dataPath = Path.GetFullPath(settings.DataPath, builder.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.Combine(dataPath, "keys"));
    builder.Services.AddChurchTranslateFoundation(dataPath);
    builder.Services.AddDataProtection().SetApplicationName("ChurchTranslate")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
}
builder.Services.AddSingleton<ITranslationProvider>(_ => settings.TranslationProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
    ? new OpenAiTranslationProvider(builder.Configuration["OPENAI_API_KEY"] ?? "") : new MockTranslationProvider());
builder.Services.AddSingleton<BroadcastRuntime>();
builder.Services.AddSingleton<ITimeLimitedDataProtector>(sp =>
    sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("listener-stream-v1").ToTimeLimitedDataProtector());
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "ct.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = settings.CrossSiteCookies ? SameSiteMode.None : SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() && !settings.CrossSiteCookies
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddCors(options => options.AddPolicy("admin", policy =>
    policy.WithOrigins(settings.AdminOrigin.TrimEnd('/'), settings.ListenerOrigin.TrimEnd('/'))
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "ct.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = settings.CrossSiteCookies ? SameSiteMode.None : SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() && !settings.CrossSiteCookies
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async context =>
    {
        var subject = context.Principal?.FindFirstValue("sub");
        var service = context.HttpContext.RequestServices.GetRequiredService<AdminService>();
        if (subject is null || await service.FindActiveUserAsync(subject, context.HttpContext.RequestAborted) is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
})
.AddOpenIdConnect(options =>
{
    options.Authority = "https://accounts.google.com";
    // The login endpoint remains unavailable until real credentials are supplied.
    var clientId = builder.Configuration["Authentication:Google:ClientId"];
    var clientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
    options.ClientId = string.IsNullOrWhiteSpace(clientId) ? "not-configured" : clientId;
    options.ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? "not-configured" : clientSecret;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.UsePkce = true;
    options.SaveTokens = false;
    options.MapInboundClaims = false;
    options.CallbackPath = "/signin-google";
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("email");
    options.Scope.Add("profile");
    options.Events.OnTokenValidated = async context =>
    {
        var claims = context.Principal;
        var subject = claims?.FindFirstValue("sub");
        var email = claims?.FindFirstValue("email");
        var verified = string.Equals(claims?.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        var service = context.HttpContext.RequestServices.GetRequiredService<AdminService>();
        var user = await service.BindVerifiedUserAsync(subject ?? "", email ?? "", verified, context.HttpContext.RequestAborted);
        if (user is null) { context.Fail("Account is not approved."); return; }
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", subject!), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Email)],
            CookieAuthenticationDefaults.AuthenticationScheme));
    };
    options.Events.OnRemoteFailure = context =>
    {
        context.HandleResponse();
        context.Response.Redirect(publicAdminUrl.TrimEnd('/') + "/access-denied");
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

if (settings.EphemeralMode)
{
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddSingleton<MemoryXmlRepository>();
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<MemoryXmlRepository>((options, repository) => options.XmlRepository = repository);
}

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    if (Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") is { Length: > 0 } renderUrl)
    {
        if (!Uri.TryCreate(renderUrl, UriKind.Absolute, out var renderUri) || renderUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("RENDER_EXTERNAL_URL must be an HTTPS URL.");
        // Render terminates TLS before forwarding HTTP to Kestrel; it already redirects public HTTP.
        // Set the known external scheme for Google's OAuth callback without trusting client headers.
        app.Use((http, next) => { http.Request.Scheme = Uri.UriSchemeHttps; return next(); });
    }
    else app.UseHttpsRedirection();
    app.UseHsts();
}
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException
        ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError
});
app.UseCors("admin");
app.UseRateLimiter();
app.UseAuthentication();
if (app.Environment.IsDevelopment() && settings.DevOpenAccess)
{
    app.Use(async (http, next) =>
    {
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "local-dev")], "LocalDevelopment"));
        await next();
    });
}
app.UseAuthorization();

if (approvedAdminEmails.Length > 0)
{
    using var scope = app.Services.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var existing = await repository.ListAsync(CancellationToken.None);
    foreach (var email in approvedAdminEmails)
    {
        if (existing.Any(x => x.Email.Equals(email, StringComparison.OrdinalIgnoreCase))) continue;
        await repository.SaveAsync(new ChurchUser { Email = email, Role = ChurchRole.PlatformAdmin }, CancellationToken.None);
        app.Logger.LogInformation("Created platform administrator invitation");
    }
}
if (app.Environment.IsDevelopment() && settings.DevOpenAccess)
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    if (await users.GetBySubjectAsync("local-dev", CancellationToken.None) is null)
    {
        var local = new ChurchUser { Email = "local@churchtranslate.test", Role = ChurchRole.PlatformAdmin };
        local.BindVerifiedGoogleSubject("local-dev");
        await users.SaveAsync(local, CancellationToken.None);
    }
    var churches = scope.ServiceProvider.GetRequiredService<IChurchRepository>();
    if (!(await churches.ListAsync(CancellationToken.None)).Any())
        await churches.SaveAsync(new Church { Name = "Local demo church", Slug = "local-demo" }, CancellationToken.None);
}

app.MapGet("/health/live", (IClock clock) => new HealthResponse("live", clock.UtcNow));
app.MapGet("/health/ready", (IClock clock) => new HealthResponse("ready", clock.UtcNow));
app.MapGet("/api/auth/config", (IConfiguration config) => new AuthConfigResponse(
    !string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"]) &&
    !string.IsNullOrWhiteSpace(config["Authentication:Google:ClientSecret"]),
    app.Environment.IsDevelopment() && settings.DevOpenAccess,
    publicListenerUrl.TrimEnd('/') + "/", settings.EphemeralMode));
app.MapGet("/auth/login", (IConfiguration config) =>
    string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"]) ||
    string.IsNullOrWhiteSpace(config["Authentication:Google:ClientSecret"])
        ? Results.Problem("Google sign-in is not configured.", statusCode: 503)
        : Results.Challenge(new AuthenticationProperties { RedirectUri = publicAdminUrl.TrimEnd('/') + "/" },
            [OpenIdConnectDefaults.AuthenticationScheme])).RequireRateLimiting("login");

app.MapAdminEndpoints();
app.MapBroadcastEndpoints();
app.Run();

public partial class Program { }
