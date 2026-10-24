namespace StadiaXbox;

public sealed class MainForm : Form
{
    private readonly Calibration _cal = Calibration.Load();
    private readonly StadiaReader _reader = new();
    private readonly XboxOutput _output;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 25 };

    private StadiaState _latest = StadiaState.Neutral;
    private volatile bool _hasState;

    private readonly Label _status = new();
    private readonly Button _toggle = new();
    private readonly ControllerView _view;

    private readonly Button _calibrate = new();
    private readonly Label _calInfo = new();
    private readonly ProgressBar _lt = new() { Minimum = 0, Maximum = 255 };
    private readonly ProgressBar _rt = new() { Minimum = 0, Maximum = 255 };
    private readonly Label _raw = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _hideStadia = new();

    // Tray: the X button only hides the window; Exit in the tray menu quits.
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayToggle = new("Start bridge");
    private readonly ToolStripMenuItem _trayStartup = new("Start with Windows");
    private readonly Dictionary<BridgeStatus, Icon> _trayIcons = new();
    private readonly RegisteredWaitHandle _showWait;

    private bool _running;
    private bool _stayHidden;
    private bool _exiting;
    private bool _bridgeAutoStarted;
    private bool _trayTipShown;
    private bool _syncingStartup;
    private bool _syncingHide;
    private bool _hideBusy;

    public MainForm(EventWaitHandle showRequested, bool startInTray)
    {
        _output = new XboxOutput(_cal);
        _view = new ControllerView(_cal) { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 25, 29) };

        Text = "Stadia → Xbox";
        Icon = AppIcon.Create(SystemInformation.IconSize.Width);
        BackColor = Color.FromArgb(30, 31, 36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(760, 626);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 566);
        _stayHidden = startInTray;

        BuildUi();
        BuildTray();
        StartupRegistration.RepairPath();
        SyncStartupUi();

        _reader.ConnectionChanged += (_, _) =>
        {
            // A new connection (USB vs Bluetooth) may not be hidden yet, so HidHide's state is re-read too.
            try { BeginInvoke(UpdateStatus); BeginInvoke(RefreshHideOption); } catch { }
        };
        _reader.StateChanged += OnStateChanged;
        _uiTimer.Tick += (_, _) => RefreshTester();

        // Launching the exe again signals this copy to bring its window back.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showRequested,
            (_, _) => { try { BeginInvoke(ShowFromTray); } catch { } }, null, Timeout.Infinite, executeOnlyOnce: false);

        VisibleChanged += (_, _) => _uiTimer.Enabled = _running && Visible; // no tester redraws while hidden
        FormClosing += OnFormClosing;
    }

    // Started with --tray (Windows startup): stay hidden until opened from the tray.
    protected override void SetVisibleCore(bool value)
    {
        if (_stayHidden)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Load never fires while the form starts hidden, so start the bridge from here.
        if (_bridgeAutoStarted) return;
        _bridgeAutoStarted = true;
        BeginInvoke(StartBridge);
        BeginInvoke(RefreshHideOption);
    }

    private void BuildUi()
    {
        // --- top status bar ---
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(12, 0, 0, 0);
        _status.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _status.Text = "Starting…";

        _toggle.Text = "Stop";
        _toggle.Dock = DockStyle.Right;
        _toggle.Width = 110;
        _toggle.FlatStyle = FlatStyle.Flat;
        _toggle.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
        _toggle.BackColor = Color.FromArgb(45, 46, 52);
        _toggle.ForeColor = Color.Gainsboro;
        _toggle.Click += (_, _) => { if (_running) StopBridge(); else StartBridge(); };

        var top = new Panel { Dock = DockStyle.Top, Height = 38 };
        top.Controls.Add(_status);
        top.Controls.Add(_toggle);

        // --- bottom panel: calibrate + triggers + raw ---
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 182,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(12, 8, 12, 8),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        _calibrate.Text = "Calibrate…";
        _calibrate.Height = 32;
        _calibrate.Dock = DockStyle.Left;
        _calibrate.Width = 160;
        _calibrate.FlatStyle = FlatStyle.Flat;
        _calibrate.BackColor = Color.FromArgb(55, 90, 130);
        _calibrate.ForeColor = Color.White;
        _calibrate.Click += (_, _) => RunCalibration();
        bottom.Controls.Add(_calibrate, 0, 0);

        _calInfo.Dock = DockStyle.Fill;
        _calInfo.TextAlign = ContentAlignment.MiddleLeft;
        _calInfo.ForeColor = Color.Silver;
        bottom.Controls.Add(_calInfo, 1, 0);

        _lt.Dock = DockStyle.Top; _lt.Height = 16;
        _rt.Dock = DockStyle.Top; _rt.Height = 16;
        bottom.Controls.Add(Labeled("Left trigger", _lt), 0, 1);
        bottom.Controls.Add(Labeled("Right trigger", _rt), 1, 1);

        _startWithWindows.Text = "Start with Windows (opens in the tray)";
        _startWithWindows.Dock = DockStyle.Fill;
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (!_syncingStartup) SetStartWithWindows(_startWithWindows.Checked);
        };
        bottom.Controls.Add(_startWithWindows, 0, 2);
        bottom.SetColumnSpan(_startWithWindows, 2);

        _hideStadia.Text = "Hide the Stadia controller from other apps, so games only see the Xbox pad (HidHide)";
        _hideStadia.Dock = DockStyle.Fill;
        _hideStadia.Enabled = false; // until HidHide's settings have been read
        _hideStadia.CheckedChanged += (_, _) =>
        {
            if (!_syncingHide) SetHideStadia(_hideStadia.Checked);
        };
        bottom.Controls.Add(_hideStadia, 0, 3);
        bottom.SetColumnSpan(_hideStadia, 2);

        _raw.Dock = DockStyle.Fill;
        _raw.ForeColor = Color.Gray;
        _raw.Font = new Font("Consolas", 8.5f);
        bottom.Controls.Add(_raw, 0, 4);
        bottom.SetColumnSpan(_raw, 2);

        Controls.Add(_view);   // fill
        Controls.Add(bottom);  // bottom
        Controls.Add(top);     // top

        UpdateCalInfo();
    }

    private void BuildTray()
    {
        var open = new ToolStripMenuItem("Open Stadia → Xbox", null, (_, _) => ShowFromTray());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _trayToggle.Click += (_, _) => { if (_running) StopBridge(); else StartBridge(); };
        _trayStartup.Click += (_, _) => SetStartWithWindows(!_trayStartup.Checked);

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            open,
            _trayToggle,
            new ToolStripSeparator(),
            _trayStartup,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        });
        _trayMenu.Opening += (_, _) => SyncStartupUi(); // it can be switched off in Windows Settings too

        foreach (var status in Enum.GetValues<BridgeStatus>())
            _trayIcons[status] = AppIcon.Create(SystemInformation.SmallIconSize.Width, status);

        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        SetTray(BridgeStatus.Stopped, "Starting…");
        _tray.Visible = true;
    }

    private void SetTray(BridgeStatus status, string detail)
    {
        _tray.Icon = _trayIcons[status];
        _tray.Text = $"Stadia → Xbox — {detail}";
    }

    private void ShowFromTray()
    {
        _stayHidden = false;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // The X button sends the window to the tray; the bridge keeps running.
        if (e.CloseReason == CloseReason.UserClosing && !_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _tray.ShowBalloonTip(3000, "Stadia → Xbox is still running",
                    "The controller keeps working from the tray. Right-click the icon to exit.", ToolTipIcon.None);
            }
            return;
        }

        _tray.Visible = false;
        StopBridge();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Visible ? this : null, "Could not change the startup setting.\n\nDetails: " + ex.Message,
                "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SyncStartupUi();
    }

    private void SyncStartupUi()
    {
        bool on = StartupRegistration.IsEnabled;
        _syncingStartup = true;
        _startWithWindows.Checked = on;
        _syncingStartup = false;
        _trayStartup.Checked = on;
    }

    // HidHide is driven through its command-line tool, so its state is read off the UI thread.
    private void RefreshHideOption()
    {
        if (_hideBusy) return;
        if (!HidHide.IsInstalled)
        {
            _hideStadia.Enabled = false;
            _hideStadia.Text = "Hide the Stadia controller from other apps (install HidHide to use this)";
            return;
        }

        Task.Run(() => HidHide.IsHidden()).ContinueWith(t =>
        {
            bool? hidden = t.IsCompletedSuccessfully ? t.Result : null;
            try { BeginInvoke(() => ShowHideState(hidden)); } catch { }
        });
    }

    private void ShowHideState(bool? hidden)
    {
        _syncingHide = true;
        _hideStadia.Checked = hidden == true;
        _syncingHide = false;
        _hideStadia.Enabled = hidden is not null && !_hideBusy;
    }

    private void SetHideStadia(bool hide)
    {
        _hideBusy = true;
        _hideStadia.Enabled = false;
        Task.Run(() => HidHide.SetHidden(hide)).ContinueWith(t =>
        {
            var error = t.Exception?.InnerException;
            try { BeginInvoke(() => HideFinished(hide, error)); } catch { }
        });
    }

    private void HideFinished(bool hide, Exception? error)
    {
        _hideBusy = false;
        var owner = Visible ? this : null;
        if (error is OperationCanceledException)
        {
            // The UAC prompt was declined: nothing changed, the checkbox just goes back.
        }
        else if (error is not null)
        {
            MessageBox.Show(owner, "Could not change the HidHide setting.\n\nDetails: " + error.Message,
                "Hide the Stadia controller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (hide)
        {
            MessageBox.Show(owner,
                "The Stadia controller is now hidden from other apps; this app can still read it.\n\n" +
                "Apps that already had it open (Steam, a running game) keep seeing it until they're restarted " +
                "or the controller is plugged in again.",
                "Hide the Stadia controller", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        RefreshHideOption();
    }

    private static Control Labeled(string caption, Control inner)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label { Text = caption, Dock = DockStyle.Top, Height = 18, AutoSize = false };
        inner.Dock = DockStyle.Top;
        panel.Controls.Add(inner);
        panel.Controls.Add(lbl);
        return panel;
    }

    private void RunCalibration()
    {
        if (!_running)
        {
            MessageBox.Show(this, "Start the bridge first so the controller is being read.",
                "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new CalibrationForm(_reader, _cal);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _cal.Save();
            UpdateCalInfo();
        }
    }

    private void UpdateCalInfo()
    {
        int lp = (int)Math.Round(_cal.LeftDeadzone * 100);
        int rp = (int)Math.Round(_cal.RightDeadzone * 100);
        _calInfo.Text = $"Dead zone  L {lp}%  /  R {rp}%";
    }

    private void StartBridge()
    {
        if (_running) return;
        try
        {
            _output.Connect();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not create the virtual Xbox controller.\n\n" +
                "The ViGEmBus driver needs to be installed.\n\nDetails: " + ex.Message,
                "ViGEmBus missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateStatus();
            return;
        }

        _reader.Start();
        _uiTimer.Enabled = Visible;
        _running = true;
        _toggle.Text = "Stop";
        UpdateStatus();
    }

    private void StopBridge()
    {
        if (!_running) return;
        _running = false;
        _uiTimer.Stop();
        _reader.Stop();
        _output.Dispose();
        _hasState = false;
        _toggle.Text = "Start";
        UpdateStatus();
    }

    private void OnStateChanged(StadiaState s)
    {
        _latest = s;
        _hasState = true;
        _output.Update(s); // low-latency: feed the virtual pad from the read thread
    }

    private void UpdateStatus()
    {
        _trayToggle.Text = _running ? "Stop bridge" : "Start bridge";

        if (!_running)
        {
            _status.Text = "Stopped";
            _status.ForeColor = Color.Gray;
            SetTray(BridgeStatus.Stopped, "Stopped");
            return;
        }

        if (_reader.IsConnected)
        {
            string how = _reader.Transport == Transport.Bluetooth ? "Bluetooth" : "USB";
            _status.Text = $"Controller connected via {how}  →  virtual Xbox pad active";
            _status.ForeColor = Color.FromArgb(100, 210, 130);
            SetTray(BridgeStatus.Connected, $"Connected via {how}");
        }
        else
        {
            _status.Text = "Searching for Stadia controller… (plug in USB or connect Bluetooth)";
            _status.ForeColor = Color.FromArgb(230, 190, 90);
            SetTray(BridgeStatus.Searching, "Searching for controller…");
        }
    }

    private void RefreshTester()
    {
        if (!_hasState) return;

        var s = _latest;
        _view.SetState(s);
        _lt.Value = s.LeftTrigger;
        _rt.Value = s.RightTrigger;
        _raw.Text = $"LX={s.LeftX:X2} LY={s.LeftY:X2}   RX={s.RightX:X2} RY={s.RightY:X2}   LT={s.LeftTrigger:X2} RT={s.RightTrigger:X2}   hat={s.Hat}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
            _output.Dispose();
            _uiTimer.Dispose();
            _showWait.Unregister(null);
            _tray.Dispose();
            _trayMenu.Dispose();
            foreach (var icon in _trayIcons.Values) icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
