using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Paster.Configuration;

/// <summary>
/// A Paster setup: the five editors, stored as one self-contained JSON file. Each section is a list
/// of lines exactly as they appear in the editors, so the file is easy to read and edit by hand.
/// Copy the file anywhere and open it with File > Open (or drop it on the window).
/// </summary>
public sealed class SetupFile
{
    /// <summary>Table lines, e.g. "DBTable" or "DBTable; schema=stg; prefix=Fact".</summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>Layer lines, e.g. "stg; schema=stg". Empty means the setup does not use layers.</summary>
    public List<string> Layers { get; set; } = new();

    /// <summary>Value of <see cref="WalkOrder"/>: every table in a layer before moving to the next layer.</summary>
    public const string ByLayerWalkOrder = "layer";

    /// <summary>Value of <see cref="WalkOrder"/>: one table through every layer before moving to the next table.</summary>
    public const string ByTableWalkOrder = "table";

    /// <summary>How the steps are listed when layers are used: "layer" or "table".</summary>
    public string WalkOrder { get; set; } = ByLayerWalkOrder;

    /// <summary>Global variable lines, e.g. "schema=wrk".</summary>
    public List<string> Variables { get; set; } = new();

    /// <summary>Joiner lines, e.g. "fulltable={prefix}_{table}{suffix}".</summary>
    public List<string> Joiners { get; set; } = new();

    /// <summary>Button template lines, e.g. "TRUNCATE TABLE [{schema}].[{fulltable}];".</summary>
    public List<string> Buttons { get; set; } = new();

    /// <summary>Value of <see cref="ActionButtonSize"/>: every button gets <see cref="ActionButtonHeight"/>.</summary>
    public const string FixedButtonSize = "fixed";

    /// <summary>Value of <see cref="ActionButtonSize"/>: the buttons share the window height between them.</summary>
    public const string FillButtonSize = "fill";

    /// <summary>
    /// How big the buttons are in Action mode: "fixed" (use <see cref="ActionButtonHeight"/>) or "fill"
    /// (stretch to fill the window). Setup mode always uses normal-sized buttons.
    /// </summary>
    public string ActionButtonSize { get; set; } = FixedButtonSize;

    /// <summary>Button height in pixels in Action mode when the size is "fixed".</summary>
    public double ActionButtonHeight { get; set; } = 60;

    /// <summary>Button text size in Action mode.</summary>
    public double ActionFontSize { get; set; } = 18;

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
    /// The setup opened when there is nothing more recent: config.json next to Paster.exe.
    /// AppContext.BaseDirectory points at the EXE's folder, also for a single-file EXE.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>
    /// Reads a setup file. Throws if it cannot be read or is not valid JSON, so the caller can tell the user.
    /// Unknown properties (like "mode" in old config.json files) are ignored.
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

    /// <summary>
    /// A blank setup for File > New: only a short hint in each editor.
    /// </summary>
    public static SetupFile CreateEmpty()
    {
        return new SetupFile
        {
            Tables = new List<string> { "// One table per line. Override variables for a single line after a ';'." },
            Layers = new List<string> { "// Optional: layers to walk every table through (stg, wrk, dwh...), with variables per layer after a ';'." },
            Variables = new List<string> { "// Global variables: name=value. {table} is built in." },
            Joiners = new List<string> { "// Joiners build new variables from other variables (and other joiners)." },
            Buttons = new List<string>
            {
                "// One button per line. Transforms: upper, lower, pascal, camel, snake, kebab, title, trim.",
                "// Multi-line button: a line with <<< and a label, the lines to copy, then a line with >>>.",
            },
        };
    }

    /// <summary>
    /// The example setup written to config.json the very first time. It doubles as a small tutorial.
    /// </summary>
    public static SetupFile CreateExample()
    {
        return new SetupFile
        {
            Tables = new List<string>
            {
                "// One table per line. Override variables for a single line after a ';'.",
                "// layers=... limits a table to some layers.",
                "DBTable",
                "Customer; suffix=_v2",
                "SalesOrder; layers=stg,wrk",
            },
            Layers = new List<string>
            {
                "// Every table is worked through each layer, in this order. {layer} holds the name.",
                "// Variables after a ';' only apply in that layer.",
                "stg; schema=stg",
                "wrk; schema=wrk",
                "dwh; schema=dwh; prefix=Dim_",
            },
            Variables = new List<string>
            {
                "// Global variables: name=value. {table} is built in.",
                "database=DW",
                "prefix=",
                "suffix=",
            },
            Joiners = new List<string>
            {
                "// Joiners build new variables from other variables (and other joiners).",
                "fulltable={prefix}{table}{suffix}",
                "fullname=[{database}].[{schema}].[{fulltable}]",
            },
            Buttons = new List<string>
            {
                "// One button per line. Transforms: upper, lower, pascal, camel, snake, kebab, title, trim.",
                "// Start a line with @stg (or @stg,wrk) to show it in those layers only.",
                "Begin work on {table} in {layer}",
                "@stg Import {table:upper} with SSIS",
                "TRUNCATE TABLE [{schema}].[{fulltable}];",
                "SELECT TOP 100 * FROM {fullname};",
                "// A multi-line button: <<< label, the lines to copy, then >>>.",
                "@dwh <<< Reload {fulltable} from wrk",
                "TRUNCATE TABLE [{schema}].[{fulltable}];",
                "INSERT INTO [{schema}].[{fulltable}]",
                "SELECT * FROM [wrk].[{table}];",
                ">>>",
            },
        };
    }
}
