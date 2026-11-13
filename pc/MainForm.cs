using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using NAudio.CoreAudioApi;

namespace LeanMic;

sealed class MainForm : Form
{
    const int ContentWidth = 360;   // 96-DPI pixels
    const string CableUrl = "https://vb-audio.com/Cable/";

    readonly Settings settings = Settings.Load();
    readonly AudioEngine engine = new();
    readonly StreamServer server = new();
    readonly AdbBridge adb = new(StreamServer.TcpPort);
    readonly MMDeviceEnumerator enumerator = new();
    readonly List<MMDevice?> deviceList = [];
    int selectedDevice;

    // UI
    readonly Panel scroller = new() { Dock = DockStyle.Fill };
    readonly Stack root = new() { Gap = 12, Inner = new Padding(20, 4, 20, 16) };
    readonly MicOrb orb = new();
    readonly TextView title = new() { SizePx = 22, TitleFont = true, Center = true, Wrap = false };
    readonly TextView subtitle = new() { SizePx = 13, Center = true, Wrap = false, Color = p => p.SubText };
    readonly Banner banner = new();
    readonly VolumeSlider volume = new();
    readonly ToggleRow muteRow = new() { Icon = Theme.Glyph.Mute, Title = "Mute", Subtitle = "Silence the mic without disconnecting", OnColor = p => p.Danger };
    readonly ToggleRow denoiseRow = new() { Icon = Theme.Glyph.Sparkle, Title = "Noise suppression", Subtitle = "AI cleans up hiss, fans and typing" };
    readonly PickerRow outputRow = new() { Icon = Theme.Glyph.Speaker, Label = "Sends audio to" };
    readonly Expander helpHeader = new() { Text = "How to connect your phone" };
    readonly Card helpCard = new();
    readonly StepView wifiStep = new() { Icon = Theme.Glyph.Wifi, Title = "Wi-Fi" };
    readonly StepView usbStep = new() { Icon = Theme.Glyph.Usb, Title = "USB" };
    readonly StepView useStep = new() { Icon = Theme.Glyph.Mic, Title = "Use it in your apps", Body = "In Discord, Zoom, OBS or Teams, pick “CABLE Output” as the microphone." };
    readonly TextView footer = new() { SizePx = 11.5f, Center = true, Color = p => p.SubText, Text = "LeanMic 1.1 · Closing this window keeps LeanMic running in the tray" };

