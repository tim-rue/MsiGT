using System.Diagnostics;

namespace GraphicsSwitcher;

internal static class Program
{
    private const string Title = "Graphics Switcher";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case null:
                    Toggle();
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
                    ShowError("Usage: GraphicsSwitcher [--hybrid | --integrated | --status-file <path>]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return 1;
        }
    }

    private static void Toggle()
    {
        var state = MsiGpuSwitch.ReadState();
        if (!state.Supported || !state.IntegratedSupported)
        {
            ShowError("This device's firmware does not report support for switching to Integrated graphics.\n\n" + Describe(state));
            return;
        }

        if (state.SwitchPending)
        {
            var restart = new TaskDialogButton("Restart now");
            var revert = new TaskDialogButton($"Stay in {Name(state.Current)}");
            var result = TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = Title,
                Heading = $"Switch to {Name(state.Requested)} is pending",
                Text = $"You're currently in {Name(state.Current)}. The switch happens when you restart.",
                Icon = TaskDialogIcon.Information,
                Buttons = { restart, revert, TaskDialogButton.Close },
            });

            if (result == restart)
            {
                MsiGpuSwitch.RequestMode(state.Requested); // make sure the firmware is armed
                Restart();
            }
            else if (result == revert)
                MsiGpuSwitch.RequestMode(state.Current);
            return;
        }

        var target = state.Current == GpuMode.Integrated ? GpuMode.Hybrid : GpuMode.Integrated;
        var switchNow = new TaskDialogButton("Switch and restart now");
        var switchLater = new TaskDialogButton("Switch on next restart");
        var choice = TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = Title,
            Heading = $"Switch to {Name(target)}?",
            Text = $"You're currently in {Name(state.Current)}. The change takes effect after a restart.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            Buttons = { switchNow, switchLater, TaskDialogButton.Cancel },
            DefaultButton = switchNow,
        });

        if (choice != switchNow && choice != switchLater)
            return;

        bool viaMsiService = MsiGpuSwitch.RequestMode(target);

        if (choice == switchNow)
        {
            Restart();
            return;
        }

        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = Title,
            Heading = $"{Name(target)} will be active after your next restart",
            Text = viaMsiService
                ? "Run Graphics Switcher again before restarting to undo it."
                : "The MSI service didn't respond, so the setting was written to the firmware directly. " +
                  "Run Graphics Switcher again before restarting to undo it.",
            Icon = TaskDialogIcon.ShieldSuccessGreenBar,
            Buttons = { TaskDialogButton.OK },
        });
    }

    private static void Restart() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });

    private static string Name(GpuMode mode) => mode switch
    {
        GpuMode.Hybrid => "Hybrid (MSHybrid) graphics",
        GpuMode.Integrated => "Integrated graphics",
        GpuMode.Discrete => "Discrete graphics",
        _ => $"unknown mode {(int)mode}",
    };

    private static string Describe(GpuSwitchState s) =>
        $"Current: {s.Current}\nRequested for next boot: {s.Requested}\n" +
        $"Switch supported: {s.Supported}, Integrated supported: {s.IntegratedSupported}, Discrete supported: {s.DiscreteSupported}\n" +
        $"Raw flags: 0x{s.RawFlags:X2}\n";

    private static void ShowError(string message) =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = Title,
            Heading = "Couldn't switch graphics mode",
            Text = message,
            Icon = TaskDialogIcon.Error,
            Buttons = { TaskDialogButton.OK },
        });
}
