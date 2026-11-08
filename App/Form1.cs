using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace App;

public partial class Form1 : Form
{
    // hl=en makes YouTube Music itself use English (page text and window title), whatever the Windows language is.
    private const string StartUrl = "https://music.youtube.com/?hl=en";

    // Background throttling is disabled on purpose so playback keeps running while hidden in the tray.
    // Background services (component updater, sync, crash reporter, prediction hints, form-autofill
    // sync, ...) are all off: this is a music player, none of them are needed and each one costs
    // RAM/CPU/network in the background. Playback/navigation are unaffected by these flags.
    private const string BrowserArgs =
        "--enable-gpu-rasterization --enable-zero-copy --ignore-gpu-blocklist " +
        "--disable-background-timer-throttling --disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-background-networking --disable-component-update --disable-sync " +
        "--disable-domain-reliability --disable-breakpad " +
        "--no-first-run --no-default-browser-check " +
        "--disable-features=TranslateUI,Translate,Extensions,PDFViewer,CalculateNativeWinOcclusion," +
        "OptimizationHints,AutofillServerCommunication,InterestFeedContentSuggestions " +
        "--autoplay-policy=no-user-gesture-required " +
        // Gives the WebView2 browser process the same Windows identity, so the media card shows "YouTube Music".
        "--app-user-model-id=" + Program.AppId;

    // Ctrl+P (print) and Ctrl+S (save page) do nothing. The page sees the key first and cancels it.
    private const string BlockPrintSaveScript = """
        (() => {
            if (window.__ytmNoPrint) return;
            window.__ytmNoPrint = true;
            window.addEventListener('keydown', e => {
                const k = (e.key || '').toLowerCase();
                if ((e.ctrlKey || e.metaKey) && !e.altKey && (k === 'p' || k === 's')) {
                    e.preventDefault();
                    e.stopImmediatePropagation();
                }
            }, true);
            window.print = () => {};
        })();
        """;

    // Mutes/unmutes YTMusic's own <video> element (the page's audio only, not the system volume).
    // window.__ytmSetMuted(true|false) is called from ToggleMute(). The re-apply timer only runs
    // while muted (nothing to do otherwise), and catches a new <video> after a song change.
    private const string MuteScript = """
        (() => {
            if (location.hostname !== 'music.youtube.com') return;
            if (window.__ytmMuteHooked) return;
            window.__ytmMuteHooked = true;

            let savedVolume = 1;
            let tick = null;

            const apply = (v, on) => {
                if (!v) return;
                if (on) {
                    if (!v.muted) {
                        if (v.volume > 0) savedVolume = v.volume;
                        v.muted = true;
                        v.volume = 0;
                    }
                } else {
                    v.muted = false;
                    if (v.volume === 0) v.volume = savedVolume > 0 ? savedVolume : 1;
                }
            };

            window.__ytmSetMuted = (on) => {
                apply(document.querySelector('video'), on);
                if (on && tick === null) {
                    tick = setInterval(() => {
                        const v = document.querySelector('video');
                        if (v) apply(v, true); // no-op unless a new (unmuted) video appeared
                    }, 400);
                } else if (!on && tick !== null) {
                    clearInterval(tick);
                    tick = null;
                }
            };
        })();
        """;

    // Reports the current track URL to the app (used to resume it after restart). Posts only when
    // the video id actually changes, so the IPC/JSON cost is once per song instead of every 2 s.
    // Never fires during an ad, so lasttrack.txt can never point at one.
    private const string PresenceScript = """
        (() => {
            if (location.hostname !== 'music.youtube.com') return;
            if (window.__ytmPresence || !window.chrome || !window.chrome.webview) return;
            window.__ytmPresence = true;

            let lastId = '';
            const post = o => { try { window.chrome.webview.postMessage(o); } catch (e) {} };

            setInterval(() => {
                try {
                    const p = document.getElementById('movie_player');
                    if (!p || p.classList.contains('ad-showing')) return;
                    const d = p.getVideoData();
                    const id = d && d.video_id;
                    if (!id || id === lastId) return; // same song: nothing new to report
                    lastId = id;

                    const md = navigator.mediaSession && navigator.mediaSession.metadata;
                    post({
                        playing: true,
                        title: (md && md.title) || (d && d.title) || '',
                        url: 'https://music.youtube.com/watch?v=' + id,
                        id: id
                    });
                } catch (e) {}
            }, 2000);
        })();
        """;

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YTMDesktop");

