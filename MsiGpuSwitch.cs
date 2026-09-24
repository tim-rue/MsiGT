using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GraphicsSwitcher;

public enum GpuMode
{
    Hybrid = 0,
    Discrete = 1,
    Integrated = 2,
}

public sealed record GpuSwitchState(
    bool Supported,
    bool IntegratedSupported,
    bool DiscreteSupported,
    GpuMode Current,
    GpuMode Requested,
    byte RawFlags)
{
    public bool SwitchPending => Requested != Current;
}

/// <summary>
/// Drives the MSI "new GPU switch" the same way MSI Center does.
///
/// The mode lives in byte 5 of the UEFI variable MsiDCVarData:
///   bits 0-1  mode requested for the next boot (0 = Hybrid, 1 = Discrete, 2 = Integrated)
///   bits 2-3  mode the machine booted in
///   bit 4     GPU switch supported
///   bit 5     Integrated (UMA) mode supported
///   bit 6     set when Discrete mode is NOT supported
///
/// MSI Center requests a change by writing GPUswitchCH in the registry; the MSI
/// NBFoundation service (OmApSvcBroker) watches that value and edits the UEFI
/// variable. The firmware applies the requested mode on the next reboot.
/// </summary>
public static class MsiGpuSwitch
{
    private const string VariableName = "MsiDCVarData";
    private const string VariableGuid = "{DD96BAAF-145E-4F56-B1CF-193256298E99}";
    private const int FlagsOffset = 5;

    private const string GeneralSettingKey = @"SOFTWARE\WOW6432Node\MSI\MSI Center\Component\Base Module\GeneralSetting";
    private const string ChangeRequestValue = "GPUswitchCH";
    private const int NoChangeRequest = 6; // what the MSI service resets GPUswitchCH to on startup

    public static GpuSwitchState ReadState()
    {
        var (data, _) = ReadVariable();
        return Parse(data[FlagsOffset]);
    }

    private static GpuSwitchState Parse(byte flags) => new(
        Supported: (flags & 0x10) != 0,
        IntegratedSupported: (flags & 0x20) != 0,
        DiscreteSupported: (flags & 0x40) == 0,
        Current: (GpuMode)((flags >> 2) & 0x3),
        Requested: (GpuMode)(flags & 0x3),
        RawFlags: flags);

    /// <summary>
    /// Requests <paramref name="mode"/> for the next boot: records it in the UEFI
    /// variable, then (unless it is the mode we're already in) arms the switch in the
    /// EC so the firmware acts on it. Returns true if the MSI service recorded the
    /// mode, false if the direct UEFI fallback was used.
    /// If arming fails, the stored mode is reset to the current one so no half-done
    /// switch is left behind.
    /// </summary>
    public static bool RequestMode(GpuMode mode)
    {
        bool viaMsiService = StoreRequestedMode(mode);

        var current = ReadState().Current;
        if (current == mode)
            return viaMsiService;

        try
        {
            MsiAcpi.ArmGpuSwitch();
        }
        catch (Exception armError)
        {
            try
            {
                StoreRequestedMode(current);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException(
                    $"{armError.Message}\n\nResetting the saved mode to {current} also failed: {rollbackError.Message}", armError);
            }
            throw new InvalidOperationException(
                $"{armError.Message}\n\nThe saved mode was reset to {current}, so nothing changes on the next restart.", armError);
        }
        return viaMsiService;
    }

    /// <summary>
    /// Goes through the MSI service first so MSI Center stays in sync; if the service
    /// does not pick the request up (e.g. its registry watchers were dropped after sleep),
    /// applies the identical UEFI edit directly.
    /// </summary>
    private static bool StoreRequestedMode(GpuMode mode)
    {
        if (ReadState().Requested == mode)
            return true;

        using (var key = Registry.LocalMachine.OpenSubKey(GeneralSettingKey, writable: true))
        {
            if (key != null)
            {
                // The service re-reads the value when it changes; bouncing through the
                // idle value guarantees a change event even if the same mode was requested before.
                key.SetValue(ChangeRequestValue, NoChangeRequest, RegistryValueKind.DWord);
                key.SetValue(ChangeRequestValue, (int)mode, RegistryValueKind.DWord);

                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    if (ReadState().Requested == mode)
                        return true;
                }
            }
        }

        WriteRequestedModeDirectly(mode);
        return false;
    }

    private static void WriteRequestedModeDirectly(GpuMode mode)
    {
        var (data, attributes) = ReadVariable();
        data[FlagsOffset] = (byte)((data[FlagsOffset] & 0xFC) | (int)mode);

        if (!SetFirmwareEnvironmentVariableEx(VariableName, VariableGuid, data, (uint)data.Length, attributes))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Writing the MsiDCVarData UEFI variable failed");

        if (ReadState().Requested != mode)
            throw new InvalidOperationException("The UEFI variable did not keep the requested GPU mode.");
    }

    private static (byte[] Data, uint Attributes) ReadVariable()
    {
        EnableSystemEnvironmentPrivilege();

        var buffer = new byte[4096];
        uint length = GetFirmwareEnvironmentVariableEx(VariableName, VariableGuid, buffer, (uint)buffer.Length, out uint attributes);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading the MsiDCVarData UEFI variable failed");
        if (length <= FlagsOffset)
            throw new InvalidOperationException($"MsiDCVarData is only {length} bytes long.");

        return (buffer[..(int)length], attributes);
    }

    private static bool _privilegeEnabled;

    private static void EnableSystemEnvironmentPrivilege()
    {
        if (_privilegeEnabled)
            return;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out LUID luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            // AdjustTokenPrivileges succeeds even when the privilege is not held, so check the last error too.
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                || Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                throw new Win32Exception(ERROR_NOT_ALL_ASSIGNED, "SeSystemEnvironmentPrivilege is not available (run as administrator)");
        }
        finally
        {
            CloseHandle(token);
        }
        _privilegeEnabled = true;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFirmwareEnvironmentVariableExW")]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid, byte[] buffer, uint size, out uint attributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetFirmwareEnvironmentVariableExW")]
    private static extern bool SetFirmwareEnvironmentVariableEx(string name, string guid, byte[] value, uint size, uint attributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
