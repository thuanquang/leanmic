using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LeanMic;

sealed record Palette(
    bool Dark, Color Back, Color Card, Color Border, Color Hover, Color Text, Color SubText,
    Color Accent, Color AccentSoft, Color OnAccent, Color Track, Color Danger, Color DangerSoft, Color Warn, Color WarnSoft);

/// <summary>Colors, fonts and drawing helpers. Follows the Windows light/dark app setting live.</summary>
static class Theme
{
    static readonly Palette LightPalette = new(
        Dark: false,
        Back: Hex(0xF3F4F6), Card: Color.White, Border: Hex(0xE2E4E8), Hover: Hex(0xF0F1F3),
        Text: Hex(0x1C1F23), SubText: Hex(0x667080),
        Accent: Hex(0x1A9A5F), AccentSoft: Hex(0xD9F2E6), OnAccent: Color.White, Track: Hex(0xD9DCE1),
        Danger: Hex(0xD64545), DangerSoft: Hex(0xFBE3E3), Warn: Hex(0xB86E00), WarnSoft: Hex(0xFFF1DC));

    static readonly Palette DarkPalette = new(
        Dark: true,
        Back: Hex(0x16181B), Card: Hex(0x212428), Border: Hex(0x2E3238), Hover: Hex(0x2A2E33),
        Text: Hex(0xECEEF1), SubText: Hex(0x9AA1AA),
        Accent: Hex(0x3CC283), AccentSoft: Hex(0x1C3A2C), OnAccent: Hex(0x0B1F15), Track: Hex(0x3A3F46),
        Danger: Hex(0xF07171), DangerSoft: Hex(0x3D2224), Warn: Hex(0xF2A93B), WarnSoft: Hex(0x3A2E1A));

    public static Palette P { get; private set; } = Detect();

    /// <summary>Raised on the UI thread when Windows switches between light and dark.</summary>
    public static event Action? Changed;

    static readonly string TextFamily = Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    static readonly string BoldFamily = Installed("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";
    static readonly string TitleFamily = Installed("Segoe UI Variable Display Semib") ? "Segoe UI Variable Display Semib" : "Segoe UI Semibold";
    static readonly string IconFamily = Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    static readonly Dictionary<(string, float, FontStyle), Font> Fonts = [];

    public static class Glyph
    {
        public const string Mic = "", MicOff = "", Volume = "", Mute = "", Sparkle = "",
            Wifi = "", Usb = "", Info = "", Warning = "", Speaker = "",
            ChevronDown = "", ChevronRight = "", Check = "";
    }

    public static void Watch()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var p = Detect();
            if (p == P) return;
            P = p;
            Changed?.Invoke();
        };
    }

    static Palette Detect()
    {
        // LEANMIC_THEME=dark|light forces a theme (handy for screenshots/testing).
        switch (Environment.GetEnvironmentVariable("LEANMIC_THEME")?.ToLowerInvariant())
        {
            case "dark": return DarkPalette;
            case "light": return LightPalette;
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0 ? DarkPalette : LightPalette;
        }
        catch { return LightPalette; }
    }

    public static Font Text(float px, Control c, bool bold = false) => Get(bold ? BoldFamily : TextFamily, px * c.DeviceDpi / 96f, FontStyle.Regular);
    public static Font Title(float px, Control c) => Get(TitleFamily, px * c.DeviceDpi / 96f, FontStyle.Regular);
    public static Font Icon(float px, Control c) => Get(IconFamily, px * c.DeviceDpi / 96f, FontStyle.Regular);

    static Font Get(string family, float px, FontStyle style)
    {
        var key = (family, MathF.Round(px, 1), style);
        if (!Fonts.TryGetValue(key, out var f)) Fonts[key] = f = new Font(family, key.Item2, style, GraphicsUnit.Pixel);
        return f;
    }

    static bool Installed(string family) => FontFamily.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase));
    static Color Hex(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.NoPadding);

    // Dark title bar on Windows 10 2004+ / 11.
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public static void ApplyTitleBar(Form f)
    {
        int dark = P.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(f.Handle, 20, ref dark, sizeof(int));
    }

    /// <summary>Colors for ContextMenuStrip menus (tray and output picker).</summary>
    public sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? P.Text : P.SubText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var b = new SolidBrush(P.Accent);
            TextRenderer.DrawText(e.Graphics, Glyph.Check, Icon(12, e.ToolStrip!), r, P.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => P.Card;
        public override Color ImageMarginGradientBegin => P.Card;
        public override Color ImageMarginGradientMiddle => P.Card;
        public override Color ImageMarginGradientEnd => P.Card;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => P.Hover;
        public override Color MenuItemSelected => P.Hover;
        public override Color MenuItemSelectedGradientBegin => P.Hover;
        public override Color MenuItemSelectedGradientEnd => P.Hover;
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Card;
        public override Color CheckBackground => P.Card;
        public override Color CheckSelectedBackground => P.Hover;
        public override Color CheckPressedBackground => P.Hover;
    }
}
