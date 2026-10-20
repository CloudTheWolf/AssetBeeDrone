using System.Text.Json;

namespace AssetBeeDrone.Infrastructure;

public sealed record BitLockerRemediationVolumeResult(
    string Volume,
    string Action,
    string? Detail,
    bool Succeeded);

public sealed record BitLockerRemediationResult(
    bool Attempted,
    IReadOnlyList<BitLockerRemediationVolumeResult> Volumes,
    string? Detail = null);

public interface IBitLockerRemediationService
{
    Task<BitLockerRemediationResult> EnsureFixedDrivesEncryptedAsync(
        CancellationToken cancellationToken);
}

public sealed class UnsupportedBitLockerRemediationService : IBitLockerRemediationService
{
    public Task<BitLockerRemediationResult> EnsureFixedDrivesEncryptedAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new BitLockerRemediationResult(
            false,
            [],
            "BitLocker remediation is only available on Windows."));
}

public sealed class WindowsBitLockerRemediationService(
    IProcessRunner processRunner,
    ILogger<WindowsBitLockerRemediationService> logger) : IBitLockerRemediationService
{
    private static readonly TimeSpan RemediationTimeout = TimeSpan.FromMinutes(5);

    private const string RemediationScript = """
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
        function Test-HasRecoveryPassword {
            param($Volume)
            $keys = @($Volume.KeyProtector |
                Where-Object { Test-IsRecoveryPasswordProtector $_ } |
                ForEach-Object { $_.RecoveryPassword } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($keys.Count -gt 0) { return $true }
            $mountPoint = [string]$Volume.MountPoint
            if ([string]::IsNullOrWhiteSpace($mountPoint)) { return $false }
            $letter = $mountPoint.TrimEnd('\').ToUpperInvariant()
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

        $results = @()
        try {
            $volumes = @(Get-BitLockerVolume | Where-Object {
                $_.VolumeType -eq 'OperatingSystem' -or $_.VolumeType -eq 'FixedData'
            } | Sort-Object {
                if ($_.VolumeType -eq 'OperatingSystem') { 0 } else { 1 }
            }, MountPoint)
        } catch {
            [pscustomobject]@{
                MountPoint = ''
                Action = 'Failed'
                Detail = "BitLocker module unavailable: $($_.Exception.Message)"
                Succeeded = $false
            } | ConvertTo-Json -Compress
            return
        }

        foreach ($volume in $volumes) {
            $mount = [string]$volume.MountPoint
            $protection = [string]$volume.ProtectionStatus
            $status = [string]$volume.VolumeStatus
            $hasKey = Test-HasRecoveryPassword $volume
            $inProgress = $status -match 'EncryptionInProgress|EncryptionPaused|DecryptionInProgress|DecryptionPaused'
            $encrypted = $protection -eq 'On' -or $status -match 'FullyEncrypted|UsedSpaceOnlyEncrypted|EncryptionInProgress'

            try {
                if ($inProgress) {
                    $results += [pscustomobject]@{
                        MountPoint = $mount
                        Action = 'Skipped'
                        Detail = "Volume status is $status"
                        Succeeded = $true
                    }
                    continue
                }

                if (-not $encrypted) {
                    if ($volume.VolumeType -eq 'OperatingSystem') {
                        Enable-BitLocker -MountPoint $mount -EncryptionMethod XtsAes256 `
                            -UsedSpaceOnly -TpmProtector -ErrorAction Stop | Out-Null
                        Add-BitLockerKeyProtector -MountPoint $mount -RecoveryPasswordProtector `
                            -ErrorAction Stop | Out-Null
                        $action = 'EnabledOsVolume'
                    } else {
                        Enable-BitLocker -MountPoint $mount -EncryptionMethod XtsAes256 `
                            -UsedSpaceOnly -RecoveryPasswordProtector -ErrorAction Stop | Out-Null
                        $action = 'EnabledFixedDataVolume'
                        try {
                            Enable-BitLockerAutoUnlock -MountPoint $mount -ErrorAction Stop | Out-Null
                            $action = 'EnabledFixedDataVolumeWithAutoUnlock'
                        } catch {
                            $action = "EnabledFixedDataVolume;AutoUnlockFailed"
                        }
                    }
                    $results += [pscustomobject]@{
                        MountPoint = $mount
                        Action = $action
                        Detail = $null
                        Succeeded = $true
                    }
                    continue
                }

                $actions = @()
                if ($protection -ne 'On') {
                    Resume-BitLocker -MountPoint $mount -ErrorAction Stop | Out-Null
                    $actions += 'Resumed'
                }

                if (-not $hasKey) {
                    Add-BitLockerKeyProtector -MountPoint $mount -RecoveryPasswordProtector `
                        -ErrorAction Stop | Out-Null
                    $actions += 'AddedRecoveryPassword'
                }

                $results += [pscustomobject]@{
                    MountPoint = $mount
                    Action = $(if ($actions.Count -eq 0) { 'AlreadyCompliant' } else { $actions -join ',' })
                    Detail = $null
                    Succeeded = $true
                }
            } catch {
                $results += [pscustomobject]@{
                    MountPoint = $mount
                    Action = 'Failed'
                    Detail = $_.Exception.Message
                    Succeeded = $false
                }
            }
        }

        if ($results.Count -eq 0) {
            [pscustomobject]@{
                MountPoint = ''
                Action = 'NoFixedVolumes'
                Detail = 'No operating-system or fixed-data BitLocker volumes were found.'
                Succeeded = $true
            } | ConvertTo-Json -Compress
        } else {
            ,@($results) | ConvertTo-Json -Compress
        }
        """;

    public async Task<BitLockerRemediationResult> EnsureFixedDrivesEncryptedAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", RemediationScript],
            RemediationTimeout,
            cancellationToken);

        if (!result.Succeeded)
        {
            string detail = result.TimedOut
                ? "Timed out while enabling BitLocker on fixed drives."
                : string.IsNullOrWhiteSpace(result.StandardError)
                    ? "BitLocker remediation failed."
                    : result.StandardError.Trim();
            logger.LogWarning("BitLocker remediation failed: {Detail}", detail);
            return new BitLockerRemediationResult(true, [], detail);
        }

        string stdout = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(stdout) || stdout == "null")
        {
            return new BitLockerRemediationResult(true, [], "No BitLocker remediation output.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stdout);
            List<BitLockerRemediationVolumeResult> volumes = [];
            foreach (JsonElement element in AsArray(document.RootElement))
            {
                string volume = GetString(element, "MountPoint") ?? string.Empty;
                string action = GetString(element, "Action") ?? "Unknown";
                string? detail = GetString(element, "Detail");
                bool succeeded = !element.TryGetProperty("Succeeded", out JsonElement ok) ||
                                 ok.ValueKind is not JsonValueKind.False;
                volumes.Add(new BitLockerRemediationVolumeResult(volume, action, detail, succeeded));
                if (!succeeded)
                {
                    logger.LogWarning(
                        "BitLocker remediation failed for {Volume}: {Action} {Detail}",
                        volume,
                        action,
                        detail);
                }
                else if (!string.Equals(action, "AlreadyCompliant", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(action, "Skipped", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(action, "NoFixedVolumes", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "BitLocker remediation on {Volume}: {Action}",
                        volume,
                        action);
                }
            }

            return new BitLockerRemediationResult(true, volumes);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "BitLocker remediation returned invalid JSON");
            return new BitLockerRemediationResult(true, [], "BitLocker remediation returned invalid JSON.");
        }
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
