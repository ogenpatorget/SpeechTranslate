using System.Text.RegularExpressions;
using ChurchTranslate.Domain;

namespace ChurchTranslate.Application;

public sealed record ChurchChange(string Name, string Slug, string[] TargetLanguages,
    bool Enabled, int MaxConcurrentBroadcasts, int? MonthlyTranslationMinuteLimit);

public sealed class AdminService(IChurchRepository churches, IUserRepository users, IClock clock)
{
    public async Task<ChurchUser?> FindActiveUserAsync(string subject, CancellationToken ct)
    {
        var user = await users.GetBySubjectAsync(subject, ct);
        return user?.Enabled == true ? user : null;
    }

    public async Task<ChurchUser?> BindVerifiedUserAsync(string subject, string email, bool emailVerified, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var established = await FindActiveUserAsync(subject, ct);
        if (established is not null) return established;
        if (!emailVerified || string.IsNullOrWhiteSpace(email)) return null;
        var invitation = await users.GetInvitationByEmailAsync(email.Trim(), ct);
        if (invitation?.Enabled != true) return null;
        invitation.BindVerifiedGoogleSubject(subject);
        try
        {
            await users.SaveAsync(invitation, ct);
            return invitation;
        }
        catch (InvalidOperationException)
        {
            // A concurrent sign-in may have bound the invitation first.
            return await FindActiveUserAsync(subject, ct);
        }
    }

    public async Task<IReadOnlyList<Church>> ListChurchesAsync(ChurchUser actor, CancellationToken ct)
    {
        var all = await churches.ListAsync(ct);
        return actor.Role == ChurchRole.PlatformAdmin ? all :
            all.Where(x => actor.AssignedChurchIds.Contains(x.Id)).ToArray();
    }

    public async Task<Church?> GetChurchAsync(ChurchUser actor, Guid id, CancellationToken ct)
    {
        var church = await churches.GetAsync(id, ct);
        return church is not null && actor.CanManage(id) ? church : null;
    }

    public async Task<Church> CreateChurchAsync(ChurchUser actor, ChurchChange change, CancellationToken ct)
    {
        RequirePlatformAdmin(actor);
        Validate(change);
        var church = new Church { Name = change.Name.Trim(), Slug = change.Slug.Trim().ToLowerInvariant(),
            Enabled = change.Enabled, MaxConcurrentBroadcasts = change.MaxConcurrentBroadcasts,
            MonthlyTranslationMinuteLimit = change.MonthlyTranslationMinuteLimit, CreatedAtUtc = clock.UtcNow };
        church.SetTargetLanguages(change.TargetLanguages);
        await churches.SaveAsync(church, ct);
        return church;
    }

    public async Task<Church?> UpdateChurchAsync(ChurchUser actor, Guid id, ChurchChange change, CancellationToken ct)
    {
        var existing = await churches.GetAsync(id, ct);
        if (existing is null || !actor.CanManage(id)) return null;
        Validate(change);
        if (actor.Role != ChurchRole.PlatformAdmin &&
            (existing.Enabled != change.Enabled || existing.MaxConcurrentBroadcasts != change.MaxConcurrentBroadcasts ||
             existing.MonthlyTranslationMinuteLimit != change.MonthlyTranslationMinuteLimit || existing.Slug != change.Slug))
            throw new UnauthorizedAccessException("Only platform administrators can change limits, status, or slug.");
        var updated = new Church { Id = id, Name = change.Name.Trim(), Slug = change.Slug.Trim().ToLowerInvariant(),
            Enabled = change.Enabled, MaxConcurrentBroadcasts = change.MaxConcurrentBroadcasts,
            MonthlyTranslationMinuteLimit = change.MonthlyTranslationMinuteLimit, CreatedAtUtc = existing.CreatedAtUtc };
        updated.SetTargetLanguages(change.TargetLanguages);
        await churches.SaveAsync(updated, ct);
        return updated;
    }

    public async Task<ChurchUser> InviteAsync(ChurchUser actor, string email, ChurchRole role,
        IEnumerable<Guid> churchIds, CancellationToken ct)
    {
        RequirePlatformAdmin(actor);
        email = email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length > 254 || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
            throw new ArgumentException("Enter a valid email address.", nameof(email));
        var assignments = churchIds.Distinct().ToArray();
        if (role == ChurchRole.ChurchAdmin && assignments.Length == 0)
            throw new ArgumentException("Assign at least one church.", nameof(churchIds));
        foreach (var id in assignments)
            if (await churches.GetAsync(id, ct) is null) throw new ArgumentException("Unknown church.", nameof(churchIds));
        var user = new ChurchUser { Email = email, Role = role, AssignedChurchIds = new HashSet<Guid>(assignments) };
        await users.SaveAsync(user, ct);
        return user;
    }

    public async Task<IReadOnlyList<ChurchUser>> ListUsersAsync(ChurchUser actor, CancellationToken ct)
    {
        RequirePlatformAdmin(actor);
        return await users.ListAsync(ct);
    }

    public async Task<ChurchUser?> UpdateUserAsync(ChurchUser actor, Guid id, bool enabled,
        IEnumerable<Guid> churchIds, CancellationToken ct)
    {
        RequirePlatformAdmin(actor);
        var user = await users.GetAsync(id, ct);
        if (user is null) return null;
        if (user.Id == actor.Id && !enabled) throw new ArgumentException("You cannot disable your own account.");
        var assignments = churchIds.Distinct().ToArray();
        foreach (var churchId in assignments)
            if (await churches.GetAsync(churchId, ct) is null) throw new ArgumentException("Unknown church.", nameof(churchIds));
        if (user.Role == ChurchRole.ChurchAdmin && assignments.Length == 0 && enabled)
            throw new ArgumentException("Assign at least one church.", nameof(churchIds));
        user.Enabled = enabled;
        user.AssignedChurchIds.Clear();
        user.AssignedChurchIds.UnionWith(assignments);
        await users.SaveAsync(user, ct);
        return user;
    }

    private static void RequirePlatformAdmin(ChurchUser actor)
    {
        if (!actor.Enabled || actor.Role != ChurchRole.PlatformAdmin)
            throw new UnauthorizedAccessException("Platform administrator required.");
    }

    private static void Validate(ChurchChange change)
    {
        if (string.IsNullOrWhiteSpace(change.Name) || change.Name.Length > 120)
            throw new ArgumentException("Church name must be 1–120 characters.");
        if (!Regex.IsMatch(change.Slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) || change.Slug.Length > 80)
            throw new ArgumentException("Slug must contain lowercase letters, numbers, and hyphens.");
        if (change.MaxConcurrentBroadcasts is < 1 or > 10 || change.MonthlyTranslationMinuteLimit is < 0)
            throw new ArgumentException("Usage limits are invalid.");
        Languages.ValidateTargets(change.TargetLanguages ?? []);
    }
}
