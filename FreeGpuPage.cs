namespace MsiGT;

/// <summary>
/// The "Free up GPU" tab: a live list of the processes that keep the discrete GPU awake,
/// and a button that closes them so the GPU can power down.
/// </summary>
internal sealed class FreeGpuPage : UserControl
{
    private const int RefreshMilliseconds = 2000;

    private readonly Settings _settings;
    private readonly Label _headline = Ui.Heading();
    private readonly Label _detail = Ui.Label();
    private readonly Label _warning = Ui.Label();
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        MultiSelect = false,
        Margin = new Padding(0, 8, 0, 8),
    };
    private readonly Label _status = Ui.Label();
    private readonly ProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 16, Visible = false, Margin = new Padding(0, 0, 0, 8) };
    private readonly Button _closeButton = Ui.Button("Close processes");
    private readonly Button _settingsButton = Ui.Button("Settings…");
    private readonly ToolStripMenuItem _restartItem = new() { CheckOnClick = false };
    private readonly ToolStripMenuItem _neverCloseItem = new() { CheckOnClick = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = RefreshMilliseconds };

    private DiscreteGpu? _gpu;
    private bool _gpuSearched;
    private bool _displayAttached;
    private List<GpuUser> _users = [];
    private string _shownKey = "";
    private bool _refreshing;
    private bool _running;
    private bool _monitoring;

    /// <summary>Raised after each refresh with the number of processes on the GPU (null if unknown).</summary>
    public event Action<int?>? CountChanged;

    public FreeGpuPage(Settings settings)
    {
        _settings = settings;
        AutoScaleMode = AutoScaleMode.Inherit;
        Padding = new Padding(12);

        _warning.ForeColor = Color.FromArgb(0xB0, 0x50, 0x00);
        _warning.Visible = false;

        _list.Columns.Add("Process");
        _list.Columns.Add("PID", -2, HorizontalAlignment.Right);
        _list.Columns.Add("When freeing the GPU");
        _list.ClientSizeChanged += (_, _) => SizeColumns();
        _list.ContextMenuStrip = new ContextMenuStrip { Items = { _restartItem, _neverCloseItem } };
        _list.ContextMenuStrip.Opening += OnMenuOpening;
        _restartItem.Click += (_, _) => ToggleListEntry(_settings.Restart);
        _neverCloseItem.Click += (_, _) => ToggleListEntry(_settings.NeverClose);

        _closeButton.Click += (_, _) => CloseProcesses();
        _settingsButton.Click += (_, _) => EditSettings();
        _timer.Tick += (_, _) => RefreshNow();

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(_settingsButton);
        buttons.Controls.Add(_closeButton);
        _closeButton.Margin = Padding.Empty;

        var bottom = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _status.Dock = DockStyle.None;
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control, RowStyle style)
        {
            // With an explicit row: auto-placement skips hidden controls (the warning) and shifts the rest.
            layout.RowStyles.Add(style);
            layout.Controls.Add(control, 0, layout.RowStyles.Count - 1);
        }
        Add(_headline, new RowStyle(SizeType.AutoSize));
        Add(_detail, new RowStyle(SizeType.AutoSize));
        Add(_warning, new RowStyle(SizeType.AutoSize));
        Add(_list, new RowStyle(SizeType.Percent, 100));
        Add(_progress, new RowStyle(SizeType.AutoSize));
        Add(bottom, new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        _headline.Text = "Looking for processes on the discrete GPU…";
        UpdateButtons();
        Disposed += (_, _) => _timer.Dispose();
    }

    /// <summary>Starts or stops the periodic refresh (it's only needed while the window is visible).</summary>
    public void SetMonitoring(bool enabled)
    {
        if (enabled == _monitoring)
            return;
        _monitoring = enabled;
        _timer.Enabled = enabled && !_running;
        if (enabled)
            RefreshNow();
    }

    private sealed record Snapshot(DiscreteGpu? Gpu, List<GpuUser> Users, bool? PoweredOn, bool DisplayAttached);

    private async void RefreshNow()
    {
        if (_refreshing || _running)
            return;
        _refreshing = true;
        try
        {
            var settings = _settings.Clone();
            var snapshot = await Task.Run(() =>
            {
                if (!_gpuSearched)
                {
                    _gpu = DiscreteGpu.Find();
                    _gpuSearched = true;
                }
                if (_gpu == null)
                    return new Snapshot(null, [], null, false);
                return new Snapshot(_gpu, GpuUsers.Scan(_gpu, settings), _gpu.IsPoweredOn(), _gpu.HasActiveDisplay());
            });
            if (!_running)
                Show(snapshot);
        }
        catch (Exception ex)
        {
            _headline.Text = "Couldn't read what's using the GPU";
            _detail.Text = ex.Message;
            CountChanged?.Invoke(null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Show(Snapshot snapshot)
    {
        _users = snapshot.Users;
        _displayAttached = snapshot.DisplayAttached;
        CountChanged?.Invoke(snapshot.Gpu == null ? null : _users.Count);

        if (snapshot.Gpu == null)
        {
            _headline.Text = "No discrete GPU found";
            _detail.Text = "Windows doesn't report a switchable discrete GPU, so there's nothing to free up. " +
                           "This only works in Hybrid mode.";
        }
        else if (_users.Count == 0)
        {
            _headline.Text = $"Nothing is using the {snapshot.Gpu.Name}";
            _detail.Text = snapshot.PoweredOn switch
            {
                false => "It has powered down.",
                true => "It's still on and should power down shortly.",
                null => "",
            };
        }
        else
        {
            int closable = _users.Count(u => u.CanClose);
            _headline.Text = $"{Plural(_users.Count, "process")} {(_users.Count == 1 ? "keeps" : "keep")} the {snapshot.Gpu.Name} awake";
            _detail.Text = (closable == _users.Count ? "All of them can be closed." : $"{closable} of them can be closed.") +
                           " Right-click a process to change what happens to it.";
        }

        _warning.Visible = snapshot.DisplayAttached;
        _warning.Text = "A display is connected to this GPU. It stays on until you disconnect that display.";

        ShowList();
        UpdateButtons();
    }

    private void ShowList()
    {
        var key = string.Join("|", _users.Select(u => $"{u.Process.Pid}:{u.Kind}:{u.Restart}:{u.DisplayName}"));
        if (key == _shownKey)
            return;
        _shownKey = key;

        int? selectedPid = (_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as GpuUser : null)?.Process.Pid;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var user in _users)
        {
            var item = new ListViewItem([user.DisplayName, user.Process.Pid.ToString(), user.ActionText])
            {
                Tag = user,
                ToolTipText = user.Process.ExePath ?? user.Process.FileName,
                ForeColor = user.CanClose ? SystemColors.WindowText : SystemColors.GrayText,
                Selected = user.Process.Pid == selectedPid,
            };
            _list.Items.Add(item);
        }
        _list.ShowItemToolTips = true;
        _list.EndUpdate();
        SizeColumns();
    }

    private void SizeColumns()
    {
        if (_list.Columns.Count < 3)
            return;
        int width = _list.ClientSize.Width;
        int pid = LogicalToDeviceUnits(64);
        _list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(120), (width - pid) * 55 / 100);
        _list.Columns[1].Width = pid;
        _list.Columns[2].Width = Math.Max(LogicalToDeviceUnits(100), width - pid - _list.Columns[0].Width);
    }

    private void UpdateButtons()
    {
        _closeButton.Enabled = !_running && _users.Any(u => u.CanClose);
        _settingsButton.Enabled = !_running;
        _list.ContextMenuStrip!.Enabled = !_running;
    }

    private GpuUser? SelectedUser => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as GpuUser : null;

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var user = SelectedUser;
        if (user == null || user.Kind == GpuUserKind.System || _running)
        {
            e.Cancel = true;
            return;
        }
        string file = user.Process.FileName;
        _restartItem.Text = $"Restart {file} after closing it";
        _restartItem.Checked = _settings.ShouldRestart(file);
        _restartItem.Enabled = user.Kind == GpuUserKind.App || _restartItem.Checked;
        _neverCloseItem.Text = $"Never close {file}";
        _neverCloseItem.Checked = _settings.IsNeverClose(file);
    }

    private void ToggleListEntry(List<string> list)
    {
        if (SelectedUser is not { } user)
            return;
        string file = user.Process.FileName;
        if (list.RemoveAll(entry => string.Equals(entry, file, StringComparison.OrdinalIgnoreCase)) == 0)
            list.Add(file);
        SaveSettings();
        RefreshNow();
    }

    private void EditSettings()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        dialog.ApplyTo(_settings);
        SaveSettings();
        RefreshNow();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = Program.Title,
                Heading = "Couldn't save the settings",
                Text = ex.Message,
                Icon = TaskDialogIcon.Error,
            });
        }
    }

    private async void CloseProcesses()
    {
        var gpu = _gpu;
        if (gpu == null || _running)
            return;

        if (_displayAttached)
        {
            var closeAnyway = new TaskDialogButton("Close them anyway");
            var answer = TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = Program.Title,
                Heading = "A display is connected to the discrete GPU",
                Text = $"The {gpu.Name} stays on while a display uses it, so closing these processes won't let it " +
                       "power down, and they may start using it again right away.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { closeAnyway, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel,
            });
            if (answer != closeAnyway)
                return;
        }

        _running = true;
        _timer.Stop();
        UpdateButtons();
        _progress.Value = 0;
        _progress.Visible = true;

        var progress = new Progress<FreeProgress>(p =>
        {
            _progress.Maximum = Math.Max(1, p.Total);
            _progress.Value = Math.Clamp(p.Done, 0, _progress.Maximum);
            _status.Text = p.Text;
        });

        FreeReport? report = null;
        try
        {
            var settings = _settings.Clone();
            report = await Task.Run(() => GpuFreer.Run(gpu, settings, progress));
        }
        catch (Exception ex)
        {
            _status.Text = "";
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = Program.Title,
                Heading = "Freeing the GPU failed",
                Text = ex.Message,
                Icon = TaskDialogIcon.Error,
            });
        }
        finally
        {
            _running = false;
            _progress.Visible = false;
            _shownKey = "";
            _timer.Enabled = _monitoring;
            UpdateButtons();
        }

        if (report != null)
        {
            _status.Text = report.Remaining == 0
                ? "Done. Nothing is using the GPU any more."
                : $"Done. {Plural(report.Remaining, "process")} still {(report.Remaining == 1 ? "uses" : "use")} the GPU.";
            if (report.Problems.Count > 0)
                TaskDialog.ShowDialog(this, new TaskDialogPage
                {
                    Caption = Program.Title,
                    Heading = "Some processes need your attention",
                    Text = string.Join("\n\n", report.Problems),
                    Icon = TaskDialogIcon.Warning,
                });
        }
        RefreshNow();
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}es";
}
