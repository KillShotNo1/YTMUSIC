using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace App;

/// <summary>
/// Animated loading screen: bouncing/pulsing logo with a red glow, rotating status
/// messages and bouncing dots. Fades in on show and fades out on close.
/// </summary>
internal sealed class SplashForm : Form
{
    private const float W = 480f, H = 300f;

    private static readonly string[] Messages =
    {
        "Loading...",
        "Connecting to YouTube Music...",
        "Warming up the speakers...",
        "Tuning the volume knobs..."
    };

    private static readonly Color Accent = Color.FromArgb(255, 0, 51);

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Bitmap? logo;
    private readonly Font titleFont = new("Segoe UI Semibold", 20f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font messageFont = new("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    private bool closing;
    private double fadeOutStart;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SplashForm(Bitmap? logo)
    {
        this.logo = logo;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 15, 15);
        float s = DeviceDpi / 96f;
        ClientSize = new Size((int)(W * s), (int)(H * s));
        Opacity = 0;

        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2; // DWMWCP_ROUND: rounded corners on Windows 11
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { }
    }

    /// <summary>Fades the splash out, then closes and disposes it.</summary>
    public void FadeOutAndClose()
    {
        if (closing) return;
        closing = true;
        fadeOutStart = clock.Elapsed.TotalSeconds;
    }

    private void OnTick()
    {
        double t = clock.Elapsed.TotalSeconds;
        if (!closing)
        {
            Opacity = Math.Min(1.0, t / 0.25);
        }
        else
        {
            double k = (t - fadeOutStart) / 0.2;
            Opacity = Math.Max(0.0, 1.0 - k);
            if (k >= 1.0)
            {
                timer.Stop();
                Close();
                Dispose();
                return;
            }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = DeviceDpi / 96f;
        g.ScaleTransform(s, s);

        double t = clock.Elapsed.TotalSeconds;
        float cx = W / 2f, cy = 112f;
        float pulse = (float)(0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 1.6));
        float bounce = (float)Math.Abs(Math.Sin(t * Math.PI / 1.1));

        // Soft red glow behind the logo
        float gr = 118f + 14f * pulse;
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb((int)(50 + 35 * pulse), Accent),
                SurroundColors = new[] { Color.FromArgb(0, Accent) }
            };
            g.FillPath(glow, path);
        }

        // Logo: gentle pulse + bounce
        float size = 96f * (1f + 0.05f * pulse);
        float y = cy - 9f * bounce;
        var rect = new RectangleF(cx - size / 2, y - size / 2, size, size);
        if (logo != null)
        {
            g.DrawImage(logo, rect);
        }
        else
        {
            using var b = new SolidBrush(Accent);
            g.FillEllipse(b, rect);
        }

        // Title
        g.DrawString("YouTube Music", titleFont, Brushes.White, new RectangleF(0, 190, W, 30), centered);

        // Rotating status message with a fade in/out
        const double period = 2.2;
        int idx = (int)(t / period) % Messages.Length;
        double phase = (t % period) / period;
        double a = Math.Clamp(Math.Min(phase / 0.12, (1 - phase) / 0.12), 0, 1);
        using (var mb = new SolidBrush(Color.FromArgb((int)(255 * a), 160, 160, 160)))
            g.DrawString(Messages[idx], messageFont, mb, new RectangleF(0, 226, W, 20), centered);

        // Three bouncing dots
        for (int i = 0; i < 3; i++)
        {
            double wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (t / 1.0 - i * 0.18));
            float r = 3.2f + 1.8f * (float)wave;
            float dx = cx + (i - 1) * 16f;
            float dy = 270f - 4f * (float)wave;
            using var db = new SolidBrush(Color.FromArgb((int)(120 + 135 * wave), Accent));
            g.FillEllipse(db, dx - r, dy - r, r * 2, r * 2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            logo?.Dispose();
            titleFont.Dispose();
            messageFont.Dispose();
            centered.Dispose();
        }
        base.Dispose(disposing);
    }
}
