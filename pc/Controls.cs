using System.ComponentModel;

namespace LeanMic;

// Small custom-drawn control set for LeanMic's window. Everything paints from Theme.P, so a
// light/dark switch is just an Invalidate. Sizes are given in 96-DPI pixels and scaled with S().

/// <summary>Base for custom-drawn controls: double-buffered, transparent, theme-aware.</summary>
abstract class Themed : Control
{
    protected Themed()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Invalidate;
        base.Dispose(disposing);
    }

    protected int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    /// <summary>Height this control wants at the given width.</summary>
    public virtual int HeightFor(int width) => Height;

    protected bool Hovered { get; private set; }
    protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected void DrawFocus(Graphics g, Rectangle r, float radius)
    {
        if (!Focused || !ShowFocusCues) return;
        using var pen = new Pen(Theme.P.Accent, S(2));
        using var path = Theme.Round(r, S(radius));
        g.DrawPath(pen, path);
    }
}

/// <summary>Vertical stack container. Lays children top to bottom at full width.</summary>
class Stack : Panel
{
    public int Gap { get; set; } = 10;
    public Padding Inner { get; set; }

    public Stack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Invalidate;
        base.Dispose(disposing);
    }

    protected int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    public int HeightFor(int width)
    {
        int inner = width - S(Inner.Horizontal), h = S(Inner.Vertical), n = 0;
        foreach (Control c in Controls)
        {
            if (!IsShown(c)) continue;
            h += ChildHeight(c, inner);
            n++;
        }
        return h + Math.Max(0, n - 1) * S(Gap);
    }

    // Visible reports false while the form is hidden, so track the intended state instead.
    static bool IsShown(Control c) => c.Tag as string != "hidden";

    public static void SetShown(Control c, bool shown) { c.Tag = shown ? null : "hidden"; c.Visible = shown; }

    static int ChildHeight(Control c, int width) => c switch
    {
        Themed t => t.HeightFor(width),
        Stack s => s.HeightFor(width),
        _ => c.Height,
    };

    protected override void OnLayout(LayoutEventArgs e)
    {
        int x = S(Inner.Left), y = S(Inner.Top), w = Width - S(Inner.Horizontal);
        foreach (Control c in Controls)
        {
            if (!IsShown(c)) continue;
            int h = ChildHeight(c, w);
            c.SetBounds(x, y, w, h);
            if (c is Stack) c.PerformLayout();
            y += h + S(Gap);
        }
    }
}

/// <summary>Rounded card surface.</summary>
sealed class Card : Stack
{
    public Card() { Gap = 0; Inner = new Padding(6, 4, 6, 4); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, S(10));
        using var fill = new SolidBrush(Theme.P.Card);
        using var pen = new Pen(Theme.P.Border);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }
}

sealed class Divider : Themed
{
    public override int HeightFor(int width) => Math.Max(1, S(1));

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Theme.P.Border);
        e.Graphics.FillRectangle(b, S(12), 0, Width - S(24), Height);
    }
}

/// <summary>Text block. Wraps by default; single-line blocks end with an ellipsis.</summary>
sealed class TextView : Themed
{
    public float SizePx { get; set; } = 13;
    public bool Bold { get; set; }
    public bool TitleFont { get; set; }
    public bool Wrap { get; set; } = true;
    public bool Center { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Palette, Color> Color { get; set; } = p => p.Text;

    public TextView() { SetStyle(ControlStyles.Selectable, false); TabStop = false; AccessibleRole = AccessibleRole.StaticText; }

    Font F => TitleFont ? Theme.Title(SizePx, this) : Theme.Text(SizePx, this, Bold);

    TextFormatFlags Flags => (Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis)
                             | (Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);

    public override int HeightFor(int width) =>
        TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, F, new Size(width, int.MaxValue), Flags | TextFormatFlags.NoPadding).Height;

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e) =>
        Theme.DrawText(e.Graphics, Text, F, Color(Theme.P), ClientRectangle, Flags);
}

