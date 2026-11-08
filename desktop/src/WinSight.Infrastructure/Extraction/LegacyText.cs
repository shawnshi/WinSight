using System.IO.Compression;
using System.Text;
using System.Xml;

namespace WinSight.Infrastructure.Extraction;

/// <summary>
/// Plain-text recovery for formats that are common in Chinese offices but are not UTF-8 text: GBK/GB18030 files,
/// RTF and OpenDocument text. Every reader is bounded in size and time because it parses untrusted bytes in-process.
/// </summary>
internal static class LegacyText
{
    private static readonly object ProviderGate = new();
    private static bool providerRegistered;

    internal static Encoding? TryGetEncoding(int codePage, bool strict)
    {
        try
        {
            lock (ProviderGate)
            {
                if (!providerRegistered) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); providerRegistered = true; }
            }
            return strict
                ? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : Encoding.GetEncoding(codePage);
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>GB18030 strictly decodes most Latin-1 text that has an accent followed by a letter, so byte structure decides.</summary>
    /// <remarks>
    /// Chinese text is dominated by double-byte characters whose trail byte is also &gt;= 0x80 (GB2312 range). Western text in a
    /// single-byte code page produces mostly ASCII trail bytes. Accept GB18030 only when the high-trail pairs dominate.
    /// </remarks>
    internal static bool LooksLikeGb18030(ReadOnlySpan<byte> sample)
    {
        long high = 0, low = 0;
        for (var i = 0; i < sample.Length;)
        {
            var lead = sample[i];
            if (lead < 0x80) { i++; continue; }
            if (lead is 0x80 or 0xFF) return false;
            if (i + 1 >= sample.Length) break; // A pair cut by the end of the sample says nothing.
            var trail = sample[i + 1];
            if (trail is >= 0x30 and <= 0x39)
            {
                // Four-byte GB18030 (rare characters): lead, digit, lead, digit.
                if (i + 3 >= sample.Length) break;
                if (sample[i + 2] is < 0x81 or > 0xFE || sample[i + 3] is < 0x30 or > 0x39) return false;
                high++;
                i += 4;
                continue;
            }
            if (trail is >= 0x80 and <= 0xFE) high++;
            else if (trail is >= 0x40 and <= 0x7E) low++;
            else return false;
            i += 2;
        }
        return high > 0 && high >= 0.7 * (high + low);
    }
}

internal static class RtfText
{
    private const int MaxDepth = 512;
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "colorschememapping", "latentstyles",
        "datastore", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "fldinst", "private", "mmathPr", "revtbl",
    };

    private struct State
    {
        public bool Skip;
        public int Unicode; // \ucN: fallback characters that follow a \uN escape
    }

    /// <summary>Extracts visible text from an RTF document; non-RTF input yields an empty string.</summary>
    internal static string Extract(string rtf, int maxCharacters, CancellationToken token)
    {
        if (!rtf.AsSpan().TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal)) return string.Empty;
        var output = new StringBuilder();
        var stack = new Stack<State>();
        var state = new State { Unicode = 1 };
        var codePage = 1252;
        var pendingBytes = new List<byte>();
        var skipFallback = 0;
        var firstControlInGroup = false;

        void Flush()
        {
            if (pendingBytes.Count == 0) return;
            var encoding = LegacyText.TryGetEncoding(codePage, strict: false) ?? Encoding.Latin1;
            if (!state.Skip) output.Append(encoding.GetString(pendingBytes.ToArray()));
            pendingBytes.Clear();
        }
        void Emit(char value) { if (!state.Skip) output.Append(value); }

        for (var i = 0; i < rtf.Length && output.Length <= maxCharacters; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    Flush();
                    if (stack.Count >= MaxDepth) throw new InvalidDataException("RTF group nesting exceeds " + MaxDepth + ".");
                    stack.Push(state);
                    firstControlInGroup = true;
                    break;
                case '}':
                    Flush();
                    if (stack.Count > 0) state = stack.Pop();
                    firstControlInGroup = false;
                    break;
                case '\r' or '\n':
                    break;
                case '\\':
                    if (++i >= rtf.Length) break;
                    var next = rtf[i];
                    if (next is '\\' or '{' or '}') { Flush(); if (skipFallback > 0) skipFallback--; else Emit(next); firstControlInGroup = false; break; }
                    if (next == '*') { state.Skip = true; firstControlInGroup = false; break; }
                    if (next == '\'')
                    {
                        if (i + 2 < rtf.Length && Uri.IsHexDigit(rtf[i + 1]) && Uri.IsHexDigit(rtf[i + 2]))
                        {
                            if (skipFallback > 0) skipFallback--;
                            else pendingBytes.Add(Convert.ToByte(rtf.Substring(i + 1, 2), 16));
                            i += 2;
                        }
                        firstControlInGroup = false;
                        break;
                    }
                    if (next == '~') { Flush(); Emit('\u00A0'); break; }
                    if (next == '-' || next == '_') { Flush(); if (next == '_') Emit('-'); break; }
                    if (!char.IsAsciiLetter(next)) { firstControlInGroup = false; break; }
                    var start = i;
                    while (i + 1 < rtf.Length && char.IsAsciiLetter(rtf[i + 1])) i++;
                    var word = rtf.Substring(start, i - start + 1);
                    var sign = 1;
                    if (i + 1 < rtf.Length && rtf[i + 1] == '-') { sign = -1; i++; }
                    var number = 0;
                    var hasNumber = false;
                    while (i + 1 < rtf.Length && char.IsAsciiDigit(rtf[i + 1]) && number < 100_000_000)
                    { number = number * 10 + (rtf[++i] - '0'); hasNumber = true; }
                    if (i + 1 < rtf.Length && rtf[i + 1] == ' ') i++;
                    Flush();
                    if (firstControlInGroup && SkippedDestinations.Contains(word)) state.Skip = true;
                    firstControlInGroup = false;
                    switch (word)
                    {
                        case "ansicpg" when hasNumber: codePage = number; break;
                        case "uc" when hasNumber: state.Unicode = Math.Clamp(number, 0, 8); break;
                        case "u" when hasNumber:
                            var code = sign * number;
                            if (code < 0) code += 65536;
                            if (code is >= 0 and <= 0xFFFF) Emit((char)code);
                            skipFallback = state.Unicode;
                            break;
                        case "par" or "line" or "row" or "sect" or "page": Emit('\n'); break;
                        case "cell": Emit('\t'); break;
                        case "tab": Emit('\t'); break;
                        case "emdash": Emit('\u2014'); break;
                        case "endash": Emit('\u2013'); break;
                        case "lquote": Emit('\u2018'); break;
                        case "rquote": Emit('\u2019'); break;
                        case "ldblquote": Emit('\u201C'); break;
                        case "rdblquote": Emit('\u201D'); break;
                        case "bullet": Emit('\u2022'); break;
                    }
                    break;
                default:
                    if (skipFallback > 0) { skipFallback--; break; }
                    Flush();
                    firstControlInGroup = false;
                    Emit(c);
                    break;
            }
        }
        Flush();
        return output.ToString();
    }
}

