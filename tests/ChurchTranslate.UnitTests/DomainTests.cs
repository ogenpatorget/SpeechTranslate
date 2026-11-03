using ChurchTranslate.Application;
using ChurchTranslate.Domain;

namespace ChurchTranslate.UnitTests;

public class DomainTests
{
    [Fact]
    public void Broadcast_OnlyAllowsValidTransitions()
    {
        var broadcast = new Broadcast { ChurchId = Guid.NewGuid(), TargetLanguages = ["en"] };
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<InvalidOperationException>(() => broadcast.TransitionTo(BroadcastStatus.Live, now));
        broadcast.TransitionTo(BroadcastStatus.Starting, now);
        broadcast.TransitionTo(BroadcastStatus.Live, now);
        Assert.Equal(now, broadcast.StartedAtUtc);
        broadcast.TransitionTo(BroadcastStatus.Stopping, now.AddMinutes(1));
        broadcast.TransitionTo(BroadcastStatus.Stopped, now.AddMinutes(2));
        Assert.Equal(now.AddMinutes(2), broadcast.EndedAtUtc);
        Assert.Throws<InvalidOperationException>(() => broadcast.TransitionTo(BroadcastStatus.Starting, now));
    }

    [Fact]
    public void ChurchAccess_RequiresEnabledUserAndAssignment()
    {
        var church = new Church { Name = "Test", Slug = "test" };
        var user = new ChurchUser { Email = "admin@example.org", Role = ChurchRole.ChurchAdmin };
        Assert.False(ChurchAccess.CanManage(user, church));
        user.AssignedChurchIds.Add(church.Id);
        Assert.True(ChurchAccess.CanManage(user, church));
        user.Enabled = false;
        Assert.False(ChurchAccess.CanManage(user, church));
    }

    [Fact]
    public void VerifiedSubjectCannotBeRebound()
    {
        var user = new ChurchUser { Email = "admin@example.org" };
        user.BindVerifiedGoogleSubject("google-sub-1");
        Assert.Throws<InvalidOperationException>(() => user.BindVerifiedGoogleSubject("google-sub-2"));
    }

    [Fact]
    public void LanguagesRejectUnsupportedTargets()
    {
        var church = new Church { Name = "Test", Slug = "test" };
        Assert.Throws<ArgumentException>(() => church.SetTargetLanguages(["es"]));
        church.SetTargetLanguages(["en", "en", "ar"]);
        Assert.Equal(["en", "ar"], church.AllowedTargetLanguages);
    }

    [Fact]
    public void PricingUsesConfiguredPerMinuteRate()
    {
        var price = new Price("test", "test", DateTimeOffset.UtcNow, "audio-minute", "SEK", 2m, "v1");
        Assert.Equal(3m, new PricingCalculator().Estimate(TimeSpan.FromSeconds(90), price));
    }
}
