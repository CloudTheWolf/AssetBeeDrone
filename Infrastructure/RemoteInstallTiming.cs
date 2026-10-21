namespace AssetBeeDrone.Infrastructure;

/// <summary>
/// Timing rules for remote package installs that require user consent.
/// </summary>
public static class RemoteInstallTiming
{
    /// <summary>
    /// If the user never responds to the prompt, install automatically after this delay
    /// from <c>firstNotifiedAt</c>.
    /// </summary>
    public static TimeSpan NoResponseAutoInstallDelay { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum window for postpone/schedule after first notice (enforced by AssetBee).
    /// </summary>
    public static TimeSpan MaxPostponeWindow { get; } = TimeSpan.FromHours(24);

    public static DateTimeOffset? NoResponseAutoInstallAt(DateTimeOffset? firstNotifiedAt) =>
        firstNotifiedAt?.Add(NoResponseAutoInstallDelay);

    public static bool ShouldInstallAfterNoResponse(
        DateTimeOffset now,
        DateTimeOffset? firstNotifiedAt)
    {
        DateTimeOffset? autoInstallAt = NoResponseAutoInstallAt(firstNotifiedAt);
        return autoInstallAt is not null && autoInstallAt <= now;
    }
}
