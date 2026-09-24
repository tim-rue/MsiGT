using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MsiGT;

/// <summary>
/// Everything needed to start a closed process again as it was: same user token (so an app is
/// not restarted elevated just because MsiGT is), same command line, and for packaged (Store)
/// apps, activation through the shell, since their executables can't be started directly.
/// Captured before the process is closed; the token stays valid after the process exits.
/// </summary>
internal sealed class Relaunch : IDisposable
{
    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private readonly SafeAccessTokenHandle _token;

    public string DisplayName { get; }
    public string ExePath { get; }

    private Relaunch(string displayName, string exePath, string commandLine, string? workingDirectory, SafeAccessTokenHandle token)
    {
        DisplayName = displayName;
        ExePath = exePath;
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
        _token = token;
    }

    public static Relaunch Capture(GpuUser user)
    {
        var p = user.Process;
        var exe = p.ExePath ?? throw new InvalidOperationException("Its program file is unknown.");

        if (p.AppUserModelId != null)
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return new Relaunch(user.DisplayName, exe, $"\"{explorer}\" shell:AppsFolder\\{p.AppUserModelId}", null,
                ProcessLauncher.DuplicateToken(ProcessLauncher.GetShellProcessId()));
        }

        return new Relaunch(user.DisplayName, exe, p.CommandLine ?? $"\"{exe}\"", Path.GetDirectoryName(exe),
            ProcessLauncher.DuplicateToken(p.Pid));
    }

    public void Start() => ProcessLauncher.Start(_token, _commandLine, _workingDirectory);

    public void Dispose() => _token.Dispose();
}

internal static class ProcessLauncher
{
    public static int GetShellProcessId()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out int pid) == 0)
            throw new InvalidOperationException("Windows Explorer isn't running.");
        return pid;
    }

    /// <summary>Returns a primary token copied from a running process, for starting processes as its user.</summary>
    public static SafeAccessTokenHandle DuplicateToken(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(process, TOKEN_DUPLICATE | TOKEN_QUERY, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                // Minimal rights CreateProcessWithTokenW needs, found by experimentation (the docs list fewer).
                const uint rights = 395;
                if (!DuplicateTokenEx(token, rights, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return primary;
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static void Start(SafeAccessTokenHandle token, string commandLine, string? workingDirectory)
    {
        if (!CreateEnvironmentBlock(out var environment, token, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            // CreateProcess* may write to the command line buffer, so it must not be an interned .NET string.
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!CreateProcessWithTokenW(token, 0, null, mutableCommandLine, CREATE_UNICODE_ENVIRONMENT, environment,
                    workingDirectory, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.hProcess);
            CloseHandle(info.hThread);
        }
        finally
        {
            DestroyEnvironmentBlock(environment);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int logonFlags, string? applicationName,
        StringBuilder commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
}
