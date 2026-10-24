using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace StadiaXbox;

/// <summary>
/// Guided calibration that follows the controller instead of a clock. Each step
/// finishes once it has seen what it needs: both sticks left alone (resting centre
/// and drift), every direction pushed to the edge (full travel) plus where the stick
/// settles each time it is let go (return-to-centre drift), and the triggers
/// squeezed and released (trigger range). A review screen then shows what games will
/// get and lets the dead zones be fine-tuned. The shared <see cref="Calibration"/> is
/// only changed on Save, so Cancel always leaves the old calibration in place.
/// </summary>
public sealed class CalibrationForm : Form
{
    private enum Step { Intro, Center, Range, Triggers, Review }

    private static readonly Step[] Order = { Step.Center, Step.Range, Step.Triggers, Step.Review };
    private static readonly string[] StepNames = { "Center", "Range", "Triggers", "Review" };

    private static readonly Color Good = Color.FromArgb(100, 210, 130);
    private static readonly Color Warn = Color.FromArgb(230, 190, 90);
    private static readonly Color Live = Color.FromArgb(90, 160, 240);
    private static readonly Color Rim = Color.FromArgb(80, 80, 90);
    private static readonly Color Muted = Color.FromArgb(150, 150, 160);

    private const int Sectors = 32;           // directions the range step wants to see
    private const int StillMs = 1500;         // how long both sticks must rest untouched
    private const int SuccessBeatMs = 700;    // a finished step shows its tick this long before moving on
    private const int MovedNoticeMs = 800;
    private const int SettleMs = 300;         // a released stick that holds still this long has settled
    private const float TouchedRadius = 0.25f; // further than this from the resting point = a thumb on it
    private const float ReleasedRadius = 0.3f;

    private readonly StadiaReader _reader;
    private readonly Calibration _target;
    private readonly Calibration _result = new();

    private readonly object _gate = new();
    private StadiaState _latest = StadiaState.Neutral; // written by the read thread
    private StadiaState _shown = StadiaState.Neutral;  // UI-thread copy for drawing

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _still = new();
    private readonly Stopwatch _sinceMoved = new();
    private readonly Stopwatch _beat = new();

    private Step _step = Step.Intro;
    private readonly HashSet<Step> _done = new();
    private readonly HashSet<Step> _skipped = new();
    private bool _prevA = true; // an A already held when the dialog opens must be released first

    private readonly RestMeter _leftRest = new(), _rightRest = new();
    private readonly RangeMeter _leftRange = new(), _rightRange = new();
    private readonly TriggerMeter _ltMeter = new(), _rtMeter = new();

    private readonly Canvas _stepBar = new() { Dock = DockStyle.Top, Height = 44 };
    private readonly Label _title = new();
    private readonly Label _body = new();
    private readonly Canvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _tune = new();
    private readonly TrackBar _leftDz = new(), _rightDz = new();
    private readonly Label _leftDzLabel = new(), _rightDzLabel = new();
    private readonly Button _primary = new(), _cancel = new(), _redo = new(), _skip = new();

    public CalibrationForm(StadiaReader reader, Calibration target)
    {
        _reader = reader;
        _target = target;
        _result.CopyFrom(target); // skipped steps keep their current values

        Text = "Calibrate";
        BackColor = Color.FromArgb(30, 31, 36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(640, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _stepBar.BackColor = Color.FromArgb(24, 25, 29);
        _stepBar.Paint += DrawStepBar;

        _title.Dock = DockStyle.Top;
        _title.Height = 36;
        _title.Padding = new Padding(16, 10, 16, 0);
        _title.Font = new Font("Segoe UI Semibold", 12.5f);

        _body.Dock = DockStyle.Top;
        _body.Height = 50;
        _body.Padding = new Padding(16, 4, 16, 0);
        _body.ForeColor = Color.Silver;

        _canvas.BackColor = Color.FromArgb(24, 25, 29);
        _canvas.Paint += DrawCanvas;

        BuildTuning();
        var buttons = BuildButtons();

        Controls.Add(_canvas);  // fill
        Controls.Add(_tune);    // bottom, above the buttons
        Controls.Add(buttons);  // bottom
        Controls.Add(_body);    // top, below the title
        Controls.Add(_title);
        Controls.Add(_stepBar); // top

        _reader.StateChanged += OnState;
        _timer.Tick += OnTick;
        _timer.Start();

        FormClosing += (_, _) =>
        {
            _reader.StateChanged -= OnState;
            _timer.Stop();
        };

        GoTo(Step.Intro);
    }

    private void BuildTuning()
    {
        _tune.Dock = DockStyle.Bottom;
        _tune.Height = 64;
        _tune.ColumnCount = 2;
        _tune.RowCount = 2;
        _tune.Padding = new Padding(16, 6, 16, 0);
        _tune.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _tune.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _tune.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        _tune.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        foreach (var (bar, label, column) in new[] { (_leftDz, _leftDzLabel, 0), (_rightDz, _rightDzLabel, 1) })
        {
            bar.Minimum = 0;
            bar.Maximum = 35;
            bar.TickFrequency = 5;
            bar.LargeChange = 5;
            bar.Dock = DockStyle.Fill;
            bar.BackColor = BackColor;
            bar.ValueChanged += (_, _) => ApplyDeadzones();
            label.Dock = DockStyle.Fill;
            label.ForeColor = Color.Silver;
            _tune.Controls.Add(label, column, 0);
            _tune.Controls.Add(bar, column, 1);
        }
    }

    private Panel BuildButtons()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(12, 11, 12, 11) };
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };

