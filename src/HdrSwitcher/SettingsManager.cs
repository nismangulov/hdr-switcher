using System.Text.Json;

namespace HdrSwitcher;

public record ManualGame(string Name, string ExePath);

public class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
    };

    public string ConfigPath { get; }
    public IReadOnlyList<string>     Blacklist   { get; private set; } = [];
    public IReadOnlyList<ManualGame> ManualGames { get; private set; } = [];

    // Default path: next to exe, same directory as the log file
    public SettingsManager() : this(Path.Combine(AppContext.BaseDirectory, "hdr-switcher.json")) { }

    public SettingsManager(string configPath)
    {
        ConfigPath = configPath;
        Load();
    }

    private void Load()
    {
        // Remove any leftover .tmp from a prior crashed Save
        try { File.Delete(ConfigPath + ".tmp"); } catch { }

        if (!File.Exists(ConfigPath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(ConfigPath), JsonOpts);
            if (dto is null) return;
            Blacklist   = dto.Blacklist   ?? [];
            ManualGames = dto.ManualGames ?? [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        { /* corrupt or unreadable config — silently use defaults */ }
    }

    public void Save(IReadOnlyList<string> blacklist, IReadOnlyList<ManualGame> manualGames)
    {
        var dto = new SettingsDto
        {
            Blacklist   = [..blacklist],
            ManualGames = [..manualGames],
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp  = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);

        // Update in-memory state only after successful persist
        Blacklist   = dto.Blacklist!;
        ManualGames = dto.ManualGames!;
    }

    // Internal DTO — separate from the public record so deserialization stays simple
    private sealed class SettingsDto
    {
        public List<string>?     Blacklist   { get; set; }
        public List<ManualGame>? ManualGames { get; set; }
    }
}
