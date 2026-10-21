using AssetBeeDrone.Infrastructure;

namespace AssetBeeDrone.Tests;

public sealed class RemoteInstallTimingTests
{
    [Fact]
    public void No_response_auto_install_is_fifteen_minutes_after_first_notice()
    {
        DateTimeOffset firstNotified = DateTimeOffset.Parse("2026-10-21T10:00:00Z");

        DateTimeOffset? autoInstallAt = RemoteInstallTiming.NoResponseAutoInstallAt(firstNotified);

        Assert.Equal(DateTimeOffset.Parse("2026-10-21T10:15:00Z"), autoInstallAt);
        Assert.Equal(TimeSpan.FromMinutes(15), RemoteInstallTiming.NoResponseAutoInstallDelay);
    }

    [Fact]
    public void Should_install_after_no_response_when_window_elapsed()
    {
        DateTimeOffset firstNotified = DateTimeOffset.Parse("2026-10-21T10:00:00Z");

        Assert.False(RemoteInstallTiming.ShouldInstallAfterNoResponse(
            DateTimeOffset.Parse("2026-10-21T10:14:59Z"),
            firstNotified));
        Assert.True(RemoteInstallTiming.ShouldInstallAfterNoResponse(
            DateTimeOffset.Parse("2026-10-21T10:15:00Z"),
            firstNotified));
    }

    [Fact]
    public void Should_not_install_after_no_response_without_first_notice()
    {
        Assert.Null(RemoteInstallTiming.NoResponseAutoInstallAt(null));
        Assert.False(RemoteInstallTiming.ShouldInstallAfterNoResponse(
            DateTimeOffset.UtcNow,
            firstNotifiedAt: null));
    }
}
