using System.Diagnostics;

namespace MsiGT;

internal static class Program
{
    public const string Title = "MSI GPU Tools";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case null:
                    Application.Run(new MainForm());
                    return 0;
                case "--hybrid":
                    MsiGpuSwitch.RequestMode(GpuMode.Hybrid);
                    return 0;
                case "--integrated":
                    MsiGpuSwitch.RequestMode(GpuMode.Integrated);
                    return 0;
                case "--status-file" when args.Length > 1:
                    File.WriteAllText(args[1], Describe(MsiGpuSwitch.ReadState()) +
                        $"Get_AP(0): {Convert.ToHexString(MsiAcpi.ReadActionStatus())}\n");
                    return 0;
                default:
                    ShowError("Usage: MsiGT [--hybrid | --integrated | --status-file <path>]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            // Restart() is only reached after every step succeeded, so no error here can follow a restart.
            ShowError(ex.Message + "\n\nYour PC was not restarted.");
            return 1;
        }
    }

    /// <summary>Only called once the switch is fully set up, so a failure here just means restarting by hand.</summary>
    internal static void Restart()
    {
        string? problem;
        try
        {
            using var shutdown = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false })!;
            shutdown.WaitForExit();
            problem = shutdown.ExitCode == 0 ? null : $"shutdown.exe exited with code {shutdown.ExitCode}.";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (problem != null)
            TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = Title,
                Heading = "Windows couldn't restart automatically",
                Text = $"The graphics switch is set up and will happen when you restart your PC yourself.\n\n{problem}",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.OK },
            });
    }

    internal static string Name(GpuMode mode) => mode switch
    {
        GpuMode.Hybrid => "Hybrid (MSHybrid) graphics",
        GpuMode.Integrated => "Integrated graphics",
        GpuMode.Discrete => "Discrete graphics",
        _ => $"unknown mode {(int)mode}",
    };

    internal static string Describe(GpuSwitchState s) =>
        $"Current: {s.Current}\nRequested for next boot: {s.Requested}\n" +
        $"Switch supported: {s.Supported}, Integrated supported: {s.IntegratedSupported}, Discrete supported: {s.DiscreteSupported}\n" +
        $"Raw flags: 0x{s.RawFlags:X2}\n";

    internal static void ShowError(string message) =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = Title,
            Heading = "Couldn't switch graphics mode",
            Text = message,
            Icon = TaskDialogIcon.Error,
            Buttons = { TaskDialogButton.OK },
        });
}
