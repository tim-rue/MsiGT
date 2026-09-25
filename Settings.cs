using System.Text.Json;

namespace MsiGT;

/// <summary>User preferences for freeing the discrete GPU, stored in %APPDATA%\MsiGT\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>
    /// Programs (file names like "firefox.exe") to start again after they were closed. The default covers
    /// PowerToys, a common tray app whose modules stop working if they aren't started again.
    /// </summary>
    public List<string> Restart { get; set; } = ["PowerToys.exe", "PowerToys.PowerLauncher.exe"];

    /// <summary>Programs that are never closed, even if they keep the GPU awake.</summary>
    public List<string> NeverClose { get; set; } = [];

    /// <summary>Whether Explorer and shell hosts such as Start and Search may be restarted.</summary>
    public bool CloseWindowsComponents { get; set; } = true;

    /// <summary>Whether services and non-critical system processes may be ended (their services are started again).</summary>
    public bool CloseSystemProcesses { get; set; } = true;

    /// <summary>How long an app gets to exit on its own before it is ended.</summary>
    public int CloseTimeoutSeconds { get; set; } = 5;

    public bool ShouldRestart(string fileName) => Restart.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public bool IsNeverClose(string fileName) => NeverClose.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MsiGT", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Loads the settings; a missing file gives the defaults, a damaged one throws.</summary>
    public static Settings Load()
    {
        if (!File.Exists(FilePath))
            return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FilePath} could not be read: {ex.Message}", ex);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Settings Clone() => new()
    {
        Restart = [.. Restart],
        NeverClose = [.. NeverClose],
        CloseWindowsComponents = CloseWindowsComponents,
        CloseSystemProcesses = CloseSystemProcesses,
        CloseTimeoutSeconds = CloseTimeoutSeconds,
    };
}