    readonly NotifyIcon tray = new() { Text = "LeanMic" };
    readonly ToolStripMenuItem trayMute = new("Mute");
    readonly ToolStripMenuItem trayDenoise = new("Noise suppression");
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 50 };

    readonly Icon iconIdle = Icons.Make(Color.FromArgb(120, 124, 130));
    readonly Icon iconLive = Icons.Make(Color.FromArgb(26, 154, 95));
    readonly Icon iconMuted = Icons.Make(Color.FromArgb(214, 69, 69));

    string? phoneName, phoneVia;   // null when no phone is connected
    bool phoneUsb, cableMissing, exiting, hintShown, collapsedOnce;
    string? bannerKey;
    int tick;
    float level, vadSum;
    int vadCount;

    public MainForm()
    {
        Text = "LeanMic";
        Icon = iconLive;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.P.Back;
        KeyPreview = true;

        foreach (var c in new Control[] { useStep, footer }) c.TabStop = false;
        helpCard.Gap = 0;
        helpCard.Inner = new Padding(4, 6, 4, 6);
        helpCard.Controls.AddRange([wifiStep, usbStep, useStep]);

        var volumeCard = new Card { Inner = new Padding(4, 4, 4, 6) };
        volumeCard.Controls.Add(volume);
        var togglesCard = new Card();
        togglesCard.Controls.AddRange([muteRow, new Divider(), denoiseRow]);
        var outputCard = new Card();
        outputCard.Controls.Add(outputRow);

        var heading = new Stack { Gap = 2 };
        heading.Controls.AddRange([title, subtitle]);

        root.Controls.AddRange([orb, heading, banner, volumeCard, togglesCard, outputCard, helpHeader, helpCard, footer]);
        scroller.Controls.Add(root);
        Controls.Add(scroller);
        Stack.SetShown(banner, false);

        // Initial state from settings.
        volume.Value = Math.Clamp(settings.Volume, 0, 400);
        muteRow.Checked = trayMute.Checked = settings.Muted;
        denoiseRow.Checked = trayDenoise.Checked = settings.NoiseSuppression;
        engine.Volume = volume.Value / 100f;
        engine.Muted = muteRow.Checked;
        engine.NoiseSuppression = denoiseRow.Checked;

        volume.ValueChanged += (_, _) => { engine.Volume = volume.Value / 100f; settings.Volume = volume.Value; };
        volume.Committed += (_, _) => settings.Save();
        muteRow.CheckedChanged += (_, _) => ApplyMute();
        denoiseRow.CheckedChanged += (_, _) =>
        {
            engine.NoiseSuppression = trayDenoise.Checked = denoiseRow.Checked;
            settings.NoiseSuppression = denoiseRow.Checked;
            settings.Save();
            Log.Write("Noise suppression " + (denoiseRow.Checked ? "on" : "off"));
        };
        orb.Click += (_, _) =>
        {
            if (phoneName != null) muteRow.Checked = !muteRow.Checked;
            else helpHeader.Expanded = true;
        };
        outputRow.Click += (_, _) => ShowDeviceMenu();
        helpHeader.ExpandedChanged += (_, _) => { Stack.SetShown(helpCard, helpHeader.Expanded); Relayout(); };
        banner.Click += (_, _) =>
        {
            if (banner.ActionText == null) return;
            try { Process.Start(new ProcessStartInfo(CableUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"Couldn't open your browser ({ex.Message}).\nThe download page is {CableUrl}", "LeanMic"); }
        };

        // Tray.
        trayMute.Click += (_, _) => muteRow.Checked = !muteRow.Checked;
        trayDenoise.Click += (_, _) => denoiseRow.Checked = !denoiseRow.Checked;
        tray.ContextMenuStrip = new ContextMenuStrip { Renderer = new Theme.MenuRenderer() };
        tray.ContextMenuStrip.Items.Add("Open LeanMic", null, (_, _) => ShowWindow());
        tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add(trayMute);
        tray.ContextMenuStrip.Items.Add(trayDenoise);
        tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        tray.Visible = true;

        // Network -> UI.
        server.Audio = engine.Write;
        server.Connected += (name, rate, wifi) => OnUi(() =>
        {
            phoneUsb = wifi == null;
            phoneName = name;
            phoneVia = phoneUsb ? "USB" : "Wi-Fi";
            engine.BeginStream(rate, phoneUsb);
            if (!collapsedOnce && helpHeader.Expanded) { collapsedOnce = true; helpHeader.Expanded = false; }
            UpdateStatus();
        });
        server.Disconnected += () => OnUi(() => { phoneName = null; engine.Buffer?.Clear(); UpdateStatus(); });

        Theme.Changed += OnThemeChanged;
        uiTimer.Tick += (_, _) => OnUiTick();
    }

    int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    void OnUi(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    void OnThemeChanged() => OnUi(() =>
    {
        BackColor = Theme.P.Back;
        Theme.ApplyTitleBar(this);
        Invalidate(true);
    });

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadDevices();
        ApplyDevice();
        try
        {
            server.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            MessageBox.Show($"Couldn't listen on port {StreamServer.TcpPort}: {ex.Message}", "LeanMic");
        }
        adb.Start();
        UpdateHelp();
        helpHeader.Expanded = true;    // first thing a new user needs; folds away once a phone connects
        Stack.SetShown(helpCard, true);
        UpdateStatus();
        UpdateBanner();
        Relayout();
        CenterToScreen();   // StartPosition was applied before the window knew its real size
        Relayout();         // re-clamp to the work area of the screen it ended up on
        uiTimer.Start();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        orb.SetAnimating(Visible);
    }

    /// <summary>
    /// Sizes the window to its content, but never taller than the screen's work area (then it scrolls),
    /// and nudges it up so it never hangs off the bottom of the screen.
    /// </summary>
    void Relayout()
    {
        int w = S(ContentWidth) + S(root.Inner.Horizontal);
        int h = root.HeightFor(w);
        var wa = Screen.FromControl(this).WorkingArea;
        int frameH = Height - ClientSize.Height;
        int maxClientH = wa.Height - frameH;
        bool scroll = h > maxClientH;
        int barW = scroll ? SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) : 0;

        scroller.AutoScroll = scroll;
        root.SetBounds(scroller.AutoScrollPosition.X, scroller.AutoScrollPosition.Y, w, h);
        root.PerformLayout();
        ClientSize = new Size(w + barW, Math.Min(h, maxClientH));

        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
        if (Top < wa.Top) Top = wa.Top;
    }

    // ---- Output device ----

    string? SelectedId => selectedDevice > 0 ? deviceList[selectedDevice]?.ID : null;

    static string DeviceName(MMDevice? d) => d?.FriendlyName ?? "Nothing (audio is paused)";

    /// <summary>(Re)lists output devices keeping the choice; true if the choice changed (device unplugged).</summary>
    bool LoadDevices()
    {
        bool initial = deviceList.Count == 0;
        string? before = initial ? settings.DeviceId : SelectedId;
        deviceList.Clear();
        deviceList.Add(null);
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) deviceList.Add(d);

        int cable = deviceList.FindIndex(d => d != null && d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        cableMissing = cable < 0;
        // On startup with no saved choice, prefer VB-Cable. Never default to speakers
        // (that would play the phone mic into the room).
        int pick = before != null ? deviceList.FindIndex(d => d != null && d.ID == before) : initial ? cable : 0;
        selectedDevice = Math.Max(pick, 0);
        outputRow.Value = DeviceName(deviceList[selectedDevice]);
        return SelectedId != before;
    }

    void ApplyDevice()
    {
        var dev = deviceList[selectedDevice];
        outputRow.Value = DeviceName(dev);
        engine.SetDevice(dev?.ID);
        if (phoneName != null) engine.Buffer?.Reset(phoneUsb);
        settings.DeviceId = dev?.ID;
        settings.Save();
        UpdateBanner();
    }

    void ShowDeviceMenu()
    {
        if (LoadDevices()) ApplyDevice();
        UpdateBanner();
        var menu = new ContextMenuStrip { Renderer = new Theme.MenuRenderer(), Font = Theme.Text(13, this) };
        for (int i = 0; i < deviceList.Count; i++)
        {
            int index = i;
            var d = deviceList[i];
            string label = d == null ? "Nothing (pause audio)" : d.FriendlyName;
            if (d != null && d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) label += "   – recommended";
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => { selectedDevice = index; ApplyDevice(); })
            {
                Checked = i == selectedDevice,
                Padding = new Padding(0, S(3), 0, S(3)),
            });
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(outputRow, new Point(S(8), outputRow.Height - S(4)));
    }

    // ---- State display ----

    void ApplyMute()
    {
        engine.Muted = trayMute.Checked = muteRow.Checked;
        settings.Muted = muteRow.Checked;
        settings.Save();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (phoneName == null)
        {
            orb.State = OrbState.Waiting;
            title.Text = "Waiting for your phone";
            subtitle.Text = "Open LeanMic on your phone and tap Start";
        }
        else if (muteRow.Checked)
        {
            orb.State = OrbState.Muted;
            title.Text = "Muted";
            subtitle.Text = $"{phoneName} · click the mic to unmute";
        }
        else
        {
            orb.State = OrbState.Live;
            title.Text = "You're live";
            UpdateLiveSubtitle();
        }
        tray.Icon = phoneName == null ? iconIdle : muteRow.Checked ? iconMuted : iconLive;
        tray.Text = phoneName == null ? "LeanMic – waiting for phone" : "LeanMic – " + (muteRow.Checked ? "muted" : "live");
    }

    void UpdateLiveSubtitle()
    {
        var jb = engine.Buffer;
        subtitle.Text = jb == null ? $"{phoneName} · {phoneVia}" : $"{phoneName} · {phoneVia} · {jb.TargetMs} ms delay";
    }

    void UpdateBanner()
    {
        string? text = engine.Error ?? engine.NoiseSuppressionError;
        bool isError = text != null;
        string? action = null;
        if (text == null && cableMissing)
        {
            text = "Install VB-Cable so Discord, Zoom and other apps can use LeanMic as a microphone. Then restart your PC.";
            action = "Get VB-Cable";
        }
        string key = $"{text}|{action}";
        if (key == bannerKey) return;
        bannerKey = key;
        banner.Text = text ?? "";
        banner.IsError = isError;
        banner.ActionText = action;
        Stack.SetShown(banner, text != null);
        banner.Invalidate();
        Relayout();
    }

    void UpdateHelp()
    {
        var ips = StreamServer.LocalIPv4();
        string pc = ips.Count > 0 ? $" This PC is {string.Join(", ", ips)}." : "";
        string wifi = $"Same network as this PC. In the phone app pick Wi-Fi and tap Start; it finds the PC by itself.{pc}";
        string usbState = adb.Status.StartsWith("USB: ", StringComparison.Ordinal) ? adb.Status[5..] : adb.Status;
        string usb = $"Turn on USB debugging, plug in, pick USB and tap Start.\nStatus: {usbState}";
        bool changed = wifiStep.Body != wifi || usbStep.Body != usb;
        wifiStep.Body = wifi;
        usbStep.Body = usb;
        if (changed && helpHeader.Expanded) Relayout();
    }

    void OnUiTick()
    {
        float peak = engine.Buffer?.TakePeak() ?? 0f;
        level = peak <= 0.001f ? 0f : Math.Clamp((float)(20 * Math.Log10(peak) + 60) / 60f, 0f, 1f);
        // Glow: ignore the quiet room floor (below -42 dB) and emphasise speech-level peaks.
        float glow = Math.Clamp((level - 0.3f) / 0.7f, 0f, 1f);
        orb.Level = glow * glow;

        float vad = engine.VoiceProbability;
        if (vad >= 0) { vadSum += vad; vadCount++; }

        if (++tick % 10 != 0) return;   // the rest only needs ~2 updates per second
        engine.Heal();
        if (phoneName != null && !muteRow.Checked) UpdateLiveSubtitle();
        UpdateHelp();
        UpdateBanner();

        var jb = engine.Buffer;
        if (phoneName != null && jb != null && tick % 40 == 0)
        {
            string ns = vadCount > 0 ? $" voice {vadSum / vadCount:0.00}" : "";
            vadSum = 0; vadCount = 0;
            Log.Write($"stats: level {level:0.000} buffer {jb.BufferedMs} ms target {jb.TargetMs} ms underruns {jb.Underruns} volume {volume.Value}% muted {muteRow.Checked}{ns}");
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
        Theme.Changed -= OnThemeChanged;
        tray.Visible = false;
        adb.Dispose();
        server.Dispose();
        engine.Dispose();
        settings.Save();
        base.OnFormClosing(e);
    }
}

static class Icons
{
    /// <summary>Microphone glyph on a colored circle (tray and window icon).</summary>
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
