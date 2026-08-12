using System.ComponentModel;
using System.Diagnostics;

namespace AssetBeeDrone.Infrastructure;

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private const string UnixDefaultPath =
        "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        EnsureUnixProbePath(process.StartInfo);

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"Could not start {fileName}.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new ProcessResult(-1, string.Empty, $"{fileName} timed out.", true);
            }

            return new ProcessResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (Win32Exception exception)
        {
            logger.LogDebug("Probe executable {Executable} was unavailable: {Message}",
                fileName, exception.Message);
            return new ProcessResult(-1, string.Empty, $"{fileName} is unavailable.");
        }
    }

    private static void EnsureUnixProbePath(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        string path = startInfo.Environment.TryGetValue("PATH", out string? existing)
            ? existing ?? string.Empty
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            startInfo.Environment["PATH"] = UnixDefaultPath;
            return;
        }

        if (!path.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Contains("/usr/bin", StringComparer.Ordinal))
        {
            startInfo.Environment["PATH"] = $"{path}:{UnixDefaultPath}";
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between timeout detection and termination.
        }
    }
}
