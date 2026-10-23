using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.InferenceHost;

internal sealed class NativeTokenizer : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Library = "winsight_tokenizer";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static NativeTokenizer() => NativeLibrary.SetDllImportResolver(typeof(NativeTokenizer).Assembly, Resolve);
    private NativeTokenizer() : base(true) { }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes",
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", "native", "winsight_tokenizer.dll")) : 0;

    internal static NativeTokenizer Load(byte[] json)
    {
        var status = ws_tokenizer_new(json, (nuint)json.Length, out var pointer, out var error);
        Check(status, error);
        if (pointer == 0) throw new InvalidDataException("Native tokenizer returned a null handle");
        var tokenizer = new NativeTokenizer();
        tokenizer.SetHandle(pointer);
        return tokenizer;
    }

    internal long[] Encode(string text, bool addSpecialTokens = true)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var bytes = StrictUtf8.GetBytes(text);
        var byteCount = bytes.Length;
        // Even empty text needs a valid address; its semantic length remains zero.
        if (byteCount == 0) bytes = [0];
        var status = ws_tokenizer_encode(this, bytes, (nuint)byteCount,
            addSpecialTokens ? (byte)1 : (byte)0, out var ids, out var count, out var error);
        try
        {
            Check(status, error);
            if (count > 1024 * 1024 || (count != 0 && ids == 0))
                throw new InvalidDataException("Invalid native tokenizer result");
            var signed = new int[(int)count];
            Marshal.Copy(ids, signed, 0, signed.Length);
            return signed.Select(id => (long)(uint)id).ToArray();
        }
        finally { ws_tokenizer_ids_free(ids, count); }
    }

    private static void Check(int status, nint error)
    {
        try
        {
            if (status != 0) throw new InvalidDataException($"Native tokenizer ({status}): {Marshal.PtrToStringUTF8(error) ?? "missing error"}");
        }
        finally { ws_tokenizer_error_free(error); }
    }
    protected override bool ReleaseHandle() { ws_tokenizer_free(handle); return true; }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ws_tokenizer_new(byte[] json, nuint len, out nint handle, out nint error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ws_tokenizer_encode(NativeTokenizer handle, byte[] text, nuint len, byte special,
        out nint ids, out nuint count, out nint error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ws_tokenizer_free(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ws_tokenizer_ids_free(nint ids, nuint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ws_tokenizer_error_free(nint error);
}
