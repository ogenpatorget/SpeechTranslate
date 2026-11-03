namespace ChurchTranslate.Domain;

public static class Languages
{
    public const string Swedish = "sv";
    public static readonly IReadOnlySet<string> SupportedTargets = new HashSet<string>(StringComparer.Ordinal)
    { "en", "ar", "fr" };

    public static string[] ValidateTargets(IEnumerable<string> languages)
    {
        var result = languages.Distinct(StringComparer.Ordinal).ToArray();
        if (result.Length == 0 || result.Any(x => !SupportedTargets.Contains(x)))
            throw new ArgumentException("Select at least one supported target language.", nameof(languages));
        return result;
    }
}

public enum ChurchRole { PlatformAdmin, ChurchAdmin }
public enum BroadcastStatus { Created, Starting, Live, Stopping, Stopped, Failed }
public enum ChannelStatus { Starting, Live, Stopped, Failed }

public sealed class Church
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public bool Enabled { get; set; } = true;
    public string SourceLanguage { get; init; } = Languages.Swedish;
    public string[] AllowedTargetLanguages { get; private set; } = ["en", "ar", "fr"];
    public int MaxConcurrentBroadcasts { get; set; } = 1;
    public int? MonthlyTranslationMinuteLimit { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public void SetTargetLanguages(IEnumerable<string> languages) =>
        AllowedTargetLanguages = Languages.ValidateTargets(languages);
}

public sealed class ChurchUser
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? GoogleSubjectId { get; private set; }
    public required string Email { get; init; }
    public ChurchRole Role { get; set; }
    public HashSet<Guid> AssignedChurchIds { get; init; } = [];
    public bool Enabled { get; set; } = true;

    public void BindVerifiedGoogleSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Subject is required.", nameof(subject));
        if (GoogleSubjectId is not null && GoogleSubjectId != subject)
            throw new InvalidOperationException("A Google identity is already bound.");
        GoogleSubjectId = subject;
    }

    public bool CanManage(Guid churchId) => Enabled &&
        (Role == ChurchRole.PlatformAdmin || AssignedChurchIds.Contains(churchId));
}

public sealed class Broadcast
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ChurchId { get; init; }
    public BroadcastStatus Status { get; private set; } = BroadcastStatus.Created;
    public string SourceLanguage { get; init; } = Languages.Swedish;
    public required string[] TargetLanguages { get; init; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }

    public void TransitionTo(BroadcastStatus next, DateTimeOffset now)
    {
        bool allowed = (Status, next) switch
        {
            (BroadcastStatus.Created, BroadcastStatus.Starting) or
            (BroadcastStatus.Starting, BroadcastStatus.Live) or
            (BroadcastStatus.Starting, BroadcastStatus.Failed) or
            (BroadcastStatus.Live, BroadcastStatus.Stopping) or
            (BroadcastStatus.Live, BroadcastStatus.Failed) or
            (BroadcastStatus.Stopping, BroadcastStatus.Stopped) or
            (BroadcastStatus.Stopping, BroadcastStatus.Failed) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Cannot transition from {Status} to {next}.");
        Status = next;
        if (next == BroadcastStatus.Live) StartedAtUtc = now;
        if (next is BroadcastStatus.Stopped or BroadcastStatus.Failed) EndedAtUtc = now;
    }
}

public sealed record TranslationChannel(
    Guid BroadcastId, string TargetLanguage, ChannelStatus Status, int ListenerCount,
    string TranslationProvider, DateTimeOffset StartedAtUtc);

public sealed record UsageRecord(
    Guid BroadcastId, Guid ChurchId, string TargetLanguage, DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc, TimeSpan BillableAudioDuration,
    decimal? ProviderUsage, decimal EstimatedCost, string Currency, string PricingVersion);
