using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;


namespace WinSight.Infrastructure.Search;

/// <summary>Raised when the native Chinese segmenter cannot be loaded or fails. Lexical search degrades
/// visibly instead of silently returning nothing.</summary>
public sealed class SegmenterUnavailableException : Exception
{
    public SegmenterUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Managed wrapper over winsight_segmenter.dll (jieba-rs). Indexing and querying must both go through this
/// so the lexical rules are identical on both sides.
/// </summary>
public static class NativeSegmenter
{
    private const string Library = "winsight_segmenter";
    private static readonly Lazy<IntPtr> Handle = new(() => Create(null, Array.Empty<string>()));
    private static readonly object Gate = new();

    static NativeSegmenter() => NativeLibrary.SetDllImportResolver(typeof(NativeSegmenter).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", "native", Library + ".dll"),
            Path.Combine(AppContext.BaseDirectory, Library + ".dll")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
        return 0;
    }

    public static bool IsAvailable
    {
        get
        {
            try { _ = Handle.Value; return true; }
            catch (Exception error) when (error is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or SegmenterUnavailableException) { return false; }
        }
    }

    /// <summary>
    /// Query-mode segmentation (precise, HMM on). Order-stable and NOT de-duplicated. The text is used as given;
    /// call <see cref="SegmentQuery"/> to apply the shared lexical normalization first.
    /// Throws when the native library is unusable.
    /// </summary>
    public static IReadOnlyList<string> Segment(string text) => SegmentCore(text, QueryMode);

    /// <summary>Normalizes like the lexical index, then segments in query mode. Throws when the library is unusable.</summary>
    public static IReadOnlyList<string> SegmentQuery(string text) => SegmentCore(SearchText.Normalize(text ?? ""), QueryMode);

    /// <summary>
    /// Index-mode segmentation (jieba search mode: whole words plus their sub-words, HMM on). Duplicates are kept so
    /// BM25 sees real term frequency. The text must already be normalized by the caller.
    /// </summary>
    internal static IReadOnlyList<string> SegmentForIndex(string normalizedText) => SegmentCore(normalizedText, IndexMode);

    private const uint QueryMode = 0, IndexMode = 1;

    private static IReadOnlyList<string> SegmentCore(string text, uint mode)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var handle = LoadHandle();
        var bytes = Encoding.UTF8.GetBytes(text);
        var status = ws_segment_text_ex(handle, bytes, (nuint)bytes.Length, mode, out var tokens, out var length);
        if (status != 0)
        {
            if (tokens != IntPtr.Zero) ws_segment_free_tokens(tokens, length);
            throw new SegmenterUnavailableException($"Segmenter rejected input (status {status}).");
        }
        try
        {
            if (tokens == IntPtr.Zero || length == 0) return Array.Empty<string>();
            var payload = new byte[(int)length];
            Marshal.Copy(tokens, payload, 0, payload.Length);
            return Encoding.UTF8.GetString(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            ws_segment_free_tokens(tokens, length);
        }
    }

    /// <summary>Stable fingerprint of the lexicon configuration, stored with the index metadata.</summary>
    public static string Fingerprint(IEnumerable<string> userWords)
    {
        var ordered = userWords.Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToArray();
        var payload = Encoding.UTF8.GetBytes("jieba-rs=0.11.0|schema=3|hmm=on|index=search|tf=kept|norm=shared|user=" + string.Join('\u0001', ordered));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static IntPtr LoadHandle()
    {
        try
        {
            lock (Gate) { return Handle.Value; }
        }
        catch (Exception error) when (error is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or SegmenterUnavailableException)
        {
            throw new SegmenterUnavailableException("The Chinese segmenter library is not available.", error);
        }
    }

    private static IntPtr Create(string? baseDictionary, IReadOnlyList<string> userWords)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { @base = baseDictionary, user = userWords }));
        IntPtr handle = IntPtr.Zero;
        var status = ws_segment_new(payload, (nuint)payload.Length, out handle);
        if (status != 0 || handle == IntPtr.Zero) throw new SegmenterUnavailableException($"Segmenter failed to initialise (status {status}).");
        return handle;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ws_segment_new(byte[] dictJsonUtf8, nuint length, out IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ws_segment_text_ex(IntPtr handle, byte[] textUtf8, nuint length, uint mode, out IntPtr tokens, out nuint tokenLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ws_segment_free_tokens(IntPtr tokens, nuint length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ws_segment_free(IntPtr handle);
}
