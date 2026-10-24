using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinSight.Core;

namespace WinSight.Infrastructure.Search;

internal sealed class DocumentSearchQuery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Tokens = new(@"[A-Za-z0-9]+(?:[-_./:][A-Za-z0-9]+)*", RegexOptions.CultureInvariant, Timeout);
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project"] = "project", ["项目"] = "project", ["项目编号"] = "project", ["项目编码"] = "project",
        ["amount"] = "amount", ["金额"] = "amount", ["date"] = "date", ["日期"] = "date",
        ["version"] = "version", ["版本"] = "version", ["model"] = "model", ["型号"] = "model",
        ["sheet"] = "sheet", ["工作表"] = "sheet", ["cell"] = "cell", ["单元格"] = "cell",
        ["name"] = "name", ["文件名"] = "name", ["folder"] = "folder", ["目录"] = "folder",
        ["folder1"] = "folder1", ["folder2"] = "folder2", ["folder3"] = "folder3"
    };
    internal string Text { get; }
    internal IReadOnlyList<SearchField> Filters { get; }
    internal string[] Literals { get; }
    internal bool HasFilters => Filters.Count != 0;
    internal DocumentSearchQuery(string input, IReadOnlyList<SearchField>? filters)
    {
        var list = new List<SearchField>();
        if (filters is not null)
            foreach (var field in filters)
            {
                if (field is null || !Aliases.TryGetValue(field.Name, out var name)) throw new ArgumentException("Unknown structured filter.");
                list.Add(new(name, Canonical(name, field.Value)));
            }
        Text = Regex.Replace(input, "(?<key>[\\p{L}0-9]+):(?:\"(?<quoted>[^\"]*)\"|(?<plain>[^\\s]+))", match =>
        {
            if (!Aliases.TryGetValue(match.Groups["key"].Value, out var name)) return match.Value;
            var value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
            if (!match.Groups["quoted"].Success && value.StartsWith('"')) throw new ArgumentException("Unterminated quoted filter value.");
            list.Add(new(name, Canonical(name, value)));
            return " ";
        }, RegexOptions.CultureInvariant, Timeout).Trim();
        if (list.Count > 16) throw new ArgumentException("At most16 structured filters are supported.");
        Filters = list;
        Literals = AllLiteralTokens(Text);
        if (Literals.Length > 32) throw new ArgumentException("At most32 literal identifiers are supported per query.");
    }

    private static string[] AllLiteralTokens(string value) => Tokens.Matches(SearchText.Normalize(value)).Select(m => m.Value)
        .Where(t => t.Any(char.IsDigit))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal static string[] LiteralTokens(string value) => AllLiteralTokens(value).Take(32).ToArray();
    internal static long LiteralMask(string? text, string? path, string[] literals)
    {
        var tokens = AllLiteralTokens(text ?? "").Concat(PathLiterals(path ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long mask = 0;
        for (int i = 0; i < literals.Length; i++) if (tokens.Contains(literals[i])) mask |= 1L << i;
        return mask;
    }
    private static IEnumerable<string> PathLiterals(string path) => FolderLiterals(path).Concat(AllLiteralTokens(Path.GetFileNameWithoutExtension(path)));
    private static IEnumerable<string> FolderLiterals(string path) => (Path.GetDirectoryName(path) ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SelectMany(AllLiteralTokens);
    internal bool NameLiteralsMatch(string path) => Literals.All(AllLiteralTokens(Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);
    internal bool FolderLiteralsMatch(string path) => Literals.All(FolderLiterals(path).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);

    internal static string? HeaderField(string label)
    {
        var key = SearchText.Normalize(label).Trim().ToLowerInvariant();
        key = Regex.Replace(key, @"\([^)]*\)|\s+|[_:：*]", "", RegexOptions.CultureInvariant, Timeout);
        return key switch
        {
            "项目编号" or "项目编码" or "项目代码" or "project" or "projectid" or "projectcode" or "projectnumber" => "project",
            "金额" or "总价" or "合同额" or "amount" => "amount",
            "日期" or "date" => "date", "产品版本" or "版本" or "version" or "productversion" => "version",
            "型号" or "产品型号" or "model" => "model", _ => null
        };
    }

    internal static IReadOnlyList<SearchField> FieldsFromText(string text)
    {
        var normalized = SearchText.Normalize(text);
        var fields = new List<SearchField>();
        fields.AddRange(LiteralTokens(normalized).Where(t => t.Length <= 256).Select(t => new SearchField("literal", t)));
        foreach (Match m in Regex.Matches(normalized, @"(?:项目(?:编码|编号|代码)|project)\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9_./-]{1,127})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) fields.Add(new("project", m.Groups[1].Value));
        foreach (Match m in Regex.Matches(normalized, @"(?:金额|合同额|总价|amount)\s*[:：]?\s*(?:人民币|RMB|CNY|[￥¥$])?\s*([0-9][0-9,]*(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) fields.Add(new("amount", m.Groups[1].Value));
        foreach (Match m in Regex.Matches(normalized, @"(?:版本|version)\s*[:：]?\s*(v?[0-9]+(?:\.[0-9A-Za-z-]+)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) fields.Add(new("version", m.Groups[1].Value));
        foreach (Match m in Regex.Matches(normalized, @"(?:型号|model)\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9_./-]{1,127})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) fields.Add(new("model", m.Groups[1].Value));
        foreach (Match m in Regex.Matches(normalized, @"[12][0-9]{3}(?:-[0-9]{1,2}-[0-9]{1,2}|年[0-9]{1,2}月[0-9]{1,2}日)", RegexOptions.CultureInvariant, Timeout))
            if (TryDate(m.Value, out var date)) fields.Add(new("date", date));
        return fields.Distinct().Take(64).ToArray();
    }

    internal bool Matches(string path, string? info, SourceAnchor? source, string? excerpt = null, bool checkLiterals = true)
    {
        var fields = source?.Fields ?? (excerpt is null ? [] : FieldsFromText(excerpt));
        foreach (var literal in checkLiterals ? Literals : [])
        {
            bool exact = fields.Any(f => (f.Name is "literal" or "project" or "model" or "version") && Equal(f.Value, literal))
                || PathLiterals(path).Contains(literal, StringComparer.OrdinalIgnoreCase);
            if (!exact && excerpt is not null) exact = AllLiteralTokens(excerpt).Contains(literal, StringComparer.OrdinalIgnoreCase);
            if (!exact) return false;
        }
        foreach (var filter in Filters)
        {
            bool match = filter.Name switch
            {
                "name" => SearchText.Normalize(Path.GetFileName(path)).Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
                "folder" => SearchText.Normalize(Path.GetDirectoryName(path) ?? "").Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
                "folder1" or "folder2" or "folder3" => MatchFolderLevel(path, info, filter),
                "sheet" => source?.Location.Sheet is { } sheet && Equal(sheet, filter.Value),
                "cell" => source?.Location.Range is { } range && Intersects(range, filter.Value),
                _ => fields.Any(f => f.Name == filter.Name && ValueEqual(filter.Name, f.Value, filter.Value))
            };
            if (!match) return false;
        }
        return true;
    }

    private static bool MatchFolderLevel(string path, string? info, SearchField filter)
    {
        if (info is null) return false;
        using var doc = JsonDocument.Parse(info);
        if (!doc.RootElement.TryGetProperty("indexRoot", out var property) || property.GetString() is not { } root) return false;
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int level = filter.Name[^1] - '1';
        return level < parts.Length && Equal(parts[level], filter.Value);
    }
    private static bool Equal(string a, string b) => SearchText.Normalize(a).Equals(SearchText.Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static bool ValueEqual(string name, string a, string b)
    {
        if (name == "amount") return TryAmount(a, out var number) && TryAmount(b, out var expected) && number == expected;
        if (name == "date") return TryDate(a, out var date) && date == b;
        return Equal(a, b);
    }
    private static string Canonical(string name, string value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Contains('\0')) throw new ArgumentException("Filter value is empty, NUL or exceeds256 characters.");
        value = SearchText.Normalize(value).Trim();
        if (name == "amount") return TryAmount(value, out var number) ? number.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Amount must be an invariant decimal; no unit guessing.");
        if (name == "date") return TryDate(value, out var date) ? date : throw new ArgumentException("Date must be a valid yyyy-MM-dd or yyyy年M月d日.");
        if (name == "cell" && !TryRange(value, out _)) throw new ArgumentException("Cell filter must be an A1 or A1:C5 range.");
        return value;
    }
    private static bool TryAmount(string value, out decimal amount)
    {
        value = SearchText.Normalize(value).Trim();
        foreach (var prefix in new[] { "人民币", "RMB", "CNY", "￥", "¥", "$" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { value = value[prefix.Length..].Trim(); break; }
        if (!Regex.IsMatch(value, @"^-?(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?$", RegexOptions.CultureInvariant, Timeout)) { amount = 0; return false; }
        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out amount);
    }
    private static bool TryDate(string value, out string canonical)
    {
        bool valid = DateOnly.TryParseExact(SearchText.Normalize(value), ["yyyy-MM-dd", "yyyy-M-d", "yyyy年M月d日"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        canonical = valid ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        return valid;
    }
    private static bool TryRange(string value, out (int X1, int Y1, int X2, int Y2) range)
    {
        range = default;
        var m = Regex.Match(value.ToUpperInvariant(), @"^([A-Z]{1,3})([1-9][0-9]{0,6})(?::([A-Z]{1,3})([1-9][0-9]{0,6}))?$", RegexOptions.CultureInvariant, Timeout);
        if (!m.Success) return false;
        int Column(string name) => name.Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
        int x1 = Column(m.Groups[1].Value), y1 = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int x2 = m.Groups[3].Success ? Column(m.Groups[3].Value) : x1, y2 = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : y1;
        range = (x1, y1, x2, y2);
        return x1 <= x2 && y1 <= y2 && x2 <= 16384 && y2 <= 1048576;
    }
    private static bool Intersects(string a, string b) => TryRange(a, out var x) && TryRange(b, out var y)
        && x.X1 <= y.X2 && y.X1 <= x.X2 && x.Y1 <= y.Y2 && y.Y1 <= x.Y2;
}
