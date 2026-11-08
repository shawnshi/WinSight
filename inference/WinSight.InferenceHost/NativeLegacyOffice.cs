using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinSight.Infrastructure.Extraction;

namespace WinSight.InferenceHost;

internal sealed record LegacyOfficeReply(int Protocol, string State, string? Detail, string Format, string Text);

internal static partial class NativeMediaCommand
{
    /// <summary>
    /// One-shot, isolated reader for .doc/.xls/.ppt: the parent owns the job object, time limit and output bound; this
    /// process opens the pinned file, parses bounded OLE structures and prints a single JSON line.
    /// </summary>
    internal static async Task<LegacyOfficeReply> RunLegacyOfficeRequestFileAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ValidateLocalPath(requestPath, GetDriveType);
        using var input = OpenPinnedFile(path, MaxRequestBytes);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Legacy Office request must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Name is not ("inputPath" or "kind") || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("Unknown or duplicate legacy Office request member: " + property.Name);
        if (fields.Count != 2 || !fields.TryGetValue("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "legacy-office"
            || !fields.TryGetValue("inputPath", out var sourceValue) || sourceValue.ValueKind != JsonValueKind.String)
            throw new JsonException("Legacy Office request requires inputPath and kind=legacy-office.");
        var sourcePath = ValidateLocalPath(sourceValue.GetString()!, GetDriveType);
        var extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (extension is not ("doc" or "dot" or "xls" or "xlt" or "ppt" or "pot" or "pps"))
            throw new ArgumentException("Unsupported legacy Office extension.");
        using var source = OpenPinnedFile(sourcePath, MaxInputBytes);
        var data = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(data, cancellationToken);
        var result = LegacyOffice.Extract(data, extension, cancellationToken);
        if (result.State == "skipped" && string.IsNullOrWhiteSpace(result.Text)
            && result.Detail?.Contains("encrypted", StringComparison.OrdinalIgnoreCase) != true
            && result.Detail?.Contains("password", StringComparison.OrdinalIgnoreCase) != true)
        {
            var ifilterText = TryExtractViaWindowsIFilter(sourcePath, 4 * 1024 * 1024, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ifilterText))
            {
                return new LegacyOfficeReply(1, "done", null, result.Format, ifilterText);
            }
        }
        return new LegacyOfficeReply(1, result.State, result.Detail, result.Format, result.Text);
    }

    [DllImport("query.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, [MarshalAs(UnmanagedType.Interface)] out IFilter? ppIUnk);

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig] int Init(uint grfFlags, uint cAttributes, IntPtr aAttributes, out uint pdwFlags);
        [PreserveSig] int GetChunk(out STAT_CHUNK pStat);
        [PreserveSig] int GetText(ref uint pcwcBuffer, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[] awcBuffer);
        [PreserveSig] int GetValue(out IntPtr ppPropValue);
        [PreserveSig] int BindRegion(IntPtr origPos, ref Guid riid, out IntPtr ppunk);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STAT_CHUNK
    {
        public uint idChunk;
        public uint breakType;
        public uint flags;
        public uint locale;
        public Guid attributeGuid;
        public uint attributeKind;
        public IntPtr attributeName;
        public uint idChunkSource;
        public uint cwcStartSource;
        public uint cwcLenSource;
    }

    private static string? TryExtractViaWindowsIFilter(string filePath, int maxChars, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IFilter? filter = null;
        try
        {
            if (LoadIFilter(filePath, IntPtr.Zero, out filter) != 0 || filter is null) return null;
            // IFILTER_INIT_CANON_PARAGRAPHS (1) | IFILTER_INIT_APPLY_INDEX_ATTRIBUTES (16)
            if (filter.Init(1u | 16u, 0, IntPtr.Zero, out _) != 0) return null;
            var sb = new StringBuilder();
            var buf = new char[8192];
            while (filter.GetChunk(out var chunk) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if ((chunk.flags & 1u /* CHUNK_TEXT */) == 0) continue;
                while (true)
                {
                    uint count = (uint)buf.Length;
                    int hr = filter.GetText(ref count, buf);
                    if (hr is not (0 /* S_OK */ or 0x00041790 /* FILTER_S_LAST_TEXT */) || count == 0) break;
                    int toAppend = Math.Min((int)count, maxChars - sb.Length);
                    if (toAppend > 0) sb.Append(buf, 0, toAppend);
                    if (sb.Length >= maxChars || hr == 0x00041790) break;
                }
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                if (sb.Length >= maxChars) break;
            }
            return sb.ToString().Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (filter is not null && Marshal.IsComObject(filter))
            {
                try { Marshal.ReleaseComObject(filter); } catch { }
            }
        }
    }
}
