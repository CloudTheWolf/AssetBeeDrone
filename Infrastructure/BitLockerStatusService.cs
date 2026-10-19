using System.Text.Json;
using AssetBeeDrone.Models;

namespace AssetBeeDrone.Infrastructure;

public sealed record TrayBitLockerVolume(
    string Volume,
    string State,
    string? ProtectionStatus,
    bool HasRecoveryKey);

public sealed record BitLockerStatusSnapshot(
    DateTimeOffset? CollectedAtUtc,
    IReadOnlyList<TrayBitLockerVolume>? Volumes,
    string? Detail,
    bool Refreshing = false);

public interface IBitLockerStatusService
{
    BitLockerStatusSnapshot GetSnapshot();

    void UpdateFromInventory(ProbeValue<IReadOnlyList<EncryptionVolume>> encryption);

    Task RefreshAsync(CancellationToken cancellationToken);
}

public sealed class UnsupportedBitLockerStatusService : IBitLockerStatusService
{
    public BitLockerStatusSnapshot GetSnapshot() =>
        new(null, null, "BitLocker status is only available on Windows.");

    public void UpdateFromInventory(ProbeValue<IReadOnlyList<EncryptionVolume>> encryption)
    {
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class WindowsBitLockerStatusService(
    IProcessRunner processRunner,
    TimeProvider timeProvider) : IBitLockerStatusService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    private readonly object _lock = new();
    private BitLockerStatusSnapshot _snapshot = new(null, null, "BitLocker status has not been collected yet.");
    private int _refreshing;

    private const string ProbeScript = """
        $ErrorActionPreference = 'Stop'
        function Test-IsRecoveryPasswordProtector {
            param($Protector)
            if ($null -eq $Protector) { return $false }
            $type = $Protector.KeyProtectorType
            if ($null -eq $type) { return $false }
            $name = [string]$type
            if ($name -eq 'RecoveryPassword' -or $name -eq 'NumericalPassword') { return $true }
            try { return ([int]$type) -eq 3 } catch { return $false }
        }
        function Test-HasCimRecoveryPassword {
            param([string]$MountPoint)
            if ([string]::IsNullOrWhiteSpace($MountPoint)) { return $false }
            $letter = $MountPoint.TrimEnd('\').ToUpperInvariant()
            try {
                $cimVolume = Get-CimInstance `
                    -Namespace 'root/cimv2/Security/MicrosoftVolumeEncryption' `
                    -ClassName Win32_EncryptableVolume `
                    -ErrorAction Stop |
                    Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_.DriveLetter) -and
                        $_.DriveLetter.TrimEnd('\').ToUpperInvariant() -eq $letter
                    } |
                    Select-Object -First 1
                if (-not $cimVolume) { return $false }
                $protectorResult = $cimVolume | Invoke-CimMethod -MethodName GetKeyProtectors
                if ($protectorResult.ReturnValue -ne 0) { return $false }
                foreach ($id in @($protectorResult.VolumeKeyProtectorID)) {
                    if ([string]::IsNullOrWhiteSpace($id)) { continue }
                    $typeResult = $cimVolume | Invoke-CimMethod `
                        -MethodName GetKeyProtectorType `
                        -Arguments @{ VolumeKeyProtectorID = $id }
                    if ($typeResult.ReturnValue -ne 0 -or [int]$typeResult.KeyProtectorType -ne 3) {
                        continue
                    }
                    $passwordResult = $cimVolume | Invoke-CimMethod `
                        -MethodName GetKeyProtectorNumericalPassword `
                        -Arguments @{ VolumeKeyProtectorID = $id }
                    if ($passwordResult.ReturnValue -eq 0 -and
                        -not [string]::IsNullOrWhiteSpace([string]$passwordResult.NumericalPassword)) {
                        return $true
                    }
                }
            } catch {}
            return $false
        }
        @(Get-BitLockerVolume | ForEach-Object {
            $mountPoint = [string]$_.MountPoint
            $keys = @($_.KeyProtector |
                Where-Object { Test-IsRecoveryPasswordProtector $_ } |
                ForEach-Object { $_.RecoveryPassword } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $hasKey = $keys.Count -gt 0
            if (-not $hasKey) {
                $hasKey = Test-HasCimRecoveryPassword -MountPoint $mountPoint
            }
            [pscustomobject]@{
                MountPoint = $mountPoint
                Status = [string]$_.VolumeStatus
                ProtectionStatus = [string]$_.ProtectionStatus
                HasRecoveryKey = [bool]$hasKey
            }
        }) | ConvertTo-Json -Compress
        """;

    public BitLockerStatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return _snapshot with { Refreshing = Volatile.Read(ref _refreshing) != 0 };
        }
    }

    public void UpdateFromInventory(ProbeValue<IReadOnlyList<EncryptionVolume>> encryption)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (encryption.Status != ProbeStatus.Available || encryption.Value is null)
        {
            SetSnapshot(new BitLockerStatusSnapshot(
                now,
                null,
                encryption.Detail ?? "BitLocker status was not available in the last inventory."));
            return;
        }

        List<TrayBitLockerVolume> volumes = encryption.Value
            .Where(static volume =>
                string.Equals(volume.Technology, "BitLocker", StringComparison.OrdinalIgnoreCase))
            .Select(static volume => new TrayBitLockerVolume(
                volume.Volume,
                volume.State,
                volume.ProtectionStatus,
                HasRecoveryKey(volume)))
            .ToList();

        SetSnapshot(new BitLockerStatusSnapshot(
            now,
            volumes,
            volumes.Count == 0 ? "No BitLocker volumes were reported." : null));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            ProcessResult result = await processRunner.RunAsync(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ProbeScript],
                ProbeTimeout,
                cancellationToken);

            if (!result.Succeeded)
            {
                string detail = result.TimedOut
                    ? "Timed out while reading BitLocker status."
                    : string.IsNullOrWhiteSpace(result.StandardError)
                        ? "BitLocker status could not be read."
                        : result.StandardError.Trim();
                SetSnapshot(new BitLockerStatusSnapshot(timeProvider.GetUtcNow(), null, detail));
                return;
            }

            string stdout = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(stdout) || stdout == "null")
            {
                SetSnapshot(new BitLockerStatusSnapshot(
                    timeProvider.GetUtcNow(),
                    [],
                    "No BitLocker volumes were reported on this PC."));
                return;
            }

            using JsonDocument document = JsonDocument.Parse(stdout);
            List<TrayBitLockerVolume> volumes = [];
            foreach (JsonElement element in AsArray(document.RootElement))
            {
                string volume = GetString(element, "MountPoint") ?? "Unknown";
                string status = GetString(element, "Status") ?? "Unknown";
                string? protection = GetString(element, "ProtectionStatus");
                bool hasKey = element.TryGetProperty("HasRecoveryKey", out JsonElement hasKeyElement) &&
                              hasKeyElement.ValueKind is JsonValueKind.True;
                string state = !hasKey && IsProtectedOrEncrypted(status, protection)
                    ? "TPM Only (No Key)"
                    : status;
                volumes.Add(new TrayBitLockerVolume(volume, state, protection, hasKey));
            }

            SetSnapshot(new BitLockerStatusSnapshot(
                timeProvider.GetUtcNow(),
                volumes,
                volumes.Count == 0 ? "No BitLocker volumes were reported on this PC." : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetSnapshot(new BitLockerStatusSnapshot(
                timeProvider.GetUtcNow(),
                null,
                exception.Message));
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void SetSnapshot(BitLockerStatusSnapshot snapshot)
    {
        lock (_lock)
        {
            _snapshot = snapshot;
        }
    }

    private static bool HasRecoveryKey(EncryptionVolume volume) =>
        (volume.RecoveryKeys?.Any(static key => !string.IsNullOrWhiteSpace(key)) ?? false) ||
        (volume.KeyProtectors?.Any(static protector =>
            !string.IsNullOrWhiteSpace(protector.RecoveryKey)) ?? false);

    private static bool IsProtectedOrEncrypted(string status, string? protectionStatus)
    {
        if (string.Equals(protectionStatus, "On", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return status.Contains("Encrypt", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> AsArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                yield return child;
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
