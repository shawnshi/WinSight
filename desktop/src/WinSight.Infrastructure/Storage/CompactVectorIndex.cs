using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using WinSight.Core;

namespace WinSight.Infrastructure.Storage;

internal readonly record struct VectorMeta(
    long SegmentId,
    long FileId,
    float Scale,
    ulong UnitHash,
    byte ChannelId);

/// <summary>
/// Bounded, chunked Int8 vector store decoupling full-corpus semantic scan memory (~796 B/vector)
/// from SQLite text excerpts and source-anchor JSON payloads (hydrated on hit only).
/// </summary>
internal sealed class CompactVectorIndex
{
    internal const int Dimension = 768;
    internal const int ChunkCapacity = 64;

    private readonly Dictionary<long, IndexedFile> files;
    private readonly Dictionary<string, long> fileIdByPath;
    private readonly List<string> channels = [];
    private readonly Dictionary<string, byte> channelIds = new(StringComparer.Ordinal);
    private readonly List<sbyte[]> vectorChunks = [];
    private readonly List<VectorMeta[]> metaChunks = [];
    private readonly HashSet<int> ownedMetaChunks = [];
    private int count;
    private int tombstones;

    internal CompactVectorIndex(Dictionary<long, IndexedFile> files)
    {
        this.files = files;
        fileIdByPath = new Dictionary<string, long>(files.Count, StringComparer.Ordinal);
        foreach (var kv in files)
            fileIdByPath[kv.Value.Path] = kv.Key;
    }

    private CompactVectorIndex(
        Dictionary<long, IndexedFile> files,
        Dictionary<string, long> fileIdByPath,
        List<string> channels,
        Dictionary<string, byte> channelIds,
        List<sbyte[]> vectorChunks,
        List<VectorMeta[]> metaChunks,
        int count,
        int tombstones)
    {
        this.files = files;
        this.fileIdByPath = fileIdByPath;
        this.channels = channels;
        this.channelIds = channelIds;
        this.vectorChunks = vectorChunks;
        this.metaChunks = metaChunks;
        this.count = count;
        this.tombstones = tombstones;
    }

    /// <summary>
    /// Creates an isolated copy for CoW patching so concurrent readers holding the previous snapshot
    /// never observe dictionary corruption or torn chunk writes.
    /// </summary>
    internal CompactVectorIndex CloneForMutation()
    {
        var copy = new CompactVectorIndex(
            new Dictionary<long, IndexedFile>(files),
            new Dictionary<string, long>(fileIdByPath, StringComparer.Ordinal),
            new List<string>(channels),
            new Dictionary<string, byte>(channelIds, StringComparer.Ordinal),
            new List<sbyte[]>(vectorChunks),
            new List<VectorMeta[]>(metaChunks),
            count,
            tombstones);
        if (copy.vectorChunks.Count > 0 && count % ChunkCapacity != 0)
        {
            int tail = copy.vectorChunks.Count - 1;
            copy.vectorChunks[tail] = (sbyte[])copy.vectorChunks[tail].Clone();
            copy.metaChunks[tail] = (VectorMeta[])copy.metaChunks[tail].Clone();
            copy.ownedMetaChunks.Add(tail);
        }
        return copy;
    }

    internal IReadOnlyDictionary<long, IndexedFile> Files => files;
    internal IReadOnlyList<string> Channels => channels;
    internal int Count => count;
    internal int Tombstones => tombstones;

    internal byte InternChannel(string channel)
    {
        if (channelIds.TryGetValue(channel, out var id)) return id;
        if (channels.Count >= 250) return 0;
        id = (byte)channels.Count;
        channels.Add(channel);
        channelIds[channel] = id;
        return id;
    }

    internal void Add(long segmentId, long fileId, ulong unitHash, byte channelId, ReadOnlySpan<sbyte> quantized, float scale)
    {
        int chunkIdx = count / ChunkCapacity;
        int slotIdx = count % ChunkCapacity;
        if (chunkIdx >= vectorChunks.Count)
        {
            vectorChunks.Add(new sbyte[ChunkCapacity * Dimension]);
            metaChunks.Add(new VectorMeta[ChunkCapacity]);
            ownedMetaChunks.Add(chunkIdx);
        }
        quantized.CopyTo(vectorChunks[chunkIdx].AsSpan(slotIdx * Dimension, Dimension));
        metaChunks[chunkIdx][slotIdx] = new VectorMeta(segmentId, fileId, scale, unitHash, channelId);
        count++;
    }

