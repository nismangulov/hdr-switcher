using System.Drawing;
using System.Text.Json;

namespace HdrSwitcher;

public record ManualGame(string Name, string ExePath);
public record WindowBoundsDto(int X, int Y, int Width, int Height);

public class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
    };

    public string ConfigPath { get; }
    public IReadOnlyList<string>     Blacklist    { get; private set; } = [];
    public IReadOnlyList<ManualGame> ManualGames  { get; private set; } = [];
    public WindowBoundsDto?          WindowBounds { get; private set; }

    public SettingsManager() : this(Path.Combine(AppContext.BaseDirectory, "hdr-switcher.json")) { }

    public SettingsManager(string configPath)
    {
        ConfigPath = configPath;
        Load();
    }

    private void Load()
    {
        try { File.Delete(ConfigPath + ".tmp"); } catch { }

        if (!File.Exists(ConfigPath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(ConfigPath), JsonOpts);
            if (dto is null) return;
            Blacklist    = dto.Blacklist   ?? [];
            ManualGames  = dto.ManualGames ?? [];
            WindowBounds = dto.WindowBounds;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { /* corrupt or unreadable config — silently use defaults */ }
    }

    public void Save(IReadOnlyList<string> blacklist, IReadOnlyList<ManualGame> manualGames)
    {
        var dto = new SettingsDto
        {
            Blacklist    = [..blacklist],
            ManualGames  = [..manualGames],
            WindowBounds = WindowBounds,   // preserve existing window state
        };
        WriteDto(dto);
        Blacklist    = dto.Blacklist!;
        ManualGames  = dto.ManualGames!;
        // WindowBounds unchanged — not managed by Save(); use SaveWindowBounds() for that
    }

    public void SaveWindowBounds(Rectangle bounds)
    {
        var dto = new SettingsDto
        {
            Blacklist    = [..Blacklist],
            ManualGames  = [..ManualGames],
            WindowBounds = new WindowBoundsDto(bounds.X, bounds.Y, bounds.Width, bounds.Height),
        };
        WriteDto(dto);
        WindowBounds = dto.WindowBounds;
    }

    private void WriteDto(SettingsDto dto)
    {
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp  = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private sealed class SettingsDto
    {
        public List<string>?     Blacklist    { get; set; }
        public List<ManualGame>? ManualGames  { get; set; }
        public WindowBoundsDto?  WindowBounds { get; set; }
    }
}
