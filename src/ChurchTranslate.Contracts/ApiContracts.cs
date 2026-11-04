namespace ChurchTranslate.Contracts;

public sealed record HealthResponse(string Status, DateTimeOffset TimestampUtc);
public sealed record ChurchSummary(Guid Id, string Name, string Slug, bool Enabled, string[] TargetLanguages);
public sealed record BroadcastSummary(Guid Id, Guid ChurchId, string Status, string[] TargetLanguages);
public sealed record ProblemResponse(string Code, string Message);

public sealed record CurrentUserResponse(Guid Id, string Email, string Role, Guid[] AssignedChurchIds);
public sealed record ChurchResponse(Guid Id, string Name, string Slug, bool Enabled, string SourceLanguage,
    string[] TargetLanguages, int MaxConcurrentBroadcasts, int? MonthlyTranslationMinuteLimit, DateTimeOffset CreatedAtUtc);
public sealed record ChurchChangeRequest(string Name, string Slug, string[] TargetLanguages,
    bool Enabled, int MaxConcurrentBroadcasts, int? MonthlyTranslationMinuteLimit);
public sealed record UserResponse(Guid Id, string Email, string Role, bool Enabled, bool HasGoogleIdentity, Guid[] AssignedChurchIds);
public sealed record InviteUserRequest(string Email, string Role, Guid[] AssignedChurchIds);
public sealed record UpdateUserRequest(bool Enabled, Guid[] AssignedChurchIds);
public sealed record CsrfResponse(string Token);
public sealed record GoogleAuthStatusResponse(bool Configured);
public sealed record AuthConfigResponse(bool Configured, bool DevOpenAccess, string ListenerUrl, bool EphemeralMode);
public sealed record StartBroadcastRequest(Guid ChurchId, string[] TargetLanguages);
public sealed record BroadcastResponse(Guid Id, Guid ChurchId, string ChurchName, string Status,
    string[] TargetLanguages, string Provider, DateTimeOffset? StartedAtUtc, int ListenerCount);
public sealed record ListenTokenResponse(string Token, DateTimeOffset ExpiresAtUtc);
