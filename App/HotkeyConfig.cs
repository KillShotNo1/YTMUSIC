using System.Text.Json;

namespace App;

internal sealed class HotkeyConfig
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTMDesktop");
    private static readonly string FilePath = Path.Combine(AppDataFolder, "hotkeys.json");

    public uint PlayPauseMod { get; set; } = 0x0001;           // Alt
    public uint PlayPauseVk { get; set; } = 0x20;              // Space
    public uint NextMod { get; set; } = 0x0001;                // Alt
    public uint NextVk { get; set; } = 0x45;                   // E
    public uint PrevMod { get; set; } = 0x0001;                // Alt
    public uint PrevVk { get; set; } = 0x51;                   // Q
    public uint MuteMod { get; set; } = 0x0001;                // Alt
    public uint MuteVk { get; set; } = 0x4D;                   // M

    public static HotkeyConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<HotkeyConfig>(File.ReadAllText(FilePath));
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new HotkeyConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}