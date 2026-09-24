using System.Runtime.InteropServices;

namespace MsiGT;

/// <summary>
/// The discrete GPU of a hybrid-graphics laptop: the adapter Windows offers as "High performance".
///
/// Nothing here wakes the GPU up (polling nvidia-smi can, since NVML talks to the device):
/// adapter details come from the kernel graphics stack, per-process usage from dxgkrnl's
/// "GPU Process Memory" performance counters (the data Task Manager uses, and the same
/// process list nvidia-smi reports), and the power state from Plug and Play.
/// </summary>
internal sealed class DiscreteGpu
{
    private static readonly Guid DisplayDeviceArrivalInterface = new("1CA05180-A699-450A-9A0C-DE4FBE3DDD89");
    private const uint AdapterTypeHybridDiscrete = 1 << 4;
    private const int PowerDeviceD0 = 1;

    public string Name { get; }
    private readonly string _instanceId;
    private readonly uint _luidLow;
    private readonly int _luidHigh;
    private readonly string _counterTag;

    private DiscreteGpu(string name, string instanceId, uint luidLow, int luidHigh)
    {
        Name = name;
        _instanceId = instanceId;
        _luidLow = luidLow;
        _luidHigh = luidHigh;
        _counterTag = $"_luid_0x{(uint)luidHigh:X8}_0x{luidLow:X8}_";
    }

    /// <summary>Returns the adapter the kernel flags as the hybrid discrete GPU, or null if there is none.</summary>
    public static DiscreteGpu? Find()
    {
        foreach (var deviceName in GetDeviceInterfaces(DisplayDeviceArrivalInterface))
        {
            var open = new D3DKMT_OPENADAPTERFROMDEVICENAME { pDeviceName = deviceName };
            if (D3DKMTOpenAdapterFromDeviceName(ref open) != 0)
                continue;

            uint adapterType;
            var typeBuffer = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                var query = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter,
                    Type = KMTQAITYPE_ADAPTERTYPE,
                    pPrivateDriverData = typeBuffer,
                    PrivateDriverDataSize = sizeof(uint),
                };
                if (D3DKMTQueryAdapterInfo(ref query) != 0)
                    continue;
                adapterType = (uint)Marshal.ReadInt32(typeBuffer);
            }
            finally
            {
                Marshal.FreeHGlobal(typeBuffer);
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }

            if ((adapterType & AdapterTypeHybridDiscrete) == 0)
                continue;

