using System.Globalization;
using System.Text.RegularExpressions;

namespace Paster.Engine;

/// <summary>
/// The case transforms that can be chained onto a token, e.g. {table:snake:upper}.
/// </summary>
public static class CaseTransforms
{
    /// <summary>
    /// Splits text into words. Handles separators (space, _, -, .) as well as
    /// camelCase / PascalCase boundaries and acronyms ("DBTable" -> "DB", "Table").
    /// </summary>
    private static readonly Regex WordPattern = new(
        @"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+",
        RegexOptions.Compiled);

    /// <summary>
    /// Every supported transform, keyed by the name used in templates (case-insensitive).
    /// </summary>
    private static readonly Dictionary<string, Func<string, string>> TransformsByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["upper"] = text => text.ToUpperInvariant(),
            ["lower"] = text => text.ToLowerInvariant(),
            ["trim"] = text => text.Trim(),
            ["pascal"] = text => string.Concat(SplitWords(text).Select(Capitalize)),
            ["camel"] = ToCamelCase,
            ["snake"] = text => string.Join("_", SplitWords(text).Select(word => word.ToLowerInvariant())),
            ["kebab"] = text => string.Join("-", SplitWords(text).Select(word => word.ToLowerInvariant())),
            ["title"] = text => string.Join(" ", SplitWords(text).Select(Capitalize)),
        };

    /// <summary>
    /// Names of all transforms, used in error messages so the user sees what is allowed.
    /// </summary>
    public static IEnumerable<string> KnownTransformNames => TransformsByName.Keys;

    /// <summary>
    /// Looks up a transform by name. Returns false if there is no transform with that name.
    /// </summary>
    public static bool TryGet(string transformName, out Func<string, string> transform)
    {
        return TransformsByName.TryGetValue(transformName, out transform!);
    }

    /// <summary>
    /// Returns the individual words in a piece of text.
    /// </summary>
    public static List<string> SplitWords(string text)
    {
        return WordPattern.Matches(text).Select(match => match.Value).ToList();
    }

    /// <summary>
    /// "dbTABLE" -> "Dbtable": first letter upper case, the rest lower case.
    /// </summary>
    private static string Capitalize(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        return char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..].ToLowerInvariant();
    }

    /// <summary>
    /// "DB Table" -> "dbTable": like Pascal case, but the first word is all lower case.
    /// </summary>
    private static string ToCamelCase(string text)
    {
        List<string> words = SplitWords(text);
        if (words.Count == 0)
        {
            return string.Empty;
        }

        string firstWord = words[0].ToLowerInvariant();
        return firstWord + string.Concat(words.Skip(1).Select(Capitalize));
    }
}