/// <summary>Row with an icon, title, optional subtitle and an animated switch. Whole row is clickable.</summary>
sealed class ToggleRow : Themed
{
    readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
    bool isChecked;
    float knob;   // 0 = off, 1 = on (animated)

    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Palette, Color> OnColor { get; set; } = p => p.Accent;

    public event EventHandler? CheckedChanged;

    public ToggleRow()
    {
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        anim.Tick += (_, _) =>
        {
            float target = isChecked ? 1 : 0;
            knob += Math.Sign(target - knob) * 0.16f;
            if (Math.Abs(knob - target) < 0.17f) { knob = target; anim.Stop(); }
            Invalidate();
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            if (IsHandleCreated && Visible) anim.Start(); else knob = value ? 1 : 0;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public override int HeightFor(int width) => S(string.IsNullOrEmpty(Subtitle) ? 48 : 58);

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }

    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        var row = new Rectangle(S(2), S(2), Width - S(4), Height - S(4));
        if (Hovered)
        {
            using var hb = new SolidBrush(p.Hover);
            using var hp = Theme.Round(row, S(8));
            g.FillPath(hb, hp);
        }
        DrawFocus(g, row, 8);

        var onColor = OnColor(p);
        // Icon in a soft circle.
        int ic = S(32), ix = S(12), iy = (Height - ic) / 2;
        using (var cb = new SolidBrush(isChecked ? Theme.Mix(p.Card, onColor, p.Dark ? 0.22f : 0.14f) : p.Hover))
            g.FillEllipse(cb, ix, iy, ic, ic);
        Theme.DrawText(g, Icon, Theme.Icon(15, this), isChecked ? onColor : p.SubText, new Rectangle(ix, iy, ic, ic),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Switch geometry.
        int sw = S(40), sh = S(22), sx = Width - sw - S(14), sy = (Height - sh) / 2;

        // Text.
        int tx = ix + ic + S(12), tw = sx - tx - S(10);
        if (string.IsNullOrEmpty(Subtitle))
        {
            Theme.DrawText(g, Title, Theme.Text(14, this, true), p.Text, new Rectangle(tx, 0, tw, Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        else
        {
            int mid = Height / 2;
            Theme.DrawText(g, Title, Theme.Text(14, this, true), p.Text, new Rectangle(tx, mid - S(19), tw, S(20)),
                TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Theme.DrawText(g, Subtitle, Theme.Text(12, this), p.SubText, new Rectangle(tx, mid + S(1), tw, S(18)),
                TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        // Switch: track + knob.
        using (var track = Theme.Round(new RectangleF(sx, sy, sw, sh), sh / 2f))
        using (var tb = new SolidBrush(Theme.Mix(p.Track, onColor, knob)))
            g.FillPath(tb, track);
        float kd = sh - S(6), kx = sx + S(3) + (sw - S(6) - kd) * knob;
        using var kb = new SolidBrush(knob > 0.5f ? p.OnAccent == Color.White ? Color.White : p.Card : Color.White);
        g.FillEllipse(kb, kx, sy + S(3), kd, kd);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

    sealed class Acc(ToggleRow owner) : ControlAccessibleObject(owner)
    {
        public override string? Name => owner.Title;
        public override string? Description => owner.Subtitle;
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : 0);
        public override string DefaultAction => owner.Checked ? "Turn off" : "Turn on";
        public override void DoDefaultAction() => owner.Checked = !owner.Checked;
    }
}

/// <summary>Volume slider with title, live value, a marker at the default and snap-to-default.</summary>
sealed class VolumeSlider : Themed
{
    int value = 100;
    bool dragging;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 400;
    public int DefaultValue { get; set; } = 100;
    public string Title { get; set; } = "Volume";

    public event EventHandler? ValueChanged;
    /// <summary>Raised when the user finishes a change (mouse up / key up) — a good moment to save.</summary>
    public event EventHandler? Committed;

    public VolumeSlider()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == this.value) return;
            this.value = v;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public override int HeightFor(int width) => S(66);

    Rectangle TrackRect => new(S(16), S(46), Width - S(32), S(4));

    int ValueAt(int x)
    {
        var t = TrackRect;
        float f = Math.Clamp((x - t.X) / (float)t.Width, 0, 1);
        int v = (int)Math.Round(Minimum + f * (Maximum - Minimum));
        if (Math.Abs(v - DefaultValue) <= 4) v = DefaultValue;   // gentle snap to 100%
        return v;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        Capture = true;
        Value = ValueAt(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging && (e.Button & MouseButtons.Left) != 0) Value = ValueAt(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) => EndDrag();

    // Alt+Tab, a popup or a dialog can steal the mouse mid-drag without a MouseUp.
    protected override void OnMouseCaptureChanged(EventArgs e) { EndDrag(); base.OnMouseCaptureChanged(e); }
    protected override void OnLostFocus(EventArgs e) { EndDrag(); base.OnLostFocus(e); }

    void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        Capture = false;
        Invalidate();
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) { Value = DefaultValue; Committed?.Invoke(this, EventArgs.Empty); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += Math.Sign(e.Delta) * 5;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.KeyCode switch
        {
            Keys.Left or Keys.Down => -5,
            Keys.Right or Keys.Up => 5,
            Keys.PageDown => -25,
            Keys.PageUp => 25,
            _ => 0,
        };
        if (step != 0) { Value += step; e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { Value = Minimum; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { Value = Maximum; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e) { Committed?.Invoke(this, EventArgs.Empty); base.OnKeyUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        DrawFocus(g, new Rectangle(S(2), S(2), Width - S(4), Height - S(4)), 8);

        // Header: icon, title, value.
        string icon = value == 0 ? Theme.Glyph.Mute : Theme.Glyph.Volume;
        Theme.DrawText(g, icon, Theme.Icon(15, this), p.SubText, new Rectangle(S(14), S(10), S(20), S(20)),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, Title, Theme.Text(14, this, true), p.Text, new Rectangle(S(40), S(8), Width / 2, S(24)),
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var valueColor = value > 200 ? p.Warn : p.Text;
        Theme.DrawText(g, $"{value}%", Theme.Text(14, this, true), valueColor, new Rectangle(Width / 2, S(8), Width / 2 - S(16), S(24)),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

        // Track, fill, default marker.
        var t = TrackRect;
        float f = (value - Minimum) / (float)(Maximum - Minimum);
        float fd = (DefaultValue - Minimum) / (float)(Maximum - Minimum);
        using (var tp = Theme.Round(t, t.Height / 2f))
        using (var tb = new SolidBrush(p.Track))
            g.FillPath(tb, tp);
        if (f > 0)
        {
            using var fp = Theme.Round(new RectangleF(t.X, t.Y, t.Width * f, t.Height), t.Height / 2f);
            using var fb = new SolidBrush(value > 200 ? p.Warn : p.Accent);
            g.FillPath(fb, fp);
        }
        using (var mb = new SolidBrush(Theme.Mix(p.SubText, p.Card, 0.3f)))
            g.FillEllipse(mb, t.X + t.Width * fd - S(2), t.Y + t.Height + S(5), S(4), S(4));

        // Thumb.
        float cx = t.X + t.Width * f, cy = t.Y + t.Height / 2f, r = S(10);
        using (var sb = new SolidBrush(Color.FromArgb(p.Dark ? 90 : 40, 0, 0, 0)))
            g.FillEllipse(sb, cx - r, cy - r + S(1), r * 2, r * 2);
        using (var ob = new SolidBrush(p.Dark ? Theme.Mix(p.Card, Color.White, 0.12f) : Color.White))
            g.FillEllipse(ob, cx - r, cy - r, r * 2, r * 2);
        float ir = (dragging || Hovered ? S(6) : S(5));
        using (var ib = new SolidBrush(value > 200 ? p.Warn : p.Accent))
            g.FillEllipse(ib, cx - ir, cy - ir, ir * 2, ir * 2);

        // Hint under the track while above 100%.
        if (value > DefaultValue)
            Theme.DrawText(g, value > 200 ? "Boosted – may distort" : "Boosted", Theme.Text(11, this), p.SubText,
                new Rectangle(t.X, t.Bottom + S(4), t.Width, S(14)), TextFormatFlags.Right | TextFormatFlags.SingleLine);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

    sealed class Acc(VolumeSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Name => owner.Title;
        public override string? Value { get => $"{owner.Value}%"; set { if (int.TryParse(value?.TrimEnd('%'), out int v)) owner.Value = v; } }
    }
}

public enum OrbState { Waiting, Live, Muted }

/// <summary>The big round mic. Its glow follows the voice level; it breathes while waiting.</summary>
sealed class MicOrb : Themed
{
    readonly System.Windows.Forms.Timer anim = new() { Interval = 16 };
    readonly long start = Environment.TickCount64;
    OrbState state;
    float shown;        // displayed level 0..1 (smoothed)
    bool pressed;

    public float Level { get; set; }

    public MicOrb()
    {
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        anim.Tick += (_, _) =>
        {
            float target = state == OrbState.Waiting ? 0 : Level;
            shown = target > shown ? shown + (target - shown) * 0.5f : Math.Max(target, shown - 0.025f);
            Invalidate();
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public OrbState State
    {
        get => state;
        set { state = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.NameChange, -1); }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        // Animate only while the window is on screen (no CPU use while in the tray).
        if (Visible && FindForm()?.Visible == true) anim.Start(); else anim.Stop();
        base.OnVisibleChanged(e);
    }

    public void SetAnimating(bool on) { if (on) anim.Start(); else anim.Stop(); }

    public override int HeightFor(int width) => S(158);

    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        float cx = Width / 2f, cy = Height / 2f, r = S(46) * (pressed ? 0.96f : 1f);
        double t = (Environment.TickCount64 - start) / 1000.0;

        Color core = state switch { OrbState.Live => p.Accent, OrbState.Muted => p.Danger, _ => p.Dark ? p.Hover : p.Track };
        if (Hovered && !pressed) core = Theme.Mix(core, p.Dark ? Color.White : Color.Black, 0.07f);

        if (state == OrbState.Waiting)
        {
            // Slow "breathing" rings: looking for a phone.
            for (int i = 0; i < 2; i++)
            {
                double phase = (t / 2.4 + i * 0.5) % 1.0;
                float rr = r + S(6) + (float)phase * S(26);
                int a = (int)(70 * (1 - phase));
                using var pen = new Pen(Color.FromArgb(a, p.SubText), S(2));
                g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            }
        }
        else
        {
            // Voice glow: two soft rings that grow with the level.
            float lvl = shown;
            for (int i = 2; i >= 1; i--)
            {
                float rr = r + S(4) + lvl * S(i == 2 ? 28 : 15);
                int a = (int)((i == 2 ? 45 : 85) * Math.Min(1, 0.25 + lvl) * (state == OrbState.Muted ? 0.45 : 1));
                using var gb = new SolidBrush(Color.FromArgb(a, core));
                g.FillEllipse(gb, cx - rr, cy - rr, rr * 2, rr * 2);
            }
        }

        using (var cb = new SolidBrush(core))
            g.FillEllipse(cb, cx - r, cy - r, r * 2, r * 2);

        var glyphColor = state == OrbState.Waiting ? p.SubText : p.OnAccent == Color.White || state == OrbState.Muted ? Color.White : p.OnAccent;
        string glyph = state == OrbState.Muted ? Theme.Glyph.MicOff : Theme.Glyph.Mic;
        var box = new Rectangle((int)(cx - r), (int)(cy - r), (int)(r * 2), (int)(r * 2));
        Theme.DrawText(g, glyph, Theme.Icon(40, this), glyphColor, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(p.Accent, S(2));
            float fr = r + S(4);
            g.DrawEllipse(pen, cx - fr, cy - fr, fr * 2, fr * 2);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

    sealed class Acc(MicOrb owner) : ControlAccessibleObject(owner)
    {
        public override string? Name => owner.state switch
        {
            OrbState.Live => "Microphone live. Press to mute",
            OrbState.Muted => "Microphone muted. Press to unmute",
            _ => "Waiting for phone. Press for connection help",
        };
        public override void DoDefaultAction() => owner.InvokeOnClick(owner, EventArgs.Empty);
    }
}

/// <summary>A clickable "label: value ▾" row that opens a menu (e.g. output device).</summary>
sealed class PickerRow : Themed
{
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";
    public string Value { get => Text; set => Text = value; }

    public PickerRow() { TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ButtonDropDown; }

    public override int HeightFor(int width) => S(58);

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter or Keys.Down || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.Down) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        var row = new Rectangle(S(2), S(2), Width - S(4), Height - S(4));
        if (Hovered)
        {
            using var hb = new SolidBrush(p.Hover);
            using var hp = Theme.Round(row, S(8));
            g.FillPath(hb, hp);
        }
        DrawFocus(g, row, 8);

        int ic = S(32), ix = S(12), iy = (Height - ic) / 2;
        using (var cb = new SolidBrush(p.Hover)) g.FillEllipse(cb, ix, iy, ic, ic);
        Theme.DrawText(g, Icon, Theme.Icon(15, this), p.SubText, new Rectangle(ix, iy, ic, ic),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int tx = ix + ic + S(12), tw = Width - tx - S(40), mid = Height / 2;
        Theme.DrawText(g, Label, Theme.Text(12, this), p.SubText, new Rectangle(tx, mid - S(19), tw, S(18)),
            TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
        Theme.DrawText(g, Value, Theme.Text(14, this, true), p.Text, new Rectangle(tx, mid - S(1), tw, S(20)),
            TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        Theme.DrawText(g, Theme.Glyph.ChevronDown, Theme.Icon(12, this), p.SubText, new Rectangle(Width - S(36), 0, S(20), Height),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

    sealed class Acc(PickerRow owner) : ControlAccessibleObject(owner)
    {
        public override string? Name => owner.Label;
        public override string? Value { get => owner.Value; set { } }
        public override void DoDefaultAction() => owner.InvokeOnClick(owner, EventArgs.Empty);
    }
}

/// <summary>Colored notice with an icon; optionally clickable (e.g. to open a download page).</summary>
sealed class Banner : Themed
{
    public bool IsError { get; set; }
    string? actionText;

    /// <summary>Optional action (e.g. "Get VB-Cable"). When set, the banner is a focusable button.</summary>
    public string? ActionText
    {
        get => actionText;
        set
        {
            actionText = value;
            TabStop = value != null;
            Cursor = value != null ? Cursors.Hand : Cursors.Default;
            AccessibleRole = value != null ? AccessibleRole.Link : AccessibleRole.StaticText;
        }
    }

    public Banner() { TabStop = false; AccessibleRole = AccessibleRole.StaticText; }

    protected override bool IsInputKey(Keys k) => (ActionText != null && k is Keys.Space or Keys.Enter) || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ActionText != null && e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnClick(EventArgs e) { if (ActionText != null) base.OnClick(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

    sealed class Acc(Banner owner) : ControlAccessibleObject(owner)
    {
        public override string? Name => owner.ActionText ?? owner.Text;
        public override string? Description => owner.ActionText != null ? owner.Text : null;
        public override string DefaultAction => owner.ActionText != null ? "Open" : "";
        public override void DoDefaultAction() { if (owner.ActionText != null) owner.InvokeOnClick(owner, EventArgs.Empty); }
    }

    Rectangle TextRect(int width) => new(S(42), S(12), width - S(56), 10000);

    int MessageHeight(int width) => TextRenderer.MeasureText(Text, Theme.Text(13, this), TextRect(width).Size,
        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;

    // The action (if any) sits on its own line under the message.
    public override int HeightFor(int width) =>
        Math.Max(S(48), MessageHeight(width) + (ActionText != null ? S(26) : 0) + S(24));

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        Color fg = IsError ? p.Danger : p.Warn, bg = IsError ? p.DangerSoft : p.WarnSoft;
        using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), S(10)))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        DrawFocus(g, new Rectangle(S(1), S(1), Width - S(3), Height - S(3)), 10);
        Theme.DrawText(g, Theme.Glyph.Warning, Theme.Icon(16, this), fg, new Rectangle(S(14), S(12), S(18), S(20)),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);

        var tr = TextRect(Width);
        int mh = MessageHeight(Width);
        tr.Height = mh;
        Theme.DrawText(g, Text, Theme.Text(13, this), p.Text, tr, TextFormatFlags.WordBreak);
        if (ActionText != null)
        {
            // Pill-shaped action under the message.
            string label = ActionText + "  ↗";
            var font = Theme.Text(13, this, true);
            int lw = TextRenderer.MeasureText(label, font, Size.Empty, TextFormatFlags.NoPadding).Width + S(20);
            var pill = new Rectangle(tr.X, tr.Y + mh + S(6), lw, S(24));
            using (var pp = Theme.Round(pill, S(12)))
            using (var pb = new SolidBrush(Hovered ? Theme.Mix(bg, fg, 0.28f) : Theme.Mix(bg, fg, 0.16f)))
                g.FillPath(pb, pp);
            Theme.DrawText(g, label, font, fg, pill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>"Title ›" header that folds a section open and closed.</summary>
sealed class Expander : Themed
{
    bool expanded;
    public event EventHandler? ExpandedChanged;

    public Expander() { TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.OutlineButton; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Expanded
    {
        get => expanded;
        set { if (expanded == value) return; expanded = value; Invalidate(); ExpandedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public override int HeightFor(int width) => S(36);

    protected override void OnClick(EventArgs e) { Expanded = !Expanded; base.OnClick(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { Expanded = !Expanded; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        DrawFocus(g, new Rectangle(S(1), S(1), Width - S(2), Height - S(2)), 8);
        var color = Hovered ? p.Text : p.SubText;
        Theme.DrawText(g, expanded ? Theme.Glyph.ChevronDown : Theme.Glyph.ChevronRight, Theme.Icon(11, this), color,
            new Rectangle(S(8), 0, S(16), Height), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, Text, Theme.Text(13, this, true), color, new Rectangle(S(30), 0, Width - S(30), Height),
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

    sealed class Acc(Expander owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Expanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
        public override string DefaultAction => owner.Expanded ? "Collapse" : "Expand";
        public override void DoDefaultAction() => owner.Expanded = !owner.Expanded;
    }
}

/// <summary>Help step: icon bubble, bold title and wrapped body text.</summary>
sealed class StepView : Themed
{
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get => Text; set => Text = value; }

    public StepView() { SetStyle(ControlStyles.Selectable, false); }

    Rectangle BodyRect(int width) => new(S(56), S(32), width - S(68), 10000);

    public override int HeightFor(int width) =>
        S(32) + TextRenderer.MeasureText(Body, Theme.Text(12.5f, this), BodyRect(width).Size, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + S(12);

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        Theme.Smooth(g);
        int ic = S(32), ix = S(12), iy = S(10);
        using (var cb = new SolidBrush(Theme.Mix(p.Card, p.Accent, p.Dark ? 0.2f : 0.12f))) g.FillEllipse(cb, ix, iy, ic, ic);
        Theme.DrawText(g, Icon, Theme.Icon(15, this), p.Accent, new Rectangle(ix, iy, ic, ic),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, Title, Theme.Text(14, this, true), p.Text, new Rectangle(S(56), S(10), Width - S(68), S(20)),
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        var br = BodyRect(Width);
        br.Height = Height - br.Y;
        Theme.DrawText(g, Body, Theme.Text(12.5f, this), p.SubText, br, TextFormatFlags.WordBreak);
    }
}