internal static class OdfText
{
    private const long MaxExpanded = 64L << 20;
    private const long MaxContentBytes = 32L << 20;

    /// <summary>Reads paragraph text from an OpenDocument text file (<c>.odt</c>) without resolving DTDs or external entities.</summary>
    internal static string Extract(Stream stream, int maxCharacters, CancellationToken token)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 10_000) throw new InvalidDataException("OpenDocument package exceeds 10000 entries.");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        ZipArchiveEntry? content = null;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpanded || !names.Add(entry.FullName) || entry.FullName.Length > 1024
                || entry.FullName.Split('/').Contains("..") || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/'))
                throw new InvalidDataException("OpenDocument package has ambiguous entries or exceeds 64 MiB expanded.");
            if (entry.FullName == "content.xml") content = entry;
        }
        if (content is null) throw new InvalidDataException("OpenDocument package has no content.xml.");
        if (content.Length > MaxContentBytes) throw new InvalidDataException("OpenDocument content.xml exceeds 32 MiB.");
        var mime = zip.GetEntry("mimetype");
        if (mime is not null)
        {
            using var reader = new StreamReader(mime.Open(), Encoding.ASCII);
            var value = reader.ReadToEnd();
            if (!value.StartsWith("application/vnd.oasis.opendocument.text", StringComparison.Ordinal))
                throw new InvalidDataException("Not an OpenDocument text file.");
        }
        var output = new StringBuilder();
        using var xml = content.Open();
        using var xmlReader = XmlReader.Create(xml, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxContentBytes });
        long nodes = 0;
        var skipDepth = -1;
        while (xmlReader.Read() && output.Length <= maxCharacters)
        {
            if (++nodes > 2_000_000 || xmlReader.Depth > 256) throw new InvalidDataException("OpenDocument XML node/depth limit exceeded.");
            if ((nodes & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            if (skipDepth >= 0)
            {
                if (xmlReader.NodeType == XmlNodeType.EndElement && xmlReader.Depth <= skipDepth) skipDepth = -1;
                continue;
            }
            switch (xmlReader.NodeType)
            {
                case XmlNodeType.Element when xmlReader.LocalName is "tracked-changes" or "sequence-decls" or "annotation" && !xmlReader.IsEmptyElement:
                    skipDepth = xmlReader.Depth;
                    break;
                case XmlNodeType.Element when xmlReader.LocalName == "tab": output.Append('\t'); break;
                case XmlNodeType.Element when xmlReader.LocalName == "line-break": output.Append('\n'); break;
                case XmlNodeType.Element when xmlReader.LocalName == "s":
                    var count = int.TryParse(xmlReader.GetAttribute("c", "urn:oasis:names:tc:opendocument:xmlns:text:1.0"), out var spaces) ? Math.Clamp(spaces, 1, 64) : 1;
                    output.Append(' ', count);
                    break;
                case XmlNodeType.EndElement when xmlReader.LocalName is "p" or "h" or "list-item" or "table-row":
                    output.Append('\n');
                    break;
                case XmlNodeType.EndElement when xmlReader.LocalName == "table-cell":
                    output.Append('\t');
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA:
                    output.Append(xmlReader.Value);
                    break;
            }
        }
        return output.ToString();
    }
}
