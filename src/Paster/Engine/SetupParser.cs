using System.Text.RegularExpressions;

namespace Paster.Engine;

/// <summary>
/// Turns the raw text of the four editors (tables, variables, joiners, buttons) into a <see cref="ParsedSetup"/>.
/// Rules shared by all editors:
///   - blank lines are ignored
///   - lines starting with "//" are comments and are ignored
/// </summary>
public static class SetupParser
{
    /// <summary>The built-in variable that always holds the current table name.</summary>
    public const string TableVariableName = "table";

    /// <summary>Allowed variable / joiner names: a letter or _ followed by letters, digits or _.</summary>
    private static readonly Regex VariableNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Parses all four editors and then checks joiners and button templates against every table,
    /// so that unknown variables and joiner loops show up as soft errors.
    /// </summary>
    public static ParsedSetup Parse(string tablesText, string variablesText, string joinersText, string buttonsText)
    {
        var parsedSetup = new ParsedSetup();

        ParseTables(tablesText, parsedSetup);
        ParseVariables(variablesText, parsedSetup);
        ParseJoiners(joinersText, parsedSetup);
        ParseButtons(buttonsText, parsedSetup);
        ValidateByRendering(parsedSetup);

        return parsedSetup;
    }

    /// <summary>
    /// Returns true if the name can be used as a variable / joiner name.
    /// </summary>
    public static bool IsValidVariableName(string name) => VariableNamePattern.IsMatch(name);

