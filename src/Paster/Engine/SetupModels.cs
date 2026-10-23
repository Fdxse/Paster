namespace Paster.Engine;

/// <summary>
/// One line from the table list, e.g. "DBTable; schema=stg; prefix=Fact".
/// </summary>
public sealed class TableEntry
{
    /// <summary>1-based line number in the table editor, used in error messages.</summary>
    public int LineNumber { get; init; }

    /// <summary>The table name, i.e. the part before the first ';'.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Per-line variable values. These win over the global variables.</summary>
    public Dictionary<string, string> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Problems found while parsing this line (shown as soft errors).</summary>
    public List<string> Errors { get; } = new();
}

/// <summary>
/// One button template line, e.g. "TRUNCATE TABLE [{schema}].[{fulltable}];".
/// </summary>
public sealed class ButtonTemplate
{
    /// <summary>1-based line number in the button editor, used in error messages.</summary>
    public int LineNumber { get; init; }

    /// <summary>The raw template text with {tokens} in it.</summary>
    public string Template { get; init; } = string.Empty;
}

/// <summary>
/// Everything the user has typed into the four editors, parsed and ready to render.
/// Each section keeps its own error list so the matching editor can be marked red.
/// </summary>
public sealed class ParsedSetup
{
    public List<TableEntry> Tables { get; } = new();
    public List<string> TableErrors { get; } = new();

    /// <summary>Global variables: name -> literal value.</summary>
    public Dictionary<string, string> GlobalVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> VariableErrors { get; } = new();

    /// <summary>Joiners: name -> template that may reference other variables and joiners.</summary>
    public Dictionary<string, string> Joiners { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> JoinerErrors { get; } = new();

    public List<ButtonTemplate> ButtonTemplates { get; } = new();
    public List<string> ButtonErrors { get; } = new();
}

/// <summary>
/// The outcome of rendering one template: the finished text and any problems on the way.
/// </summary>
public sealed class RenderResult
{
    public string Text { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = new();
    public bool HasErrors => Errors.Count > 0;
}
