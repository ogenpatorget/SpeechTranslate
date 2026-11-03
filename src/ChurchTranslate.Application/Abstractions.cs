using ChurchTranslate.Domain;

namespace ChurchTranslate.Application;

public sealed record TranslationOptions(string SourceLanguage, string TargetLanguage, int SampleRateHz, int TargetLatencyMilliseconds);
public sealed record TranslatedAudioChunk(long Sequence, DateTimeOffset TimestampUtc, string Encoding, int SampleRateHz, ReadOnlyMemory<byte> Payload);
public sealed record AudioPacket(long Sequence, DateTimeOffset TimestampUtc, string Encoding, int SampleRateHz, ReadOnlyMemory<byte> Payload);

public interface ITranslationProvider
{
    Task<ITranslationSession> StartAsync(TranslationOptions options, CancellationToken cancellationToken);
}

public interface ITranslationSession : IAsyncDisposable
{
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken);
    ValueTask CompleteInputAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<TranslatedAudioChunk> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IAudioDistributor
{
    Task StartChannelAsync(Guid broadcastId, string language, CancellationToken cancellationToken);
    ValueTask PublishAsync(Guid broadcastId, string language, AudioPacket packet, CancellationToken cancellationToken);
    Task StopChannelAsync(Guid broadcastId, string language, CancellationToken cancellationToken);
}

public interface IChurchRepository
{
    Task<Church?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Church>> ListAsync(CancellationToken cancellationToken);
    Task SaveAsync(Church church, CancellationToken cancellationToken);
}

public interface IUserRepository
{
    Task<ChurchUser?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ChurchUser?> GetBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<ChurchUser?> GetInvitationByEmailAsync(string verifiedEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChurchUser>> ListAsync(CancellationToken cancellationToken);
    Task SaveAsync(ChurchUser user, CancellationToken cancellationToken);
}

public interface IUsageRepository
{
    Task AddAsync(UsageRecord usage, CancellationToken cancellationToken);
    Task<IReadOnlyList<UsageRecord>> ListForChurchAsync(Guid churchId, CancellationToken cancellationToken);
}

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed record Price(string Provider, string Model, DateTimeOffset EffectiveAtUtc,
    string BillingUnit, string Currency, decimal UnitPrice, string Version);

public sealed class PricingCalculator
{
    public decimal Estimate(TimeSpan duration, Price price)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (price.UnitPrice < 0) throw new ArgumentOutOfRangeException(nameof(price));
        return price.BillingUnit switch
        {
            "audio-minute" => decimal.Round((decimal)duration.TotalMinutes * price.UnitPrice, 4),
            _ => throw new NotSupportedException($"Billing unit {price.BillingUnit} is unsupported.")
        };
    }
}

public static class ChurchAccess
{
    public static bool CanManage(ChurchUser? user, Church church) =>
        user is not null && church.Enabled && user.CanManage(church.Id);
}
