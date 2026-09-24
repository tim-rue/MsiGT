namespace MsiGT;

/// <summary>The "Graphics mode" tab: shows the current mode and switches between Hybrid and Integrated.</summary>
internal sealed class ModePage : UserControl
{
    private readonly Label _caption = Ui.Label(dim: true);
    private readonly Label _heading = Ui.Heading();
    private readonly Label _text = Ui.Label();
    private readonly FlowLayoutPanel _buttons = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0), WrapContents = true };
    private readonly Label _status = Ui.Label();
    private readonly Label _details = Ui.Label(dim: true);

    private GpuSwitchState? _state;
    private Exception? _error;

    public ModePage(GpuSwitchState? state, Exception? error)
    {
        _state = state;
        _error = error;
        AutoScaleMode = AutoScaleMode.Inherit;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var control in new Control[] { _caption, _heading, _text, _buttons, _status })
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Panel());
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_details);
        _status.Margin = new Padding(0, 12, 0, 0);
        Controls.Add(layout);

        Render();
    }

    private void Render()
    {
        _buttons.Controls.Clear();
        _caption.Text = "Current mode";

        if (_error != null || _state == null)
        {
            _caption.Text = "";
            _heading.Text = "Couldn't read the graphics mode";
            _text.Text = _error?.Message ?? "";
            _details.Text = "";
            return;
        }

        var state = _state;
        _details.Text = Program.Describe(state).TrimEnd();

        if (!state.Supported || !state.IntegratedSupported)
        {
            _heading.Text = Program.Name(state.Current);
            _text.Text = "This device's firmware does not report support for switching to Integrated graphics.";
            return;
        }

        if (state.SwitchPending)
        {
            _heading.Text = $"Switch to {Program.Name(state.Requested)} is pending";
            _caption.Text = $"Current mode: {Program.Name(state.Current)}";
            _text.Text = "The switch happens when you restart.";
            // RequestMode re-arms the firmware in case the pending switch was set up by something else.
            AddButton("Restart now", () => Switch(state.Requested, restart: true));
            AddButton($"Stay in {Program.Name(state.Current)}", () => Switch(state.Current, restart: false));
            return;
        }

        var target = state.Current == GpuMode.Integrated ? GpuMode.Hybrid : GpuMode.Integrated;
        _heading.Text = Program.Name(state.Current);
        _text.Text = $"Switch to {Program.Name(target)}? The change takes effect after a restart.";
        AddButton("Switch and restart now", () => Switch(target, restart: true));
        AddButton("Switch on next restart", () => Switch(target, restart: false));
    }

    private void AddButton(string text, Action onClick)
    {
        var button = Ui.Button(text);
        button.Click += (_, _) => onClick();
        _buttons.Controls.Add(button);
    }

    private async void Switch(GpuMode mode, bool restart)
    {
        var current = _state!.Current;
        _buttons.Enabled = false;
        _status.Text = "Setting up the switch…";
        try
        {
            bool viaMsiService = await Task.Run(() => MsiGpuSwitch.RequestMode(mode));
            if (restart)
            {
                _status.Text = "Restarting…";
                Program.Restart();
                return;
            }

            _status.Text = mode == current
                ? $"The switch was cancelled. You'll stay in {Program.Name(current)}."
                : $"{Program.Name(mode)} will be active after your next restart." +
                  (viaMsiService ? "" : " The MSI service didn't respond, so the setting was written to the firmware directly.");
        }
        catch (Exception ex)
        {
            _status.Text = "";
            Program.ShowError(ex.Message + "\n\nYour PC was not restarted.");
        }
        finally
        {
            _buttons.Enabled = true;
        }

        try
        {
            _state = await Task.Run(MsiGpuSwitch.ReadState);
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex;
        }
        Render();
    }
}
