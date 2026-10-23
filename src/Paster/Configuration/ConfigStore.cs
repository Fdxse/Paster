using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Paster.Configuration;

/// <summary>
/// Loads and saves config.json, which lives in the same folder as Paster.exe.
/// </summary>
public static class ConfigStore
{
    private const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Keep characters like ' [ ] < > readable in the file instead of ' escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Full path to config.json. AppContext.BaseDirectory points at the EXE's folder,
    /// also when the app is published as a single file.
    /// </summary>
    public static string ConfigFilePath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    /// <summary>
    /// Reads config.json. If it does not exist, a dummy config is created and saved first.
    /// Throws if the file exists but cannot be read or parsed, so the caller can tell the user.
    /// </summary>
    public static PasterConfig LoadOrCreate()
    {
        if (!File.Exists(ConfigFilePath))
        {
            PasterConfig defaultConfig = PasterConfig.CreateDefault();
            Save(defaultConfig);
            return defaultConfig;
        }

        string json = File.ReadAllText(ConfigFilePath);
        PasterConfig? loadedConfig = JsonSerializer.Deserialize<PasterConfig>(json, SerializerOptions);
        return loadedConfig ?? PasterConfig.CreateDefault();
    }

    /// <summary>
    /// Writes the config to disk. Writes to a temp file first so a crash never leaves half a file.
    /// </summary>
    public static void Save(PasterConfig config)
    {
        string json = JsonSerializer.Serialize(config, SerializerOptions);
        string temporaryFilePath = ConfigFilePath + ".tmp";
        File.WriteAllText(temporaryFilePath, json);
        File.Move(temporaryFilePath, ConfigFilePath, overwrite: true);
    }
}
