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

    /// <summary>
    /// The layers this table goes through, from "layers=stg,wrk" on the line. Null means every layer.
    /// </summary>
    public List<string>? LayerNames { get; set; }

    /// <summary>Problems found while parsing this line (shown as soft errors).</summary>
    public List<string> Errors { get; } = new();
}

/// <summary>
/// One line from the layer list, e.g. "dwh; schema=dwh; prefix=Dim". Every table is worked through
/// each layer (stg, wrk, dwh...), and the layer's variables fill in what differs between them.
/// </summary>
public sealed class LayerEntry
{
    /// <summary>1-based line number in the layer editor, used in error messages.</summary>
    public int LineNumber { get; init; }

    /// <summary>The layer name, i.e. the part before the first ';'. Available in templates as {layer}.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Variable values for this layer. A table line override still wins over these.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One thing to work through: a table, in a layer when layers are used.
/// Shown as "DBTable · wrk" in the list on the left, with its own set of buttons.
/// </summary>
public sealed class WorkStep
{
    public TableEntry Table { get; init; } = new();

    /// <summary>The layer, or null when the setup has no layers (then a step is just a table).</summary>
    public LayerEntry? Layer { get; init; }

    /// <summary>"DBTable · wrk", or just "DBTable" without layers.</summary>
    public string DisplayName
    {
        get
        {
            string tableName = Table.Name.Length > 0 ? Table.Name : $"(line {Table.LineNumber})";
            return Layer is null ? tableName : $"{tableName} · {Layer.Name}";
        }
    }

    /// <summary>Identifies the step when remembering which buttons were copied. Same table and layer = same key.</summary>
    public string Key => Layer is null ? Table.Name : Table.Name + "\u001F" + Layer.Name;
}

/// <summary>
/// One button template line, e.g. "TRUNCATE TABLE [{schema}].[{fulltable}];".
/// </summary>
public sealed class ButtonTemplate
{
    /// <summary>1-based line number in the button editor, used in error messages.</summary>
    public int LineNumber { get; init; }

    /// <summary>The raw template text with {tokens} in it. Can span several lines (a "&lt;&lt;&lt; ... &gt;&gt;&gt;" block).</summary>
    public string Template { get; init; } = string.Empty;

    /// <summary>
    /// Optional caption shown on the button instead of the text, e.g. "Truncate and load" for a block.
    /// May contain {tokens} too. Null means the button shows the rendered text itself.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The layers this button shows up in, from "@stg,wrk" at the start of the line. Null means every layer.
    /// </summary>
    public List<string>? LayerNames { get; init; }

    /// <summary>
    /// True if this button belongs in the given layer (always true without layers or without a tag).
    /// </summary>
    public bool AppliesTo(LayerEntry? layer)
    {
        return LayerNames is null || layer is null || LayerNames.Contains(layer.Name, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Everything the user has typed into the five editors, parsed and ready to render.
/// Each section keeps its own error list so the matching editor can be marked red.
/// </summary>
public sealed class ParsedSetup
{
    public List<TableEntry> Tables { get; } = new();
    public List<string> TableErrors { get; } = new();

    /// <summary>Layers (stg, wrk, dwh...). Empty means the setup does not use layers.</summary>
    public List<LayerEntry> Layers { get; } = new();
    public List<string> LayerErrors { get; } = new();

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

    /// <summary>True if the template uses {counter} (directly or through a joiner), so its text changes with every copy.</summary>
    public bool UsesCounter { get; init; }
}
