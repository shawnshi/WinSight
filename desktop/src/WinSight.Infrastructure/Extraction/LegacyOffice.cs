using System.Globalization;
using System.Text;

namespace WinSight.Infrastructure.Extraction;

/// <summary>Result of reading one legacy Office file. <c>State</c> is done, partial or skipped, like extraction results.</summary>
internal sealed record LegacyOfficeResult(string State, string? Detail, string Format, string Text);

/// <summary>
/// Text recovery for OLE/BIFF Office files (<c>.doc</c>, <c>.xls</c>, <c>.ppt</c> and their template/show variants).
/// The parsers handle untrusted bytes, so they are only ever run inside the isolated inference host process
/// (<c>extract-legacy-text</c>); a malformed file can end that short-lived process but not the application.
/// Every loop is bounded by the input size or an explicit cap and every offset is range-checked.
/// </summary>
internal static class LegacyOffice
{
    internal const int MaxTextCharacters = 4_000_000;
    internal const long MaxInputBytes = 32L << 20;

    internal static LegacyOfficeResult Extract(byte[] data, string extension, CancellationToken token = default)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();
        try
        {
            var container = CompoundFile.Open(data);
            return extension switch
            {
                "doc" or "dot" => WordBinary.Read(container, token),
                "xls" or "xlt" => ExcelBinary.Read(container, token),
                "ppt" or "pot" or "pps" => PowerPointBinary.Read(container, token),
                _ => new LegacyOfficeResult("skipped", $"Unsupported legacy Office extension .{extension}.", extension, ""),
            };
        }
        catch (LegacyOfficeException skip)
        {
            return new LegacyOfficeResult("skipped", skip.Message, extension, "");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return new LegacyOfficeResult("skipped", "Not a readable legacy Office file: " + error.Message, extension, "");
        }
    }

    internal static string Finish(StringBuilder text, bool truncated, string format, out LegacyOfficeResult result, string? detail = null)
    {
        var value = text.ToString().Replace("\0", "", StringComparison.Ordinal);
        var cut = truncated || value.Length > MaxTextCharacters;
        if (value.Length > MaxTextCharacters)
        {
            var length = MaxTextCharacters;
            if (char.IsHighSurrogate(value[length - 1])) length--;
            value = value[..length];
        }
        result = string.IsNullOrWhiteSpace(value)
            ? new LegacyOfficeResult("skipped", detail ?? "No searchable text was found.", format, "")
            : new LegacyOfficeResult(cut ? "partial" : "done", cut ? "Legacy Office text reached its size limit." : null, format, value);
        return value;
    }

    internal static Encoding Windows1252() => LegacyText.TryGetEncoding(1252, strict: false) ?? Encoding.Latin1;
}

/// <summary>A condition that makes the file legitimately unreadable (encrypted, empty); reported as skipped, not as a failure.</summary>
internal sealed class LegacyOfficeException(string message) : Exception(message);

