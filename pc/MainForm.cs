using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using NAudio.CoreAudioApi;

namespace LeanMic;

sealed class MainForm : Form
{
    const int W = 380;
    static readonly Color Green = Color.FromArgb(46, 125, 91), Red = Color.FromArgb(192, 57, 43);
    const string CableHelp = "VB-Cable isn't installed, so apps can't see LeanMic as a microphone yet. " +
                             "Install it from vb-audio.com/Cable, then pick “CABLE Input” above.";

    readonly Settings settings = Settings.Load();
    readonly AudioEngine engine = new();
    readonly StreamServer server = new();
    readonly AdbBridge adb = new(StreamServer.TcpPort);
    readonly MMDeviceEnumerator enumerator = new();
    readonly List<MMDevice?> deviceList = [];

    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(W, 0) };
    readonly Label wifiLabel = new() { AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = SystemColors.GrayText };
    readonly Label usbLabel = new() { AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = SystemColors.GrayText };
    readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = W };
    readonly LinkLabel warn = new() { AutoSize = true, MaximumSize = new Size(W, 0), Visible = false, LinkColor = Color.DarkOrange, ForeColor = Color.DarkOrange };
    readonly Label volLabel = new() { AutoSize = true };
    readonly TrackBar vol = new() { Minimum = 0, Maximum = 400, TickFrequency = 50, SmallChange = 5, LargeChange = 25, Width = W };
    readonly CheckBox mute = new() { Text = "Mute", AutoSize = true };
    readonly CheckBox denoise = new() { Text = "AI noise suppression (removes hiss, fans, keyboard between words)", AutoSize = true, MaximumSize = new Size(W, 0) };
    readonly ToolStripMenuItem trayDenoise = new("Noise suppression");
    readonly LevelMeter meter = new() { Width = W };
    readonly Label delayLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly NotifyIcon tray = new() { Text = "LeanMic" };
    readonly ToolStripMenuItem trayMute = new("Mute");
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 50 };

    readonly Icon iconIdle = Icons.Make(Color.FromArgb(110, 110, 110));
    readonly Icon iconLive = Icons.Make(Green);
    readonly Icon iconMuted = Icons.Make(Red);

    string? phone;              // connected phone description, null when idle
    bool phoneUsb, cableMissing, exiting, hintShown, loadingDevices;
    string? shownWarning;
    int tick;
    float vadSum;
    int vadCount;

    public MainForm()
    {
        Text = "LeanMic";
        Icon = iconLive;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        status.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        warn.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(W, 0),
            ForeColor = SystemColors.GrayText,
            Text = "In Discord, Zoom, OBS, etc., choose “CABLE Output” as your microphone.",
        };

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        // Fixed-width spacer keeps the window from resizing as status texts change.
        Control Gap(int h, int w = 1) => new Panel { Height = h, Width = w, Margin = Padding.Empty };
        layout.Controls.AddRange([
            Gap(0, W), status, wifiLabel, usbLabel, Gap(10),
            new Label { Text = "Send phone audio to:", AutoSize = true }, devices, warn, Gap(6),
            volLabel, vol, mute, denoise, Gap(4), meter, delayLabel, Gap(8), hint,
        ]);
        Controls.Add(layout);

        vol.Value = Math.Clamp(settings.Volume, 0, 400);
        mute.Checked = trayMute.Checked = settings.Muted;
        denoise.Checked = trayDenoise.Checked = settings.NoiseSuppression;
        engine.Volume = vol.Value / 100f;
        engine.Muted = mute.Checked;
        engine.NoiseSuppression = denoise.Checked;
        UpdateVolumeLabel();

        vol.ValueChanged += (_, _) => { engine.Volume = vol.Value / 100f; settings.Volume = vol.Value; UpdateVolumeLabel(); };
        vol.MouseUp += (_, _) => settings.Save();
        vol.KeyUp += (_, _) => settings.Save();
        mute.CheckedChanged += (_, _) => ApplyMute();
        denoise.CheckedChanged += (_, _) =>
        {
            engine.NoiseSuppression = trayDenoise.Checked = denoise.Checked;
            settings.NoiseSuppression = denoise.Checked;
            settings.Save();
            Log.Write("Noise suppression " + (denoise.Checked ? "on" : "off"));
        };
        devices.SelectedIndexChanged += (_, _) => { if (!loadingDevices) SelectDevice(); };
        devices.DropDown += (_, _) => { if (LoadDevices()) SelectDevice(); };   // picks up devices added/removed since startup

        trayMute.Click += (_, _) => mute.Checked = !mute.Checked;
        trayDenoise.Click += (_, _) => denoise.Checked = !denoise.Checked;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Show LeanMic", null, (_, _) => ShowWindow());
        tray.ContextMenuStrip.Items.Add(trayMute);
        tray.ContextMenuStrip.Items.Add(trayDenoise);
        tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.Visible = true;

        server.Audio = engine.Write;
        server.Connected += (name, rate, wifi) => OnUi(() =>
        {
            phoneUsb = wifi == null;
            phone = phoneUsb ? $"{name} (USB)" : $"{name} (Wi-Fi {wifi})";
            engine.BeginStream(rate, phoneUsb);
            UpdateStatus();
        });
        server.Disconnected += () => OnUi(() => { phone = null; engine.Buffer?.Clear(); UpdateStatus(); });

        uiTimer.Tick += (_, _) => OnUiTick();
    }

    /// <summary>Runs an action on the UI thread; silently skipped once the window is gone.</summary>
    void OnUi(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadDevices();
        SelectDevice();
        try
        {
            server.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            MessageBox.Show($"Couldn't listen on port {StreamServer.TcpPort}: {ex.Message}", "LeanMic");
        }
        adb.Start();
        var ips = StreamServer.LocalIPv4();
        wifiLabel.Text = ips.Count > 0 ? "Wi-Fi: this PC is " + string.Join(", ", ips) : "Wi-Fi: this PC isn't on a network";
        UpdateStatus();
        uiTimer.Start();
    }

    string? SelectedId => devices.SelectedIndex > 0 ? deviceList[devices.SelectedIndex]?.ID : null;

    /// <summary>
    /// (Re)lists output devices, keeping the current choice. Doesn't touch the audio engine;
    /// returns true when the selection changed (e.g. the chosen device was unplugged).
    /// </summary>
    bool LoadDevices()
    {
        bool initial = devices.Items.Count == 0;
        string? before = initial ? settings.DeviceId : SelectedId;
        loadingDevices = true;
        try
        {
            deviceList.Clear();
            devices.Items.Clear();
            deviceList.Add(null);
            devices.Items.Add("(nothing)");
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                deviceList.Add(d);
                devices.Items.Add(d.FriendlyName);
            }

            int cable = deviceList.FindIndex(d => d != null && d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            cableMissing = cable < 0;
            // On startup with no saved choice, prefer VB-Cable. Never default to speakers
            // (that would play the phone mic into the room).
            int pick = before != null ? deviceList.FindIndex(d => d != null && d.ID == before)
                     : initial ? cable : 0;
            devices.SelectedIndex = Math.Max(pick, 0);
        }
        finally { loadingDevices = false; }
        return SelectedId != before;
    }

    void SelectDevice()
    {
        var dev = deviceList[Math.Max(devices.SelectedIndex, 0)];
        engine.SetDevice(dev?.ID);
        if (phone != null) engine.Buffer?.Reset(phoneUsb);
        settings.DeviceId = dev?.ID;
        settings.Save();
    }

    void ApplyMute()
    {
        engine.Muted = trayMute.Checked = mute.Checked;
        settings.Muted = mute.Checked;
        settings.Save();
        UpdateStatus();
    }

    void UpdateVolumeLabel() => volLabel.Text = $"Volume: {vol.Value}%";

    void UpdateStatus()
    {
        status.Text = phone == null ? "Waiting for phone…" : (mute.Checked ? "Muted – " : "Live – ") + phone;
        status.ForeColor = phone == null ? SystemColors.ControlText : mute.Checked ? Red : Green;
        tray.Icon = phone == null ? iconIdle : mute.Checked ? iconMuted : iconLive;
        tray.Text = phone == null ? "LeanMic – waiting for phone" : "LeanMic – " + (mute.Checked ? "muted" : "live");
    }

    void OnUiTick()
    {
        meter.Push(engine.Buffer?.TakePeak() ?? 0f);
        if (++tick % 10 != 0) return;   // the rest only needs ~2 updates per second

        usbLabel.Text = adb.Status;
        engine.Heal();

        var jb = engine.Buffer;
        delayLabel.Text = phone != null && jb != null
            ? $"Network buffer: {jb.TargetMs} ms (adjusts automatically)"
            : "";

        // Output errors take priority over the VB-Cable hint.
        string? w = engine.Error ?? engine.NoiseSuppressionError ?? (cableMissing ? CableHelp : null);
        if (w != shownWarning)
        {
            shownWarning = w;
            warn.Visible = w != null;
            warn.Text = w ?? "";
            int at = w == null ? -1 : w.IndexOf("vb-audio.com/Cable", StringComparison.Ordinal);
            warn.LinkArea = at >= 0 ? new LinkArea(at, 18) : new LinkArea(0, 0);
        }

        float vad = engine.VoiceProbability;
        if (vad >= 0) { vadSum += vad; vadCount++; }
        if (phone != null && jb != null && tick % 40 == 0)
        {
            string ns = vadCount > 0 ? $" voice {vadSum / vadCount:0.00}" : "";
            vadSum = 0; vadCount = 0;
            Log.Write($"stats: meter {meter.Level:0.000} buffer {jb.BufferedMs} ms target {jb.TargetMs} ms underruns {jb.Underruns} volume {vol.Value}% muted {mute.Checked}{ns}");
        }
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!hintShown)
            {
                hintShown = true;
                tray.ShowBalloonTip(3000, "LeanMic is still running", "It keeps streaming in the background. Right-click the tray icon to exit.", ToolTipIcon.Info);
            }
            return;
        }
        uiTimer.Stop();
        tray.Visible = false;
        adb.Dispose();
        server.Dispose();
        engine.Dispose();
        settings.Save();
        base.OnFormClosing(e);
    }
}

/// <summary>Horizontal level meter on a -60..0 dB scale with a falling peak.</summary>
sealed class LevelMeter : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Level { get; private set; }   // 0..1 display position

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Height = 14;
    }

    public void Push(float peak)
    {
        float pos = peak <= 0.001f ? 0f : Math.Clamp((float)(20 * Math.Log10(peak) + 60) / 60f, 0f, 1f);
        float next = Math.Max(pos, Level - 0.03f);
        if (next < 0) next = 0;
        if (next == Level) return;     // skip repaint when idle
        Level = next;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(225, 225, 225));
        int w = (int)(Width * Level);
        if (w <= 0) return;
        var color = Level > 0.95f ? Color.FromArgb(192, 57, 43) : Level > 0.8f ? Color.FromArgb(230, 160, 30) : Color.FromArgb(46, 160, 100);
        using var b = new SolidBrush(color);
        g.FillRectangle(b, 0, 0, w, Height);
    }
}

static class Icons
{
    /// <summary>Microphone glyph (Segoe Fluent/MDL2 icon font) on a colored circle.</summary>
    public static Icon Make(Color bg)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(bg);
            g.FillEllipse(brush, 0, 0, 31, 31);
            string family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            using var font = new Font(family, 15f, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("", font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
