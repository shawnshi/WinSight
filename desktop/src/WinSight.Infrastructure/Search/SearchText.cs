using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinSight.Core;
using System.Text.RegularExpressions;

namespace WinSight.Infrastructure.Search;

internal static class SearchText
{
    private static readonly HashSet<string> Stopwords = new(("a an the of in on at to for with and or my me that this these those from about where what which who when is was are were be it its some any find show file files photo photos picture image images screenshot screenshots video videos pdf document documents recording " +
        "এবং ও এই সেই একটি আমার থেকে জন্য সাথে মধ্যে কোথায় কোন কী কি যে ছবি স্ক্রিনশট ভিডিও ফাইল পিডিএফ " +
        "في من على الى عن مع هذا هذه ذلك التي الذي اين ما صورة صور لقطة شاشة فيديو ملف مستند").Split(' '), StringComparer.Ordinal);

    internal static readonly IReadOnlyDictionary<string, string> KindWords = new Dictionary<string, string>
    {
        ["screenshot"] = "screenshot", ["screenshots"] = "screenshot", ["photo"] = "image", ["photos"] = "image",
        ["picture"] = "image", ["pictures"] = "image", ["image"] = "image", ["images"] = "image",
        ["pdf"] = "pdf", ["pdfs"] = "pdf", ["paper"] = "pdf", ["papers"] = "pdf", ["document"] = "doc",
        ["documents"] = "doc", ["note"] = "doc", ["notes"] = "doc", ["video"] = "video", ["videos"] = "video",
        ["clip"] = "video", ["movie"] = "video", ["audio"] = "audio", ["song"] = "audio", ["podcast"] = "audio",
        ["voice"] = "audio", ["sound"] = "audio", ["স্ক্রিনশট"] = "screenshot", ["ছবি"] = "image",
        ["পিডিএফ"] = "pdf", ["নোট"] = "doc", ["ভিডিও"] = "video", ["অডিও"] = "audio", ["গান"] = "audio",
        ["রেকর্ডিং"] = "audio", ["لقطة"] = "screenshot", ["صورة"] = "image", ["صور"] = "image",
        ["مستند"] = "doc", ["ملاحظات"] = "doc", ["فيديو"] = "video", ["مقطع"] = "video",
        ["صوت"] = "audio", ["تسجيل"] = "audio", ["اغنية"] = "audio"
    };

    internal static string Normalize(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC);
        if (OperatingSystem.IsWindows() && ContainsCjk(text))
            text = SimplifyChinese(text);
        var output = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Value;
            if (n == 0x200B) output.Append(' ');
            else if (n is 0x00AD or 0x061C or >= 0x200C and <= 0x200F or >= 0x202A and <= 0x202E or
                     >= 0x2060 and <= 0x2064 or >= 0x2066 and <= 0x2069 or 0xFEFF or 0x0640 or
                     >= 0x064B and <= 0x065F or 0x0670) continue;
            else if (n is 0x0622 or 0x0623 or 0x0625 or 0x0671) output.Append('\u0627');
            else output.Append(rune.ToString());
        }
        return output.ToString();
    }

    private static string SimplifyChinese(string value)
    {
        const uint simplifiedChinese = 0x02000000;
        int length = LCMapStringEx("zh-CN", simplifiedChinese, value, value.Length, null, 0, 0, 0, 0);
        if (length <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Chinese normalization length failed.");
        var buffer = new char[length];
        int written = LCMapStringEx("zh-CN", simplifiedChinese, value, value.Length, buffer, length, 0, 0, 0);
        if (written <= 0 || written > length) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Chinese normalization failed.");
        return new string(buffer, 0, written);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int LCMapStringEx(string localeName, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, nint version, nint reserved, nint sortHandle);

    internal static string[] Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool? previousCjk = null;
        foreach (var rune in Normalize(text).ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                bool cjk = ContainsCjk(rune.ToString());
                if (Rune.IsLetterOrDigit(rune) && previousCjk is { } previous && previous != cjk && word.Length > 0)
                { words.Add(word.ToString()); word.Clear(); }
                word.Append(rune.ToString());
                if (Rune.IsLetterOrDigit(rune)) previousCjk = cjk;
            }
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); previousCjk = null; }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words.ToArray();
    }

    internal static string SourceUnit(IndexSegment segment) => segment.Source is { } source
        ? source.Location.Type is "text" or "image" ? "file" : JsonSerializer.Serialize(source.Location)
        : segment.Kind == "name" || segment.Kind == "chunk" && segment.Location is null ? "file"
        : $"{segment.Kind}:{segment.Location}:{segment.LocationEnd}";

    internal static bool ContainsCjk(string text) => text.EnumerateRunes().Any(r =>
        r.Value is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af
            or >= 0x3040 and <= 0x30ff or >= 0xac00 and <= 0xd7af);

    internal static string[] Terms(string query)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Words(query).Where(w => (w.EnumerateRunes().Count() >= 2 || ContainsCjk(w) || w.All(char.IsDigit)) && !Stopwords.Contains(w)))
        {
            if (seen.Add(word)) result.Add(word);
            if (ContainsCjk(word))
            {
                var runes = word.EnumerateRunes().ToArray();
                for (int i = 0; i + 1 < runes.Length && result.Count < 64; i++)
                {
                    var term = runes[i].ToString() + runes[i + 1];
                    if (seen.Add(term)) result.Add(term);
                }
            }
            if (result.Count >= 64) break;
        }
        return result.ToArray();
    }

    internal static bool Inflects(string word) => word.EnumerateRunes().Any(r => r.Value is >= 0x0900 and <= 0x0DFF or >= 0x0600 and <= 0x06FF);
    internal static string Quote(string word) => "\"" + word.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    internal static bool IsLookup(string[] terms) => terms.Length > 0 && terms.All(t => t.Any(char.IsDigit) && (t.Any(char.IsLetter) || t.Length >= 4));

    internal static string[]? Identifier(string query)
    {
        var name = query.Trim();
        if (name.EndsWith("()", StringComparison.Ordinal)) name = name[..^2];
        if (!Regex.IsMatch(name, @"^[A-Za-z_$][A-Za-z0-9_$]*(?:(?:\.|::|->|#)[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.CultureInvariant)) return null;
        if (!(name.Skip(1).Any(char.IsUpper) && name.Any(char.IsLower)) && !name.Any(c => "_.:#$".Contains(c) || char.IsDigit(c))) return null;
        return Words(name);
    }

    internal static HashSet<string> KindHints(string query)
    {
        var hints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Words(query))
        {
            if (KindWords.TryGetValue(word, out var kind)) hints.Add(kind);
            else foreach (var entry in KindWords)
                if (Inflects(entry.Key) && entry.Key.EnumerateRunes().Count() >= 3 && word.StartsWith(entry.Key, StringComparison.Ordinal)) hints.Add(entry.Value);
        }
        return hints;
    }

    /// <summary>
    /// Tokens for the segmented FTS table. Uses the shared native segmenter so indexing and querying agree;
    /// a missing segmenter is a hard error rather than an empty token set.
    /// </summary>
    internal static string SegmentedTokens(string text)
    {
        var tokens = NativeSegmenter.Segment(text);
        return string.Join(' ', tokens);
    }

    /// <summary>True when the segmented lexical channel can be used for this process.</summary>
    internal static bool SegmentationAvailable => NativeSegmenter.IsAvailable;
}