    private static readonly string UserDataFolder = Path.Combine(AppDataFolder, "UserData");
    private static readonly string WindowSettingsFile = Path.Combine(AppDataFolder, "window.json");
    private static readonly string LastTrackFile = Path.Combine(AppDataFolder, "lasttrack.txt");

    private readonly WebView2 webView = new()
    {
        Dock = DockStyle.Fill,
        Visible = false,
        Margin = Padding.Empty,
        DefaultBackgroundColor = Color.FromArgb(3, 3, 3)
    };

    private NotifyIcon trayIcon = null!;
    private ContextMenuStrip trayMenu = null!;
    private SplashForm? splashScreen;
    private bool allowClose;
    private FormWindowState lastState = FormWindowState.Normal;
    private bool? appliedLightTheme;
    private bool signingIn;
    private bool muted; // YTMusic's own audio only (never the system volume)
    private string savedTrackUrl = ""; // last URL written to lasttrack.txt (skip identical rewrites)
    private bool? memoryLow; // last MemoryUsageTargetLevel pushed to WebView2 (null = not set yet)

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public Form1()
    {
        ShowSplash();
        InitializeComponent();

        string icoPath = Path.Combine(AppContext.BaseDirectory, "ytmusic.ico");
        Icon = File.Exists(icoPath)
            ? new Icon(icoPath)
            : Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        Opacity = 0;
        BuildChrome();
        SetInitialSize();
        InitializeTray();
        FormClosing += Form1_FormClosing;
        Resize += Form1_Resize;
        ResizeEnd += (s, e) => SaveWindowSettings(); // fires after a drag-resize or a window move
        _ = InitializeWebViewAsync();
    }

    // ---------------------------------------------------------------- window

    private void SetInitialSize()
    {
        StartPosition = FormStartPosition.Manual;

        // Restore the size, position and maximized state from the last session.
        WindowSettings? saved = LoadWindowSettings();
        if (saved != null)
        {
            var r = new Rectangle(
                saved.X, saved.Y,
                Math.Max(saved.Width, MinimumSize.Width),
                Math.Max(saved.Height, MinimumSize.Height));

            if (IsVisibleOnAnyScreen(r)) // skip if that monitor is no longer connected
            {
                Size = r.Size;
                Location = r.Location;
                if (saved.Maximized)
                {
                    WindowState = FormWindowState.Maximized;
                    lastState = FormWindowState.Maximized;
                }
                return;
            }
        }

        // First run: a sensible size relative to the work area (taskbar excluded), centered.
        float scale = DeviceDpi / 96f;
        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int w = Math.Min((int)(1200 * scale), (int)(wa.Width * 0.80));
        int h = Math.Min((int)(800 * scale), (int)(wa.Height * 0.85));
        Size = new Size(w, h);
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
    }

