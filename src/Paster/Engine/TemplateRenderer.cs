using System.Text;

namespace Paster.Engine;

/// <summary>
/// Renders templates like "TRUNCATE TABLE [{schema}].[{fulltable:upper}];" for one step (a table, in a layer when layers are used).
///
/// Token syntax:
///   {name}                 the value of a variable or joiner
///   {name:upper}           the value with a transform applied
///   {name:snake:upper}     transforms are applied left to right
///   {{ and }}              a literal { or }
///
/// Lookup order for a name:
///   1. the per-line override on the table line ("DBTable; schema=stg")
///   2. the layer's variables ("dwh; schema=dwh")
///   3. the built-in {table}, {layer} and {counter}
///   4. the global variables
///   5. the joiners (rendered recursively, loops are reported as errors)
/// </summary>
public sealed class TemplateRenderer
{
    private readonly ParsedSetup parsedSetup;
    private readonly TableEntry tableEntry;

    /// <summary>The layer of the step, or null when the setup has no layers.</summary>
    private readonly LayerEntry? layerEntry;

    /// <summary>
    /// What {counter} becomes. Null keeps "{counter}" as it is in the output (with any transforms), which gives a
    /// text that stays the same while the counter moves on, used to remember which buttons were copied.
    /// </summary>
    private readonly string? counterText;

    /// <summary>Set while rendering when {counter} is met; reset at the start of every Render.</summary>
    private bool counterWasUsed;

    public TemplateRenderer(ParsedSetup parsedSetup, WorkStep workStep, int? counterValue = null)
    {
        this.parsedSetup = parsedSetup;
        tableEntry = workStep.Table;
        layerEntry = workStep.Layer;
        counterText = counterValue?.ToString();
    }

    /// <summary>
    /// Renders a template (e.g. a button line) for the step this renderer was created for.
    /// </summary>
    public RenderResult Render(string template)
    {
        var errors = new List<string>();
        counterWasUsed = false;
        string renderedText = RenderInternal(template, new List<string>(), errors);
        return new RenderResult { Text = renderedText, Errors = errors, UsesCounter = counterWasUsed };
    }

    /// <summary>
    /// Renders a single joiner by name. Used to validate the joiner editor.
    /// </summary>
    public RenderResult RenderJoiner(string joinerName)
    {
        var errors = new List<string>();
        string renderedText = ResolveName(joinerName, new List<string>(), errors);
        return new RenderResult { Text = renderedText, Errors = errors };
    }

    /// <summary>
    /// Walks through the template character by character, copying literal text and
    /// replacing every {token}. <paramref name="joinerStack"/> holds the joiners currently
    /// being rendered so that loops (a -> b -> a) can be detected.
    /// </summary>
    private string RenderInternal(string template, List<string> joinerStack, List<string> errors)
    {
        var output = new StringBuilder();
        int position = 0;

        while (position < template.Length)
        {
            char currentCharacter = template[position];

            // "{{" is an escaped literal '{'.
            if (currentCharacter == '{' && position + 1 < template.Length && template[position + 1] == '{')
            {
                output.Append('{');
                position += 2;
                continue;
            }

            // "}}" is an escaped literal '}'. A single '}' on its own is also kept as-is.
            if (currentCharacter == '}')
            {
                output.Append('}');
                position += position + 1 < template.Length && template[position + 1] == '}' ? 2 : 1;
                continue;
            }

            if (currentCharacter != '{')
            {
                output.Append(currentCharacter);
                position++;
                continue;
            }

            // Start of a token: find the matching '}'.
            int closingBraceIndex = template.IndexOf('}', position + 1);
            if (closingBraceIndex < 0)
            {
                errors.Add($"'{{' at position {position + 1} is never closed with '}}'. Use {{{{ for a literal '{{'.");
                output.Append(template, position, template.Length - position);
                break;
            }

            string tokenText = template.Substring(position + 1, closingBraceIndex - position - 1);
            output.Append(RenderToken(tokenText, joinerStack, errors));
            position = closingBraceIndex + 1;
        }

        return output.ToString();
    }

    /// <summary>
    /// Renders the inside of one token, e.g. "fulltable:upper".
    /// On error the token is written back as "{...}" so the problem stays visible in the output.
    /// </summary>
    private string RenderToken(string tokenText, List<string> joinerStack, List<string> errors)
    {
        string[] tokenParts = tokenText.Split(':');
        string variableName = tokenParts[0].Trim();

        if (variableName.Length == 0)
        {
            errors.Add("Empty token {} found.");
            return "{" + tokenText + "}";
        }

        // Without a counter value, {counter} (with its transforms) is written back unchanged.
        if (counterText is null && string.Equals(variableName, SetupParser.CounterVariableName, StringComparison.OrdinalIgnoreCase))
        {
            counterWasUsed = true;
            return "{" + tokenText + "}";
        }

        int errorCountBefore = errors.Count;
        string value = ResolveName(variableName, joinerStack, errors);

        // Apply each transform in order.
        foreach (string rawTransformName in tokenParts.Skip(1))
        {
            string transformName = rawTransformName.Trim();
            if (CaseTransforms.TryGet(transformName, out Func<string, string> transform))
            {
                value = transform(value);
            }
            else
            {
                errors.Add($"Unknown transform \"{transformName}\" in {{{tokenText}}}. " +
                           $"Known: {string.Join(", ", CaseTransforms.KnownTransformNames)}.");
            }
        }

        bool tokenFailed = errors.Count > errorCountBefore;
        return tokenFailed ? "{" + tokenText + "}" : value;
    }

    /// <summary>
    /// Finds the value of a name following the lookup order described on the class.
    /// </summary>
    private string ResolveName(string variableName, List<string> joinerStack, List<string> errors)
    {
        if (tableEntry.Overrides.TryGetValue(variableName, out string? overrideValue))
        {
            return overrideValue;
        }

        if (layerEntry is not null && layerEntry.Variables.TryGetValue(variableName, out string? layerValue))
        {
            return layerValue;
        }

        if (string.Equals(variableName, SetupParser.TableVariableName, StringComparison.OrdinalIgnoreCase))
        {
            return tableEntry.Name;
        }

        if (layerEntry is not null && string.Equals(variableName, SetupParser.LayerVariableName, StringComparison.OrdinalIgnoreCase))
        {
            return layerEntry.Name;
        }

        if (string.Equals(variableName, SetupParser.CounterVariableName, StringComparison.OrdinalIgnoreCase))
        {
            counterWasUsed = true;
            return counterText ?? string.Empty;
        }

        if (parsedSetup.GlobalVariables.TryGetValue(variableName, out string? globalValue))
        {
            return globalValue;
        }

        if (parsedSetup.Joiners.TryGetValue(variableName, out string? joinerTemplate))
        {
            // Loop detection: if this joiner is already being rendered further up, we are going in circles.
            if (joinerStack.Contains(variableName, StringComparer.OrdinalIgnoreCase))
            {
                string loopDescription = string.Join(" -> ", joinerStack.Append(variableName));
                errors.Add($"Joiner loop: {loopDescription}.");
                return string.Empty;
            }

            joinerStack.Add(variableName);
            string joinerValue = RenderInternal(joinerTemplate, joinerStack, errors);
            joinerStack.RemoveAt(joinerStack.Count - 1);
            return joinerValue;
        }

        errors.Add($"Unknown variable \"{variableName}\".");
        return string.Empty;
    }
}
