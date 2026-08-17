using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AssetBeeDrone.MsiHelper;

/// <summary>
/// Starts the tray in interactive user sessions. MSI custom actions that simply
/// CreateProcess while running as LocalSystem land in Session 0 and never appear
/// in the logged-on desktop (common after silent auto-update).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayLauncher
{
    public static int Launch(string trayPath)
    {
        if (string.IsNullOrWhiteSpace(trayPath) || !File.Exists(trayPath))
        {
            Console.Error.WriteLine($"Tray binary not found: {trayPath}");
            return 1;
        }

        // Drop any leftover tray (e.g. Session 0 ghost from a previous CA).
        foreach (Process process in Process.GetProcessesByName("AssetBee.Drone.Tray"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch
            {
                // Best-effort; CreateProcessAsUser still attempted below.
            }
            finally
            {
                process.Dispose();
            }
        }

        int launched = 0;
        foreach (int sessionId in GetInteractiveSessionIds())
        {
            if (TryLaunchInSession(sessionId, trayPath))
            {
                launched++;
            }
        }

        if (launched == 0)
        {
            // No interactive session (e.g. install at boot) — HKLM Run covers next logon.
            Console.Error.WriteLine("No interactive session available to start the tray.");
            return 0;
        }

        return 0;
    }

    private static IEnumerable<int> GetInteractiveSessionIds()
    {
        HashSet<int> sessions = [];

        uint consoleSession = WTSGetActiveConsoleSessionId();
        if (consoleSession is not 0xFFFFFFFF and not 0)
        {
            sessions.Add((int)consoleSession);
        }

        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr sessionInfo, out int count) != 0)
        {
            try
            {
                int size = Marshal.SizeOf<WtsSessionInfo>();
                for (int i = 0; i < count; i++)
                {
                    WtsSessionInfo info = Marshal.PtrToStructure<WtsSessionInfo>(
                        sessionInfo + (i * size));
                    if (info.State == WtsConnectState.Active && info.SessionId > 0)
                    {
                        sessions.Add(info.SessionId);
                    }
                }
            }
            finally
            {
                WTSFreeMemory(sessionInfo);
            }
        }

        return sessions;
    }

    private static bool TryLaunchInSession(int sessionId, string trayPath)
    {
        if (!WTSQueryUserToken((uint)sessionId, out IntPtr userToken))
        {
            return false;
        }

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    SecurityImpersonationLevel.SecurityIdentification,
                    TokenType.TokenPrimary,
                    out primaryToken))
            {
                return false;
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                environment = IntPtr.Zero;
            }

            string commandLine = $"\"{trayPath}\"";
            StartupInfo startup = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default"
            };

            if (!CreateProcessAsUser(
                    primaryToken,
                    trayPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_UNICODE_ENVIRONMENT,
                    environment,
                    Path.GetDirectoryName(trayPath),
                    ref startup,
                    out ProcessInformation processInfo))
            {
                return false;
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            CloseHandle(userToken);
        }
    }

    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    private enum SecurityImpersonationLevel
    {
        SecurityAnonymous = 0,
        SecurityIdentification = 1,
        SecurityImpersonation = 2,
        SecurityDelegation = 3
    }

    private enum TokenType
    {
        TokenPrimary = 1,
        TokenImpersonation = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern int WTSEnumerateSessions(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
