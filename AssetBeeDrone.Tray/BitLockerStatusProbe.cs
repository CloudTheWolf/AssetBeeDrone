using System.Diagnostics;
using System.Text.Json;

namespace AssetBeeDrone.Tray;

internal sealed record BitLockerVolumeStatus(
    string Volume,
    string State,
    string? ProtectionStatus,
    bool HasRecoveryKey);

internal static class BitLockerStatusProbe
{
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
        @(Get-BitLockerVolume | ForEach-Object {
            $keys = @($_.KeyProtector |
                Where-Object { Test-IsRecoveryPasswordProtector $_ } |
                ForEach-Object { $_.RecoveryPassword } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            [pscustomobject]@{
                MountPoint = [string]$_.MountPoint
                Status = [string]$_.VolumeStatus
                ProtectionStatus = [string]$_.ProtectionStatus
                HasRecoveryKey = ($keys.Count -gt 0)
            }
        }) | ConvertTo-Json -Compress
        """;

    public static async Task<(IReadOnlyList<BitLockerVolumeStatus>? Volumes, string? Error)> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    ArgumentList =
                    {
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        ProbeScript
                    },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return (null, "Could not start PowerShell to read BitLocker status.");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"BitLocker probe exited with code {process.ExitCode}."
                    : stderr;
                return (null, detail);
            }

            if (string.IsNullOrWhiteSpace(stdout) || stdout == "null")
            {
                return ([], null);
            }

            using JsonDocument document = JsonDocument.Parse(stdout);
            List<BitLockerVolumeStatus> volumes = [];
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

                volumes.Add(new BitLockerVolumeStatus(volume, state, protection, hasKey));
            }

            return (volumes, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, exception.Message);
        }
    }

    public static string FormatStatus(IReadOnlyList<BitLockerVolumeStatus>? volumes, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return "Unable to read BitLocker status." + Environment.NewLine + error;
        }

        if (volumes is null)
        {
            return "Unable to read BitLocker status.";
        }

        if (volumes.Count == 0)
        {
            return "No BitLocker volumes were reported on this PC.";
        }

        return string.Join(Environment.NewLine, volumes.Select(static volume =>
        {
            string protection = string.IsNullOrWhiteSpace(volume.ProtectionStatus)
                ? "Protection unknown"
                : $"Protection {volume.ProtectionStatus}";
            string key = volume.HasRecoveryKey
                ? "Recovery key available"
                : "No recovery key";
            return $"{volume.Volume} — {volume.State} · {protection} · {key}";
        }));
    }

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
