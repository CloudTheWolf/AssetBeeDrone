using System.ComponentModel;
using System.Diagnostics;

namespace AssetBeeDrone.Infrastructure;

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
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
