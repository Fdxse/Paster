namespace Paster.Engine;

/// <summary>
/// The order the steps (table × layer) are listed and walked through in.
/// The steps themselves are the same either way, so switching keeps what was already copied.
/// </summary>
public enum WalkOrder
{
    /// <summary>Every table in the first layer, then every table in the next layer, and so on.</summary>
    ByLayer,

    /// <summary>The first table through every layer, then the next table, and so on.</summary>
    ByTable,
}

/// <summary>
/// Turns the parsed tables and layers into the list of steps to work through.
/// </summary>
public static class WorkPlanner
{
    /// <summary>
    /// All steps in walking order. Without layers there is one step per table.
    /// A table with "layers=..." only gets steps for the layers it names.
    /// </summary>
    public static List<WorkStep> BuildSteps(ParsedSetup parsedSetup, WalkOrder walkOrder)
    {
        var workSteps = new List<WorkStep>();

        if (parsedSetup.Layers.Count == 0)
        {
            workSteps.AddRange(parsedSetup.Tables.Select(tableEntry => new WorkStep { Table = tableEntry }));
            return workSteps;
        }

        if (walkOrder == WalkOrder.ByLayer)
        {
            foreach (LayerEntry layerEntry in parsedSetup.Layers)
            {
                foreach (TableEntry tableEntry in parsedSetup.Tables.Where(tableEntry => TableGoesThrough(tableEntry, layerEntry)))
                {
                    workSteps.Add(new WorkStep { Table = tableEntry, Layer = layerEntry });
                }
            }
        }
        else
        {
            foreach (TableEntry tableEntry in parsedSetup.Tables)
            {
                foreach (LayerEntry layerEntry in parsedSetup.Layers.Where(layerEntry => TableGoesThrough(tableEntry, layerEntry)))
                {
                    workSteps.Add(new WorkStep { Table = tableEntry, Layer = layerEntry });
                }
            }
        }

        return workSteps;
    }

    /// <summary>
    /// The button templates that show up for a step (buttons tagged "@stg,wrk" only show in those layers).
    /// </summary>
    public static List<ButtonTemplate> ButtonsFor(ParsedSetup parsedSetup, WorkStep workStep)
    {
        return parsedSetup.ButtonTemplates.Where(buttonTemplate => buttonTemplate.AppliesTo(workStep.Layer)).ToList();
    }

    /// <summary>
    /// True unless the table line says "layers=..." without this layer.
    /// </summary>
    private static bool TableGoesThrough(TableEntry tableEntry, LayerEntry layerEntry)
    {
        return tableEntry.LayerNames is null
               || tableEntry.LayerNames.Contains(layerEntry.Name, StringComparer.OrdinalIgnoreCase);
    }
}
