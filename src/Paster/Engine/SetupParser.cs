using System.Text.RegularExpressions;

namespace Paster.Engine;

/// <summary>
/// Turns the raw text of the five editors (tables, layers, variables, joiners, buttons) into a <see cref="ParsedSetup"/>.
/// Rules shared by all editors:
///   - blank lines are ignored
///   - lines starting with "//" are comments and are ignored
/// </summary>
public static class SetupParser
{
    /// <summary>The built-in variable that always holds the current table name.</summary>
    public const string TableVariableName = "table";

    /// <summary>The built-in variable that holds the current layer name (only when layers are used).</summary>
    public const string LayerVariableName = "layer";

    /// <summary>The table line setting that limits a table to some layers: "DBTable; layers=stg,wrk".</summary>
    public const string LayersSettingName = "layers";

    /// <summary>Allowed layer names: letters, digits, _ and -, so they can be listed with commas.</summary>
    private static readonly Regex LayerNamePattern = new(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled);

    /// <summary>
    /// A button line that starts with layer tags: "@stg,wrk TRUNCATE TABLE ...". Group 1 is the list, group 2 the rest.
    /// </summary>
    private static readonly Regex LayerTagPattern = new(@"^@([A-Za-z0-9_\-]+(?:\s*,\s*[A-Za-z0-9_\-]+)*)\s+(\S.*)$", RegexOptions.Compiled);