/// <summary>Minimal MS-CFB reader: enough to open named streams from Word, Excel and PowerPoint files.</summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE, FreeSector = 0xFFFFFFFF;
    private readonly byte[] data;
    private readonly int sectorSize, miniSectorSize, miniCutoff;
    private readonly uint[] fat;
    private uint[] miniFat = [];
    private byte[] miniStream = [];
    private readonly List<Entry> entries = [];

    private sealed record Entry(string Name, int Type, uint StartSector, long Size);

    private CompoundFile(byte[] data)
    {
        this.data = data;
        if (data.Length < 512 || BitConverter.ToUInt64(data, 0) != 0xE11AB1A1E011CFD0UL) throw new InvalidDataException("Missing OLE signature.");
        int sectorShift = BitConverter.ToUInt16(data, 0x1E), miniShift = BitConverter.ToUInt16(data, 0x20);
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("Unsupported OLE sector size.");
        sectorSize = 1 << sectorShift;
        miniSectorSize = 1 << miniShift;
        miniCutoff = checked((int)BitConverter.ToUInt32(data, 0x38));
        var fatSectorCount = BitConverter.ToUInt32(data, 0x2C);
        var difat = new List<uint>();
        for (var i = 0; i < 109; i++) difat.Add(BitConverter.ToUInt32(data, 0x4C + i * 4));
        var nextDifat = BitConverter.ToUInt32(data, 0x44);
        var difatCount = BitConverter.ToUInt32(data, 0x48);
        for (var i = 0u; i < difatCount && nextDifat < 0xFFFFFFFA; i++)
        {
            var offset = SectorOffset(nextDifat);
            var perSector = sectorSize / 4 - 1;
            for (var j = 0; j < perSector; j++) difat.Add(BitConverter.ToUInt32(data, offset + j * 4));
            nextDifat = BitConverter.ToUInt32(data, offset + perSector * 4);
        }
        var fatList = new List<uint>();
        var read = 0u;
        foreach (var sector in difat)
        {
            if (sector >= 0xFFFFFFFA || read >= fatSectorCount) break;
            var offset = SectorOffset(sector);
            for (var j = 0; j < sectorSize / 4; j++) fatList.Add(BitConverter.ToUInt32(data, offset + j * 4));
            read++;
        }
        fat = fatList.ToArray();
        if (fat.Length == 0) throw new InvalidDataException("OLE file has no FAT.");

        var directory = ReadChain(BitConverter.ToUInt32(data, 0x30), long.MaxValue, 4 * 1024 * 1024);
        for (var offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var type = directory[offset + 0x42];
            if (type is not (1 or 2 or 5)) continue;
            var nameLength = BitConverter.ToUInt16(directory, offset + 0x40);
            if (nameLength < 2 || nameLength > 64) continue;
            var name = Encoding.Unicode.GetString(directory, offset, nameLength - 2);
            var size = sectorSize == 512 ? BitConverter.ToUInt32(directory, offset + 0x78) : BitConverter.ToInt64(directory, offset + 0x78);
            entries.Add(new Entry(name, type, BitConverter.ToUInt32(directory, offset + 0x74), size));
        }
        var root = entries.FirstOrDefault(e => e.Type == 5) ?? throw new InvalidDataException("OLE file has no root entry.");
        var miniFatStart = BitConverter.ToUInt32(data, 0x3C);
        if (miniFatStart < 0xFFFFFFFA)
        {
            var raw = ReadChain(miniFatStart, long.MaxValue, 8 * 1024 * 1024);
            miniFat = new uint[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, miniFat, 0, miniFat.Length * 4);
            miniStream = ReadChain(root.StartSector, root.Size, (int)Math.Min(root.Size, MaxStream));
        }
    }

    private const int MaxStream = 64 << 20;

    internal static CompoundFile Open(byte[] data) => new(data);

    internal bool Has(string name) => entries.Any(e => e.Type == 2 && string.Equals(e.Name, name, StringComparison.Ordinal));

    /// <summary>Reads a stream by name; names are compared exactly (they are case-insensitive in the spec but written canonically).</summary>
    internal byte[]? Stream(string name)
    {
        var entry = entries.FirstOrDefault(e => e.Type == 2 && string.Equals(e.Name, name, StringComparison.Ordinal));
        if (entry is null) return null;
        if (entry.Size > MaxStream) throw new InvalidDataException("OLE stream is too large.");
        return entry.Size < miniCutoff ? ReadMini(entry.StartSector, entry.Size) : ReadChain(entry.StartSector, entry.Size, (int)entry.Size);
    }

    private int SectorOffset(uint sector)
    {
        var offset = (long)(sector + 1) * sectorSize;
        if (offset + sectorSize > data.Length) throw new InvalidDataException("OLE sector lies outside the file.");
        return (int)offset;
    }

    private byte[] ReadChain(uint start, long size, int cap)
    {
        using var output = new MemoryStream();
        var visited = 0;
        var sector = start;
        while (sector < 0xFFFFFFFA && output.Length < size)
        {
            if (++visited > fat.Length + 1) throw new InvalidDataException("OLE FAT chain loops.");
            if (sector >= fat.Length) throw new InvalidDataException("OLE chain leaves the FAT.");
            var offset = SectorOffset(sector);
            var take = (int)Math.Min(sectorSize, size - output.Length);
            output.Write(data, offset, take);
            if (output.Length > cap) throw new InvalidDataException("OLE chain exceeds its size bound.");
            sector = fat[sector];
        }
        return output.ToArray();
    }

    private byte[] ReadMini(uint start, long size)
    {
        using var output = new MemoryStream();
        var visited = 0;
        var sector = start;
        while (sector < 0xFFFFFFFA && output.Length < size)
        {
            if (++visited > miniFat.Length + 1) throw new InvalidDataException("OLE mini FAT chain loops.");
            if (sector >= miniFat.Length) throw new InvalidDataException("OLE mini chain leaves the mini FAT.");
            var offset = (long)sector * miniSectorSize;
            if (offset + miniSectorSize > miniStream.Length) throw new InvalidDataException("OLE mini sector lies outside the mini stream.");
            output.Write(miniStream, (int)offset, (int)Math.Min(miniSectorSize, size - output.Length));
            sector = miniFat[sector];
        }
        return output.ToArray();
    }
}

