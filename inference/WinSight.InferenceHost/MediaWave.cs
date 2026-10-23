using System.Text;

namespace WinSight.InferenceHost;

internal static class MediaWave
{
    internal static double[] Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
        if (file.Length < 12 || file.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("WAV must have a RIFF header and fit a 16 MiB input bound.");
        if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() != file.Length - 8 || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("Expected a complete RIFF/WAVE file (RF64 and RIFX are not supported).");

        bool hasFormat = false;
        long dataOffset = -1;
        uint dataLength = 0;
        while (file.Position < file.Length)
        {
            if (file.Length - file.Position < 8)
                throw new InvalidDataException("Truncated WAV chunk header.");
            uint id = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            long start = file.Position;
            long end = start + length + (length & 1);
            if (end > file.Length)
                throw new InvalidDataException("Truncated WAV chunk payload or padding.");
            if (id == 0x20746d66)
            {
                if (hasFormat || length < 16)
                    throw new InvalidDataException("WAV requires one complete fmt chunk.");
                ushort format = reader.ReadUInt16();
                ushort channels = reader.ReadUInt16();
                uint rate = reader.ReadUInt32();
                uint byteRate = reader.ReadUInt32();
                ushort align = reader.ReadUInt16();
                ushort bits = reader.ReadUInt16();
                if (format != 1 || channels != 1 || rate != 16000 || bits != 16)
                    throw new NotSupportedException("Only 16 kHz mono PCM16 WAV is accepted; decode/resample upstream explicitly.");
                if (align != 2 || byteRate != 32000)
                    throw new InvalidDataException("Inconsistent PCM16 WAV block alignment or byte rate.");
                hasFormat = true;
            }
            else if (id == 0x61746164)
            {
                if (dataOffset >= 0)
                    throw new InvalidDataException("Multiple WAV data chunks are ambiguous.");
                dataOffset = start;
                dataLength = length;
            }
            file.Position = end;
        }
        if (!hasFormat || dataOffset < 0 || dataLength % 2 != 0)
            throw new InvalidDataException("WAV requires fmt and even-length PCM data chunks.");
        int samples = checked((int)(dataLength / 2));
        if (samples <= 160)
            throw new InvalidDataException("Audio must contain at least 161 samples for a valid Gemma4 frame.");
        if (samples > 480000)
            throw new InvalidDataException("Audio exceeds the 30 second window. Split it upstream; no samples were truncated.");
        var waveform = new double[samples];
        file.Position = dataOffset;
        for (int i = 0; i < samples; ++i)
            waveform[i] = reader.ReadInt16() / 32768.0;
        return waveform;
    }
}