    internal void UpdateAnchorMeta(int globalIndex, ulong unitHash, byte channelId)
    {
        int c = globalIndex / ChunkCapacity;
        int s = globalIndex % ChunkCapacity;
        var metas = metaChunks[c];
        if (ownedMetaChunks.Add(c))
        {
            metas = (VectorMeta[])metas.Clone();
            metaChunks[c] = metas;
        }
        metas[s] = metas[s] with { UnitHash = unitHash, ChannelId = channelId };
    }

    internal void MarkFileRemoved(string path)
    {
        if (!fileIdByPath.TryGetValue(path, out var fileId)) return;
        MarkFileRemoved(fileId);
        files.Remove(fileId);
        fileIdByPath.Remove(path);
    }

    internal void MarkFileRemoved(long fileId)
    {
        for (int c = 0; c < metaChunks.Count; c++)
        {
            var metas = metaChunks[c];
            int limit = c == metaChunks.Count - 1 ? count - c * ChunkCapacity : ChunkCapacity;
            for (int i = 0; i < limit; i++)
            {
                if (metas[i].FileId == fileId)
                {
                    if (ownedMetaChunks.Add(c))
                    {
                        metas = (VectorMeta[])metas.Clone();
                        metaChunks[c] = metas;
                    }
                    metas[i] = metas[i] with { FileId = -1 };
                    tombstones++;
                }
            }
        }
    }

    internal void UpsertHeader(IndexedFile header)
    {
        if (fileIdByPath.TryGetValue(header.Path, out var oldId) && oldId != header.Id)
        {
            MarkFileRemoved(oldId);
            files.Remove(oldId);
        }
        files[header.Id] = header;
        fileIdByPath[header.Path] = header.Id;
    }

    internal bool NeedsCompaction => tombstones > 4096 && tombstones * 4 > count;

    internal CompactVectorIndex Compact()
    {
        var fresh = new CompactVectorIndex(new Dictionary<long, IndexedFile>(files));
        foreach (var ch in channels) fresh.InternChannel(ch);
        for (int c = 0; c < metaChunks.Count; c++)
        {
            var metas = metaChunks[c];
            var vecs = vectorChunks[c];
            int limit = c == metaChunks.Count - 1 ? count - c * ChunkCapacity : ChunkCapacity;
            for (int i = 0; i < limit; i++)
            {
                var m = metas[i];
                if (m.FileId < 0 || !files.ContainsKey(m.FileId)) continue;
                fresh.Add(m.SegmentId, m.FileId, m.UnitHash, m.ChannelId, vecs.AsSpan(i * Dimension, Dimension), m.Scale);
            }
        }
        return fresh;
    }

    internal ReadOnlySpan<sbyte> GetVectorSpan(int globalIndex)
    {
        int c = globalIndex / ChunkCapacity;
        int s = globalIndex % ChunkCapacity;
        return vectorChunks[c].AsSpan(s * Dimension, Dimension);
    }

    internal VectorMeta GetMeta(int globalIndex)
    {
        int c = globalIndex / ChunkCapacity;
        int s = globalIndex % ChunkCapacity;
        return metaChunks[c][s];
    }

    internal (sbyte[] VecChunk, VectorMeta[] MetaChunk, int Length) GetChunk(int chunkIndex)
    {
        int len = chunkIndex == metaChunks.Count - 1 ? count - chunkIndex * ChunkCapacity : ChunkCapacity;
        return (vectorChunks[chunkIndex], metaChunks[chunkIndex], len);
    }

    internal int ChunkCount => metaChunks.Count;

    internal static void QuantizeFromBlob(byte[] blob, Span<float> scratch, Span<sbyte> dest, out float scale)
    {
        if (blob.Length == Dimension * 2)
        {
            if (BitConverter.IsLittleEndian)
            {
                var src = MemoryMarshal.Cast<byte, Half>(blob.AsSpan());
                for (int i = 0; i < Dimension; i++) scratch[i] = (float)src[i];
            }
            else
            {
                for (int i = 0; i < Dimension; i++)
                    scratch[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(i * 2)));
            }
            VectorContract.Validate(scratch);
        }
        else if (blob.Length == VectorContract.Int8ByteCount)
        {
            var decoded = VectorContract.DequantizeInt8(blob);
            decoded.CopyTo(scratch);
        }
        else if (blob.Length == Dimension)
        {
            double normSq = 0;
            const float inv127 = 1.0f / 127.0f;
            for (int i = 0; i < Dimension; i++)
            {
                float val = (sbyte)blob[i] * inv127;
                scratch[i] = val;
                normSq += (double)val * val;
            }
            if (normSq > 1e-12)
            {
                float invNorm = (float)(1.0 / Math.Sqrt(normSq));
                for (int i = 0; i < Dimension; i++) scratch[i] *= invNorm;
            }
            VectorContract.Validate(scratch);
        }
        else
        {
            throw new InvalidDataException($"Vector blob must contain 768 float16 values ({Dimension * 2} bytes) or int8 quantized values ({VectorContract.Int8ByteCount} bytes).");
        }