/// <summary>MS-DOC: the text lives in pieces described by the CLX structure in the table stream.</summary>
internal static class WordBinary
{
    internal static LegacyOfficeResult Read(CompoundFile file, CancellationToken token)
    {
        var word = file.Stream("WordDocument") ?? throw new InvalidDataException("No WordDocument stream.");
        if (word.Length < 0x1A2 || BitConverter.ToUInt16(word, 0) != 0xA5EC) throw new InvalidDataException("Not a Word 97+ binary document.");
        var flags = BitConverter.ToUInt16(word, 0x0A);
        if ((flags & 0x0100) != 0) throw new LegacyOfficeException("The document is encrypted; its text cannot be indexed.");
        var table = file.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("No table stream.");

        var position = 32;
        var csw = BitConverter.ToUInt16(word, position);
        position += 2 + csw * 2;
        var cslw = BitConverter.ToUInt16(word, position);
        position += 2;
        var lwStart = position;
        if (cslw < 4) throw new InvalidDataException("FIB is too short.");
        var ccpText = BitConverter.ToInt32(word, lwStart + 3 * 4);
        position += cslw * 4;
        var cbRgFcLcb = BitConverter.ToUInt16(word, position);
        position += 2;
        const int clxIndex = 33;
        if (cbRgFcLcb <= clxIndex) throw new InvalidDataException("FIB has no CLX.");
        var fcClx = BitConverter.ToUInt32(word, position + clxIndex * 8);
        var lcbClx = BitConverter.ToUInt32(word, position + clxIndex * 8 + 4);
        if (ccpText < 0 || fcClx + (long)lcbClx > table.Length) throw new InvalidDataException("CLX lies outside the table stream.");

        var clx = (int)fcClx;
        var end = clx + (int)lcbClx;
        while (clx < end && table[clx] == 0x01) clx += 3 + BitConverter.ToUInt16(table, clx + 1); // skip Prc entries
        if (clx >= end || table[clx] != 0x02) throw new InvalidDataException("CLX has no piece table.");
        var plcLength = (int)BitConverter.ToUInt32(table, clx + 1);
        var plc = clx + 5;
        if (plcLength < 4 || plc + plcLength > table.Length) throw new InvalidDataException("Piece table lies outside the table stream.");
        var pieces = (plcLength - 4) / 12;
        var codepage = LegacyOffice.Windows1252();
        var text = new StringBuilder();
        var fieldCode = new Stack<bool>();
        for (var piece = 0; piece < pieces; piece++)
        {
            token.ThrowIfCancellationRequested();
            var cpStart = BitConverter.ToInt32(table, plc + piece * 4);
            var cpEnd = BitConverter.ToInt32(table, plc + (piece + 1) * 4);
            if (cpStart >= ccpText) break;
            cpEnd = Math.Min(cpEnd, ccpText);
            if (cpEnd <= cpStart) continue;
            var pcd = plc + (pieces + 1) * 4 + piece * 8;
            var fc = BitConverter.ToUInt32(table, pcd + 2);
            var compressed = (fc & 0x40000000) != 0;
            var characters = cpEnd - cpStart;
            long offset = compressed ? (fc & 0x3FFFFFFF) / 2 : fc & 0x3FFFFFFF;
            var bytes = compressed ? characters : characters * 2L;
            if (offset < 0 || offset + bytes > word.Length) throw new InvalidDataException("Text piece lies outside the document stream.");
            var chunk = compressed ? codepage.GetString(word, (int)offset, characters) : Encoding.Unicode.GetString(word, (int)offset, characters * 2);
            Append(text, chunk, fieldCode);
            if (text.Length > LegacyOffice.MaxTextCharacters * 2) break;
        }
        LegacyOffice.Finish(text, false, "doc", out var result);
        return result;
    }