    /// <summary>
    /// Splits editor text into (line number, trimmed line) pairs, skipping blank and comment lines.
    /// </summary>
    private static IEnumerable<(int LineNumber, string Line)> MeaningfulLines(string editorText)
    {
        string[] allLines = (editorText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int lineIndex = 0; lineIndex < allLines.Length; lineIndex++)
        {
            string trimmedLine = allLines[lineIndex].Trim();
            if (trimmedLine.Length == 0 || trimmedLine.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (lineIndex + 1, trimmedLine);
        }
    }

    /// <summary>
    /// Table lines look like "DBTable" or "DBTable; schema=stg; prefix=Fact".
    /// </summary>
    private static void ParseTables(string tablesText, ParsedSetup parsedSetup)
    {
        foreach ((int lineNumber, string line) in MeaningfulLines(tablesText))
        {
            string[] lineParts = line.Split(';');
            var tableEntry = new TableEntry
            {
                LineNumber = lineNumber,
                Name = lineParts[0].Trim(),
            };

            if (tableEntry.Name.Length == 0)
            {
                tableEntry.Errors.Add($"Line {lineNumber}: the table name is missing (nothing before the first ';').");
            }

            // Every part after the table name should be "name=value".
            foreach (string overridePart in lineParts.Skip(1))
            {
                string trimmedPart = overridePart.Trim();
                if (trimmedPart.Length == 0)
                {
                    // Allows a trailing ';' without complaining.
                    continue;
                }

                if (!TrySplitNameValue(trimmedPart, out string overrideName, out string overrideValue))
                {
                    tableEntry.Errors.Add($"Line {lineNumber}: \"{trimmedPart}\" should look like name=value.");
                    continue;
                }

                if (!IsValidVariableName(overrideName))
                {
                    tableEntry.Errors.Add($"Line {lineNumber}: \"{overrideName}\" is not a valid variable name.");
                    continue;
                }

                if (tableEntry.Overrides.ContainsKey(overrideName))
                {
                    tableEntry.Errors.Add($"Line {lineNumber}: \"{overrideName}\" is set more than once (the last one wins).");
                }

                tableEntry.Overrides[overrideName] = overrideValue;
            }

            parsedSetup.TableErrors.AddRange(tableEntry.Errors);
            parsedSetup.Tables.Add(tableEntry);
        }
    }

    /// <summary>
    /// Global variable lines look like "schema=wrk".
    /// </summary>
    private static void ParseVariables(string variablesText, ParsedSetup parsedSetup)
    {
        foreach ((int lineNumber, string line) in MeaningfulLines(variablesText))
        {
            if (!TrySplitNameValue(line, out string variableName, out string variableValue))
            {
                parsedSetup.VariableErrors.Add($"Line {lineNumber}: \"{line}\" should look like name=value.");
                continue;
            }

            if (!IsValidVariableName(variableName))
            {
                parsedSetup.VariableErrors.Add($"Line {lineNumber}: \"{variableName}\" is not a valid variable name.");
                continue;
            }

            if (string.Equals(variableName, TableVariableName, StringComparison.OrdinalIgnoreCase))
            {
                parsedSetup.VariableErrors.Add($"Line {lineNumber}: \"{TableVariableName}\" is built in and always holds the table name.");
                continue;
            }

            if (parsedSetup.GlobalVariables.ContainsKey(variableName))
            {
                parsedSetup.VariableErrors.Add($"Line {lineNumber}: \"{variableName}\" is defined more than once (the last one wins).");
            }

            parsedSetup.GlobalVariables[variableName] = variableValue;
        }
    }

    /// <summary>
    /// Joiner lines look like "fulltable={prefix}_{table}{suffix}".
    /// </summary>
    private static void ParseJoiners(string joinersText, ParsedSetup parsedSetup)
    {
        foreach ((int lineNumber, string line) in MeaningfulLines(joinersText))
        {
            if (!TrySplitNameValue(line, out string joinerName, out string joinerTemplate))
            {
                parsedSetup.JoinerErrors.Add($"Line {lineNumber}: \"{line}\" should look like name=template.");
                continue;
            }

            if (!IsValidVariableName(joinerName))
            {
                parsedSetup.JoinerErrors.Add($"Line {lineNumber}: \"{joinerName}\" is not a valid joiner name.");
                continue;
            }

            if (string.Equals(joinerName, TableVariableName, StringComparison.OrdinalIgnoreCase))
            {
                parsedSetup.JoinerErrors.Add($"Line {lineNumber}: \"{TableVariableName}\" is built in and cannot be a joiner.");
                continue;
            }

            if (parsedSetup.GlobalVariables.ContainsKey(joinerName))
            {
                parsedSetup.JoinerErrors.Add($"Line {lineNumber}: \"{joinerName}\" is already a global variable, so this joiner is never used.");
            }

            if (parsedSetup.Joiners.ContainsKey(joinerName))
            {
                parsedSetup.JoinerErrors.Add($"Line {lineNumber}: \"{joinerName}\" is defined more than once (the last one wins).");
            }

            parsedSetup.Joiners[joinerName] = joinerTemplate;
        }
    }

    /// <summary>Starts a multi-line button. Text after it on the same line is the button's label.</summary>
    public const string BlockStartMarker = "<<<";

    /// <summary>Ends a multi-line button.</summary>
    public const string BlockEndMarker = ">>>";

    /// <summary>
    /// Every non-blank, non-comment line is one button (trimmed).
    /// A multi-line button is written as a block, and every line inside it is kept exactly as typed:
    ///   &lt;&lt;&lt; Truncate and load {table}
    ///   TRUNCATE TABLE [{schema}].[{fulltable}];
    ///   INSERT INTO [{schema}].[{fulltable}] SELECT * FROM stg.{table};
    ///   &gt;&gt;&gt;
    /// </summary>
    private static void ParseButtons(string buttonsText, ParsedSetup parsedSetup)
    {
        string[] allLines = (buttonsText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        int lineIndex = 0;

        while (lineIndex < allLines.Length)
        {
            string trimmedLine = allLines[lineIndex].Trim();
            int lineNumber = lineIndex + 1;
            lineIndex++;

            if (trimmedLine.Length == 0 || trimmedLine.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!trimmedLine.StartsWith(BlockStartMarker, StringComparison.Ordinal))
            {
                parsedSetup.ButtonTemplates.Add(new ButtonTemplate { LineNumber = lineNumber, Template = trimmedLine });
                continue;
            }

            // A block: collect lines until ">>>" (or the end of the editor).
            string labelText = trimmedLine[BlockStartMarker.Length..].Trim();
            var blockLines = new List<string>();
            bool blockClosed = false;

            while (lineIndex < allLines.Length)
            {
                string blockLine = allLines[lineIndex];
                lineIndex++;

                if (blockLine.Trim() == BlockEndMarker)
                {
                    blockClosed = true;
                    break;
                }

                blockLines.Add(blockLine.TrimEnd());
            }

            if (!blockClosed)
            {
                parsedSetup.ButtonErrors.Add($"Line {lineNumber}: the {BlockStartMarker} block is never closed with {BlockEndMarker} on a line of its own.");
            }

            // Blank lines at the start and end of a block are not part of the text.
            while (blockLines.Count > 0 && blockLines[0].Trim().Length == 0)
            {
                blockLines.RemoveAt(0);
            }

            while (blockLines.Count > 0 && blockLines[^1].Trim().Length == 0)
            {
                blockLines.RemoveAt(blockLines.Count - 1);
            }

            if (blockLines.Count == 0)
            {
                parsedSetup.ButtonErrors.Add($"Line {lineNumber}: the {BlockStartMarker} block is empty.");
                continue;
            }

            parsedSetup.ButtonTemplates.Add(new ButtonTemplate
            {
                LineNumber = lineNumber,
                Template = string.Join(Environment.NewLine, blockLines),
                Label = labelText.Length > 0 ? labelText : null,
            });
        }
    }

    /// <summary>
    /// Renders every joiner and every button template for every table and collects the errors
    /// into the joiner / button sections. Uses a placeholder table when the table list is empty
    /// so that typos are still caught. An error that only happens for some tables says which ones.
    /// </summary>
    private static void ValidateByRendering(ParsedSetup parsedSetup)
    {
        bool usingPlaceholderTable = parsedSetup.Tables.Count == 0;
        List<TableEntry> tablesToCheck = usingPlaceholderTable
            ? new List<TableEntry> { new() { LineNumber = 0, Name = "Table" } }
            : parsedSetup.Tables;

        // Error message -> names of the tables it happened for. Insertion order is kept by the lists.
        var joinerErrorTables = new Dictionary<string, List<string>>();
        var buttonErrorTables = new Dictionary<string, List<string>>();
        var joinerErrorOrder = new List<string>();
        var buttonErrorOrder = new List<string>();

        foreach (TableEntry tableEntry in tablesToCheck)
        {
            var templateRenderer = new TemplateRenderer(parsedSetup, tableEntry);
            string tableDisplayName = tableEntry.Name.Length > 0 ? tableEntry.Name : $"line {tableEntry.LineNumber}";

            foreach (string joinerName in parsedSetup.Joiners.Keys)
            {
                foreach (string errorMessage in templateRenderer.RenderJoiner(joinerName).Errors)
                {
                    RecordError($"{joinerName}: {errorMessage}", tableDisplayName, joinerErrorTables, joinerErrorOrder);
                }
            }

            foreach (ButtonTemplate buttonTemplate in parsedSetup.ButtonTemplates)
            {
                IEnumerable<string> buttonErrors = templateRenderer.Render(buttonTemplate.Template).Errors;
                if (buttonTemplate.Label is not null)
                {
                    buttonErrors = buttonErrors.Concat(templateRenderer.Render(buttonTemplate.Label).Errors);
                }

                foreach (string errorMessage in buttonErrors)
                {
                    RecordError($"Line {buttonTemplate.LineNumber}: {errorMessage}", tableDisplayName, buttonErrorTables, buttonErrorOrder);
                }
            }
        }

        int tableCount = tablesToCheck.Count;
        parsedSetup.JoinerErrors.AddRange(joinerErrorOrder.Select(message => DescribeError(message, joinerErrorTables[message], tableCount, usingPlaceholderTable)));
        parsedSetup.ButtonErrors.AddRange(buttonErrorOrder.Select(message => DescribeError(message, buttonErrorTables[message], tableCount, usingPlaceholderTable)));
    }

    /// <summary>
    /// Remembers that an error happened for a table, without duplicating the message.
    /// </summary>
    private static void RecordError(string errorMessage, string tableName, Dictionary<string, List<string>> errorTables, List<string> errorOrder)
    {
        if (!errorTables.TryGetValue(errorMessage, out List<string>? tableNames))
        {
            tableNames = new List<string>();
            errorTables[errorMessage] = tableNames;
            errorOrder.Add(errorMessage);
        }

        if (!tableNames.Contains(tableName))
        {
            tableNames.Add(tableName);
        }
    }

    /// <summary>
    /// Adds " (only for: A, B)" to an error that does not happen for every table.
    /// </summary>
    private static string DescribeError(string errorMessage, List<string> tableNames, int tableCount, bool usingPlaceholderTable)
    {
        if (usingPlaceholderTable || tableNames.Count >= tableCount)
        {
            return errorMessage;
        }

        return $"{errorMessage} (only for: {string.Join(", ", tableNames)})";
    }

    /// <summary>
    /// Splits "name=value" on the first '='. The name is trimmed, the value is trimmed too.
    /// </summary>
    private static bool TrySplitNameValue(string text, out string name, out string value)
    {
        int equalsIndex = text.IndexOf('=');
        if (equalsIndex <= 0)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }

        name = text[..equalsIndex].Trim();
        value = text[(equalsIndex + 1)..].Trim();
        return name.Length > 0;
    }
}
