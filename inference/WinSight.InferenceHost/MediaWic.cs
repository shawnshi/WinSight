using System.Runtime.InteropServices;

namespace WinSight.InferenceHost;

internal sealed record MediaRgb(byte[] Data, int Width, int Height);

// Windows SDK 10.0.26100 wincodec.h ABI. WIC is only the decoder; resize is not delegated to WIC.
internal static class MediaWic
{
    internal static MediaRgb Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WIC requires Windows.");
        if (!File.Exists(path)) throw new FileNotFoundException("Image was not found.", path);
        int apartment = CoInitializeEx(IntPtr.Zero, 0);
        if (apartment < 0 && apartment != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(apartment);
        IFactory? factory = null;
        IDecoder? decoder = null;
        IFrame? frame = null;
        IConverter? converter = null;
        try
        {
            Guid clsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
            Guid iid = typeof(IFactory).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out factory));
            factory.CreateDecoderFromFilename(Path.GetFullPath(path), IntPtr.Zero, 0x80000000, 0, out decoder);
            decoder.GetFrameCount(out uint count);
            if (count == 0) throw new InvalidDataException("Image contains no frame.");
            decoder.GetFrame(0, out frame);
            frame.GetSize(out uint width, out uint height);
            if (width == 0 || height == 0 || width > 32768 || height > 32768 || (ulong)width * height > 40_000_000)
                throw new InvalidDataException("Image exceeds the 32768-side / 40 megapixel decode bound.");
            factory.CreateFormatConverter(out converter);
            Guid rgb = new("6fddc324-4e03-4bfe-b185-3d77768dc90d");
            converter.Initialize(frame, ref rgb, 0, IntPtr.Zero, 0, 0);
            var data = new byte[checked((int)(width * height * 3))];
            converter.CopyPixels(IntPtr.Zero, checked(width * 3), (uint)data.Length, data);
            return Orient(new MediaRgb(data, (int)width, (int)height), Orientation(frame));
        }
        finally
        {
            if (converter is not null) Marshal.FinalReleaseComObject(converter);
            if (frame is not null) Marshal.FinalReleaseComObject(frame);
            if (decoder is not null) Marshal.FinalReleaseComObject(decoder);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
            if (apartment >= 0) CoUninitialize();
        }
    }

    private static ushort Orientation(IFrame frame)
    {
        int hr = frame.GetMetadataQueryReader(out IMetadata? metadata);
        // Some WIC codecs explicitly do not support metadata readers.
        if (hr == unchecked((int)0x88982f81)) return 1;
        Marshal.ThrowExceptionForHR(hr);
        if (metadata is null) throw new InvalidDataException("WIC returned a null metadata reader.");
        try
        {
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                PropVariant value = default;
                try
                {
                    hr = metadata.GetMetadataByName(query, ref value);
                    if (hr == unchecked((int)0x88982f40) || hr == unchecked((int)0x88982f41)) continue;
                    Marshal.ThrowExceptionForHR(hr);
                    if (value.Type != 18 || value.UInt16 < 1 || value.UInt16 > 8)
                        throw new InvalidDataException("Invalid EXIF orientation.");
                    return value.UInt16;
                }
                finally { PropVariantClear(ref value); }
            }
            return 1;
        }
        finally { Marshal.FinalReleaseComObject(metadata); }
    }

    private static MediaRgb Orient(MediaRgb image, ushort orientation)
    {
        if (orientation == 1) return image;
        int w = image.Width, h = image.Height;
        int targetW = orientation >= 5 ? h : w, targetH = orientation >= 5 ? w : h;
        var output = new byte[image.Data.Length];
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                (int tx, int ty) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (h - 1 - y, x),
                    7 => (h - 1 - y, w - 1 - x),
                    8 => (y, w - 1 - x),
                    _ => throw new InvalidDataException("Invalid EXIF orientation.")
                };
                image.Data.AsSpan((y * w + x) * 3, 3).CopyTo(output.AsSpan((ty * targetW + tx) * 3, 3));
            }
        return new MediaRgb(output, targetW, targetH);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IFactory factory);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal ushort UInt16;
    }

    // Unused slots are intentionally not callable: retaining them preserves the SDK vtable order.
    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFactory
    {
        void CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr vendor, uint access, uint options, out IDecoder decoder);
        void UnusedDecoderStream();
        void UnusedDecoderHandle();
        void UnusedComponentInfo();
        void UnusedDecoder();
        void UnusedEncoder();
        void UnusedPalette();
        void CreateFormatConverter(out IConverter converter);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDecoder
    {
        void UnusedCapability();
        void UnusedInitialize();
        void UnusedContainerFormat();
        void UnusedDecoderInfo();
        void UnusedPalette();
        void UnusedMetadata();
        void UnusedPreview();
        void UnusedColorContexts();
        void UnusedThumbnail();
        void GetFrameCount(out uint count);
        void GetFrame(uint index, out IFrame frame);
    }

    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrame
    {
        void GetSize(out uint width, out uint height);
        void UnusedPixelFormat();
        void UnusedResolution();
        void UnusedPalette();
        void UnusedCopyPixels();
        [PreserveSig] int GetMetadataQueryReader(out IMetadata? metadata);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConverter
    {
        void UnusedSize();
        void UnusedPixelFormat();
        void UnusedResolution();
        void UnusedPalette();
        void CopyPixels(IntPtr rect, uint stride, uint size, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
        void Initialize(IFrame source, ref Guid format, uint dither, IntPtr palette, double alphaThreshold, uint paletteTranslate);
    }

    [ComImport, Guid("30989668-e1c9-4597-b395-458eedb808df"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMetadata
    {
        void UnusedContainerFormat();
        void UnusedLocation();
        [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string query, ref PropVariant value);
    }
}