    private static void Append(StringBuilder text, string chunk, Stack<bool> fieldCode)
    {
        foreach (var c in chunk)
        {
            switch (c)
            {
                case '\u0013': fieldCode.Push(true); break;                       // field begin: instruction text follows
                case '\u0014': if (fieldCode.Count > 0) { fieldCode.Pop(); fieldCode.Push(false); } break; // separator: result follows
                case '\u0015': if (fieldCode.Count > 0) fieldCode.Pop(); break;
                default:
                    if (fieldCode.Count > 0 && fieldCode.Peek()) break;            // hide field instructions
                    if (c is '\r' or '\u000B' or '\u000C') text.Append('\n');
                    else if (c == '\u0007') text.Append('\t');                     // cell / row end
                    else if (c == '\t' || c >= ' ') text.Append(c == '\u00A0' ? ' ' : c);
                    break;
            }
        }
    }
}

/// <summary>MS-XLS (BIFF8): shared strings plus cell records, grouped per sheet as tab-separated rows.</summary>
internal static class ExcelBinary
{
    private const int MaxCells = 1_000_000;

    private sealed class Segments
    {
        private readonly List<byte[]> parts = [];
        private int part, position;
        internal void Add(byte[] value) => parts.Add(value);
        internal bool AtEnd => part >= parts.Count || (position >= parts[part].Length && part == parts.Count - 1);
        internal bool AtPartEnd => part < parts.Count && position >= parts[part].Length;
        internal void NextPart() { part++; position = 0; if (part >= parts.Count) throw new InvalidDataException("Shared string table is truncated."); }
        internal int Remaining => part < parts.Count ? parts[part].Length - position : 0;
        internal byte Byte() { if (AtPartEnd) NextPart(); return parts[part][position++]; }
        internal int UInt16() => Byte() | Byte() << 8;
        internal long UInt32() => (uint)(UInt16() | UInt16() << 16);
        internal void Skip(long count) { while (count > 0) { if (AtPartEnd) NextPart(); var take = (int)Math.Min(count, Remaining); position += take; count -= take; } }
        internal string Chars(int count, ref bool highByte)
        {
            var text = new StringBuilder(count);
            var remaining = count;
            while (remaining > 0)
            {
                if (AtPartEnd) { NextPart(); highByte = (Byte() & 1) != 0; continue; }   // continued text restarts with a flag byte
                var width = highByte ? 2 : 1;
                var take = Math.Min(remaining, Remaining / width);
                if (take == 0) { position = parts[part].Length; continue; }
                var span = parts[part];
                if (highByte) text.Append(Encoding.Unicode.GetString(span, position, take * 2));
                else for (var i = 0; i < take; i++) text.Append((char)span[position + i]);
                position += take * width;
                remaining -= take;
            }
            return text.ToString();
        }
    }