    private static WindowSettings? LoadWindowSettings()
    {
        try
        {
            if (File.Exists(WindowSettingsFile))
                return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(WindowSettingsFile));
        }
        catch { }
        return null;
    }

    private void SaveWindowSettings()
    {
        try
        {
            // When maximized or minimized, Bounds is not the "normal" size, so use RestoreBounds.
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            bool maximized = WindowState == FormWindowState.Maximized ||
                             (WindowState == FormWindowState.Minimized && lastState == FormWindowState.Maximized);

            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(WindowSettingsFile, JsonSerializer.Serialize(new WindowSettings
            {
                X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = maximized
            }));
        }
        catch { }
    }

    private static bool IsVisibleOnAnyScreen(Rectangle r)
    {
        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle i = Rectangle.Intersect(s.WorkingArea, r);
            if (i.Width >= 100 && i.Height >= 100) return true;
        }
        return false;
    }

    private void Form1_Resize(object? sender, EventArgs e)
    {
        // Minimize behaves normally (stays on the taskbar). Only remember the last visible state.
        if (WindowState != FormWindowState.Minimized)
        {
            ApplyEdgePadding();
            btnMax.Glyph = WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922";
            btnMax.Invalidate();
            bool changed = WindowState != lastState;
            lastState = WindowState;
            if (changed) SaveWindowSettings(); // maximize / restore
        }
    }

    private void HideToTray()
    {
        Hide();
        trayIcon.Visible = true;
        SetMemoryMode(low: true);
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveWindowSettings();

        // The close (X) button sends the app to the tray. Never block Windows shutdown / app exit.
        if (!allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    public void RestoreFromTray()
    {
        SetMemoryMode(low: false);
        Show();
        WindowState = lastState == FormWindowState.Maximized
            ? FormWindowState.Maximized
            : FormWindowState.Normal;
        Activate();
        BringToFront();
        SetForegroundWindow(Handle);
        trayIcon.Visible = false;
    }

    // Lower the WebView memory target while hidden; audio keeps playing.
    private void SetMemoryMode(bool low)
    {
        try
        {
            CoreWebView2? c = webView.CoreWebView2;
            if (c == null || memoryLow == low) return; // skip the COM round-trip when unchanged
            memoryLow = low;
            c.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
    }

    // ---------------------------------------------------------------- splash

    private void ShowSplash()
    {
        try
        {
            splashScreen = new SplashForm(LoadLogo());
            splashScreen.Show();
            Application.DoEvents();
        }
        catch { splashScreen = null; }
    }

    private static Bitmap? LoadLogo()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "logo.png");
            if (!File.Exists(path)) return null;
            using var tmp = Image.FromFile(path);
            return new Bitmap(tmp); // copy so the file is not locked
        }
        catch { return null; }
    }

    private void CloseSplash()
    {
        splashScreen?.FadeOutAndClose();
        splashScreen = null;
    }

    // ---------------------------------------------------------------- webview

    private async Task InitializeWebViewAsync()
    {
        try
        {
            content.Controls.Add(webView);
            Text = "YouTube Music";
            BackColor = Color.FromArgb(3, 3, 3);

            Directory.CreateDirectory(UserDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder,
                options: new CoreWebView2EnvironmentOptions(additionalBrowserArguments: BrowserArgs, language: "en-US"));
            await webView.EnsureCoreWebView2Async(env);

            // Default zoom 80%
            try { webView.ZoomFactor = 0.8; } catch { }

            CoreWebView2 core = webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // Print and Save are disabled: Ctrl+P / Ctrl+S are swallowed by BlockPrintSaveScript and
            // the right-click menu has no such entries.
            core.ContextMenuRequested += OnContextMenuRequested;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            // The page only needs chrome.webview messaging. Host objects, form autofill and touch
            // pinch-zoom are extra per-page work and pinch would fight the fixed 80% zoom.
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;

            // Runs once per document, before the page's own scripts.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BlockPrintSaveScript);

            // Keeps YTMusic muted while the mute button is on (survives song changes).
            await core.AddScriptToExecuteOnDocumentCreatedAsync(MuteScript);

            // PresenceScript also reports the current track so the app can resume it after restart.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PresenceScript);
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += (s, e) => ApplyMuteState();

            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    core.Navigate(uri.ToString());
                }
            };

            core.HistoryChanged += (s, e) => UpdateNavButtons();
            core.SourceChanged += (s, e) => UpdateNavButtons();

            core.DocumentTitleChanged += (s, e) =>
            {
                Text = string.IsNullOrEmpty(core.DocumentTitle) ? "YouTube Music" : core.DocumentTitle;
            };

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnFirstNavigationCompleted;
            string startUrl = StartUrl;
            try
            {
                if (File.Exists(LastTrackFile))
                {
                    string saved = File.ReadAllText(LastTrackFile).Trim();
                    if (!string.IsNullOrWhiteSpace(saved)) startUrl = saved;
                }
            }
            catch { }
            webView.Source = new Uri(startUrl);
        }
        catch (Exception ex)
        {
            CloseSplash();
            Opacity = 1;
            MessageBox.Show(
                $"WebView2 initialization error: {ex.Message}\n\n" +
                "Make sure the Microsoft Edge WebView2 Runtime is installed.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // After a successful sign-in Google can land on the account page (myaccount.google.com)
    // instead of going back to YouTube Music. Send the user back to the home page, but only
    // right after a sign-in. Everything on accounts.google.com (password, verification,
    // 2-step prompts, account chooser) is left completely alone.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri)) return;

        switch (uri.Host)
        {
            case "accounts.google.com":
                signingIn = true;
                break;

            case "music.youtube.com":
                signingIn = false;
                break;

            case "myaccount.google.com":
                if (signingIn || uri.Query.Contains("sign_in_no_continue"))
                {
                    signingIn = false;
                    e.Cancel = true;
                    BeginInvoke(new Action(() => webView.CoreWebView2.Navigate(StartUrl)));
                }
                break;
        }
    }

    private static readonly HashSet<string> BlockedContextMenuItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "print", "saveAs", "savePageAs", "saveImageAs", "saveLinkAs", "saveVideoAs", "saveAudioAs", "saveFrameAs"
    };

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        for (int i = e.MenuItems.Count - 1; i >= 0; i--)
        {
            if (BlockedContextMenuItems.Contains(e.MenuItems[i].Name))
                e.MenuItems.RemoveAt(i);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement r = doc.RootElement;

            if (!r.TryGetProperty("playing", out JsonElement playing) ||
                playing.ValueKind != JsonValueKind.True)
                return;

            string Str(string name) =>
                r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            string url = Str("url");
            if (!string.IsNullOrEmpty(url) && url != savedTrackUrl)
            {
                savedTrackUrl = url; // remember so 2-second refreshes don't rewrite the file
                try { File.WriteAllText(LastTrackFile, url); } catch { }
            }
        }
        catch { }
    }

    private void OnFirstNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        webView.CoreWebView2.NavigationCompleted -= OnFirstNavigationCompleted;
        CloseSplash();
        Opacity = 1;
        Activate();
        webView.Visible = true;
    }

    // ---------------------------------------------------------------- tray

    private void InitializeTray()
    {
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Play / Pause", null, (s, e) => RunPlayerScript(JsPlayPause));
        trayMenu.Items.Add("Next", null, (s, e) => RunPlayerScript(JsNext));
        trayMenu.Items.Add("Previous", null, (s, e) => RunPlayerScript(JsPrevious));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Show", null, (s, e) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());

        trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "YouTube Music",
            ContextMenuStrip = trayMenu,
            Visible = false
        };
        trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) RestoreFromTray();
        };

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void ExitApp()
    {
        allowClose = true;
        Application.Exit();
    }

    // ---------------------------------------------------------------- media keys

    // Works while the app is in the background or hidden in the tray.
    //   Media Play/Pause, Next, Previous, Stop keys: caught by a low-level keyboard hook (below)
    //   Ctrl+Alt+Space = play/pause, Ctrl+Alt+Right = next, Ctrl+Alt+Left = previous: global hotkeys
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;

    // Hotkey configuration is loaded from %LocalAppData%\YTMDesktop\hotkeys.json (see HotkeyConfig).
    // Defaults: Play/Pause = Alt+Space, Next = Alt+E, Previous = Alt+Q, Mute = Alt+M.

    private const string JsPlayPause =
        "(()=>{const b=document.querySelector('ytmusic-player-bar .play-pause-button');" +
        "if(b){b.click();return;}const v=document.querySelector('video');if(v){v.paused?v.play():v.pause();}})()";

    private const string JsNext =
        "(()=>{const b=document.querySelector('ytmusic-player-bar .next-button');if(b)b.click();})()";

    private const string JsPrevious =
        "(()=>{const b=document.querySelector('ytmusic-player-bar .previous-button');if(b)b.click();})()";

    // Stop = pause (nothing happens when already paused)
    private const string JsStop =
        "(()=>{const v=document.querySelector('video');if(v&&!v.paused){" +
        "const b=document.querySelector('ytmusic-player-bar .play-pause-button');if(b)b.click();else v.pause();}})()";

    private void RegisterHotKeys()
    {
        var cfg = HotkeyConfig.Load();
        var list = new (int Id, uint Mod, uint Vk)[]
        {
            (5, cfg.PlayPauseMod, cfg.PlayPauseVk),
            (6, cfg.NextMod, cfg.NextVk),
            (7, cfg.PrevMod, cfg.PrevVk),
            (8, cfg.MuteMod, cfg.MuteVk),
        };
        // If another app already owns a key, Windows refuses it and we simply skip that one.
        foreach (var (id, mod, vk) in list)
        {
            if (vk == 0) continue;
            try { RegisterHotKey(Handle, id, mod | MOD_NOREPEAT, vk); } catch { }
        }
    }

    private void UnregisterHotKeys()
    {
        foreach (int id in new[] { 5, 6, 7, 8 })
            try { UnregisterHotKey(Handle, id); } catch { }
    }

    // The media keys are caught with a low-level keyboard hook instead of RegisterHotKey. RegisterHotKey fails
    // when another app (a browser that is playing music, for example) already owns the keys; the hook still sees them.
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private IntPtr keyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? keyboardProc; // keep a reference so the delegate is not garbage collected
    private uint heldMediaKey;

    private void InstallMediaKeyHook()
    {
        try
        {
            keyboardProc = MediaKeyHookProc;
            keyboardHook = SetWindowsHookEx(13, keyboardProc, GetModuleHandle(null), 0); // 13 = WH_KEYBOARD_LL
        }
        catch { }
    }

    private void RemoveMediaKeyHook()
    {
        if (keyboardHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(keyboardHook);
        keyboardHook = IntPtr.Zero;
    }

    private IntPtr MediaKeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && webView.CoreWebView2 != null && IsHandleCreated)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            string? js = kb.vkCode switch
            {
                0xB3 => JsPlayPause, // VK_MEDIA_PLAY_PAUSE
                0xB0 => JsNext,      // VK_MEDIA_NEXT_TRACK
                0xB1 => JsPrevious,  // VK_MEDIA_PREV_TRACK
                0xB2 => JsStop,      // VK_MEDIA_STOP
                _ => null
            };

            if (js != null)
            {
                int msg = wParam.ToInt32();
                bool keyDown = msg == 0x0100 || msg == 0x0104; // WM_KEYDOWN / WM_SYSKEYDOWN
                if (keyDown)
                {
                    if (heldMediaKey != kb.vkCode) // ignore auto-repeat while the key is held
                    {
                        heldMediaKey = kb.vkCode;
                        string script = js;
                        BeginInvoke(new Action(() => RunPlayerScript(script)));
                    }
                }
                else
                {
                    heldMediaKey = 0;
                }
                return (IntPtr)1; // swallow it so no other app reacts to the same key press
            }
        }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private void RunPlayerScript(string js)
    {
        try
        {
            if (webView.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch { }
    }

    // ---------------------------------------------------------------- mute (YTMusic's own audio only)

    private void ToggleMute()
    {
        muted = !muted;
        ApplyMuteState();
        UpdateMuteGlyph();
    }

    private void ApplyMuteState()
    {
        // __ytmWantedMuted survives navigation only via this re-apply; the script itself
        // starts/stops its own timer, so just hand over the desired state.
        RunPlayerScript("window.__ytmWantedMuted=" + (muted ? "true" : "false") + ";" +
                        "(window.__ytmSetMuted||function(){})(window.__ytmWantedMuted);");
    }

    private void UpdateMuteGlyph()
    {
        if (btnMute == null) return;
        btnMute.Glyph = muted ? "\uE74F" : "\uE767"; // Mute / Volume
        tips.SetToolTip(btnMute, muted ? "Unmute YTMusic" : "Mute YTMusic");
        btnMute.Invalidate();
    }

    private const int WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024, WM_APPCOMMAND = 0x0319;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_HOTKEY:
                switch (m.WParam.ToInt32())
                {
                    case 5: RunPlayerScript(JsPlayPause); break;
                    case 6: RunPlayerScript(JsNext); break;
                    case 7: RunPlayerScript(JsPrevious); break;
                    case 8: ToggleMute(); break;
                }
                return;

            case WM_NCHITTEST:
                base.WndProc(ref m);
                if ((int)m.Result == 1) // HTCLIENT: decide here (resize edges, drag area)
                    m.Result = (IntPtr)HitTest(m.LParam);
                return;

            case WM_GETMINMAXINFO:
                // Maximize to the work area (not over the taskbar) on whichever monitor the window is on.
                if (ApplyMinMaxInfo(m.HWnd, m.LParam))
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
                break;

            case WM_APPCOMMAND: // mouse back/forward buttons, keyboard browser keys
                int cmd = (int)((m.LParam.ToInt64() >> 16) & 0xFFF);
                if (cmd == 1) { GoBack(); m.Result = (IntPtr)1; return; }
                if (cmd == 2) { GoForward(); m.Result = (IntPtr)1; return; }
                break;
        }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // The window has no native frame; keep the minimize/maximize/system-menu abilities.
            CreateParams cp = base.CreateParams;
            cp.Style |= 0x00020000 | 0x00010000 | 0x00080000; // WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU
            return cp;
        }
    }

    private int HitTest(IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
        Point pt = PointToClient(new Point(x, y));

        if (WindowState == FormWindowState.Normal)
        {
            int edge = S(6);
            bool left = pt.X < edge, right = pt.X >= ClientSize.Width - edge;
            bool top = pt.Y < S(4), bottom = pt.Y >= ClientSize.Height - edge;
            if (top && left) return 13;      // HTTOPLEFT
            if (top && right) return 14;     // HTTOPRIGHT
            if (bottom && left) return 16;   // HTBOTTOMLEFT
            if (bottom && right) return 17;  // HTBOTTOMRIGHT
            if (left) return 10;             // HTLEFT
            if (right) return 11;            // HTRIGHT
            if (top) return 12;              // HTTOP
            if (bottom) return 15;           // HTBOTTOM
        }
        if (pt.Y < titleBar.Height) return 2; // HTCAPTION: drag, double-click to maximize, snap
        return 1;                             // HTCLIENT
    }

    private bool ApplyMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;

            var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = Math.Abs(mi.rcWork.Left - mi.rcMonitor.Left);
            mmi.ptMaxPosition.Y = Math.Abs(mi.rcWork.Top - mi.rcMonitor.Top);
            mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
            mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
            mmi.ptMinTrackSize.X = MinimumSize.Width;
            mmi.ptMinTrackSize.Y = MinimumSize.Height;
            Marshal.StructureToPtr(mmi, lParam, true);
            return true;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- custom title bar
    // Layout: [icon] [back] [forward] [title ........................] [min] [max] [close]

    private const int TitleBarHeight = 34; // logical px at 100% scaling
    private const int EdgePx = 5;          // invisible-looking gutter that provides the resize grip

    private HitTransparentPanel titleBar = null!;
    private HitTransparentPanel content = null!;
    private ChromeButton btnBack = null!, btnHome = null!, btnForward = null!, btnMin = null!, btnMax = null!, btnClose = null!, btnSettings = null!, btnMute = null!;
    private readonly Font titleFont = new("Segoe UI", 9f);
    private readonly ToolTip tips = new();
    private Bitmap? titleLogo;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    private void BuildChrome()
    {
        titleLogo = LoadLogo();

        content = new HitTransparentPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 3, 3) };
        titleBar = new HitTransparentPanel { Dock = DockStyle.Top };
        titleBar.Paint += TitleBar_Paint;
        titleBar.SizeChanged += (s, e) => LayoutTitleBar();

        btnBack = new ChromeButton { Glyph = "\uE72B", Enabled = false };
        btnHome = new ChromeButton { Glyph = "\uE80F" };
        btnForward = new ChromeButton { Glyph = "\uE72A", Enabled = false };
        btnSettings = new ChromeButton { Glyph = "\uE713" };
        btnMute = new ChromeButton { Glyph = "\uE767" }; // Volume / Mute toggle (YTMusic's own audio only)
        btnMin = new ChromeButton { Glyph = "\uE921" };
        btnMax = new ChromeButton { Glyph = "\uE922" };
        btnClose = new ChromeButton { Glyph = "\uE8BB", IsClose = true };

        btnBack.Click += (s, e) => GoBack();
        btnHome.Click += (s, e) => GoHome();
        btnForward.Click += (s, e) => GoForward();
        btnSettings.Click += (s, e) => OpenSettings();
        btnMute.Click += (s, e) => ToggleMute();
        btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
        btnMax.Click += (s, e) =>
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        btnClose.Click += (s, e) => Close(); // goes to the tray, see Form1_FormClosing

        tips.SetToolTip(btnBack, "Back (Alt+Left)");
        tips.SetToolTip(btnHome, "Home");
        tips.SetToolTip(btnForward, "Forward (Alt+Right)");
        tips.SetToolTip(btnSettings, "Settings");
        tips.SetToolTip(btnMute, "Mute YTMusic");

        titleBar.Controls.AddRange(new Control[] { btnBack, btnHome, btnForward, btnSettings, btnMute, btnMin, btnMax, btnClose });

        // Fill control first, Top control second, so the title bar is docked before the content.
        Controls.Add(content);
        Controls.Add(titleBar);

        ApplyEdgePadding();
        LayoutTitleBar();
    }

    private void ApplyEdgePadding()
    {
        // The gutter around the page is what the mouse grabs for resizing; not needed when maximized.
        int e = WindowState == FormWindowState.Maximized ? 0 : S(EdgePx);
        content.Padding = new Padding(e, 0, e, e);
        int h = S(TitleBarHeight);
        if (titleBar.Height != h) titleBar.Height = h;
    }

    private void LayoutTitleBar()
    {
        int h = S(TitleBarHeight), cw = S(46), nw = S(40), w = titleBar.Width;
        btnClose.SetBounds(w - cw, 0, cw, h);
        btnMax.SetBounds(w - 2 * cw, 0, cw, h);
        btnMin.SetBounds(w - 3 * cw, 0, cw, h);
        btnSettings.SetBounds(w - 4 * cw, 0, cw, h);
        btnMute.SetBounds(w - 5 * cw, 0, cw, h);
        btnBack.SetBounds(S(36), 0, nw, h);
        btnHome.SetBounds(S(36) + nw, 0, nw, h);
        btnForward.SetBounds(S(36) + 2 * nw, 0, nw, h);
    }

    private void TitleBar_Paint(object? sender, PaintEventArgs e)
    {
        int h = titleBar.Height;
        int icon = S(16);

        if (titleLogo != null)
        {
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawImage(titleLogo, new Rectangle(S(12), (h - icon) / 2, icon, icon));
        }

        int textLeft = S(36) + 3 * S(40) + S(8);
        int textRight = titleBar.Width - 5 * S(46) - S(8);
        if (textRight > textLeft)
        {
            TextRenderer.DrawText(e.Graphics, Text, titleFont,
                new Rectangle(textLeft, 0, textRight - textLeft, h), titleBar.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        titleBar?.Invalidate(); // the title is drawn by us, not by Windows
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyEdgePadding();
        LayoutTitleBar();
        titleBar.Invalidate();
    }

    private void GoBack()
    {
        try { if (webView.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); } catch { }
    }

    private void GoHome()
    {
        try { webView.CoreWebView2?.Navigate(StartUrl); } catch { }
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm();
        dlg.ShowDialog(this);
        ReloadHotKeys();
    }

    private void ReloadHotKeys()
    {
        UnregisterHotKeys();
        RegisterHotKeys();
    }

    private void GoForward()
    {
        try { if (webView.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); } catch { }
    }

    private void UpdateNavButtons()
    {
        CoreWebView2? c = webView.CoreWebView2;
        btnBack.Enabled = c?.CanGoBack ?? false;
        btnForward.Enabled = c?.CanGoForward ?? false;
    }

    private void ApplyChromeTheme(Color bg, Color fg, Color hover)
    {
        titleBar.BackColor = bg;
        titleBar.ForeColor = fg;
        foreach (ChromeButton b in new[] { btnBack, btnHome, btnForward, btnSettings, btnMute, btnMin, btnMax, btnClose })
        {
            b.BackColor = bg;
            b.Fore = fg;
            b.HoverBack = hover;
            b.Invalidate();
        }
        titleBar.Invalidate();
    }

    // ---------------------------------------------------------------- theme

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCornerPreference();
        RegisterHotKeys();
        InstallMediaKeyHook();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKeys();
        RemoveMediaKeyHook();
        base.OnHandleDestroyed(e);
    }

    // Windows app theme: true = light (white), false = dark (black)
    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") as int?) != 0;
        }
        catch { return true; }
    }

    private void ApplyCornerPreference()
    {
        // Rounded window corners on Windows 11; ignored on older versions.
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { }
    }

    private void ApplyTheme()
    {
        bool light = IsLightTheme();
        if (appliedLightTheme == light) return;
        appliedLightTheme = light;
        Color bg = light ? Color.White : Color.FromArgb(18, 18, 18);
        Color fg = light ? Color.Black : Color.White;
        Color hover = light ? Color.FromArgb(229, 229, 229) : Color.FromArgb(58, 58, 58);
        Color border = light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(70, 70, 70);

        trayMenu.Renderer = new ToolStripProfessionalRenderer(new ThemeColors(bg, hover, border))
        {
            RoundedEdges = false
        };
        trayMenu.BackColor = bg;
        trayMenu.ForeColor = fg;
        foreach (ToolStripItem item in trayMenu.Items)
        {
            item.BackColor = bg;
            item.ForeColor = fg;
        }
        ApplyChromeTheme(bg, fg, hover);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(ApplyTheme));
    }

    private sealed class ThemeColors : ProfessionalColorTable
    {
        private readonly Color bg, hover, border;

        public ThemeColors(Color bg, Color hover, Color border)
        {
            this.bg = bg;
            this.hover = hover;
            this.border = border;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => bg;
        public override Color ImageMarginGradientBegin => bg;
        public override Color ImageMarginGradientMiddle => bg;
        public override Color ImageMarginGradientEnd => bg;
        public override Color MenuBorder => border;
        public override Color MenuItemBorder => hover;
        public override Color MenuItemSelected => hover;
        public override Color MenuItemSelectedGradientBegin => hover;
        public override Color MenuItemSelectedGradientEnd => hover;
        public override Color SeparatorDark => border;
        public override Color SeparatorLight => border;
    }
}

internal sealed class WindowSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}