            var instanceId = GetInterfaceString(deviceName, DEVPKEY_Device_InstanceId) ?? "";
            var name = GetDeviceString(instanceId, DEVPKEY_Device_FriendlyName)
                       ?? GetDeviceString(instanceId, DEVPKEY_Device_DeviceDesc)
                       ?? "discrete GPU";
            return new DiscreteGpu(name, instanceId, open.AdapterLuid.LowPart, open.AdapterLuid.HighPart);
        }
        return null;
    }

    /// <summary>
    /// Processes that hold memory on the GPU. Each one keeps it from powering down.
    /// </summary>
    public List<int> GetProcessIds()
    {
        var pids = new SortedSet<int>();
        foreach (var path in ExpandCounterPath(@"\GPU Process Memory(*)\Total Committed"))
        {
            // \GPU Process Memory(pid_1234_luid_0x00000000_0x0001ABCD_phys_0)\Total Committed
            int start = path.IndexOf("(pid_", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || path.IndexOf(_counterTag, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            start += 5;
            int end = path.IndexOf('_', start);
            if (end > start && int.TryParse(path.AsSpan(start, end - start), out int pid))
                pids.Add(pid);
        }
        return pids.ToList();
    }

    /// <summary>True while the GPU is in D0, false once it has powered down, null if Windows doesn't say.</summary>
    public bool? IsPoweredOn()
    {
        if (CM_Locate_DevNodeW(out uint devInst, _instanceId, 0) != 0)
            return null;
        var data = GetDevNodeProperty(devInst, DEVPKEY_Device_PowerData);
        // CM_POWER_DATA: ULONG PD_Size; DEVICE_POWER_STATE PD_MostRecentPowerState; ...
        return data is { Length: >= 8 } ? BitConverter.ToInt32(data, 4) == PowerDeviceD0 : null;
    }

    /// <summary>True if an active display is driven by or rendered on this GPU; it can't power down then.</summary>
    public bool HasActiveDisplay()
    {
        const int pathSize = 72, modeSize = 64;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
                return false;
            var paths = new byte[pathCount * pathSize];
            var modes = new byte[modeCount * modeSize];
            int result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ERROR_INSUFFICIENT_BUFFER)
                continue;
            if (result != 0)
                return false;

            // DISPLAYCONFIG_PATH_INFO: sourceInfo.adapterId at offset 0, targetInfo.adapterId at offset 20.
            for (int i = 0; i < pathCount; i++)
                if (IsThisAdapter(paths, i * pathSize) || IsThisAdapter(paths, i * pathSize + 20))
                    return true;
            return false;
        }
        return false;
    }

    private bool IsThisAdapter(byte[] buffer, int offset) =>
        BitConverter.ToUInt32(buffer, offset) == _luidLow && BitConverter.ToInt32(buffer, offset + 4) == _luidHigh;

    private static List<string> ExpandCounterPath(string wildcardPath)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint length = 0;
            uint status = PdhExpandWildCardPathW(null, wildcardPath, null, ref length, 0);
            if (status != PDH_MORE_DATA && status != 0)
                return [];
            if (length == 0)
                return [];

            var buffer = new char[length];
            status = PdhExpandWildCardPathW(null, wildcardPath, buffer, ref length, 0);
            if (status == PDH_MORE_DATA)
                continue; // instances appeared between the two calls
            if (status != 0)
                return [];
            return SplitMultiString(buffer);
        }
        return [];
    }

    private static List<string> GetDeviceInterfaces(Guid interfaceClass)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0)
                return [];
            var buffer = new char[length];
            uint result = CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (result == CR_BUFFER_SMALL)
                continue;
            return result == 0 ? SplitMultiString(buffer) : [];
        }
        return [];
    }

    private static List<string> SplitMultiString(char[] buffer) =>
        new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string? GetInterfaceString(string deviceInterface, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(deviceInterface, ref key, out _, null, ref size, 0);
        if (size == 0)
            return null;
        var buffer = new byte[size];
        return CM_Get_Device_Interface_PropertyW(deviceInterface, ref key, out _, buffer, ref size, 0) == 0
            ? DecodeString(buffer)
            : null;
    }

    private static string? GetDeviceString(string instanceId, DEVPROPKEY key)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
            return null;
        var data = GetDevNodeProperty(devInst, key);
        return data == null ? null : DecodeString(data);
    }

    private static byte[]? GetDevNodeProperty(uint devInst, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0)
            return null;
        var buffer = new byte[size];
        return CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0) == 0 ? buffer : null;
    }

    private static string? DecodeString(byte[] data)
    {
        var s = System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
        return s.Length > 0 ? s : null;
    }

    #region Interop

    private const int KMTQAITYPE_ADAPTERTYPE = 15;
    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private static readonly Guid DevPropDevice = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new(DevPropDevice, 2);
    private static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(DevPropDevice, 14);
    private static readonly DEVPROPKEY DEVPKEY_Device_PowerData = new(DevPropDevice, 32);
    private static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMDEVICENAME
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDeviceName;
        public uint hAdapter;
        public LUID AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromDeviceName(ref D3DKMT_OPENADAPTERFROMDEVICENAME open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhExpandWildCardPathW(string? dataSource, string wildCardPath, char[]? expandedPathList, ref uint pathListLength, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, byte[] paths, ref uint modeCount, byte[] modes, IntPtr topologyId);

    #endregion
}
