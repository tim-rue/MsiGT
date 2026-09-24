namespace MsiGT;

internal sealed class MainForm : Form
{
    private const string FreeTabTitle = "Free up GPU";

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly FreeGpuPage? _freePage;

    public MainForm()
    {
        GpuSwitchState? state = null;
        Exception? stateError = null;
        try
        {
            state = MsiGpuSwitch.ReadState();
        }
        catch (Exception ex)
        {
            stateError = ex;
        }

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        Text = Program.Title;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(480, 360);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Padding = new Padding(8);

        var modeTab = new TabPage("Graphics mode") { UseVisualStyleBackColor = true };
        modeTab.Controls.Add(new ModePage(state, stateError) { Dock = DockStyle.Fill });
        _tabs.TabPages.Add(modeTab);

        // Freeing the discrete GPU only matters in Hybrid mode; offer it too if the mode is unknown.
        if (state == null || state.Current == GpuMode.Hybrid)
        {
            _freePage = new FreeGpuPage(LoadSettings()) { Dock = DockStyle.Fill };
            var freeTab = new TabPage(FreeTabTitle) { UseVisualStyleBackColor = true };
            freeTab.Controls.Add(_freePage);
            _tabs.TabPages.Add(freeTab);
            _freePage.CountChanged += count => freeTab.Text = count is > 0 ? $"{FreeTabTitle} ({count})" : FreeTabTitle;
        }

        Controls.Add(_tabs);
        ResumeLayout();
    }

    private static Settings LoadSettings()
    {
        try
        {
            return Settings.Load();
        }
        catch (Exception ex)
        {
            TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = Program.Title,
                Heading = "Your settings couldn't be loaded",
                Text = $"{ex.Message}\n\nThe default settings are used. Saving settings will replace the file.",
                Icon = TaskDialogIcon.Warning,
            });
            return new Settings();
        }
    }

    // The process list is refreshed while the window is on screen; it also feeds the count in the tab title.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateMonitoring();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateMonitoring();
    }

    private void UpdateMonitoring() => _freePage?.SetMonitoring(Visible && WindowState != FormWindowState.Minimized);
}