    internal static LegacyOfficeResult Read(CompoundFile file, CancellationToken token)
    {
        var book = file.Stream("Workbook") ?? file.Stream("Book") ?? throw new InvalidDataException("No Workbook stream.");
        var sharedStrings = new List<string>();
        var sheetNames = new List<string>();
        var sheets = new List<SortedDictionary<(int Row, int Column), string>>();
        SortedDictionary<(int Row, int Column), string>? current = null;
        (int Row, int Column)? pendingFormula = null;
        var cells = 0;
        var scopes = new Stack<SortedDictionary<(int Row, int Column), string>?>(); // BOF/EOF nesting: globals, sheets, embedded charts
        var position = 0;
        while (position + 4 <= book.Length)
        {
            token.ThrowIfCancellationRequested();
            var id = BitConverter.ToUInt16(book, position);
            var length = BitConverter.ToUInt16(book, position + 2);
            var body = position + 4;
            if (body + length > book.Length) break;
            position = body + length;
            switch (id)
            {
                case 0x002F: throw new LegacyOfficeException("The workbook is password-protected; its cells cannot be indexed.");
                case 0x0809: // BOF: only worksheet substreams (0x0010) carry cells; globals and charts do not
                    if (scopes.Count > 64) throw new InvalidDataException("BIFF substreams are nested too deeply.");
                    scopes.Push(current);
                    var type = length >= 4 ? BitConverter.ToUInt16(book, body + 2) : 0;
                    if (type == 0x0010) { current = new(); sheets.Add(current); }
                    else current = null;
                    break;
                case 0x000A: // EOF: return to the enclosing substream
                    current = scopes.Count > 0 ? scopes.Pop() : null;
                    break;
                case 0x0085: // BOUNDSHEET
                    if (length >= 8) sheetNames.Add(ReadShortString(book, body + 6, length - 6));
                    break;
                case 0x00FC: // SST (+ CONTINUE records)
                    var parts = new Segments();
                    parts.Add(book.AsSpan(body, length).ToArray());
                    while (position + 4 <= book.Length && BitConverter.ToUInt16(book, position) == 0x003C)
                    {
                        var continueLength = BitConverter.ToUInt16(book, position + 2);
                        if (position + 4 + continueLength > book.Length) break;
                        parts.Add(book.AsSpan(position + 4, continueLength).ToArray());
                        position += 4 + continueLength;
                    }
                    ReadSharedStrings(parts, sharedStrings, token);
                    break;
                case 0x00FD when current is not null && length >= 10: // LABELSST
                    var index = (int)BitConverter.ToUInt32(book, body + 6);
                    if (index >= 0 && index < sharedStrings.Count && cells++ < MaxCells)
                        current[(BitConverter.ToUInt16(book, body), BitConverter.ToUInt16(book, body + 2))] = sharedStrings[index];
                    break;
                case 0x0204 when current is not null && length >= 8: // LABEL (BIFF5-style cell string)
                    var labelLength = BitConverter.ToUInt16(book, body + 6);
                    if (8 + labelLength <= length && cells++ < MaxCells)
                        current[(BitConverter.ToUInt16(book, body), BitConverter.ToUInt16(book, body + 2))] = LegacyOffice.Windows1252().GetString(book, body + 8, labelLength);
                    break;
                case 0x0203 when current is not null && length >= 14: // NUMBER
                    if (cells++ < MaxCells) current[(BitConverter.ToUInt16(book, body), BitConverter.ToUInt16(book, body + 2))] = FormatNumber(BitConverter.ToDouble(book, body + 6));
                    break;
                case 0x027E when current is not null && length >= 10: // RK
                    if (cells++ < MaxCells) current[(BitConverter.ToUInt16(book, body), BitConverter.ToUInt16(book, body + 2))] = FormatNumber(DecodeRk(BitConverter.ToUInt32(book, body + 6)));
                    break;
                case 0x00BD when current is not null && length >= 6: // MULRK
                    var row = BitConverter.ToUInt16(book, body);
                    var first = BitConverter.ToUInt16(book, body + 2);
                    for (var offset = 4; offset + 6 <= length - 2; offset += 6)
                        if (cells++ < MaxCells) current[(row, first + (offset - 4) / 6)] = FormatNumber(DecodeRk(BitConverter.ToUInt32(book, body + offset + 2)));
                    break;
                case 0x0006 when current is not null && length >= 20: // FORMULA: cached numeric result, or a STRING record follows
                    var rowF = BitConverter.ToUInt16(book, body);
                    var colF = BitConverter.ToUInt16(book, body + 2);
                    if (BitConverter.ToUInt16(book, body + 12) == 0xFFFF && book[body + 6] == 0) pendingFormula = (rowF, colF);
                    else if (BitConverter.ToUInt16(book, body + 12) != 0xFFFF && cells++ < MaxCells) current[(rowF, colF)] = FormatNumber(BitConverter.ToDouble(book, body + 6));
                    break;
                case 0x0207 when current is not null && pendingFormula is { } at && length >= 3: // STRING (formula result)
                    var stringLength = BitConverter.ToUInt16(book, body);
                    var wide = (book[body + 2] & 1) != 0;
                    if (3 + stringLength * (wide ? 2 : 1) <= length && cells++ < MaxCells)
                        current[at] = wide ? Encoding.Unicode.GetString(book, body + 3, stringLength * 2) : LegacyOffice.Windows1252().GetString(book, body + 3, stringLength);
                    pendingFormula = null;
                    break;
            }
        }

        var text = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            if (sheets[i].Count == 0) continue;
            text.Append('[').Append(i < sheetNames.Count ? sheetNames[i] : "Sheet" + (i + 1)).Append("]\n");
            var rowNumber = -1;
            var column = 0;
            foreach (var ((r, c), value) in sheets[i])
            {
                if (r != rowNumber) { if (rowNumber >= 0) text.Append('\n'); rowNumber = r; column = 0; }
                while (column < c && c - column < 64) { text.Append('\t'); column++; }
                text.Append(value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
                text.Append('\t');
                column = c + 1;
            }
            text.Append('\n');
            if (text.Length > LegacyOffice.MaxTextCharacters * 2) break;
        }
        LegacyOffice.Finish(text, cells >= MaxCells, "xls", out var result);
        return result;
    }

