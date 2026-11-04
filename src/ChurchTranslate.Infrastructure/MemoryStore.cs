using ChurchTranslate.Application;
using ChurchTranslate.Domain;

namespace ChurchTranslate.Infrastructure;

// One-process store for a disposable MVP. All state disappears when the API restarts.
public sealed class MemoryStore : IChurchRepository, IUserRepository, IUsageRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Church> churches = [];
    private readonly Dictionary<Guid, ChurchUser> users = [];
    private readonly List<UsageRecord> usage = [];

    Task<Church?> IChurchRepository.GetAsync(Guid id, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(churches.TryGetValue(id, out var church) ? Copy(church) : null);
    }

    Task<IReadOnlyList<Church>> IChurchRepository.ListAsync(CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<Church>>(churches.Values.Select(Copy).ToArray());
    }

    Task IChurchRepository.SaveAsync(Church church, CancellationToken ct)
    {
        lock (gate)
        {
            if (churches.Values.Any(x => x.Id != church.Id && x.Slug == church.Slug))
                throw new InvalidOperationException("Church slug already exists.");
            churches[church.Id] = Copy(church);
        }
        return Task.CompletedTask;
    }

    Task<ChurchUser?> IUserRepository.GetAsync(Guid id, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(users.TryGetValue(id, out var user) ? Copy(user) : null);
    }

    Task<ChurchUser?> IUserRepository.GetBySubjectAsync(string subject, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(CopyOrNull(users.Values.FirstOrDefault(x => x.GoogleSubjectId == subject)));
    }

    Task<ChurchUser?> IUserRepository.GetInvitationByEmailAsync(string email, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(CopyOrNull(users.Values.FirstOrDefault(x =>
            x.GoogleSubjectId is null && x.Email.Equals(email, StringComparison.OrdinalIgnoreCase))));
    }

    Task<IReadOnlyList<ChurchUser>> IUserRepository.ListAsync(CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ChurchUser>>(users.Values.Select(Copy).ToArray());
    }

    Task IUserRepository.SaveAsync(ChurchUser user, CancellationToken ct)
    {
        lock (gate)
        {
            if (users.TryGetValue(user.Id, out var previous) && previous.GoogleSubjectId is not null &&
                previous.GoogleSubjectId != user.GoogleSubjectId)
                throw new InvalidOperationException("A Google identity is already bound to this account.");
            if (users.Values.Any(x => x.Id != user.Id &&
                (x.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase) ||
                 user.GoogleSubjectId is not null && x.GoogleSubjectId == user.GoogleSubjectId)))
                throw new InvalidOperationException("Google account is already approved.");
            users[user.Id] = Copy(user);
        }
        return Task.CompletedTask;
    }

    Task IUsageRepository.AddAsync(UsageRecord record, CancellationToken ct)
    {
        lock (gate) usage.Add(record);
        return Task.CompletedTask;
    }

    Task<IReadOnlyList<UsageRecord>> IUsageRepository.ListForChurchAsync(Guid churchId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<UsageRecord>>(usage.Where(x => x.ChurchId == churchId).ToArray());
    }

    private static ChurchUser? CopyOrNull(ChurchUser? user) => user is null ? null : Copy(user);

    private static Church Copy(Church church)
    {
        var copy = new Church { Id = church.Id, Name = church.Name, Slug = church.Slug,
            Enabled = church.Enabled, SourceLanguage = church.SourceLanguage,
            MaxConcurrentBroadcasts = church.MaxConcurrentBroadcasts,
            MonthlyTranslationMinuteLimit = church.MonthlyTranslationMinuteLimit,
            CreatedAtUtc = church.CreatedAtUtc };
        copy.SetTargetLanguages(church.AllowedTargetLanguages);
        return copy;
    }

    private static ChurchUser Copy(ChurchUser user)
    {
        var copy = new ChurchUser { Id = user.Id, Email = user.Email, Role = user.Role, Enabled = user.Enabled,
            AssignedChurchIds = new HashSet<Guid>(user.AssignedChurchIds) };
        if (user.GoogleSubjectId is not null) copy.BindVerifiedGoogleSubject(user.GoogleSubjectId);
        return copy;
    }
}
