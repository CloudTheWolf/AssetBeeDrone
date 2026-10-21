using System.Text.Json;
using AssetBeeDrone.Infrastructure;

namespace AssetBeeDrone.Tests;

public sealed class RemoteInstallIpcTests
{
    [Fact]
    public void Pending_round_trips_with_source_generated_json()
    {
        PendingRemoteInstall pending = new(
            42,
            "GCPW",
            "winget",
            DateTimeOffset.Parse("2026-10-20T15:00:00Z"),
            null,
            DateTimeOffset.Parse("2026-10-20T12:00:00Z"));

        string json = JsonSerializer.Serialize(pending, RemoteInstallJsonContext.Default.PendingRemoteInstall);
        PendingRemoteInstall? loaded =
            JsonSerializer.Deserialize(json, RemoteInstallJsonContext.Default.PendingRemoteInstall);

        Assert.NotNull(loaded);
        Assert.Equal(42, loaded.TargetId);
        Assert.Equal("GCPW", loaded.PackageName);
        Assert.Equal("winget", loaded.Source);
        Assert.Equal(pending.PostponeDeadlineAt, loaded.PostponeDeadlineAt);
        Assert.Null(loaded.AutoInstallAtUtc);
        Assert.Contains("targetId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_round_trips_auto_install_at()
    {
        PendingRemoteInstall pending = new(
            42,
            "GCPW",
            "winget",
            DateTimeOffset.Parse("2026-10-21T12:00:00Z"),
            null,
            DateTimeOffset.Parse("2026-10-21T10:00:00Z"),
            DateTimeOffset.Parse("2026-10-21T10:15:00Z"));

        string json = JsonSerializer.Serialize(pending, RemoteInstallJsonContext.Default.PendingRemoteInstall);
        PendingRemoteInstall? loaded =
            JsonSerializer.Deserialize(json, RemoteInstallJsonContext.Default.PendingRemoteInstall);

        Assert.NotNull(loaded);
        Assert.Equal(pending.AutoInstallAtUtc, loaded.AutoInstallAtUtc);
        Assert.Contains("autoInstallAtUtc", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Decision_round_trips_with_source_generated_json()
    {
        RemoteInstallUserDecision decision = new(
            7,
            "install_now",
            null,
            DateTimeOffset.Parse("2026-10-20T12:30:00Z"));

        string json = JsonSerializer.Serialize(decision, RemoteInstallJsonContext.Default.RemoteInstallUserDecision);
        RemoteInstallUserDecision? loaded =
            JsonSerializer.Deserialize(json, RemoteInstallJsonContext.Default.RemoteInstallUserDecision);

        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.TargetId);
        Assert.Equal("install_now", loaded.Action);
    }
}
