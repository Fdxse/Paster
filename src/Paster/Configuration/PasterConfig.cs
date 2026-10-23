namespace Paster.Configuration;

/// <summary>
/// What is stored in config.json. Each section is a list of lines, exactly as they appear in the editors,
/// so the file is easy to read and edit by hand as well.
/// </summary>
public sealed class PasterConfig
{
    /// <summary>Value of <see cref="Mode"/> when the editors are shown.</summary>
    public const string SetupModeName = "setup";

    /// <summary>Value of <see cref="Mode"/> when only the tables and buttons are shown.</summary>
    public const string ActionModeName = "action";

    /// <summary>The mode the app was last in ("setup" or "action"). The app starts in this mode.</summary>
    public string Mode { get; set; } = SetupModeName;

    /// <summary>True if the window should stay above all other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// The setup file last opened or saved (see <see cref="SetupFile"/>), so "Save" knows where to write.
    /// Null when the current setup has never been saved to a file.
    /// </summary>
    public string? CurrentSetupFile { get; set; }

    /// <summary>Table lines, e.g. "DBTable" or "DBTable; schema=stg; prefix=Fact".</summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>Global variable lines, e.g. "schema=wrk".</summary>
    public List<string> Variables { get; set; } = new();

    /// <summary>Joiner lines, e.g. "fulltable={prefix}_{table}{suffix}".</summary>
    public List<string> Joiners { get; set; } = new();

    /// <summary>Button template lines, e.g. "TRUNCATE TABLE [{schema}].[{fulltable}];".</summary>
    public List<string> Buttons { get; set; } = new();

    /// <summary>
    /// The dummy setup written when no config.json exists yet. It doubles as a small tutorial.
    /// </summary>
    public static PasterConfig CreateDefault()
    {
        return new PasterConfig
        {
            Tables = new List<string>
            {
                "// One table per line. Override variables for a single line after a ';'.",
                "DBTable",
                "Customer; schema=stg",
                "SalesOrder; prefix=Fact; suffix=",
            },
            Variables = new List<string>
            {
                "// Global variables: name=value. {table} is built in.",
                "prefix=Dim",
                "suffix=_test",
                "schema=wrk",
                "database=DW",
            },
            Joiners = new List<string>
            {
                "// Joiners build new variables from other variables (and other joiners).",
                "fulltable={prefix}_{table}{suffix}",
                "fullname=[{database}].[{schema}].[{fulltable}]",
            },
            Buttons = new List<string>
            {
                "// One button per line. Transforms: upper, lower, pascal, camel, snake, kebab, title, trim.",
                "Begin work on {table}",
                "TRUNCATE TABLE [{schema}].[{fulltable}];",
                "SELECT TOP 100 * FROM {fullname};",
                "Import {table:upper} with SSIS",
                "{table:snake}",
            },
        };
    }
}
