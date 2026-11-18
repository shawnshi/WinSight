using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.InferenceHost;

internal sealed class NativeTokenizer : SafeHandleZeroOrMinusOneIsInvalid
{
    // Algorithm source identity is separate from the unchanged ABI1 / wrapper package0.1.0.
    internal const string RuntimeIdentity = "tokenizers-git-v0.23.3;rust=0.23.2;rev=301277072263fb3830eb66eec7e47adadf76f1dc;abi=1;special=true";
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
        int byteCount = StrictUtf8.GetByteCount(text);
        // Even empty text needs a valid non-null address; its semantic length remains zero.
        var rentedUtf8 = ArrayPool<byte>.Shared.Rent(Math.Max(1, byteCount));
        nint ids = 0;
        nuint count = 0;
        try
        {
            if (byteCount > 0)
            {
                int written = StrictUtf8.GetBytes(text.AsSpan(), rentedUtf8.AsSpan());
                if (written != byteCount) throw new InvalidDataException("UTF-8 encoding length mismatch");
            }
            var status = ws_tokenizer_encode(this, rentedUtf8, (nuint)byteCount,
                addSpecialTokens ? (byte)1 : (byte)0, out ids, out count, out var error);
            Check(status, error);
            if (count > 1024 * 1024 || (count != 0 && ids == 0))
                throw new InvalidDataException("Invalid native tokenizer result");
            int len = (int)count;
            if (len == 0) return [];
            var result = new long[len];
            var rentedIds = ArrayPool<int>.Shared.Rent(len);
            try
            {
                Marshal.Copy(ids, rentedIds, 0, len);
                for (int i = 0; i < len; i++)
                    result[i] = (uint)rentedIds[i];
                return result;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rentedIds);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedUtf8);
            ws_tokenizer_ids_free(ids, count);
        }
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