    private static void ReadSharedStrings(Segments parts, List<string> output, CancellationToken token)
    {
        parts.Skip(4);                         // total reference count
        var unique = parts.UInt32();
        for (long i = 0; i < unique && output.Count < 2_000_000; i++)
        {
            if (parts.AtEnd) break;
            token.ThrowIfCancellationRequested();
            var count = parts.UInt16();
            var flags = parts.Byte();
            var rich = (flags & 0x08) != 0;
            var extended = (flags & 0x04) != 0;
            var high = (flags & 0x01) != 0;
            var runs = rich ? parts.UInt16() : 0;
            var extension = extended ? parts.UInt32() : 0;
            output.Add(parts.Chars(count, ref high));
            parts.Skip(runs * 4L + extension);
        }
    }

    private static string ReadShortString(byte[] data, int offset, int available)
    {
        if (available < 2) return "";
        var count = data[offset];
        var wide = (data[offset + 1] & 1) != 0;
        var bytes = count * (wide ? 2 : 1);
        if (2 + bytes > available) return "";
        return wide ? Encoding.Unicode.GetString(data, offset + 2, bytes) : LegacyOffice.Windows1252().GetString(data, offset + 2, bytes);
    }

    private static double DecodeRk(uint rk)
    {
        var value = (rk & 2) != 0 ? (double)((int)rk >> 2) : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 1) != 0 ? value / 100 : value;
    }

    private static string FormatNumber(double value) => double.IsFinite(value) ? value.ToString("G15", CultureInfo.InvariantCulture) : "";
}

