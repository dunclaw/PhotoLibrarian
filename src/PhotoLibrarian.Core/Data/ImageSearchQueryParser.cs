// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoLibrarian.Core.Data;

public static class ImageSearchQueryParser
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}\p{M}]+", RegexOptions.Compiled);

    public static string? GetSyntaxError(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text).ErrorMessage;
    }

    internal static ParsedImageSearchQuery Parse(string text)
    {
        if (!TryTokenize(text, out var tokens, out var error))
            return ParsedImageSearchQuery.Invalid(error!);

        var fullTextTerms = new List<string>();
        var fieldClauses = new List<ImageSearchFieldClause>();
        foreach (var token in tokens)
        {
            var colon = FindFieldSeparator(token);
            if (colon > 0)
            {
                var name = token[..colon];
                if (!IsFieldName(name))
                {
                    AddFreeTextTerms(token, fullTextTerms);
                    continue;
                }

                if (!TryParseFieldClause(name, token[(colon + 1)..], fieldClauses, out error))
                    return ParsedImageSearchQuery.Invalid(error!);
                continue;
            }

            if (!TryRemoveOuterQuotes(token, out var value, out error))
                return ParsedImageSearchQuery.Invalid(error!);
            AddFreeTextTerms(value, fullTextTerms);
        }

        var textFieldQueries = fieldClauses
            .Where(clause => clause.Field is ImageSearchField.Camera or
                ImageSearchField.Keyword or ImageSearchField.Person)
            .Select(clause => clause.TextPhrase);
        var metadataClauses = fieldClauses
            .Where(clause => clause.Field is ImageSearchField.Rating or ImageSearchField.Flag or
                ImageSearchField.Iso or ImageSearchField.Date)
            .ToArray();
        return new ParsedImageSearchQuery(
            string.Join(" AND ", fullTextTerms.Concat(textFieldQueries)),
            metadataClauses,
            null);
    }

    private static int FindFieldSeparator(string token)
    {
        var quote = token.IndexOf('"');
        var colon = token.IndexOf(':');
        return colon >= 0 && (quote < 0 || colon < quote) ? colon : -1;
    }

    private static bool IsFieldName(string name) =>
        name.Length > 0 && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool TryParseFieldClause(
        string name,
        string rawValue,
        List<ImageSearchFieldClause> clauses,
        out string? error)
    {
        error = null;
        if (!TryRemoveOuterQuotes(rawValue, out var value, out error))
        {
            error = $"Invalid value for {name}: {error}";
            return false;
        }

        if (value.Length == 0)
        {
            error = $"The {name} field needs a value.";
            return false;
        }

        var field = name.ToLowerInvariant();
        switch (field)
        {
            case "rating":
            case "iso":
                if (!TryParseNumber(value, out var comparison, out var number))
                {
                    error = $"{field} expects a number, optionally preceded by >, >=, <, <=, or =.";
                    return false;
                }

                clauses.Add(ImageSearchFieldClause.Numeric(
                    field == "rating" ? ImageSearchField.Rating : ImageSearchField.Iso,
                    comparison,
                    number));
                return true;

            case "date":
                if (!TryParseDate(value, out comparison, out var start, out var end))
                {
                    error = "date expects YYYY, YYYY-MM, or YYYY-MM-DD, optionally preceded by >, >=, <, <=, or =.";
                    return false;
                }

                clauses.Add(ImageSearchFieldClause.Date(comparison, start, end));
                return true;

            case "flag":
                if (value.Equals("pick", StringComparison.OrdinalIgnoreCase))
                    clauses.Add(ImageSearchFieldClause.Flag(true));
                else if (value.Equals("unpick", StringComparison.OrdinalIgnoreCase) ||
                         value.Equals("unflagged", StringComparison.OrdinalIgnoreCase))
                    clauses.Add(ImageSearchFieldClause.Flag(false));
                else
                {
                    error = "flag expects pick, unpick, or unflagged.";
                    return false;
                }
                return true;

            case "camera":
            case "keyword":
            case "person":
                if (value.Contains('>') || value.Contains('<') || value.Contains('='))
                {
                    error = $"{field} does not support comparison operators.";
                    return false;
                }

                if (!TryCreateFieldPhrase(field, value, out var phrase))
                {
                    error = $"{field} needs at least one letter or number.";
                    return false;
                }

                var searchField = field switch
                {
                    "camera" => ImageSearchField.Camera,
                    "keyword" => ImageSearchField.Keyword,
                    _ => ImageSearchField.Person
                };
                clauses.Add(ImageSearchFieldClause.Text(searchField, phrase));
                return true;

            default:
                error = $"Unknown search field '{name}'. Supported fields: rating, flag, iso, camera, date, keyword, person.";
                return false;
        }
    }

    private static bool TryParseNumber(
        string value,
        out SearchComparison comparison,
        out int number)
    {
        comparison = SearchComparison.Equal;
        number = 0;
        var offset = GetComparisonPrefix(value, out comparison);
        return int.TryParse(value.AsSpan(offset), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static int GetComparisonPrefix(string value, out SearchComparison comparison)
    {
        if (value.StartsWith(">=", StringComparison.Ordinal))
        {
            comparison = SearchComparison.GreaterThanOrEqual;
            return 2;
        }
        if (value.StartsWith("<=", StringComparison.Ordinal))
        {
            comparison = SearchComparison.LessThanOrEqual;
            return 2;
        }
        if (value.StartsWith('>'))
        {
            comparison = SearchComparison.GreaterThan;
            return 1;
        }
        if (value.StartsWith('<'))
        {
            comparison = SearchComparison.LessThan;
            return 1;
        }
        if (value.StartsWith('='))
        {
            comparison = SearchComparison.Equal;
            return 1;
        }

        comparison = SearchComparison.Equal;
        return 0;
    }

    private static bool TryParseDate(
        string value,
        out SearchComparison comparison,
        out DateOnly start,
        out DateOnly end)
    {
        var offset = GetComparisonPrefix(value, out comparison);
        var dateText = value[offset..];
        var format = dateText.Length switch
        {
            4 => "yyyy",
            7 => "yyyy-MM",
            10 => "yyyy-MM-dd",
            _ => string.Empty
        };
        if (format.Length == 0 ||
            !DateOnly.TryParseExact(dateText, format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out start))
        {
            start = default;
            end = default;
            return false;
        }

        try
        {
            end = format.Length switch
            {
                4 => start.AddYears(1),
                7 => start.AddMonths(1),
                _ => start.AddDays(1)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            end = default;
            return false;
        }
        return true;
    }

    private static bool TryCreateFieldPhrase(string field, string value, out string phrase)
    {
        var words = WordPattern.Matches(value).Select(match => match.Value).ToArray();
        var indexField = field switch
        {
            "keyword" => "tags",
            "person" => "people",
            _ => field
        };
        phrase = words.Length == 0 ? string.Empty : $"{indexField} : \"{string.Join(" ", words)}\"*";
        return words.Length > 0;
    }

    private static bool TryRemoveOuterQuotes(
        string value,
        out string unquoted,
        out string? error)
    {
        error = null;
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
        {
            unquoted = value[1..^1];
            return true;
        }

        if (value.Contains('"'))
        {
            unquoted = string.Empty;
            error = "Put a quoted value in double quotes, for example person:\"Jane Doe\".";
            return false;
        }

        unquoted = value;
        return true;
    }

    private static void AddFreeTextTerms(string value, List<string> terms)
    {
        foreach (Match match in WordPattern.Matches(value))
            terms.Add($"\"{match.Value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*");
    }

    private static bool TryTokenize(string text, out List<string> tokens, out string? error)
    {
        tokens = [];
        error = null;
        var token = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\' && inQuotes && index + 1 < text.Length &&
                text[index + 1] is '"' or '\\')
            {
                token.Append(text[++index]);
            }
            else if (character == '"')
            {
                inQuotes = !inQuotes;
                token.Append(character);
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                AddToken(tokens, token);
            }
            else
            {
                token.Append(character);
            }
        }

        if (inQuotes)
        {
            error = "A quoted search value is missing its closing double quote.";
            return false;
        }

        AddToken(tokens, token);
        return true;
    }

    private static void AddToken(List<string> tokens, StringBuilder token)
    {
        if (token.Length == 0)
            return;

        tokens.Add(token.ToString());
        token.Clear();
    }
}