        Style(_primary, Color.FromArgb(60, 120, 90), 120);
        Style(_cancel, Color.FromArgb(45, 46, 52), 90);
        Style(_redo, Color.FromArgb(45, 46, 52), 110);
        Style(_skip, Color.FromArgb(45, 46, 52), 90);
        _primary.ForeColor = Color.White;
        _cancel.Text = "Cancel";
        _skip.Text = "Skip step";

        _primary.Click += (_, _) => { if (_step == Step.Review) Save(); else Advance(); };
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _redo.Click += (_, _) => { if (_step == Step.Review) StartOver(); else GoTo(_step); };
        _skip.Click += (_, _) => { _skipped.Add(_step); Advance(); };
        AcceptButton = _primary;
        CancelButton = _cancel;

        left.Controls.Add(_redo);
        left.Controls.Add(_skip);
        right.Controls.Add(_cancel);
        right.Controls.Add(_primary);
        bar.Controls.Add(left);
        bar.Controls.Add(right);
        return bar;
    }

    private static void Style(Button b, Color back, int width)
    {
        b.Size = new Size(width, 32);
        b.Margin = new Padding(4, 0, 4, 0);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
        b.BackColor = back;
        b.ForeColor = Color.Gainsboro;
    }

    // ---- flow ----

    private void GoTo(Step step)
    {
        _step = step;
        _beat.Reset();
        _done.Remove(step);

        switch (step)
        {
            case Step.Center:
                _leftRest.Reset();
                _rightRest.Reset();
                _still.Restart();
                _sinceMoved.Reset();
                break;
            case Step.Range:
                _leftRange.Reset();
                _rightRange.Reset();
                break;
            case Step.Triggers:
                _ltMeter.Reset();
                _rtMeter.Reset();
                break;
            case Step.Review:
                // Read both first: moving one slider re-applies both values.
                int left = Percent(_result.LeftDeadzone), right = Percent(_result.RightDeadzone);
                _leftDz.Value = left;
                _rightDz.Value = right;
                ApplyDeadzones();
                break;
        }

        (_title.Text, _body.Text) = step switch
        {
            Step.Intro => ("Calibrate your controller",
                "Three short steps that finish on their own: let go of the sticks, roll them around, squeeze the "
                + "triggers. You'll see the result before anything is saved."),
            Step.Center => ("Let go of both sticks",
                "Put the controller down, or hold it without touching the sticks. This finds where each stick rests "
                + "and how much it drifts."),
            Step.Range => ("Roll each stick around the edge",
                "Push a stick all the way out and roll it slowly in a full circle until its ring is green, then let "
                + "go. One at a time or both together."),
            Step.Triggers => ("Squeeze the triggers",
                "Pull each trigger all the way in, then let it go."),
            _ => ("Check and save",
                "This is what games will see. Untouched, both sticks should read 0%; if one creeps, raise its dead "
                + "zone. Pushed all the way, they should reach 100%."),
        };

        bool measuring = step is Step.Center or Step.Range or Step.Triggers;
        _primary.Visible = !measuring;
        _primary.Text = step == Step.Review ? "Save  (A)" : "Begin  (A)";
        _redo.Visible = step != Step.Intro;
        _redo.Text = step == Step.Review ? "Start over" : "Restart step";
        _skip.Visible = measuring;
        _tune.Visible = step == Step.Review;
        _stepBar.Invalidate();
        _canvas.Invalidate();
    }

    private void Advance() => GoTo(Order[Array.IndexOf(Order, _step) + 1]);

    // A step has what it needs: show its tick briefly, then OnTick moves on.
    private void Complete()
    {
        _done.Add(_step);
        _skipped.Remove(_step);
        _beat.Restart();
        _stepBar.Invalidate();
    }

    private void StartOver()
    {
        _result.CopyFrom(_target);
        _done.Clear();
        _skipped.Clear();
        GoTo(Step.Center);
    }

    private void Save()
    {
        _target.CopyFrom(_result);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ApplyDeadzones()
    {
        _result.LeftDeadzone = _leftDz.Value / 100f;
        _result.RightDeadzone = _rightDz.Value / 100f;
        _leftDzLabel.Text = $"Left stick dead zone: {_leftDz.Value}%";
        _rightDzLabel.Text = $"Right stick dead zone: {_rightDz.Value}%";
    }

    private static int Percent(float v) => Math.Clamp((int)MathF.Round(v * 100), 0, 35);

    // ---- measuring ----

    private void OnState(StadiaState s)
    {
        lock (_gate) _latest = s;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        StadiaState s;
        lock (_gate) s = _latest;
        _shown = s;

        // A on the controller presses the green button (Begin / Save).
        if (s.A && !_prevA && ActiveForm == this && _primary.Visible && _primary.Enabled) _primary.PerformClick();
        _prevA = s.A;

        if (_beat.IsRunning)
        {
            if (_beat.ElapsedMilliseconds >= SuccessBeatMs) Advance();
        }
        else if (!_reader.IsConnected)
        {
            _still.Restart(); // don't count time with no controller as "resting"
        }
        else
        {
            switch (_step)
            {
                case Step.Center: MeasureCenter(s); break;
                case Step.Range: MeasureRange(s); break;
                case Step.Triggers: MeasureTriggers(s); break;
            }
        }

        if (_step == Step.Intro) _primary.Enabled = _reader.IsConnected;
        _canvas.Invalidate();
    }

    private void MeasureCenter(StadiaState s)
    {
        bool leftStill = _leftRest.Add(s.LeftX, s.LeftY);
        bool rightStill = _rightRest.Add(s.RightX, s.RightY);
        if (!leftStill || !rightStill)
        {
            _still.Restart();
            _sinceMoved.Restart();
        }
        if (_still.ElapsedMilliseconds < StillMs) return;

        _result.LeftCenterX = (int)MathF.Round(_leftRest.CenterX);
        _result.LeftCenterY = (int)MathF.Round(_leftRest.CenterY);
        _result.RightCenterX = (int)MathF.Round(_rightRest.CenterX);
        _result.RightCenterY = (int)MathF.Round(_rightRest.CenterY);
        _result.LeftDeadzone = DeadzoneFor(_leftRest.Jitter);
        _result.RightDeadzone = DeadzoneFor(_rightRest.Jitter);
        Complete();
    }

    private static float DeadzoneFor(float jitter) => Math.Clamp(jitter * 1.6f + 0.02f, 0.03f, 0.35f);

    private void MeasureRange(StadiaState s)
    {
        _leftRange.Add(s.LeftX, s.LeftY, _result.LeftCenterX, _result.LeftCenterY);
        _rightRange.Add(s.RightX, s.RightY, _result.RightCenterX, _result.RightCenterY);
        if (!_leftRange.Done || !_rightRange.Done) return;

        // Full tilt at the weakest direction, so every direction can reach 100%.
        _result.LeftMax = MathF.Min(_leftRange.Edge * 0.98f, 1f);
        _result.RightMax = MathF.Min(_rightRange.Edge * 0.98f, 1f);

        // Widen the dead zone to cover where each stick stopped after being let go.
        _result.LeftDeadzone = MathF.Max(_result.LeftDeadzone, SettleDeadzone(_leftRange.Settle));
        _result.RightDeadzone = MathF.Max(_result.RightDeadzone, SettleDeadzone(_rightRange.Settle));
        Complete();
    }

    // Capped lower than the rest-based value: a thumb left resting on the stick shouldn't
    // produce a huge dead zone (and the review sliders can always change it).
    private static float SettleDeadzone(float settle) => Math.Clamp(settle * 1.25f + 0.02f, 0.03f, 0.25f);

    private void MeasureTriggers(StadiaState s)
    {
        _ltMeter.Add(s.LeftTrigger);
        _rtMeter.Add(s.RightTrigger);
        if (!_ltMeter.Done || !_rtMeter.Done) return;

        // A little margin both ends so rest reads 0 and a full squeeze reads 100% despite noise.
        _result.LeftTriggerMin = _ltMeter.Min + 3;
        _result.LeftTriggerMax = _ltMeter.Max - 3;
        _result.RightTriggerMin = _rtMeter.Min + 3;
        _result.RightTriggerMax = _rtMeter.Max - 3;
        Complete();
    }

    private static float Radius(float dx, float dy) => MathF.Sqrt(dx * dx + dy * dy);

    private static int SectorOf(float dx, float dy)
    {
        float a = MathF.Atan2(dy, dx) + MathF.PI; // 0..2π, screen orientation (y down)
        return Math.Min(Sectors - 1, (int)(a / (2 * MathF.PI) * Sectors));
    }

    private static float SectorAngle(int i) => -MathF.PI + (i + 0.5f) * 2 * MathF.PI / Sectors;

    /// <summary>Where a stick rests and how far it wanders while nobody touches it.</summary>
    private sealed class RestMeter
    {
        private long _sumX, _sumY, _n;

        public float CenterX => _n == 0 ? 128 : _sumX / (float)_n;
        public float CenterY => _n == 0 ? 128 : _sumY / (float)_n;
        public float Jitter { get; private set; }

        public void Reset()
        {
            _sumX = _sumY = _n = 0;
            Jitter = 0;
        }

        /// <summary>Adds a sample; false if the stick was clearly moved, which starts the window over.</summary>
        public bool Add(byte x, byte y)
        {
            bool still = true;
            if (_n > 0 && Radius((x - CenterX) / 127f, (y - CenterY) / 127f) > TouchedRadius)
            {
                Reset();
                still = false;
            }
            _sumX += x; _sumY += y; _n++;
            Jitter = MathF.Max(Jitter, Radius((x - CenterX) / 127f, (y - CenterY) / 127f));
            return still;
        }
    }

    /// <summary>
    /// How far a stick reaches in each of <see cref="Sectors"/> directions, and how far
    /// from the middle it comes to rest after being let go.
    /// </summary>
    private sealed class RangeMeter
    {
        public readonly float[] Reach = new float[Sectors];
        public bool Done { get; private set; }

        /// <summary>
        /// The furthest from centre the stick settled after a push. A stick that doesn't
        /// spring all the way back is what shows up as drift in games, and sitting untouched
        /// (the centre step) can't see it.
        /// </summary>
        public float Settle { get; private set; }

        private readonly Stopwatch _settling = new();
        private float _settleX, _settleY;
        private bool _pushed;

        public void Reset()
        {
            Array.Clear(Reach);
            Done = false;
            Settle = 0;
            _pushed = false;
            _settling.Reset();
        }

        public void Add(byte rawX, byte rawY, int cx, int cy)
        {
            if (Done) return;
            float dx = (rawX - cx) / 127f, dy = (rawY - cy) / 127f;
            float r = Radius(dx, dy);
            if (r >= 0.5f)
            {
                int i = SectorOf(dx, dy);
                Reach[i] = MathF.Max(Reach[i], r);
                _pushed = true;
            }

            // After each push, wait for the stick to stop moving near the middle and note where.
            if (!_pushed || r >= ReleasedRadius)
            {
                _settling.Reset();
                return;
            }
            if (!_settling.IsRunning || Radius(dx - _settleX, dy - _settleY) > 0.02f)
            {
                (_settleX, _settleY) = (dx, dy);
                _settling.Restart();
                return;
            }
            if (_settling.ElapsedMilliseconds < SettleMs) return;

            Settle = MathF.Max(Settle, r);
            _pushed = false;
            _settling.Reset();
            if (AllCovered) Done = true;
        }

        // A direction counts once it got close to the stick's usual reach (and never below 0.65),
        // so a half-hearted push doesn't pass but a square-ish gate still does.
        public float Needed
        {
            get
            {
                var seen = Reach.Where(v => v > 0).OrderBy(v => v).ToArray();
                return seen.Length == 0 ? 0.65f : MathF.Max(0.65f, seen[seen.Length / 2] * 0.8f);
            }
        }

        public int Covered
        {
            get
            {
                float needed = Needed;
                return Reach.Count(v => v >= needed);
            }
        }

        public bool AllCovered => Covered == Sectors;
        public float Edge => Reach.Min();
    }

    /// <summary>A trigger's resting and fully-squeezed raw values.</summary>
    private sealed class TriggerMeter
    {
        public int Min = 255, Max;
        public bool Done { get; private set; }

        // Travelled at least half the raw range: a real squeeze, not a brush.
        public bool Squeezed => Max - Min >= 128;

        public void Reset()
        {
            Min = 255;
            Max = 0;
            Done = false;
        }

        public void Add(byte v)
        {
            if (Done) return;
            Min = Math.Min(Min, v);
            Max = Math.Max(Max, v);
            if (Squeezed && v <= Min + (Max - Min) / 10) Done = true;
        }
    }

    // ---- drawing ----

    private void DrawStepBar(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float x = 16;
        for (int i = 0; i < Order.Length; i++)
        {
            var step = Order[i];
            bool current = step == _step, done = _done.Contains(step), skipped = _skipped.Contains(step);
            string text = done ? $"✓  {StepNames[i]}" : skipped ? $"{StepNames[i]} (skipped)" : $"{i + 1}  {StepNames[i]}";

            var size = TextRenderer.MeasureText(g, text, Font);
            var pill = new RectangleF(x, 10, size.Width + 18, 24);
            using (var path = AppIcon.RoundedRect(pill, 12))
            using (var fill = new SolidBrush(current ? Color.FromArgb(55, 90, 130) : Color.FromArgb(40, 41, 47)))
            {
                g.FillPath(fill, path);
            }
            Color fore = current ? Color.White : done ? Good : Muted;
            TextRenderer.DrawText(g, text, Font, Rectangle.Round(pill), fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x = pill.Right + 8;
        }
    }

    private void DrawCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = _canvas.Width, h = _canvas.Height;
        var s = _shown;

        if (_step == Step.Triggers)
        {
            DrawTriggerMeter(g, new RectangleF(w * 0.35f - 22, 20, 44, h - 100), "LT", _ltMeter, s.LeftTrigger);
            DrawTriggerMeter(g, new RectangleF(w * 0.65f - 22, 20, 44, h - 100), "RT", _rtMeter, s.RightTrigger);
        }
        else
        {
            float r = MathF.Min(w / 6f, (h - 90) / 2f);
            var lc = new PointF(w * 0.3f, 20 + r);
            var rc = new PointF(w * 0.7f, 20 + r);
            switch (_step)
            {
                case Step.Center:
                    DrawRestStick(g, lc, r, "Left stick", s.LeftX, s.LeftY);
                    DrawRestStick(g, rc, r, "Right stick", s.RightX, s.RightY);
                    break;
                case Step.Range:
                    DrawRangeStick(g, lc, r, "Left stick", _leftRange, s.LeftX, s.LeftY, _result.LeftCenterX, _result.LeftCenterY);
                    DrawRangeStick(g, rc, r, "Right stick", _rightRange, s.RightX, s.RightY, _result.RightCenterX, _result.RightCenterY);
                    break;
                default: // Intro shows the current calibration, Review the new one
                    DrawOutputStick(g, lc, r, "Left", s.LeftX, s.LeftY,
                        _result.LeftCenterX, _result.LeftCenterY, _result.LeftDeadzone, _result.LeftMax);
                    DrawOutputStick(g, rc, r, "Right", s.RightX, s.RightY,
                        _result.RightCenterX, _result.RightCenterY, _result.RightDeadzone, _result.RightMax);
                    DrawOutputTrigger(g, new RectangleF(lc.X - r - 46, lc.Y - r, 12, 2 * r), "LT",
                        XboxOutput.ScaleTrigger(s.LeftTrigger, _result.LeftTriggerMin, _result.LeftTriggerMax));
                    DrawOutputTrigger(g, new RectangleF(rc.X + r + 34, rc.Y - r, 12, 2 * r), "RT",
                        XboxOutput.ScaleTrigger(s.RightTrigger, _result.RightTriggerMin, _result.RightTriggerMax));
                    break;
            }
        }

        var (status, color) = StatusLine();
        TextRenderer.DrawText(g, status, Font, new Rectangle(0, h - 30, w, 24), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private (string, Color) StatusLine()
    {
        if (!_reader.IsConnected)
            return ("Controller not connected. Plug it in or wake it to continue.", Warn);

        bool finished = _beat.IsRunning;
        bool moved = _sinceMoved.IsRunning && _sinceMoved.ElapsedMilliseconds < MovedNoticeMs;
        return _step switch
        {
            Step.Intro => ("This is what games get right now, with your current calibration.", Muted),
            Step.Center when finished => ("✓  Resting positions measured", Good),
            Step.Center when moved => ("A stick moved, starting over…", Warn),
            Step.Center => ("Measuring… keep your thumbs off both sticks", Muted),
            Step.Range when finished => ("✓  Full range measured", Good),
            Step.Range when (_leftRange.AllCovered && !_leftRange.Done) || (_rightRange.AllCovered && !_rightRange.Done)
                => ("That ring is complete. Let go of the stick and leave it a moment.", Good),
            Step.Range => ("Roll slowly along the outer edge", Muted),
            Step.Triggers when finished => ("✓  Trigger range measured", Good),
            Step.Triggers => ("Squeeze each trigger all the way in, then let go", Muted),
            Step.Review when _skipped.Count > 0 => ("Skipped steps keep their previous values.", Muted),
            _ => ("", Muted),
        };
    }

    private void DrawRestStick(Graphics g, PointF c, float r, string name, byte rawX, byte rawY)
    {
        DrawFrame(g, c, r);

        float progress = _beat.IsRunning ? 1f : Math.Min(1f, _still.ElapsedMilliseconds / (float)StillMs);
        if (progress > 0.01f)
        {
            using var arc = new Pen(Good, 4f);
            float rr = r + 6;
            g.DrawArc(arc, c.X - rr, c.Y - rr, rr * 2, rr * 2, -90, 360 * progress);
        }

        bool moved = _sinceMoved.IsRunning && _sinceMoved.ElapsedMilliseconds < MovedNoticeMs;
        DrawDot(g, c, r, (rawX - 128) / 127f, (rawY - 128) / 127f, moved ? Warn : Live);
        DrawCaption(g, c, r + 6, _beat.IsRunning ? "✓  " + name : name, _beat.IsRunning ? Good : ForeColor);
    }

    private void DrawRangeStick(Graphics g, PointF c, float r, string name, RangeMeter m, byte rawX, byte rawY, int cx, int cy)
    {
        DrawFrame(g, c, r);
        static float Disp(float v) => MathF.Min(v * 0.92f, 1f); // a full-reach stick lands just inside the rim

        // The shape traced so far.
        if (m.Reach.Any(v => v > 0))
        {
            var pts = new PointF[Sectors];
            for (int i = 0; i < Sectors; i++)
            {
                float a = SectorAngle(i), d = Disp(m.Reach[i]) * r;
                pts[i] = new PointF(c.X + MathF.Cos(a) * d, c.Y + MathF.Sin(a) * d);
            }
            using var fill = new SolidBrush(Color.FromArgb(45, Live));
            using var edge = new Pen(Color.FromArgb(150, Live), 1.5f);
            g.FillPolygon(fill, pts);
            g.DrawPolygon(edge, pts);
        }

        // One rim segment per direction, lit once that direction has been reached.
        float needed = m.Needed, rr = r + 6, sweep = 360f / Sectors;
        using (var lit = new Pen(Good, 5f))
        using (var unlit = new Pen(Rim, 3f))
        {
            for (int i = 0; i < Sectors; i++)
            {
                bool hit = m.Done || m.Reach[i] >= needed;
                g.DrawArc(hit ? lit : unlit, c.X - rr, c.Y - rr, rr * 2, rr * 2, -180f + i * sweep + 1f, sweep - 2f);
            }
        }

        float nx = (rawX - cx) / 127f, ny = (rawY - cy) / 127f;
        float mag = Radius(nx, ny);
        float k = mag > 1e-4f ? Disp(mag) / mag : 0f;
        DrawDot(g, c, r, nx * k, ny * k, m.Done ? Muted : Live);

        string caption = m.Done ? "✓  " + name : $"{name}  {m.Covered * 100 / Sectors}%";
        DrawCaption(g, c, r + 6, caption, m.Done ? Good : ForeColor);
    }

    private void DrawTriggerMeter(Graphics g, RectangleF bar, string name, TriggerMeter m, byte raw)
    {
        float Y(int v) => bar.Bottom - bar.Height * v / 255f;

        using (var back = new SolidBrush(Color.FromArgb(36, 37, 43))) g.FillRectangle(back, bar);
        if (m.Max > m.Min)
        {
            // The range captured so far.
            using var band = new SolidBrush(Color.FromArgb(55, Good));
            g.FillRectangle(band, bar.X, Y(m.Max), bar.Width, Y(m.Min) - Y(m.Max));
        }
        using (var fill = new SolidBrush(m.Done ? Good : Live))
            g.FillRectangle(fill, bar.X + 8, Y(raw), bar.Width - 16, bar.Bottom - Y(raw));
        using (var edge = new Pen(m.Done ? Good : Rim, m.Done ? 2f : 1.5f))
            g.DrawRectangle(edge, bar.X, bar.Y, bar.Width, bar.Height);

        string caption = m.Done ? "✓  " + name : m.Squeezed ? name + "  ·  let go" : name + "  ·  squeeze";
        TextRenderer.DrawText(g, caption, Font, new Rectangle((int)bar.X - 70, (int)bar.Bottom + 8, (int)bar.Width + 140, 22),
            m.Done ? Good : ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // What games get with the calibration in _result. The circle's edge is full tilt and
    // the amber ring the dead zone; the dot greys out while inside it (no output).
    private void DrawOutputStick(Graphics g, PointF c, float r, string name, byte rawX, byte rawY,
        int cx, int cy, float deadzone, float max)
    {
        DrawFrame(g, c, r);

        float upper = MathF.Max(max, deadzone + 0.1f); // same rule as XboxOutput.ApplyStick
        var (ox, oy) = XboxOutput.ApplyStick(rawX, rawY, cx, cy, deadzone, max);
        float output = MathF.Min(1f, Radius(ox, oy) / short.MaxValue);
        DrawDot(g, c, r, (rawX - cx) / 127f / upper, (rawY - cy) / 127f / upper, output > 0 ? Live : Muted);

        // Dead zone ring on top of the dot, so a small one stays visible.
        float dz = deadzone / upper * r;
        if (dz > 1f)
        {
            using var ring = new Pen(Color.FromArgb(220, Warn), 1.5f);
            g.DrawEllipse(ring, c.X - dz, c.Y - dz, dz * 2, dz * 2);
        }

        int pct = (int)MathF.Round(output * 100);
        DrawCaption(g, c, r, $"{name}  {pct}%", pct >= 100 ? Good : ForeColor);
    }

    private void DrawOutputTrigger(Graphics g, RectangleF bar, string name, byte value)
    {
        using (var back = new SolidBrush(Color.FromArgb(36, 37, 43))) g.FillRectangle(back, bar);
        float fill = bar.Height * value / 255f;
        using (var brush = new SolidBrush(value >= 255 ? Good : Live))
            g.FillRectangle(brush, bar.X, bar.Bottom - fill, bar.Width, fill);

        TextRenderer.DrawText(g, $"{name} {value * 100 / 255}%", Font,
            new Rectangle((int)bar.X - 40, (int)bar.Bottom + 10, (int)bar.Width + 80, 22), Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void DrawFrame(Graphics g, PointF c, float r)
    {
        using var faint = new Pen(Color.FromArgb(50, 50, 58), 1f);
        g.DrawLine(faint, c.X - r, c.Y, c.X + r, c.Y);
        g.DrawLine(faint, c.X, c.Y - r, c.X, c.Y + r);
        using var edge = new Pen(Rim, 1.5f);
        g.DrawEllipse(edge, c.X - r, c.Y - r, r * 2, r * 2);
    }

    // nx/ny are fractions of the circle's radius; anything past the edge is pinned to it.
    private static void DrawDot(Graphics g, PointF c, float r, float nx, float ny, Color color)
    {
        float m = Radius(nx, ny);
        if (m > 1f) { nx /= m; ny /= m; }
        float d = MathF.Max(6f, r * 0.09f);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, c.X + nx * r - d, c.Y + ny * r - d, d * 2, d * 2);
    }

    private void DrawCaption(Graphics g, PointF c, float r, string text, Color color)
    {
        TextRenderer.DrawText(g, text, Font, new Rectangle((int)c.X - 120, (int)(c.Y + r + 10), 240, 22), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // A double-buffered panel so the 60 fps redraw doesn't flicker.
    private sealed class Canvas : Panel
    {
        public Canvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
