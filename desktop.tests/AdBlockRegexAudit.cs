using MishaWeb;

internal static class AdBlockRegexAudit
{
    public static int Run(string folder)
    {
        var lines = Directory.EnumerateFiles(folder, "list-*.txt")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(File.ReadLines)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var rows = new List<RegexAuditRow>();
        foreach (var line in lines)
        {
            if (!AdBlockEngine.TryGetRegexPatternForTesting(line, out var pattern)) continue;
            ScanFeatures(pattern, out var alternation, out var optionalQuantifier);
            var lookaround = pattern.Contains("(?=", StringComparison.Ordinal)
                || pattern.Contains("(?!", StringComparison.Ordinal)
                || pattern.Contains("(?<=", StringComparison.Ordinal)
                || pattern.Contains("(?<!", StringComparison.Ordinal);
            var domainScoped = HasDomainScope(line);
            var hasMandatoryLiteral = AdBlockEngine.TryExtractMandatoryRegexLiteralForTesting(
                pattern,
                out var mandatoryLiteral);
            rows.Add(new RegexAuditRow(
                line,
                pattern,
                alternation,
                optionalQuantifier,
                lookaround,
                domainScoped,
                hasMandatoryLiteral ? mandatoryLiteral : string.Empty,
                AdBlockEngine.IsSupportedRegexRuleForTesting(line)));
        }

        var supported = rows.Where(row => row.Supported).ToArray();
        Console.WriteLine(
            $"rawRegex={rows.Count}; supported={supported.Length}");
        PrintClassification("raw", rows);
        PrintClassification("supported", supported);
        var topLiterals = supported
            .Where(row => row.MandatoryLiteral.Length >= 3)
            .GroupBy(row => row.MandatoryLiteral, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(group => $"{group.Key}:{group.Count()}");
        Console.WriteLine("topMandatoryLiterals=" + string.Join(',', topLiterals));
        return 0;
    }

    private static void PrintClassification(string label, IReadOnlyCollection<RegexAuditRow> rows)
    {
        Console.WriteLine(
            $"{label}Total={rows.Count}; "
            + $"{label}MandatoryLiteral3={rows.Count(row => row.MandatoryLiteral.Length >= 3)}; "
            + $"{label}MandatoryLiteral5={rows.Count(row => row.MandatoryLiteral.Length >= 5)}; "
            + $"{label}MandatoryLiteral8={rows.Count(row => row.MandatoryLiteral.Length >= 8)}; "
            + $"{label}Alternation={rows.Count(row => row.Alternation)}; "
            + $"{label}Optional={rows.Count(row => row.OptionalQuantifier)}; "
            + $"{label}Lookaround={rows.Count(row => row.Lookaround)}; "
            + $"{label}NoLiteral={rows.Count(row => row.MandatoryLiteral.Length < 3)}; "
            + $"{label}DomainScoped={rows.Count(row => row.DomainScoped)}; "
            + $"{label}NoLiteralDomainScoped={rows.Count(row => row.DomainScoped && row.MandatoryLiteral.Length < 3)}; "
            + $"{label}NoLiteralGeneric={rows.Count(row => !row.DomainScoped && row.MandatoryLiteral.Length < 3)}");
    }

    private static bool HasDomainScope(string line)
    {
        var closingSlash = line.LastIndexOf('/');
        if (closingSlash < 0 || closingSlash + 1 >= line.Length || line[closingSlash + 1] != '$')
        {
            return false;
        }
        var options = line.AsSpan(closingSlash + 2);
        return options.Contains("domain=", StringComparison.OrdinalIgnoreCase)
            || options.Contains("from=", StringComparison.OrdinalIgnoreCase)
            || options.Contains("to=", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanFeatures(
        string pattern,
        out bool alternation,
        out bool optionalQuantifier)
    {
        alternation = false;
        optionalQuantifier = false;
        var inCharacterClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var value = pattern[index];
            if (value == '\\')
            {
                index++;
                continue;
            }
            if (inCharacterClass)
            {
                if (value == ']') inCharacterClass = false;
                continue;
            }
            if (value == '[')
            {
                inCharacterClass = true;
                continue;
            }
            if (value == '|') alternation = true;
            if (value is '?' or '*') optionalQuantifier = true;
            if (value == '{' && index + 1 < pattern.Length && pattern[index + 1] == '0')
            {
                optionalQuantifier = true;
            }
        }
    }

    private sealed record RegexAuditRow(
        string Line,
        string Pattern,
        bool Alternation,
        bool OptionalQuantifier,
        bool Lookaround,
        bool DomainScoped,
        string MandatoryLiteral,
        bool Supported);
}