internal sealed record ParsedImageSearchQuery(
    string FullTextQuery,
    IReadOnlyList<ImageSearchFieldClause> FieldClauses,
    string? ErrorMessage)
{
    public bool IsValid => ErrorMessage is null;

    public static ParsedImageSearchQuery Invalid(string error) =>
        new(string.Empty, [], error);
}

internal sealed record ImageSearchFieldClause(
    ImageSearchField Field,
    SearchComparison Comparison,
    int Number,
    DateOnly DateStart,
    DateOnly DateEnd,
    bool FlagValue,
    string TextPhrase)
{
    public static ImageSearchFieldClause Numeric(
        ImageSearchField field, SearchComparison comparison, int value) =>
        new(field, comparison, value, default, default, false, string.Empty);

    public static ImageSearchFieldClause Date(
        SearchComparison comparison, DateOnly start, DateOnly end) =>
        new(ImageSearchField.Date, comparison, 0, start, end, false, string.Empty);

    public static ImageSearchFieldClause Flag(bool value) =>
        new(ImageSearchField.Flag, SearchComparison.Equal, 0, default, default, value, string.Empty);

    public static ImageSearchFieldClause Text(ImageSearchField field, string phrase) =>
        new(field, SearchComparison.Equal, 0, default, default, false, phrase);
}

internal enum ImageSearchField
{
    Rating,
    Flag,
    Iso,
    Date,
    Camera,
    Keyword,
    Person
}

internal enum SearchComparison
{
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}
