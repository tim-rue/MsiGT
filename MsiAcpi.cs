using System.Management;

namespace GraphicsSwitcher;

/// <summary>
/// Minimal client for MSI's MSI_ACPI WMI interface (root\WMI), using the same
/// 32-byte packet layout as MSI's MSIWMIACPI2.dll.
/// </summary>
internal static class MsiAcpi
{
    private const string InstancePath = @"MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'";
    private const int PackageSize = 32;

    // EC register that MSI Center's "GraphicsSwithc" command writes to. Bits 0-1 hold a
    // pending firmware action; 1 tells the firmware to apply the GPU mode stored in
    // MsiDCVarData on the next boot. Other bits (e.g. bit 3, Battery Boost) are preserved.
    private const byte GpuSwitchActionRegister = 0xD1;

    /// <summary>
    /// Mirrors API_NB_Base Module's SetStatus("GraphicsSwithc"): read Get_AP(0),
    /// take byte 1, set its low two bits to 01 and write it to EC register 0xD1.
    /// </summary>
    public static void ArmGpuSwitch()
    {
        var ap = Invoke("Get_AP", Package(0));
        byte value = (byte)((ap[1] & 0xFC) | 0x01);

        Invoke("Set_Data", Package(GpuSwitchActionRegister, value));

        // MSI Center observes Get_AP(0) byte 2 turning 2 once the firmware has accepted the request.
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            if (Invoke("Get_AP", Package(0))[2] == 2)
                return;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("The firmware did not acknowledge the GPU switch request.");
    }

    /// <summary>Raw Get_AP(0) response (read-only), for diagnostics.</summary>
    public static byte[] ReadActionStatus() => Invoke("Get_AP", Package(0))[..8];

    private static byte[] Package(params byte[] header)
    {
        var bytes = new byte[PackageSize];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>Invokes an MSI_ACPI method; returns the output bytes, where byte 0 is the success flag.</summary>
    private static byte[] Invoke(string method, byte[] input)
    {
        using var acpi = new ManagementObject(@"root\WMI", InstancePath, null);
        using var inParams = acpi.GetMethodParameters(method);
        using var packageClass = new ManagementClass(@"root\WMI", "Package_32", null);
        using var package = packageClass.CreateInstance();
        package["Bytes"] = input;
        inParams["Data"] = package;

        using var outParams = acpi.InvokeMethod(method, inParams, null);
        var output = (byte[])((ManagementBaseObject)outParams["Data"])["Bytes"];
        if (output.Length < 3 || output[0] == 0)
            throw new InvalidOperationException($"MSI_ACPI.{method} failed.");
        return output;
    }
}