        float maxAbs = 0f;
        for (int i = 0; i < Dimension; i++)
        {
            float a = MathF.Abs(scratch[i]);
            if (a > maxAbs) maxAbs = a;
        }
        if (maxAbs <= 1e-12f)
        {
            dest.Clear();
            scale = 0f;
            return;
        }
        scale = maxAbs / 127.0f;
        float invScale = 127.0f / maxAbs;
        for (int i = 0; i < Dimension; i++)
        {
            int q = (int)MathF.Round(scratch[i] * invScale);
            dest[i] = (sbyte)Math.Clamp(q, -127, 127);
        }
    }

    internal static ulong ComputeUnitHash(string unit) => ComputeUnitHash(unit.AsSpan());

    internal static ulong ComputeUnitHash(ReadOnlySpan<char> unit)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in unit)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double DotInt8(ReadOnlySpan<sbyte> quantized, float scale, ReadOnlySpan<float> query)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            ref sbyte qRef = ref MemoryMarshal.GetReference(quantized);
            ref float fRef = ref MemoryMarshal.GetReference(query);
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            for (nuint i = 0; i < Dimension; i += 16)
            {
                var v128 = Vector128.LoadUnsafe(ref qRef, i);
                var (sLo, sHi) = Vector128.Widen(v128);
                var (i0, i1) = Vector128.Widen(sLo);
                var (i2, i3) = Vector128.Widen(sHi);
                var f0 = Vector256.ConvertToSingle(Vector256.Create(i0, i1));
                var f1 = Vector256.ConvertToSingle(Vector256.Create(i2, i3));
                acc0 += f0 * Vector256.LoadUnsafe(ref fRef, i);
                acc1 += f1 * Vector256.LoadUnsafe(ref fRef, i + 8);
            }
            return (double)Vector256.Sum(acc0 + acc1) * scale;
        }
        double sum = 0;
        for (int i = 0; i < Dimension; i++)
            sum += quantized[i] * query[i];
        return sum * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void QuantizeQuery(ReadOnlySpan<float> query, Span<short> qInt16, out float queryScale)
    {
        float maxAbs = 0f;
        for (int i = 0; i < Dimension; i++)
        {
            float a = MathF.Abs(query[i]);
            if (a > maxAbs) maxAbs = a;
        }
        if (maxAbs <= 1e-12f)
        {
            qInt16.Clear();
            queryScale = 0f;
            return;
        }
        queryScale = maxAbs / 16384.0f;
        float invScale = 16384.0f / maxAbs;
        for (int i = 0; i < Dimension; i++)
        {
            int q = (int)MathF.Round(query[i] * invScale);
            qInt16[i] = (short)Math.Clamp(q, -16384, 16384);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double DotInt8PreQuantized(ReadOnlySpan<sbyte> quantized, float vecScale, ReadOnlySpan<short> qInt16, float queryScale)
    {
        if (queryScale == 0f || vecScale == 0f) return 0.0;
        if (Avx2.IsSupported && Vector256.IsHardwareAccelerated)
        {
            ref sbyte vecRef = ref MemoryMarshal.GetReference(quantized);
            ref short qRef = ref MemoryMarshal.GetReference(qInt16);
            var acc0 = Vector256<int>.Zero;
            var acc1 = Vector256<int>.Zero;
            for (nuint i = 0; i < Dimension; i += 32)
            {
                var s0 = Vector128.LoadUnsafe(ref vecRef, i);
                var q0 = Vector256.LoadUnsafe(ref qRef, i);
                acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(Avx2.ConvertToVector256Int16(s0), q0));

                var s1 = Vector128.LoadUnsafe(ref vecRef, i + 16);
                var q1 = Vector256.LoadUnsafe(ref qRef, i + 16);
                acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(Avx2.ConvertToVector256Int16(s1), q1));
            }
            long total = Vector256.Sum(acc0) + (long)Vector256.Sum(acc1);
            return total * ((double)vecScale * queryScale);
        }
        long scalar = 0;
        for (int i = 0; i < Dimension; i++)
            scalar += (long)quantized[i] * qInt16[i];
        return scalar * ((double)vecScale * queryScale);
    }
}
