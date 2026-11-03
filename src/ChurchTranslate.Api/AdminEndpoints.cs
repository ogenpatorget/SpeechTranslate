using System.Security.Claims;
using ChurchTranslate.Application;
using ChurchTranslate.Contracts;
using ChurchTranslate.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ChurchTranslate.Api;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/auth/me", async (HttpContext http, AdminService service) =>
        {
            var user = await ActorAsync(http, service);
            return user is null ? Results.Unauthorized() : Results.Ok(new CurrentUserResponse(
                user.Id, user.Email, user.Role.ToString(), user.AssignedChurchIds.ToArray()));
        });
        api.MapGet("/auth/csrf", (HttpContext http, IAntiforgery antiforgery) =>
            Results.Ok(new CsrfResponse(antiforgery.GetAndStoreTokens(http).RequestToken!)));
        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AddEndpointFilter(CsrfFilter);

        var admin = api.MapGroup("/admin").AddEndpointFilter(CsrfFilter);
        admin.MapGet("/churches", async (HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            return actor is null ? Results.Unauthorized() : Results.Ok(
                (await service.ListChurchesAsync(actor, http.RequestAborted)).Select(ToResponse));
        });
        admin.MapGet("/churches/{id:guid}", async (Guid id, HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            var church = await service.GetChurchAsync(actor, id, http.RequestAborted);
            return church is null ? Results.NotFound() : Results.Ok(ToResponse(church));
        });
        admin.MapPost("/churches", async (ChurchChangeRequest request, HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var church = await service.CreateChurchAsync(actor, ToChange(request), http.RequestAborted);
                return Results.Created($"/api/admin/churches/{church.Id}", ToResponse(church));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ProblemResponse("validation", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ProblemResponse("conflict", ex.Message)); }
        });
        admin.MapPut("/churches/{id:guid}", async (Guid id, ChurchChangeRequest request, HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var church = await service.UpdateChurchAsync(actor, id, ToChange(request), http.RequestAborted);
                return church is null ? Results.NotFound() : Results.Ok(ToResponse(church));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ProblemResponse("validation", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ProblemResponse("conflict", ex.Message)); }
        });
        admin.MapGet("/churches/{id:guid}/usage", async (Guid id, HttpContext http,
            AdminService service, IUsageRepository usage) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            if (await service.GetChurchAsync(actor, id, http.RequestAborted) is null) return Results.NotFound();
            return Results.Ok(await usage.ListForChurchAsync(id, http.RequestAborted));
        });
        admin.MapGet("/users", async (HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok((await service.ListUsersAsync(actor, http.RequestAborted)).Select(ToResponse)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });
        admin.MapPost("/users", async (InviteUserRequest request, HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            if (!Enum.TryParse<ChurchRole>(request.Role, false, out var role) || !Enum.IsDefined(role))
                return Results.BadRequest(new ProblemResponse("validation", "Invalid role."));
            try
            {
                var user = await service.InviteAsync(actor, request.Email, role,
                    request.AssignedChurchIds ?? [], http.RequestAborted);
                return Results.Created($"/api/admin/users/{user.Id}", ToResponse(user));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ProblemResponse("validation", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ProblemResponse("conflict", ex.Message)); }
        });
        admin.MapPut("/users/{id:guid}", async (Guid id, UpdateUserRequest request, HttpContext http, AdminService service) =>
        {
            var actor = await ActorAsync(http, service);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var user = await service.UpdateUserAsync(actor, id, request.Enabled,
                    request.AssignedChurchIds ?? [], http.RequestAborted);
                return user is null ? Results.NotFound() : Results.Ok(ToResponse(user));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ProblemResponse("validation", ex.Message)); }
        });
    }

    private static async ValueTask<object?> CsrfFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)) return await next(context);
        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
            return Results.BadRequest(new ProblemResponse("csrf", "A valid request token is required."));
        return await next(context);
    }

    private static async Task<ChurchUser?> ActorAsync(HttpContext http, AdminService service)
    {
        var subject = http.User.FindFirstValue("sub");
        return subject is null ? null : await service.FindActiveUserAsync(subject, http.RequestAborted);
    }

    private static ChurchChange ToChange(ChurchChangeRequest request) => new(request.Name,
        request.Slug, request.TargetLanguages ?? [], request.Enabled,
        request.MaxConcurrentBroadcasts, request.MonthlyTranslationMinuteLimit);

    private static ChurchResponse ToResponse(Church church) => new(church.Id, church.Name, church.Slug,
        church.Enabled, church.SourceLanguage, church.AllowedTargetLanguages,
        church.MaxConcurrentBroadcasts, church.MonthlyTranslationMinuteLimit, church.CreatedAtUtc);

    private static UserResponse ToResponse(ChurchUser user) => new(user.Id, user.Email,
        user.Role.ToString(), user.Enabled, user.GoogleSubjectId is not null, user.AssignedChurchIds.ToArray());
}
