using System.Threading.Channels;
using ChurchTranslate.Api;
using ChurchTranslate.Application;
using ChurchTranslate.Domain;
using ChurchTranslate.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChurchTranslate.IntegrationTests;

public sealed class BroadcastTests
{
    [Fact]
    public async Task OneInputFeedsTwoLanguagesAndMultipleListenersThenRecordsUsage()
    {
        var store = new JsonStore(Path.Combine(Path.GetTempPath(), "ct-broadcast-" + Guid.NewGuid().ToString("N")));
        var church = new Church { Name = "North", Slug = "north" };
        await ((IChurchRepository)store).SaveAsync(church, default);
        var runtime = new BroadcastRuntime((IChurchRepository)store, (IUsageRepository)store,
            new MockTranslationProvider(), new ApiOptions(), NullLogger<BroadcastRuntime>.Instance);
        var started = await runtime.StartAsync(church.Id, ["en", "fr"], default);
        var englishA = runtime.Subscribe(started.Id, "en")!.Value;
        var englishB = runtime.Subscribe(started.Id, "en")!.Value;
        var french = runtime.Subscribe(started.Id, "fr")!.Value;

        await runtime.SendInputAsync(started.Id, new byte[9600], default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var a = await englishA.reader.ReadAsync(timeout.Token);
        var b = await englishB.reader.ReadAsync(timeout.Token);
        var f = await french.reader.ReadAsync(timeout.Token);
        Assert.Equal(a.Payload.ToArray(), b.Payload.ToArray());
        Assert.NotEqual(a.Payload.ToArray(), f.Payload.ToArray());
        Assert.Equal(3, runtime.Get(started.Id)?.ListenerCount);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.SendInputAsync(started.Id, new byte[12], default));

        Assert.True(await runtime.StopAsync(started.Id, default));
        Assert.Null(runtime.Get(started.Id));
        var usage = await ((IUsageRepository)store).ListForChurchAsync(church.Id, default);
        Assert.Equal(2, usage.Count);
        Assert.All(usage, row => Assert.Equal(0.2, row.BillableAudioDuration.TotalSeconds, 3));
        Assert.All(usage, row => Assert.Equal(0, row.EstimatedCost));
    }

    [Fact]
    public async Task BroadcastEnforcesLanguageAndConcurrencyLimits()
    {
        var store = new JsonStore(Path.Combine(Path.GetTempPath(), "ct-broadcast-" + Guid.NewGuid().ToString("N")));
        var church = new Church { Name = "North", Slug = "north" };
        church.SetTargetLanguages(["en"]);
        await ((IChurchRepository)store).SaveAsync(church, default);
        var runtime = new BroadcastRuntime((IChurchRepository)store, (IUsageRepository)store,
            new MockTranslationProvider(), new ApiOptions(), NullLogger<BroadcastRuntime>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.StartAsync(church.Id, ["fr"], default));
        var first = await runtime.StartAsync(church.Id, ["en"], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(church.Id, ["en"], default));
        await runtime.StopAsync(first.Id, default);
    }

    [Fact]
    public async Task BroadcastStopsAfterConfiguredDurationEvenWithoutInput()
    {
        var store = new JsonStore(Path.Combine(Path.GetTempPath(), "ct-broadcast-" + Guid.NewGuid().ToString("N")));
        var church = new Church { Name = "North", Slug = "north" };
        await ((IChurchRepository)store).SaveAsync(church, default);
        var runtime = new BroadcastRuntime((IChurchRepository)store, (IUsageRepository)store,
            new MockTranslationProvider(), new ApiOptions { MaxBroadcastDuration = TimeSpan.FromMilliseconds(150) },
            NullLogger<BroadcastRuntime>.Instance);
        var started = await runtime.StartAsync(church.Id, ["en"], default);
        var listener = runtime.Subscribe(started.Id, "en")!.Value;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await listener.reader.ReadAsync(timeout.Token));
        Assert.Null(runtime.Get(started.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.SendInputAsync(started.Id, new byte[9600], default));
        IReadOnlyList<UsageRecord> usage = [];
        while (usage.Count == 0 && !timeout.IsCancellationRequested)
        {
            usage = await ((IUsageRepository)store).ListForChurchAsync(church.Id, default);
            if (usage.Count == 0) await Task.Delay(10, timeout.Token);
        }
        Assert.Single(usage);
        var next = await runtime.StartAsync(church.Id, ["en"], default);
        Assert.True(await runtime.StopAsync(next.Id, default));
    }

    [Fact]
    public async Task ListenerLimitAppliesAcrossAllLanguages()
    {
        var store = new JsonStore(Path.Combine(Path.GetTempPath(), "ct-broadcast-" + Guid.NewGuid().ToString("N")));
        var church = new Church { Name = "North", Slug = "north" };
        await ((IChurchRepository)store).SaveAsync(church, default);
        var runtime = new BroadcastRuntime((IChurchRepository)store, (IUsageRepository)store,
            new MockTranslationProvider(), new ApiOptions { MaxListenersPerBroadcast = 2 },
            NullLogger<BroadcastRuntime>.Instance);
        var started = await runtime.StartAsync(church.Id, ["en", "fr"], default);
        var english = runtime.Subscribe(started.Id, "en")!.Value;
        var french = runtime.Subscribe(started.Id, "fr")!.Value;

        Assert.Null(runtime.Subscribe(started.Id, "en"));
        Assert.Equal(2, runtime.Get(started.Id)?.ListenerCount);
        runtime.Unsubscribe(started.Id, "en", english.id);
        Assert.NotNull(runtime.Subscribe(started.Id, "en"));
        runtime.Unsubscribe(started.Id, "fr", french.id);
        await runtime.StopAsync(started.Id, default);
    }
}
