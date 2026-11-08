// Windows identity of the app (media card, taskbar grouping) shared by the app and the installer.
// This file is compiled into BOTH projects (see the <Compile Include> in App.csproj and
// Installer.csproj) so the AppUserModelId key is written from exactly one place.
//
// The icon is the Microsoft Edge one (WebView2's engine) so it does not clash with our own logo.
internal static class AppIdentity
{
    // AppUserModelId of the process and of the registry key below.
    public const string AppId = "YouTubeMusic";

    // Name shown on the media card (taskbar flyout).
    public const string DisplayName = "YTMusic";

    // Writes HKCU\Software\Classes\AppUserModelId\<AppId>. The app runs it at startup and the
    // installer runs it after extracting the files, so the icon always points at the running copy.
    public static void Register()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + AppId);
            if (key == null) return;
            key.SetValue("DisplayName", DisplayName);

            string? edge = FindEdgeIcon();
            if (edge != null) key.SetValue("IconUri", edge);
        }
        catch { }
    }

    private static string? FindEdgeIcon()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (string root in roots)
        {
            string path = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
