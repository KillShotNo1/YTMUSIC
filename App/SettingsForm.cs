using Microsoft.Win32;
using System.Text;

namespace App;

public class SettingsForm : Form
{
    private readonly CheckBox chkAutorun = new() { Text = "Open at Windows startup" };
    private readonly Button btnSave = new() { Text = "Save", DialogResult = DialogResult.OK };
    private readonly Button btnCancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Button btnUpdate = new() { Text = "Check for updates" };

    // TODO: replace with the real repository URL.
    private const string UpdateUrl = "https://github.com/KillShotNo1/YTMUSIC";

    private static readonly string AppVersion =
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0))
        .ToString(3);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Keeps the dialog's own title bar black instead of the default white one.
    private void DarkTitleBar()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // Win10
            DwmSetWindowAttribute(Handle, 27, ref on, sizeof(int)); // Win11
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar();
    }

    private readonly TextBox txtPlayPause = new() { Width = 160, ReadOnly = true };
    private readonly TextBox txtNext = new() { Width = 160, ReadOnly = true };
    private readonly TextBox txtPrev = new() { Width = 160, ReadOnly = true };
    private readonly TextBox txtMute = new() { Width = 160, ReadOnly = true };

    private readonly HotkeyConfig cfg = HotkeyConfig.Load();

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTMDesktop");

    // Dark palette (same look as the window chrome).
    private static readonly Color DarkBg = Color.FromArgb(18, 18, 18);
    private static readonly Color DarkField = Color.FromArgb(32, 32, 32);
    private static readonly Color DarkFg = Color.White;
    private static readonly Color DarkBorder = Color.FromArgb(70, 70, 70);

    public SettingsForm()
    {
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 278);
        MaximizeBox = false;
        MinimizeBox = false;

        chkAutorun.Location = new Point(12, 12);
        chkAutorun.Checked = IsAutorunEnabled();

        var lblHotkeys = new Label { Text = "Global hotkeys (click a box, then press a combination):", AutoSize = true, Location = new Point(12, 42) };

        var lblPP = new Label { Text = "Play / Pause:", AutoSize = true, Location = new Point(12, 70) };
        txtPlayPause.Location = new Point(130, 66);
        SetCapture(txtPlayPause, (mod, vk) => { cfg.PlayPauseMod = mod; cfg.PlayPauseVk = vk; });

        var lblNext = new Label { Text = "Next song:", AutoSize = true, Location = new Point(12, 98) };
        txtNext.Location = new Point(130, 94);
        SetCapture(txtNext, (mod, vk) => { cfg.NextMod = mod; cfg.NextVk = vk; });

        var lblPrev = new Label { Text = "Previous song:", AutoSize = true, Location = new Point(12, 126) };
        txtPrev.Location = new Point(130, 122);
        SetCapture(txtPrev, (mod, vk) => { cfg.PrevMod = mod; cfg.PrevVk = vk; });

        var lblMute = new Label { Text = "Mute / Unmute:", AutoSize = true, Location = new Point(12, 154) };
        txtMute.Location = new Point(130, 150);
        SetCapture(txtMute, (mod, vk) => { cfg.MuteMod = mod; cfg.MuteVk = vk; });

        var lblVersion = new Label
        {
            Text = "YTMusic " + AppVersion,
            AutoSize = true,
            Location = new Point(12, 186)
        };
        btnUpdate.Location = new Point(232, 182);
        btnUpdate.Click += (_, _) => OpenUpdatePage();

        txtPlayPause.Text = Format(cfg.PlayPauseMod, cfg.PlayPauseVk);
        txtNext.Text = Format(cfg.NextMod, cfg.NextVk);
        txtPrev.Text = Format(cfg.PrevMod, cfg.PrevVk);
        txtMute.Text = Format(cfg.MuteMod, cfg.MuteVk);

        btnSave.Location = new Point(240, 224);
        btnCancel.Location = new Point(328, 224);

        Controls.AddRange(new Control[] {
            chkAutorun, lblHotkeys,
            lblPP, txtPlayPause, lblNext, txtNext, lblPrev, txtPrev, lblMute, txtMute,
            lblVersion, btnUpdate, btnSave, btnCancel });

        AcceptButton = btnSave;
        CancelButton = btnCancel;
        ApplyDarkTheme(this);

        btnSave.Click += (_, _) => Save();
    }

    // Black theme for the dialog and every control on it.
    private void ApplyDarkTheme(Control root)
    {
        root.BackColor = root is Form ? DarkBg : DarkBg;
        root.ForeColor = DarkFg;

        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BackColor = DarkField;
                    tb.ForeColor = DarkFg;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = DarkField;
                    b.ForeColor = DarkFg;
                    b.FlatAppearance.BorderColor = DarkBorder;
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 58, 58);
                    break;
                case CheckBox cb:
                    cb.BackColor = DarkBg;
                    cb.ForeColor = DarkFg;
                    break;
                case Label l:
                    l.BackColor = DarkBg;
                    l.ForeColor = DarkFg;
                    break;
                default:
                    ApplyDarkTheme(c);
                    break;
            }
        }
    }

    private void SetCapture(TextBox tb, Action<uint, uint> setter)
    {
        tb.KeyDown += (s, e) =>
        {
            uint mod = 0;
            if (e.Control) mod |= 0x0002;
            if (e.Alt) mod |= 0x0001;
            if (e.Shift) mod |= 0x0004;
            if (mod == 0) return; // require a modifier
            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey) return;
            setter(mod, (uint)key);
            tb.Text = Format(mod, (uint)key);
            e.SuppressKeyPress = true;
        };
    }

    private static string Format(uint mod, uint vk)
    {
        var sb = new StringBuilder();
        if ((mod & 0x0002) != 0) sb.Append("Ctrl + ");
        if ((mod & 0x0001) != 0) sb.Append("Alt + ");
        if ((mod & 0x0004) != 0) sb.Append("Shift + ");
        sb.Append(((Keys)vk).ToString());
        return sb.ToString();
    }

    private void OpenUpdatePage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateUrl)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void Save()
    {
        try { Directory.CreateDirectory(AppDataFolder); } catch { }
        cfg.Save();
        SetAutorun(chkAutorun.Checked);
        Close();
    }

    private static void SetAutorun(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            if (enabled)
            {
                string exe = Path.Combine(AppContext.BaseDirectory, "YouTube Music.exe");
                key.SetValue("YouTubeMusicDesktop", $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue("YouTubeMusicDesktop", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private static bool IsAutorunEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("YouTubeMusicDesktop") != null;
        }
        catch { return false; }
    }
}
