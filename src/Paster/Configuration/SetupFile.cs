using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Paster.Configuration;

/// <summary>
/// A saved setup: the four editors and nothing else. Stored as its own JSON file so you can
/// keep different setups (SSIS, dbt, ...) and switch between them with Open / Save as.
/// </summary>
public sealed class SetupFile
{
    public List<string> Tables { get; set; } = new();
    public List<string> Variables { get; set; } = new();
    public List<string> Joiners { get; set; } = new();
    public List<string> Buttons { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Default folder for setup files: "setups" next to Paster.exe. Created when first needed.
    /// </summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "setups");

    /// <summary>
    /// Reads a setup file. Throws if it cannot be read or is not valid JSON, so the caller can tell the user.
    /// </summary>
    public static SetupFile Load(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<SetupFile>(json, SerializerOptions)
               ?? throw new InvalidDataException("The file is empty.");
    }

    /// <summary>
    /// Writes this setup to a file (via a temp file, so a crash never leaves half a file).
    /// </summary>
    public void Save(string filePath)
    {
        string? folderPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        string json = JsonSerializer.Serialize(this, SerializerOptions);
        string temporaryFilePath = filePath + ".tmp";
        File.WriteAllText(temporaryFilePath, json);
        File.Move(temporaryFilePath, filePath, overwrite: true);
    }
}
