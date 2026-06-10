using System.Buffers.Binary;
using System.Text;
using OrderFlow.Domain.Events;
using ZstdSharp;

namespace OrderFlow.Infrastructure.Dbn;

public sealed record DbnWriterOptions
{
    public byte Version { get; init; } = 2;
    public bool ZstdCompress { get; init; } = true;
    public string Dataset { get; init; } = "GLBX.MDP3";
    public IReadOnlyList<string> Symbols { get; init; } = new[] { "ES.c.0" };
    public ulong StartNs { get; init; }
    public ulong EndNs { get; init; } = ulong.MaxValue;
    /// <summary>When true, appends a u64 ts_out to every record (and flags it in metadata).</summary>
    public bool TsOut { get; init; }
    public ushort PublisherId { get; init; } = 1;
}

/// <summary>
/// Minimal DBN v2/v3 writer for the mbp-10 schema. Exists for tests (decoder round-trips)
/// and the synthetic-data CLI command — NOT a general-purpose encoder.
/// Layout mirrors <see cref="DbnDecoder"/>; see the references cited there.
/// </summary>
public sealed class DbnMbp10Writer : IDisposable
{
    private const int LevelSize = 32;
    private const int LevelsOffset = 48;
    private const int Mbp10RecordSize = LevelsOffset + BookLevels.Depth * LevelSize; // 368
    private const ushort SymbolCstrLenV2 = 71;
    private const ushort SchemaMbp10 = DbnMetadata.SchemaMbp10;

    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly bool _tsOut;
    private readonly ushort _publisherId;
    private readonly byte[] _rec = new byte[Mbp10RecordSize + 8];

    public DbnMbp10Writer(Stream destination, DbnWriterOptions? options = null, bool leaveOpen = false)
    {
        options ??= new DbnWriterOptions();
        if (options.Version is not (2 or 3))
        {
            throw new ArgumentException($"Writer supports DBN v2/v3 only, got v{options.Version}.");
        }
        _leaveOpen = leaveOpen;
        _tsOut = options.TsOut;
        _out = options.ZstdCompress
            ? new CompressionStream(destination, level: 3, leaveOpen: leaveOpen)
            : destination;
        _publisherId = options.PublisherId;
        WriteHeaderAndMetadata(options);
    }

    public void WriteEvent(in MarketEvent e)
    {
        byte action = e.Kind switch
        {
            MarketEventKind.BookChanged => e.Cause switch
            {
                BookCause.Add => (byte)'A',
                BookCause.Cancel => (byte)'C',
                BookCause.Modify => (byte)'M',
                _ => 0,
            },
            MarketEventKind.Trade => (byte)'T',
            MarketEventKind.BookClear => (byte)'R',
            _ => 0, // None / SnapshotEnd are not wire records
        };
        if (action == 0)
        {
            return;
        }
        var r = _rec.AsSpan();
        int totalLen = Mbp10RecordSize + (_tsOut ? 8 : 0);
        r[0] = (byte)(totalLen / 4);
        r[1] = DbnDecoder.RTypeMbp10;
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], _publisherId);
        BinaryPrimitives.WriteUInt32LittleEndian(r[4..], e.InstrumentId);
        BinaryPrimitives.WriteUInt64LittleEndian(r[8..], e.TsEvent.UnixNanos);
        BinaryPrimitives.WriteInt64LittleEndian(r[16..], e.Price.RawNano);
        BinaryPrimitives.WriteUInt32LittleEndian(r[24..], e.Size);
        r[28] = action;
        r[29] = e.Side switch { Domain.Primitives.Side.Bid => (byte)'B', Domain.Primitives.Side.Ask => (byte)'A', _ => (byte)'N' };
        r[30] = (byte)e.Flags;
        r[31] = e.Depth;
        BinaryPrimitives.WriteUInt64LittleEndian(r[32..], e.TsRecv.UnixNanos);
        BinaryPrimitives.WriteInt32LittleEndian(r[40..], 0); // ts_in_delta
        BinaryPrimitives.WriteUInt32LittleEndian(r[44..], e.Sequence);
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var l = r.Slice(LevelsOffset + i * LevelSize, LevelSize);
            var lvl = e.Levels[i];
            BinaryPrimitives.WriteInt64LittleEndian(l, lvl.BidPrice.RawNano);
            BinaryPrimitives.WriteInt64LittleEndian(l[8..], lvl.AskPrice.RawNano);
            BinaryPrimitives.WriteUInt32LittleEndian(l[16..], lvl.BidSize);
            BinaryPrimitives.WriteUInt32LittleEndian(l[20..], lvl.AskSize);
            BinaryPrimitives.WriteUInt32LittleEndian(l[24..], lvl.BidCount);
            BinaryPrimitives.WriteUInt32LittleEndian(l[28..], lvl.AskCount);
        }
        if (_tsOut)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(r[Mbp10RecordSize..], e.TsRecv.UnixNanos);
        }
        _out.Write(_rec, 0, totalLen);
    }

    /// <summary>Writes raw record bytes verbatim — used by tests to inject foreign record types.</summary>
    public void WriteRawRecord(ReadOnlySpan<byte> record) => _out.Write(record);

    private void WriteHeaderAndMetadata(DbnWriterOptions o)
    {
        ushort cstrLen = SymbolCstrLenV2;
        int variableLen = 4 // schema_definition_length
                          + 4 + o.Symbols.Count * cstrLen // symbols
                          + 4 // partial count
                          + 4 // not_found count
                          + 4; // mappings count
        int metaLen = 100 + variableLen;

        var buf = new byte[8 + metaLen];
        var b = buf.AsSpan();
        b[0] = (byte)'D';
        b[1] = (byte)'B';
        b[2] = (byte)'N';
        b[3] = o.Version;
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)metaLen);

        var m = b[8..];
        WriteCString(m, 0, o.Dataset, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(m[16..], SchemaMbp10);
        BinaryPrimitives.WriteUInt64LittleEndian(m[18..], o.StartNs);
        BinaryPrimitives.WriteUInt64LittleEndian(m[26..], o.EndNs);
        BinaryPrimitives.WriteUInt64LittleEndian(m[34..], 0); // limit
        m[42] = 3; // stype_in: continuous
        m[43] = 0; // stype_out: instrument_id
        m[44] = (byte)(o.TsOut ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(m[45..], cstrLen);
        // 47..100: reserved, already zero
        int pos = 100;
        BinaryPrimitives.WriteUInt32LittleEndian(m[pos..], 0); // schema_definition_length
        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(m[pos..], (uint)o.Symbols.Count);
        pos += 4;
        foreach (var sym in o.Symbols)
        {
            WriteCString(m, pos, sym, cstrLen);
            pos += cstrLen;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(m[pos..], 0); // partial
        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(m[pos..], 0); // not_found
        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(m[pos..], 0); // mappings
        _out.Write(buf);
    }

    private static void WriteCString(Span<byte> dest, int offset, string value, int fixedLen)
    {
        var target = dest.Slice(offset, fixedLen);
        target.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, fixedLen - 1)), target);
    }

    public void Dispose()
    {
        _out.Flush();
        if (_out is CompressionStream || !_leaveOpen)
        {
            _out.Dispose();
        }
    }
}
