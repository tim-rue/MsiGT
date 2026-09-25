namespace MsiGT;

/// <summary>Edits which programs are restarted or never closed when freeing the GPU.</summary>
internal sealed class SettingsDialog : Form
{
    private const string RestartChoice = "Restart it";
    private const string NeverCloseChoice = "Never close it";

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        EditMode = DataGridViewEditMode.EditOnEnter,
        Margin = new Padding(0, 4, 0, 8),
    };
    private readonly CheckBox _windowsComponents = new()
    {
        Text = "Restart Windows components too (Explorer, Start menu, Search…)",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4),
    };
    private readonly CheckBox _systemProcesses = new()
    {
        Text = "End system processes and services too (critical ones are never touched)",
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
    };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 60, Width = 56, Margin = new Padding(0, 0, 4, 0) };

    public SettingsDialog(Settings settings)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        Text = "Free up GPU settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 440);
        MinimumSize = new Size(400, 360);
        Padding = new Padding(12);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Program", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "When freeing the GPU",
            Items = { RestartChoice, NeverCloseChoice },
            FillWeight = 40,
            FlatStyle = FlatStyle.Flat,
        });
        _grid.DefaultValuesNeeded += (_, e) => e.Row.Cells[1].Value = RestartChoice;
        foreach (var name in settings.Restart)
            _grid.Rows.Add(name, RestartChoice);
        foreach (var name in settings.NeverClose)
            _grid.Rows.Add(name, NeverCloseChoice);

        _windowsComponents.Checked = settings.CloseWindowsComponents;
        _systemProcesses.Checked = settings.CloseSystemProcesses;
        _timeout.Value = Math.Clamp(settings.CloseTimeoutSeconds, 1, 60);

        var intro = Ui.Label();
        intro.Text = "Programs by file name, e.g. firefox.exe. Other apps are closed and not restarted. " +
                     "Tip: right-click a process in the list to add it here.";

        var remove = Ui.Button("Remove");
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (!row.IsNewRow)
                    _grid.Rows.Remove(row);
        };

        var timeoutRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 12) };
        timeoutRow.Controls.Add(new Label { Text = "Give apps", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 4, 0) });
        timeoutRow.Controls.Add(_timeout);
        timeoutRow.Controls.Add(new Label { Text = "seconds to close before ending them", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 0) });

        var ok = Ui.Button("OK");
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = Padding.Empty;
        AcceptButton = ok;
        CancelButton = cancel;
        var dialogButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, WrapContents = false, Margin = Padding.Empty };
        dialogButtons.Controls.Add(ok);
        dialogButtons.Controls.Add(cancel);

        var bottom = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(remove, 0, 0);
        bottom.Controls.Add(dialogButtons, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (control, style) in new (Control, RowStyle)[]
                 {
                     (intro, new RowStyle(SizeType.AutoSize)),
                     (_grid, new RowStyle(SizeType.Percent, 100)),
                     (_windowsComponents, new RowStyle(SizeType.AutoSize)),
                     (_systemProcesses, new RowStyle(SizeType.AutoSize)),
                     (timeoutRow, new RowStyle(SizeType.AutoSize)),
                     (bottom, new RowStyle(SizeType.AutoSize)),
                 })
        {
            layout.RowStyles.Add(style);
            layout.Controls.Add(control);
        }
        Controls.Add(layout);
        ResumeLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _grid.CurrentCell = null;
        _grid.ClearSelection();
    }

    public void ApplyTo(Settings settings)
    {
        _grid.EndEdit();
        var restart = new List<string>();
        var neverClose = new List<string>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow || row.Cells[0].Value is not string raw || raw.Trim() is not { Length: > 0 } name)
                continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            var target = row.Cells[1].Value as string == NeverCloseChoice ? neverClose : restart;
            restart.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            neverClose.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            target.Add(name);
        }
        settings.Restart = restart;
        settings.NeverClose = neverClose;
        settings.CloseWindowsComponents = _windowsComponents.Checked;
        settings.CloseSystemProcesses = _systemProcesses.Checked;
        settings.CloseTimeoutSeconds = (int)_timeout.Value;
    }
}