    /// <summary>Allowed variable / joiner names: a letter or _ followed by letters, digits or _.</summary>
    private static readonly Regex VariableNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Parses all five editors and then checks joiners and button templates against every step (table × layer),
    /// so that unknown variables and joiner loops show up as soft errors.
    /// </summary>
    public static ParsedSetup Parse(string tablesText, string layersText, string variablesText, string joinersText, string buttonsText)
    {
        var parsedSetup = new ParsedSetup();

        ParseLayers(layersText, parsedSetup);
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

            ParseNameValueParts(lineParts.Skip(1), lineNumber, tableEntry.Overrides, tableEntry.Errors);

            // "layers=stg,wrk" is not a variable: it picks the layers this table goes through.
            if (tableEntry.Overrides.Remove(LayersSettingName, out string? layerListText))
            {
                tableEntry.LayerNames = ParseTableLayerList(layerListText, lineNumber, parsedSetup, tableEntry.Errors);
            }

            parsedSetup.TableErrors.AddRange(tableEntry.Errors);
            parsedSetup.Tables.Add(tableEntry);
        }
    }

    /// <summary>
    /// The "name=value" parts after the name on a table or layer line ("DBTable; schema=stg; prefix=Fact").
    /// </summary>
    private static void ParseNameValueParts(IEnumerable<string> lineParts, int lineNumber, Dictionary<string, string> values, List<string> errors)
    {
        foreach (string linePart in lineParts)
        {
            string trimmedPart = linePart.Trim();
            if (trimmedPart.Length == 0)
            {
                // Allows a trailing ';' without complaining.
                continue;
            }

            if (!TrySplitNameValue(trimmedPart, out string variableName, out string variableValue))
            {
                errors.Add($"Line {lineNumber}: \"{trimmedPart}\" should look like name=value.");
                continue;
            }

            if (!IsValidVariableName(variableName))
            {
                errors.Add($"Line {lineNumber}: \"{variableName}\" is not a valid variable name.");
                continue;
            }

            if (values.ContainsKey(variableName))
            {
                errors.Add($"Line {lineNumber}: \"{variableName}\" is set more than once (the last one wins).");
            }

            values[variableName] = variableValue;
        }
    }

    /// <summary>
    /// Checks the list in "layers=stg,wrk" against the layer editor. Unknown names are reported
    /// (and simply never match). Returns null, meaning every layer, if the list can't be used.
    /// </summary>
    private static List<string>? ParseTableLayerList(string layerListText, int lineNumber, ParsedSetup parsedSetup, List<string> errors)
    {
        if (parsedSetup.Layers.Count == 0)
        {
            errors.Add($"Line {lineNumber}: \"{LayersSettingName}=\" is used, but no layers are defined in the Layers editor.");
            return null;
        }

        List<string> layerNames = SplitLayerList(layerListText);
        if (layerNames.Count == 0)
        {
            errors.Add($"Line {lineNumber}: \"{LayersSettingName}=\" needs at least one layer, e.g. {LayersSettingName}=stg,wrk.");
            return null;
        }

        ReportUnknownLayers(layerNames, $"Line {lineNumber}", parsedSetup, errors);
        return layerNames;
    }

    /// <summary>
    /// "stg, wrk ,dwh" -> ["stg", "wrk", "dwh"].
    /// </summary>
    private static List<string> SplitLayerList(string layerListText)
    {
        return layerListText
            .Split(',')
            .Select(layerName => layerName.Trim())
            .Where(layerName => layerName.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Adds an error for every name in the list that is not a layer.
    /// </summary>
    private static void ReportUnknownLayers(List<string> layerNames, string where, ParsedSetup parsedSetup, List<string> errors)
    {
        foreach (string layerName in layerNames)
        {
            bool isKnownLayer = parsedSetup.Layers.Any(layerEntry => string.Equals(layerEntry.Name, layerName, StringComparison.OrdinalIgnoreCase));
            if (!isKnownLayer)
            {
                string knownLayers = string.Join(", ", parsedSetup.Layers.Select(layerEntry => layerEntry.Name));
                errors.Add($"{where}: unknown layer \"{layerName}\" (layers: {knownLayers}).");
            }
        }
    }

    /// <summary>
    /// Layer lines look like "stg" or "dwh; schema=dwh; prefix=Dim". They are listed in the order to work through them.
    /// </summary>
    private static void ParseLayers(string layersText, ParsedSetup parsedSetup)
    {
        foreach ((int lineNumber, string line) in MeaningfulLines(layersText))
        {
            string[] lineParts = line.Split(';');
            var layerEntry = new LayerEntry
            {
                LineNumber = lineNumber,
                Name = lineParts[0].Trim(),
            };

            if (layerEntry.Name.Length == 0)
            {
                parsedSetup.LayerErrors.Add($"Line {lineNumber}: the layer name is missing (nothing before the first ';').");
                continue;
            }

            if (!LayerNamePattern.IsMatch(layerEntry.Name))
            {
                parsedSetup.LayerErrors.Add($"Line {lineNumber}: \"{layerEntry.Name}\" can only contain letters, digits, _ and -.");
                continue;
            }

            if (parsedSetup.Layers.Any(existingLayer => string.Equals(existingLayer.Name, layerEntry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                parsedSetup.LayerErrors.Add($"Line {lineNumber}: the layer \"{layerEntry.Name}\" is listed more than once (only the first is used).");
                continue;
            }

            ParseNameValueParts(lineParts.Skip(1), lineNumber, layerEntry.Variables, parsedSetup.LayerErrors);

            // {table} and {layer} are built in; a layer can't change them.
            foreach (string builtInName in new[] { TableVariableName, LayerVariableName, LayersSettingName })
            {
                if (layerEntry.Variables.Remove(builtInName))
                {
                    parsedSetup.LayerErrors.Add($"Line {lineNumber}: \"{builtInName}\" is built in and can't be set on a layer.");
                }
            }

            parsedSetup.Layers.Add(layerEntry);
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

            if (parsedSetup.Layers.Count > 0 && string.Equals(variableName, LayerVariableName, StringComparison.OrdinalIgnoreCase))
            {
                parsedSetup.VariableErrors.Add($"Line {lineNumber}: \"{LayerVariableName}\" is built in and always holds the layer name.");
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

            if (parsedSetup.Layers.Count > 0 && string.Equals(joinerName, LayerVariableName, StringComparison.OrdinalIgnoreCase))
            {
                parsedSetup.JoinerErrors.Add($"Line {lineNumber}: \"{LayerVariableName}\" is built in and cannot be a joiner.");
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
    /// When layers are used, a line can start with "@stg,wrk " to show that button in those layers only
    /// (also in front of a block: "@stg &lt;&lt;&lt; Load").
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

            // "@stg,wrk ..." limits the button to some layers. Only read as tags when layers exist,
            // so a line that happens to start with @ stays plain text in a setup without layers.
            List<string>? buttonLayerNames = null;
            Match layerTagMatch = LayerTagPattern.Match(trimmedLine);
            if (parsedSetup.Layers.Count > 0 && layerTagMatch.Success)
            {
                buttonLayerNames = SplitLayerList(layerTagMatch.Groups[1].Value);
                ReportUnknownLayers(buttonLayerNames, $"Line {lineNumber}", parsedSetup, parsedSetup.ButtonErrors);
                trimmedLine = layerTagMatch.Groups[2].Value.Trim();
            }

            if (!trimmedLine.StartsWith(BlockStartMarker, StringComparison.Ordinal))
            {
                parsedSetup.ButtonTemplates.Add(new ButtonTemplate { LineNumber = lineNumber, Template = trimmedLine, LayerNames = buttonLayerNames });
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
                LayerNames = buttonLayerNames,
            });
        }
    }

    /// <summary>
    /// Renders every joiner and every button template for every step (table × layer) and collects the errors
    /// into the joiner / button sections. Uses a placeholder table when the table list is empty
    /// so that typos are still caught. An error that only happens for some steps says which ones.
    /// </summary>
    private static void ValidateByRendering(ParsedSetup parsedSetup)
    {
        bool usingPlaceholderTable = parsedSetup.Tables.Count == 0;
        List<WorkStep> stepsToCheck;
        if (usingPlaceholderTable)
        {
            // No tables yet: check against a made-up table, in every layer (if any).
            var placeholderTable = new TableEntry { LineNumber = 0, Name = "Table" };
            stepsToCheck = parsedSetup.Layers.Count == 0
                ? new List<WorkStep> { new() { Table = placeholderTable } }
                : parsedSetup.Layers.Select(layerEntry => new WorkStep { Table = placeholderTable, Layer = layerEntry }).ToList();
        }
        else
        {
            stepsToCheck = WorkPlanner.BuildSteps(parsedSetup, WalkOrder.ByLayer);
        }

        // Error message -> the steps it happened for. The order lists keep the messages in the order they were found.
        var joinerErrorTallies = new Dictionary<string, ErrorTally>();
        var buttonErrorTallies = new Dictionary<string, ErrorTally>();
        var joinerErrorOrder = new List<string>();
        var buttonErrorOrder = new List<string>();

        foreach (WorkStep workStep in stepsToCheck)
        {
            var templateRenderer = new TemplateRenderer(parsedSetup, workStep);
            string stepDisplayName = workStep.DisplayName;

            foreach (string joinerName in parsedSetup.Joiners.Keys)
            {
                foreach (string errorMessage in templateRenderer.RenderJoiner(joinerName).Errors)
                {
                    RecordError($"{joinerName}: {errorMessage}", stepDisplayName, stepsToCheck.Count, joinerErrorTallies, joinerErrorOrder);
                }
            }

            foreach (ButtonTemplate buttonTemplate in WorkPlanner.ButtonsFor(parsedSetup, workStep))
            {
                IEnumerable<string> buttonErrors = templateRenderer.Render(buttonTemplate.Template).Errors;
                if (buttonTemplate.Label is not null)
                {
                    buttonErrors = buttonErrors.Concat(templateRenderer.Render(buttonTemplate.Label).Errors);
                }

                // A button tagged for some layers is only checked in those, so "every step" means fewer steps for it.
                int stepsWithThisButton = stepsToCheck.Count(stepToCheck => buttonTemplate.AppliesTo(stepToCheck.Layer));

                foreach (string errorMessage in buttonErrors)
                {
                    RecordError($"Line {buttonTemplate.LineNumber}: {errorMessage}", stepDisplayName, stepsWithThisButton, buttonErrorTallies, buttonErrorOrder);
                }
            }
        }

        parsedSetup.JoinerErrors.AddRange(joinerErrorOrder.Select(message => DescribeError(message, joinerErrorTallies[message], usingPlaceholderTable)));
        parsedSetup.ButtonErrors.AddRange(buttonErrorOrder.Select(message => DescribeError(message, buttonErrorTallies[message], usingPlaceholderTable)));
    }

    /// <summary>
    /// Which steps an error message happened for, out of how many steps the template was checked in.
    /// </summary>
    private sealed class ErrorTally
    {
        public List<string> StepNames { get; } = new();
        public int CheckedStepCount { get; init; }
    }

    /// <summary>
    /// Remembers that an error happened for a step, without duplicating the message.
    /// </summary>
    private static void RecordError(string errorMessage, string stepName, int checkedStepCount, Dictionary<string, ErrorTally> errorTallies, List<string> errorOrder)
    {
        if (!errorTallies.TryGetValue(errorMessage, out ErrorTally? errorTally))
        {
            errorTally = new ErrorTally { CheckedStepCount = checkedStepCount };
            errorTallies[errorMessage] = errorTally;
            errorOrder.Add(errorMessage);
        }

        if (!errorTally.StepNames.Contains(stepName))
        {
            errorTally.StepNames.Add(stepName);
        }
    }

    /// <summary>
    /// Adds " (only for: A, B)" to an error that does not happen for every step. Long lists are cut short.
    /// </summary>
    private static string DescribeError(string errorMessage, ErrorTally errorTally, bool usingPlaceholderTable)
    {
        if (usingPlaceholderTable || errorTally.StepNames.Count >= errorTally.CheckedStepCount)
        {
            return errorMessage;
        }

        const int maximumNamesShown = 5;
        string shownNames = string.Join(", ", errorTally.StepNames.Take(maximumNamesShown));
        int hiddenNameCount = errorTally.StepNames.Count - maximumNamesShown;
        string moreText = hiddenNameCount > 0 ? $" and {hiddenNameCount} more" : string.Empty;
        return $"{errorMessage} (only for: {shownNames}{moreText})";
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
