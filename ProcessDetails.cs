using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace MsiGT;

/// <summary>What we need to know about a process to decide how to close it and how to start it again.</summary>
internal sealed record ProcessDetails(
    int Pid,
    int ParentPid,
    DateTime StartTime,
    string FileName,
    string? ExePath,
    string? CommandLine,
    int SessionId,
    string? UserSid,
    string? AppUserModelId)
{
    private static readonly Dictionary<string, string> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's display name (e.g. "Google Chrome"), falling back to the file name.</summary>
    public string Description
    {
        get
        {
            if (ExePath == null)
                return FileName;
            lock (DescriptionCache)
            {
                if (!DescriptionCache.TryGetValue(ExePath, out var description))
                {
                    try
                    {
                        description = FileVersionInfo.GetVersionInfo(ExePath).FileDescription?.Trim();
                    }
                    catch
                    {
                        description = null;
                    }
                    DescriptionCache[ExePath] = description = string.IsNullOrEmpty(description) ? FileName : description;
                }
                return description;
            }
        }
    }

    public bool SameExeAs(ProcessDetails other) =>
        ExePath != null && string.Equals(ExePath, other.ExePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the parent process if it is still the one that started this process (PIDs get reused).</summary>
    public ProcessDetails? GetLiveParent()
    {
        var parent = TryGet(ParentPid);
        return parent != null && parent.StartTime <= StartTime ? parent : null;
    }

    /// <summary>True if the process with this PID is still the same process.</summary>
    public bool IsAlive() => TryGet(Pid)?.StartTime == StartTime;

    public static ProcessDetails? TryGet(int pid)
    {
        if (pid <= 0)
            return null;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return pid == 4 ? new ProcessDetails(4, 0, DateTime.MinValue, "System", null, null, 0, null, null) : null;
        try
        {
            if (!GetProcessTimes(handle, out long created, out _, out _, out _))
                return null;
            if (GetExitCodeProcess(handle, out uint exitCode) && exitCode != STILL_ACTIVE)
                return null;

            string? exePath = GetImagePath(handle);
            string fileName = exePath != null ? Path.GetFileName(exePath) : GetNameFallback(pid);
            ProcessIdToSessionId(pid, out int sessionId);

            return new ProcessDetails(
                pid,
                GetParentPid(handle),
                DateTime.FromFileTimeUtc(created),
                fileName,
                exePath,
                GetCommandLine(handle),
                sessionId,
                GetUserSid(handle),
                GetAppUserModelId(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string GetNameFallback(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private static string? GetImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    private static int GetParentPid(IntPtr handle)
    {
        var info = new PROCESS_BASIC_INFORMATION();
        return NtQueryInformationProcess(handle, ProcessBasicInformation, ref info, Marshal.SizeOf(info), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : 0;
    }

    private static string? GetCommandLine(IntPtr handle)
    {
        int size = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }
                if (status != 0)
                    return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }, the text follows it in our buffer
                int length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private static string? GetUserSid(IntPtr handle)
    {
        if (!OpenProcessToken(handle, TOKEN_QUERY, out var token))
            return null;
        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int size);
            if (size == 0)
                return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                // TOKEN_USER starts with a SID_AND_ATTRIBUTES whose first field is the SID pointer
                return GetTokenInformation(token, TokenUser, buffer, size, out _)
                    ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string? GetAppUserModelId(IntPtr handle)
    {
        int length = 0;
        if (GetApplicationUserModelId(handle, ref length, null) != ERROR_INSUFFICIENT_BUFFER)
            return null; // not a packaged app
        var buffer = new StringBuilder(length);
        return GetApplicationUserModelId(handle, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const uint STILL_ACTIVE = 259;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(int pid, out int sessionId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr handle, ref int length, StringBuilder? buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr info, int size, out int returned);
}
