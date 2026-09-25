using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MsiGT;

/// <summary>Finds the Windows services a process hosts, and starts services again after their process was ended.</summary>
internal static class Services
{
    /// <summary>Running services by hosting process ID.</summary>
    public static Dictionary<int, List<string>> GetRunningByProcess()
    {
        var result = new Dictionary<int, List<string>>();
        var scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
            return result;
        try
        {
            int resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, IntPtr.Zero, 0, out int needed, out _, ref resume, null);
            if (needed == 0)
                return result;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buffer, needed, out _, out int count, ref resume, null))
                    return result;
                int size = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                for (int i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * size);
                    int pid = entry.Status.ProcessId;
                    if (pid == 0 || entry.ServiceName == null)
                        continue;
                    if (!result.TryGetValue(pid, out var names))
                        result[pid] = names = [];
                    names.Add(entry.ServiceName);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
        return result;
    }

    /// <summary>Starts the service unless it is running already (its recovery options may have restarted it).</summary>
    public static void Start(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            var service = OpenServiceW(scm, name, SERVICE_START);
            if (service == IntPtr.Zero)
                throw new Win32Exception();
            try
            {
                // The service may still be stopping for a moment after its process ended.
                for (int attempt = 0; ; attempt++)
                {
                    if (StartServiceW(service, 0, IntPtr.Zero))
                        return;
                    int error = Marshal.GetLastWin32Error();
                    if (error == ERROR_SERVICE_ALREADY_RUNNING)
                        return;
                    if (error != ERROR_SERVICE_CANNOT_ACCEPT_CTRL || attempt >= 20)
                        throw new Win32Exception(error);
                    Thread.Sleep(250);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_ACTIVE = 0x1;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
        public int ProcessId;
        public int ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public string? ServiceName;
        public string? DisplayName;
        public SERVICE_STATUS_PROCESS Status;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(IntPtr service, int argCount, IntPtr args);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, int bufferSize, out int bytesNeeded, out int count, ref int resumeHandle, string? groupName);
}
