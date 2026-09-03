using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AssetBeeDrone.Infrastructure;

public sealed partial class SystemEventLogger(ILogger<SystemEventLogger> logger) : ISystemEventLogger
{
    internal const string WindowsSourceName = "AssetBee Drone";
    internal const string UnixIdent = "assetbee-drone";
    internal const int SyncFailureEventId = 1001;

    // Keep well under the Windows Event Log ~32 KiB message limit.
    private const int MaxMessageLength = 30_000;

    private static int _unixOpened;

    public void WriteError(string title, string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        string message = BuildMessage(title, details);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteWindowsEventLog(message);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                WriteUnixSyslog(message);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                WriteUnixSyslog(message);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to write OS system event for {Title}", title);
        }
    }

    private static string BuildMessage(string title, string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return Truncate(title);
        }

        return Truncate($"{title}{Environment.NewLine}{Environment.NewLine}{details}");
    }

    private static string Truncate(string message)
    {
        if (message.Length <= MaxMessageLength)
        {
            return message;
        }

        return message[..MaxMessageLength] + "…";
    }

    [SupportedOSPlatform("windows")]
    private void WriteWindowsEventLog(string message)
    {
        EnsureWindowsEventSource();
        EventLog.WriteEntry(
            WindowsSourceName,
            message,
            EventLogEntryType.Error,
            SyncFailureEventId);
    }

    [SupportedOSPlatform("windows")]
    private void EnsureWindowsEventSource()
    {
        try
        {
            if (EventLog.SourceExists(WindowsSourceName))
            {
                return;
            }

            EventLog.CreateEventSource(WindowsSourceName, "Application");
        }
        catch (Exception exception)
        {
            // Source may already exist under another log, or creation may require elevation
            // outside LocalSystem. WriteEntry can still succeed for a pre-registered source
            // (MSI). Fall through and attempt the write.
            logger.LogDebug(exception,
                "Could not ensure Windows Event Log source {Source}", WindowsSourceName);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void WriteUnixSyslog(string message)
    {
        if (Interlocked.Exchange(ref _unixOpened, 1) == 0)
        {
            openlog(UnixIdent, LogPid, LogDaemon);
        }

        // Format string required by syslog(3); pass message as a single %s argument.
        syslog(LogErr, "%s", message);
    }

    private const int LogPid = 0x01;
    private const int LogDaemon = 3 << 3;
    private const int LogErr = 3;

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void openlog(string? ident, int option, int facility);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void syslog(int priority, string format, string arg0);
}
