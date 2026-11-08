using System.Runtime.InteropServices;

namespace App;

/// <summary>
/// Panel that lets mouse hit-testing "fall through" to the form underneath. The form then decides
/// what the area is (window caption for dragging, resize edge, ...).
/// </summary>
internal sealed class HitTransparentPanel : Panel
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    public HitTransparentPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>Flat title-bar button drawn with a Segoe MDL2 Assets glyph (back, forward, minimize, ...).</summary>
internal sealed class ChromeButton : Control
{
    private static readonly Font GlyphFont = new("Segoe MDL2 Assets", 10f);
    private bool hover;

    public string Glyph { get; set; } = "";
    public bool IsClose { get; set; }
    public Color Fore { get; set; } = Color.White;
    public Color HoverBack { get; set; } = Color.Gray;

    public ChromeButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // never steal keyboard focus from the web page
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { hover = false; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color back = BackColor;
        Color fore = Fore;

        if (Enabled && hover)
        {
            back = IsClose ? Color.FromArgb(232, 17, 35) : HoverBack;
            if (IsClose) fore = Color.White;
        }
        else if (!Enabled)
        {
            // dimmed: 40% glyph colour, 60% background
            fore = Color.FromArgb(
                (Fore.R * 4 + back.R * 6) / 10,
                (Fore.G * 4 + back.G * 6) / 10,
                (Fore.B * 4 + back.B * 6) / 10);
        }

        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, ClientRectangle);

        TextRenderer.DrawText(e.Graphics, Glyph, GlyphFont, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
