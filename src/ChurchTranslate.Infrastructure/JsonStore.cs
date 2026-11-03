using System.Text.Json;
using ChurchTranslate.Application;
using ChurchTranslate.Domain;

namespace ChurchTranslate.Infrastructure;

// Single-process store. Every operation reads or writes under one gate; writes replace the file atomically.
public sealed class JsonStore : IChurchRepository, IUserRepository, IUsageRepository
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        path = Path.Combine(dataPath, "churchtranslate.json");
    }

    private async Task<T> ReadAsync<T>(Func<State, T> read, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return read(await LoadAsync(ct)); }
        finally { gate.Release(); }
    }

    private async Task WriteAsync(Action<State> update, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            update(state);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, state, Json, ct);
                    await stream.FlushAsync(ct);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task<State> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new State();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<State>(stream, Json, ct)
            ?? throw new InvalidDataException("The data file is empty.");
    }

    Task<Church?> IChurchRepository.GetAsync(Guid id, CancellationToken ct) =>
        ReadAsync(s => s.Churches.FirstOrDefault(x => x.Id == id)?.ToDomain(), ct);

    Task<IReadOnlyList<Church>> IChurchRepository.ListAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<Church>>(s => s.Churches.Select(x => x.ToDomain()).ToArray(), ct);

    Task IChurchRepository.SaveAsync(Church church, CancellationToken ct) => WriteAsync(s =>
    {
        var value = ChurchState.From(church);
        s.Churches.RemoveAll(x => x.Id == church.Id);
        if (s.Churches.Any(x => x.Slug.Equals(church.Slug, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Church slug already exists.");
        s.Churches.Add(value);
    }, ct);

    Task<ChurchUser?> IUserRepository.GetAsync(Guid id, CancellationToken ct) =>
        ReadAsync(s => s.Users.FirstOrDefault(x => x.Id == id)?.ToDomain(), ct);

    Task<ChurchUser?> IUserRepository.GetBySubjectAsync(string subject, CancellationToken ct) =>
        ReadAsync(s => s.Users.FirstOrDefault(x => x.GoogleSubjectId == subject)?.ToDomain(), ct);

    Task<ChurchUser?> IUserRepository.GetInvitationByEmailAsync(string email, CancellationToken ct) =>
        ReadAsync(s => s.Users.FirstOrDefault(x => x.GoogleSubjectId is null &&
            x.Email.Equals(email, StringComparison.OrdinalIgnoreCase))?.ToDomain(), ct);

    Task<IReadOnlyList<ChurchUser>> IUserRepository.ListAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<ChurchUser>>(s => s.Users.Select(x => x.ToDomain()).ToArray(), ct);

    Task IUserRepository.SaveAsync(ChurchUser user, CancellationToken ct) => WriteAsync(s =>
    {
        var previous = s.Users.FirstOrDefault(x => x.Id == user.Id);
        if (previous?.GoogleSubjectId is not null && previous.GoogleSubjectId != user.GoogleSubjectId)
            throw new InvalidOperationException("A Google identity is already bound to this account.");
        if (user.GoogleSubjectId is not null && s.Users.Any(x => x.Id != user.Id && x.GoogleSubjectId == user.GoogleSubjectId))
            throw new InvalidOperationException("Google account is already approved.");
        if (s.Users.Any(x => x.Id != user.Id && x.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Email address is already invited.");
        s.Users.RemoveAll(x => x.Id == user.Id);
        s.Users.Add(UserState.From(user));
    }, ct);

    Task IUsageRepository.AddAsync(UsageRecord usage, CancellationToken ct) =>
        WriteAsync(s => s.Usage.Add(usage), ct);

    Task<IReadOnlyList<UsageRecord>> IUsageRepository.ListForChurchAsync(Guid churchId, CancellationToken ct) =>
        ReadAsync<IReadOnlyList<UsageRecord>>(s => s.Usage.Where(x => x.ChurchId == churchId).ToArray(), ct);

    private sealed class State
    {
        public List<ChurchState> Churches { get; set; } = [];
        public List<UserState> Users { get; set; } = [];
        public List<UsageRecord> Usage { get; set; } = [];
    }

    private sealed class ChurchState
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public bool Enabled { get; set; }
        public string SourceLanguage { get; set; } = Languages.Swedish;
        public string[] AllowedTargetLanguages { get; set; } = [];
        public int MaxConcurrentBroadcasts { get; set; }
        public int? MonthlyTranslationMinuteLimit { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }

        public Church ToDomain()
        {
            var church = new Church { Id = Id, Name = Name, Slug = Slug, Enabled = Enabled,
                SourceLanguage = SourceLanguage, MaxConcurrentBroadcasts = MaxConcurrentBroadcasts,
                MonthlyTranslationMinuteLimit = MonthlyTranslationMinuteLimit, CreatedAtUtc = CreatedAtUtc };
            church.SetTargetLanguages(AllowedTargetLanguages);
            return church;
        }

        public static ChurchState From(Church church) => new() { Id = church.Id, Name = church.Name,
            Slug = church.Slug, Enabled = church.Enabled, SourceLanguage = church.SourceLanguage,
            AllowedTargetLanguages = church.AllowedTargetLanguages, MaxConcurrentBroadcasts = church.MaxConcurrentBroadcasts,
            MonthlyTranslationMinuteLimit = church.MonthlyTranslationMinuteLimit, CreatedAtUtc = church.CreatedAtUtc };
    }

    private sealed class UserState
    {
        public Guid Id { get; set; }
        public string? GoogleSubjectId { get; set; }
        public string Email { get; set; } = "";
        public ChurchRole Role { get; set; }
        public Guid[] AssignedChurchIds { get; set; } = [];
        public bool Enabled { get; set; }

        public ChurchUser ToDomain()
        {
            var user = new ChurchUser { Id = Id, Email = Email, Role = Role,
                AssignedChurchIds = new HashSet<Guid>(AssignedChurchIds), Enabled = Enabled };
            if (GoogleSubjectId is not null) user.BindVerifiedGoogleSubject(GoogleSubjectId);
            return user;
        }

        public static UserState From(ChurchUser user) => new() { Id = user.Id, GoogleSubjectId = user.GoogleSubjectId,
            Email = user.Email, Role = user.Role, AssignedChurchIds = user.AssignedChurchIds.ToArray(), Enabled = user.Enabled };
    }
}