/// <summary>MS-PPT: walk the record tree and collect text atoms of slides and notes (not masters or the duplicate outline lists).</summary>
internal static class PowerPointBinary
{
    private const ushort SlideContainer = 0x03EE, NotesContainer = 0x03F0, MainMaster = 0x03F8, SlideListWithText = 0x0FF0,
        TextChars = 0x0FA0, TextBytes = 0x0FA8, Handout = 0x0FC9, NotesAtom = 0x03F1;
    private const int MaxDepth = 24;

    internal static LegacyOfficeResult Read(CompoundFile file, CancellationToken token)
    {
        var stream = file.Stream("PowerPoint Document") ?? throw new InvalidDataException("No PowerPoint Document stream.");
        var text = new StringBuilder();
        var slide = 0;
        Walk(stream, 0, stream.Length, 0, text, ref slide, false, token);
        LegacyOffice.Finish(text, false, "ppt", out var result);
        return result;
    }

    private static void Walk(byte[] data, long start, long end, int depth, StringBuilder text, ref int slide, bool inNotes, CancellationToken token)
    {
        if (depth > MaxDepth) throw new InvalidDataException("PowerPoint record nesting is too deep.");
        var position = start;
        while (position + 8 <= end)
        {
            token.ThrowIfCancellationRequested();
            var versionInstance = BitConverter.ToUInt16(data, (int)position);
            var type = BitConverter.ToUInt16(data, (int)position + 2);
            var length = BitConverter.ToUInt32(data, (int)position + 4);
            var body = position + 8;
            if (body + length > end) break;
            var container = (versionInstance & 0x0F) == 0x0F;
            if (container)
            {
                if (type is MainMaster or SlideListWithText or Handout) { position = body + length; continue; }
                // The notes master is stored as a notes container whose NotesAtom references no slide (0, or the master flag in the high bit): it only holds placeholder text.
                if (type == NotesContainer && IsNotesMaster(data, body, body + length)) { position = body + length; continue; }
                var notes = inNotes || type == NotesContainer;
                if (type == SlideContainer) { slide++; text.Append("\n--- ").Append(slide).Append(" ---\n"); }
                else if (type == NotesContainer) text.Append("\n[notes]\n");
                Walk(data, body, body + length, depth + 1, text, ref slide, notes, token);
            }
            else if (type == TextChars && length >= 2)
            {
                AppendText(text, Encoding.Unicode.GetString(data, (int)body, (int)(length & ~1u)));
            }
            else if (type == TextBytes && length > 0)
            {
                AppendText(text, LegacyOffice.Windows1252().GetString(data, (int)body, (int)length));
            }
            if (text.Length > LegacyOffice.MaxTextCharacters * 2) return;
            position = body + length;
        }
    }

    private static bool IsNotesMaster(byte[] data, long start, long end)
    {
        for (var position = start; position + 8 <= end;)
        {
            var type = BitConverter.ToUInt16(data, (int)position + 2);
            var length = BitConverter.ToUInt32(data, (int)position + 4);
            if (position + 8 + length > end) return false;
            if (type == NotesAtom && length >= 4) { var reference = BitConverter.ToUInt32(data, (int)position + 8); return reference == 0 || (reference & 0x80000000) != 0; }
            position += 8 + length;
        }
        return false;
    }

    private static void AppendText(StringBuilder text, string value)
    {
        // A lone "*" is the slide-number/date field placeholder, not content.
        if (value.Trim() == "*") return;
        foreach (var c in value)
        {
            if (c is '\r' or '\u000B' or '\u2028') text.Append('\n');
            else if (c == '\t' || c >= ' ') text.Append(c);
        }
        text.Append('\n');
    }
}
