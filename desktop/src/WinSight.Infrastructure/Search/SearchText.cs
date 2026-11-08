using System.Collections.Concurrent;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinSight.Core;
using System.Text.RegularExpressions;

namespace WinSight.Infrastructure.Search;

internal static partial class SearchText
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

    private static readonly ConcurrentDictionary<string, string> NormalizedShortCache = new(StringComparer.Ordinal);

    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= 256 && NormalizedShortCache.TryGetValue(text, out var cached))
            return cached;
        var original = text;
        text = text.Normalize(NormalizationForm.FormKC);
        if (OperatingSystem.IsWindows() && ContainsCjk(text))
            text = SimplifyChinese(text);

        bool needsScan = false;
        foreach (char c in text)
        {
            if (c is '\u00AD' or >= '\u061C' and <= '\u0671' or >= '\u200B' and <= '\u2069' or '\uFEFF')
            {
                needsScan = true;
                break;
            }
        }

        string result;
        if (!needsScan)
        {
            result = text;
        }
        else
        {
            var output = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                int n = rune.Value;
                if (n == 0x200B) output.Append(' ');
                else if (n is 0x00AD or 0x061C or >= 0x200C and <= 0x200F or >= 0x202A and <= 0x202E or
                         >= 0x2060 and <= 0x2064 or >= 0x2066 and <= 0x2069 or 0xFEFF or 0x0640 or
                         >= 0x064B and <= 0x065F or 0x0670) continue;
                else if (n is 0x0622 or 0x0623 or 0x0625 or 0x0671) output.Append('\u0627');
                else if (rune.IsBmp) output.Append((char)n);
                else output.Append(rune.ToString());
            }
            result = output.ToString();
        }

        if (original.Length <= 256)
        {
            if (NormalizedShortCache.Count > 100_000) NormalizedShortCache.Clear();
            NormalizedShortCache[original] = result;
        }
        return result;
    }

    private static unsafe string SimplifyChinese(string value)
    {
        const uint simplifiedChinese = 0x02000000;
        if (value.Length <= 256)
        {
            Span<char> stackBuf = stackalloc char[value.Length];
            fixed (char* srcPtr = value)
            fixed (char* dstPtr = stackBuf)
            {
                int w = LCMapStringEx("zh-CN", simplifiedChinese, srcPtr, value.Length, dstPtr, stackBuf.Length, 0, 0, 0);
                if (w > 0 && w <= stackBuf.Length)
                    return new string(stackBuf[..w]);
            }
        }
        fixed (char* srcPtr = value)
        {
            int length = LCMapStringEx("zh-CN", simplifiedChinese, srcPtr, value.Length, null, 0, 0, 0, 0);
            if (length <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Chinese normalization length failed.");
            var buffer = new char[length];
            fixed (char* dstPtr = buffer)
            {
                int written = LCMapStringEx("zh-CN", simplifiedChinese, srcPtr, value.Length, dstPtr, length, 0, 0, 0);
                if (written <= 0 || written > length) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Chinese normalization failed.");
                return new string(buffer, 0, written);
            }
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int LCMapStringEx(string localeName, uint flags, char* source, int sourceLength,
        char* destination, int destinationLength, nint version, nint reserved, nint sortHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint hThread, int threadInformationClass, ref ThreadPowerThrottlingState threadInformation, uint threadInformationSize);

    /// <summary>
    /// Guides the Windows 11 Thread Director on hybrid CPUs (e.g. Zen 5 + Zen 5c / P-Core + E-Core):
    /// highPerformance=true pins interactive search/manual indexing to P-Cores (HighQoS);
    /// highPerformance=false guides background idle maintenance to E-Cores (EcoQoS).
    /// </summary>
    internal static void SetCurrentThreadQoS(bool highPerformance)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var state = new ThreadPowerThrottlingState
            {
                Version = 1,
                ControlMask = 0x1, // THREAD_POWER_THROTTLING_EXECUTION_SPEED
                StateMask = highPerformance ? 0x0u : 0x1u
            };
            SetThreadInformation(GetCurrentThread(), 4 /* ThreadPowerThrottling */, ref state, (uint)Marshal.SizeOf<ThreadPowerThrottlingState>());
        }
        catch { }
    }

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
                bool cjk = IsCjkRune(rune.Value);
                if (Rune.IsLetterOrDigit(rune) && previousCjk is { } previous && previous != cjk && word.Length > 0)
                { words.Add(word.ToString()); word.Clear(); }
                if (rune.IsBmp) word.Append((char)rune.Value);
                else word.Append(rune.ToString());
                if (Rune.IsLetterOrDigit(rune)) previousCjk = cjk;
            }
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); previousCjk = null; }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words.ToArray();
    }

    // Unit keys are compared for every cached vector on every query; computing them (a JSON serialization for
    // page/sheet sources) once per segment object removes an allocation-heavy pass from the hot path.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IndexSegment, string> UnitKeys = new();
    internal static string SourceUnit(IndexSegment segment) => UnitKeys.GetValue(segment, ComputeSourceUnit);

    internal static string ComputeSourceUnit(IndexSegment segment)
    {
        if (segment.Source is { } source)
        {
            var loc = source.Location;
            return loc.Type is "text" or "image"
                ? "file"
                : $"{loc.Type}:{loc.Page}:{loc.Paragraph}:{loc.Sheet}:{loc.Range}:{loc.Part}";
        }
        return segment.Kind is "name" or "chunk"
            ? "file"
            : $"{segment.Kind}:{segment.Location}:{segment.LocationEnd}";
    }

    private static bool IsCjkRune(int v) =>
        v is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af
            or >= 0x3040 and <= 0x30ff or >= 0xac00 and <= 0xd7af;

    internal static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if (c >= 0x3040 && (c <= 0x9fff || (c >= 0xac00 && c <= 0xd7af) || (c >= 0xf900 && c <= 0xfaff) || char.IsSurrogate(c)))
            {
                if (!char.IsSurrogate(c)) return true;
                return text.EnumerateRunes().Any(r => IsCjkRune(r.Value));
            }
        }
        return false;
    }

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
        // Same normalization as the literal index, index-mode segmentation, duplicates kept (term frequency).
        var tokens = NativeSegmenter.SegmentForIndex(Normalize(text));
        return string.Join(' ', tokens);
    }

    // Single-character CJK function words. They match almost every Chinese unit, so counting them as query terms
    // only dilutes coverage and drops units that contain the real content word.
    private static readonly HashSet<string> CjkFunctionWords = new((
        "的 了 是 在 和 与 及 或 我 你 他 她 它 们 这 那 有 为 以 就 都 也 要 从 到 对 把 被 让 给 吗 呢 吧 啊 哦 之 其 而 且 等 并 但 若 则 么").Split(' '), StringComparer.Ordinal);

    /// <summary>
    /// Lexical query terms split by index: <c>Latin</c> terms go to the literal (porter) index, <c>Cjk</c> tokens come from
    /// the shared segmenter and go to the segmented index. Coverage is computed over the union.
    /// When the segmenter is unavailable the whole CJK words are returned as literal terms (degraded, never silent).
    /// </summary>
    internal static (string[] Latin, string[] Cjk) LexicalTerms(string query)
    {
        var latin = new List<string>();
        var cjk = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Words(query).Where(w => (w.EnumerateRunes().Count() >= 2 || ContainsCjk(w) || w.All(char.IsDigit)) && !Stopwords.Contains(w)))
        {
            if (latin.Count + cjk.Count >= 64) break;
            if (!ContainsCjk(word)) { if (seen.Add(word)) latin.Add(word); continue; }
            IReadOnlyList<string> tokens;
            try { tokens = NativeSegmenter.Segment(word); }
            catch (SegmenterUnavailableException) { if (seen.Add(word)) latin.Add(word); continue; }
            var content = tokens.Where(t => t.Length != 0 && !(CjkFunctionWords.Contains(t))).ToArray();
            if (tokens.Count > 1 && seen.Add(word)) cjk.Add(word);
            foreach (var token in content.Length != 0 ? content : tokens)
                if (token.Length != 0 && seen.Add(token)) cjk.Add(token);
        }
        return (latin.ToArray(), cjk.ToArray());
    }

    /// <summary>True when the segmented lexical channel can be used for this process.</summary>
    internal static bool SegmentationAvailable => NativeSegmenter.IsAvailable;
}
