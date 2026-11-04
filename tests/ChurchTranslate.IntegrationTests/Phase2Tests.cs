using ChurchTranslate.Application;
using ChurchTranslate.Domain;
using ChurchTranslate.Infrastructure;

namespace ChurchTranslate.IntegrationTests;

public sealed class Phase2Tests
{
    [Fact]
    public async Task VerifiedInvitationBindsToStableSubject()
    {
        var path = NewPath();
        var store = new JsonStore(path);
        var users = (IUserRepository)store;
        var churches = (IChurchRepository)store;
        var service = new AdminService(churches, users, new FixedClock());
        var invitation = new ChurchUser { Email = "approved@example.org", Role = ChurchRole.PlatformAdmin };
        await users.SaveAsync(invitation, default);

        Assert.Null(await service.BindVerifiedUserAsync("google-1", "approved@example.org", false, default));
        Assert.Null(await users.GetBySubjectAsync("google-1", default));
        var bound = await service.BindVerifiedUserAsync("google-1", "APPROVED@example.org", true, default);
        Assert.Equal(invitation.Id, bound?.Id);
        Assert.Equal("google-1", (await users.GetBySubjectAsync("google-1", default))?.GoogleSubjectId);
        Assert.Null(await service.BindVerifiedUserAsync("google-2", "approved@example.org", true, default));
        var stale = new ChurchUser { Id = invitation.Id, Email = invitation.Email, Role = invitation.Role };
        stale.BindVerifiedGoogleSubject("google-2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(stale, default));
    }

    [Fact]
    public async Task JsonStorePersistsChurchesUsersAndUsageAcrossInstances()
    {
        var path = NewPath();
        var first = new JsonStore(path);
        var churches = (IChurchRepository)first;
        var users = (IUserRepository)first;
        var usage = (IUsageRepository)first;
        var church = new Church { Name = "North", Slug = "north" };
        church.SetTargetLanguages(["en", "fr"]);
        var user = new ChurchUser { Email = "admin@example.org", AssignedChurchIds = [church.Id] };
        user.BindVerifiedGoogleSubject("sub-1");
        await churches.SaveAsync(church, default);
        await users.SaveAsync(user, default);
        await usage.AddAsync(new UsageRecord(Guid.NewGuid(), church.Id, "en", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), null, 3m, "SEK", "test"), default);

        var second = new JsonStore(path);
        Assert.Equal(["en", "fr"], (await ((IChurchRepository)second).GetAsync(church.Id, default))!.AllowedTargetLanguages);
        Assert.Equal(user.Id, (await ((IUserRepository)second).GetBySubjectAsync("sub-1", default))?.Id);
        Assert.Single(await ((IUsageRepository)second).ListForChurchAsync(church.Id, default));
    }

    [Fact]
    public async Task MemoryStoreAllowsVerifiedAdminOnlyForCurrentProcess()
    {
        var store = new MemoryStore();
        var users = (IUserRepository)store;
        var churches = (IChurchRepository)store;
        var service = new AdminService(churches, users, new FixedClock());
        await users.SaveAsync(new ChurchUser { Email = "approved@example.org", Role = ChurchRole.PlatformAdmin }, default);
        var church = new Church { Name = "Test", Slug = "test" };
        await churches.SaveAsync(church, default);

        Assert.Null(await service.BindVerifiedUserAsync("unapproved", "other@example.org", true, default));
        Assert.Null(await service.BindVerifiedUserAsync("unverified", "approved@example.org", false, default));
        Assert.NotNull(await service.BindVerifiedUserAsync("google-id", "approved@example.org", true, default));
        Assert.NotNull(await users.GetBySubjectAsync("google-id", default));
        Assert.NotNull(await churches.GetAsync(church.Id, default));
        Assert.Empty(await ((IUserRepository)new MemoryStore()).ListAsync(default));
    }

    [Fact]
    public async Task ChurchAdminCannotChangeLimitsOrManageAnotherChurch()
    {
        var store = new JsonStore(NewPath());
        var churches = (IChurchRepository)store;
        var service = new AdminService(churches, (IUserRepository)store, new FixedClock());
        var own = new Church { Name = "Own", Slug = "own" };
        var other = new Church { Name = "Other", Slug = "other" };
        await churches.SaveAsync(own, default);
        await churches.SaveAsync(other, default);
        var actor = new ChurchUser { Email = "admin@example.org", Role = ChurchRole.ChurchAdmin,
            AssignedChurchIds = [own.Id] };

        Assert.Null(await service.GetChurchAsync(actor, other.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateChurchAsync(actor, own.Id,
            new ChurchChange("Own", "own", ["en"], true, 2, null), default));
        var updated = await service.UpdateChurchAsync(actor, own.Id,
            new ChurchChange("New name", "own", ["en", "ar"], true, 1, null), default);
        Assert.Equal("New name", updated?.Name);
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "churchtranslate-" + Guid.NewGuid().ToString("N"));
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero); }
}
