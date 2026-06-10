using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using ZstdSharp;

namespace OrderFlow.Infrastructure.Dbn;

/// <summary>
/// Streaming decoder for Databento Binary Encoding (DBN) versions 1–3, mbo schema.
///
/// Implemented against the publicly documented layout:
///  - Databento "Databento Binary Encoding" standards doc
///    (https://databento.com/docs/standards-and-conventions/databento-binary-encoding)
///  - databento/dbn Rust crate as the authoritative struct reference:
///    rust/dbn/src/record.rs (RecordHeader, MboMsg) and
///    rust/dbn/src/decode/dbn/sync.rs (MetadataDecoder field order).
///
/// File layout: magic "DBN" + 1-byte version + u32-LE metadata length + metadata + records.
/// Records are fixed-size little-endian structs. RecordHeader.length is the TOTAL record
/// size in 32-bit words (bytes = length * 4). When metadata.ts_out != 0 an extra u64 is
/// appended to every record and included in that length — we advance past it and ignore it.
///
/// Record types other than MBO (0xA0) — symbol mapping, system, error, etc. — are skipped
/// and counted, never fatal.
/// </summary>
public sealed class DbnDecoder : IDisposable
{
    public const byte RTypeMbo = 0xA0;

    private const int HeaderSize = 16;          // RecordHeader: length u8, rtype u8, publisher_id u16, instrument_id u32, ts_event u64
    private const int MboRecordSize = 56;       // RecordHeader + order_id u64, price i64, size u32, flags u8, channel_id u8, action c_char, side c_char, ts_recv u64, ts_in_delta i32, sequence u32
    private const int MaxRecordSize = 255 * 4;  // length field is a u8 of 4-byte words
    private const int MetadataFixedLen = 100;   // identical for v1 and v2/v3 (v1 record_count u64 ≙ v2 symbol_cstr_len u16 + 6 reserved)
    private const ushort SymbolCstrLenV1 = 22;
    private const uint ZstdMagicLe = 0xFD2FB528;

    private readonly Stream _stream;
    private readonly bool _disposeStream;
    private readonly byte[] _buf = new byte[MaxRecordSize];
    private readonly long[] _skippedByRtype = new long[256];

    private bool _inSnapshot;
    private bool _hasPending;
    private MarketEvent _pending;

    public DbnMetadata Metadata { get; }

    /// <summary>Count of skipped (non-MBO) records, indexed by rtype.</summary>
    public IReadOnlyList<long> SkippedByRtype => _skippedByRtype;

    /// <summary>MBO records carrying action 'N' (None) or an unrecognized action char.</summary>
    public long IgnoredMboActionCount { get; private set; }

    private DbnDecoder(Stream stream, DbnMetadata metadata, bool disposeStream)
    {
        _stream = stream;
        Metadata = metadata;
        _disposeStream = disposeStream;
    }

    public static DbnDecoder Open(string path) => Open(File.OpenRead(path), leaveOpen: false);

    /// <summary>
    /// Opens a DBN stream, transparently unwrapping zstd (detected by magic, not extension).
    /// The stream must be seekable for magic detection (file and memory streams are).
    /// </summary>
    public static DbnDecoder Open(Stream raw, bool leaveOpen = false)
    {
        if (!raw.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable for zstd magic detection.", nameof(raw));
        }
        Span<byte> magic = stackalloc byte[4];
        int got = raw.ReadAtLeast(magic, 4, throwOnEndOfStream: false);
        raw.Seek(-got, SeekOrigin.Current);
        bool isZstd = got == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == ZstdMagicLe;
        Stream stream = isZstd ? new DecompressionStream(raw, leaveOpen: leaveOpen) : raw;
        stream = new BufferedStream(stream, 1 << 16);
        var metadata = ReadMetadata(stream);
        // Dispose the wrapper chain unless the caller keeps a bare (non-zstd) stream alive;
        // with zstd + leaveOpen the DecompressionStream itself leaves the inner stream open.
        return new DbnDecoder(stream, metadata, disposeStream: !leaveOpen || isZstd);
    }

    /// <summary>
    /// Reads the next normalized event. Returns false at clean end of stream; throws
    /// <see cref="DbnFormatException"/> on a truncated or malformed record.
    /// Synthesizes a <see cref="MarketEventKind.SnapshotEnd"/> event when the run of
    /// snapshot-flagged records at stream start gives way to the first live record.
    /// </summary>
    public bool TryReadEvent(out MarketEvent e)
    {
        if (_hasPending)
        {
            e = _pending;
            _hasPending = false;
            return true;
        }
        while (true)
        {
            if (!TryFill(0, HeaderSize, allowCleanEof: true))
            {
                if (_inSnapshot)
                {
                    // Stream ended while still inside a snapshot: close it out.
                    _inSnapshot = false;
                    e = MakeSnapshotEnd(_lastInstrumentId, _lastTsEvent, _lastTsRecv, _lastSequence);
                    return true;
                }
                e = default;
                return false;
            }
            int totalLen = _buf[0] * 4;
            if (totalLen < HeaderSize)
            {
                throw new DbnFormatException($"Record length field {_buf[0]} implies {totalLen} bytes, below the 16-byte header.");
            }
            TryFill(HeaderSize, totalLen - HeaderSize, allowCleanEof: false);

            byte rtype = _buf[1];
            if (rtype != RTypeMbo)
            {
                _skippedByRtype[rtype]++;
                continue;
            }
            if (totalLen < MboRecordSize)
            {
                throw new DbnFormatException($"MBO record of {totalLen} bytes is smaller than the {MboRecordSize}-byte MboMsg.");
            }
            if (!TryMapMbo(_buf.AsSpan(0, MboRecordSize), out e))
            {
                IgnoredMboActionCount++;
                continue;
            }

            _lastInstrumentId = e.InstrumentId;
            _lastTsEvent = e.TsEvent;
            _lastTsRecv = e.TsRecv;
            _lastSequence = e.Sequence;

            if (_inSnapshot && !e.IsSnapshot)
            {
                _inSnapshot = false;
                _pending = e;
                _hasPending = true;
                e = MakeSnapshotEnd(e.InstrumentId, e.TsEvent, e.TsRecv, e.Sequence);
                return true;
            }
            if (e.IsSnapshot)
            {
                _inSnapshot = true;
            }
            return true;
        }
    }

    private uint _lastInstrumentId;
    private Timestamp _lastTsEvent;
    private Timestamp _lastTsRecv;
    private uint _lastSequence;

    private static MarketEvent MakeSnapshotEnd(uint instrumentId, Timestamp tsEvent, Timestamp tsRecv, uint sequence) =>
        new(MarketEventKind.SnapshotEnd, instrumentId, tsEvent, tsRecv, sequence,
            OrderId: 0, Price.Undefined, Size: 0, Side.None, MarketEventFlags.None);

    /// <summary>
    /// MboMsg field offsets (databento/dbn rust/dbn/src/record.rs, identical across v1–v3):
    ///   0 length u8 | 1 rtype u8 | 2 publisher_id u16 | 4 instrument_id u32 | 8 ts_event u64
    ///  16 order_id u64 | 24 price i64 | 32 size u32 | 36 flags u8 | 37 channel_id u8
    ///  38 action c_char | 39 side c_char | 40 ts_recv u64 | 48 ts_in_delta i32 | 52 sequence u32
    /// </summary>
    private static bool TryMapMbo(ReadOnlySpan<byte> r, out MarketEvent e)
    {
        var kind = (char)r[38] switch
        {
            'A' => MarketEventKind.AddOrder,
            'M' => MarketEventKind.ModifyOrder,
            'C' => MarketEventKind.CancelOrder,
            'F' => MarketEventKind.Fill,
            'T' => MarketEventKind.Trade,
            'R' => MarketEventKind.BookClear,
            _ => MarketEventKind.None, // 'N' or unknown: counted and dropped
        };
        if (kind == MarketEventKind.None)
        {
            e = default;
            return false;
        }
        var side = (char)r[39] switch
        {
            'B' => Side.Bid,
            'A' => Side.Ask,
            _ => Side.None,
        };
        e = new MarketEvent(
            kind,
            InstrumentId: BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
            TsEvent: new Timestamp(BinaryPrimitives.ReadUInt64LittleEndian(r[8..])),
            TsRecv: new Timestamp(BinaryPrimitives.ReadUInt64LittleEndian(r[40..])),
            Sequence: BinaryPrimitives.ReadUInt32LittleEndian(r[52..]),
            OrderId: BinaryPrimitives.ReadUInt64LittleEndian(r[16..]),
            Price: new Price(BinaryPrimitives.ReadInt64LittleEndian(r[24..])),
            Size: BinaryPrimitives.ReadUInt32LittleEndian(r[32..]),
            Side: side,
            Flags: (MarketEventFlags)r[36]);
        return true;
    }

    private bool TryFill(int offset, int count, bool allowCleanEof)
    {
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(_buf, offset + read, count - read);
            if (n == 0)
            {
                if (read == 0 && allowCleanEof)
                {
                    return false;
                }
                throw new DbnFormatException("Unexpected end of stream mid-record (truncated DBN file).");
            }
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Metadata layout (v1 / v2+; fixed portion is 100 bytes in both):
    ///   dataset c_char[16] | schema u16 | start u64 | end u64 | limit u64
    ///   | v1: record_count u64 (deprecated, skipped)
    ///   | stype_in u8 | stype_out u8 | ts_out u8
    ///   | v2+: symbol_cstr_len u16 (v1: fixed 22)
    ///   | reserved (v1: 47, v2+: 53 bytes)
    ///   then: schema_definition_length u32 + bytes | symbols | partial | not_found | mappings,
    ///   where symbol lists are u32 count × cstr(symbol_cstr_len) and each mapping is
    ///   raw_symbol cstr + u32 interval_count × (start_date u32, end_date u32, symbol cstr).
    /// </summary>
    private static DbnMetadata ReadMetadata(Stream s)
    {
        Span<byte> prelude = stackalloc byte[8];
        ReadExactly(s, prelude);
        if (prelude[0] != (byte)'D' || prelude[1] != (byte)'B' || prelude[2] != (byte)'N')
        {
            throw new DbnFormatException("Missing DBN magic bytes — not a DBN stream.");
        }
        byte version = prelude[3];
        if (version is < 1 or > 3)
        {
            throw new DbnFormatException($"Unsupported DBN version {version} (supported: 1–3).");
        }
        int metaLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prelude[4..]));
        if (metaLen < MetadataFixedLen)
        {
            throw new DbnFormatException($"Metadata length {metaLen} below fixed header size {MetadataFixedLen}.");
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(metaLen);
        try
        {
            var meta = rented.AsSpan(0, metaLen);
            ReadExactly(s, meta);
            return ParseMetadata(version, meta);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static DbnMetadata ParseMetadata(byte version, ReadOnlySpan<byte> m)
    {
        int pos = 0;
        string dataset = ReadCString(m, ref pos, 16);
        ushort schema = ReadU16(m, ref pos);
        ulong start = ReadU64(m, ref pos);
        ulong end = ReadU64(m, ref pos);
        ulong limit = ReadU64(m, ref pos);
        if (version == 1)
        {
            pos += 8; // deprecated record_count
        }
        byte stypeIn = m[pos++];
        byte stypeOut = m[pos++];
        bool tsOut = m[pos++] != 0;
        ushort symbolCstrLen = version == 1 ? SymbolCstrLenV1 : ReadU16(m, ref pos);
        pos = MetadataFixedLen; // skip reserved padding (47 bytes v1, 53 bytes v2+)

        // Variable section. Real-world files can be older/newer than the docs we coded
        // against, so parse defensively: any bounds overrun degrades to empty lists
        // rather than failing the whole file. Record decoding does not depend on this.
        var symbols = Array.Empty<string>();
        var partial = Array.Empty<string>();
        var notFound = Array.Empty<string>();
        int mappingCount = 0;
        if (TryReadU32(m, ref pos, out uint schemaDefLen) && pos + (int)schemaDefLen <= m.Length)
        {
            pos += (int)schemaDefLen;
            if (TryReadSymbolList(m, ref pos, symbolCstrLen, out symbols) &&
                TryReadSymbolList(m, ref pos, symbolCstrLen, out partial) &&
                TryReadSymbolList(m, ref pos, symbolCstrLen, out notFound) &&
                TryReadU32(m, ref pos, out uint mappings))
            {
                mappingCount = (int)mappings;
            }
        }

        return new DbnMetadata(
            version, dataset, schema,
            new Timestamp(start), new Timestamp(end), limit,
            stypeIn, stypeOut, tsOut, symbolCstrLen,
            symbols, partial, notFound, mappingCount);
    }

    private static bool TryReadSymbolList(ReadOnlySpan<byte> m, ref int pos, ushort cstrLen, out string[] list)
    {
        list = Array.Empty<string>();
        if (!TryReadU32(m, ref pos, out uint count))
        {
            return false;
        }
        long bytes = (long)count * cstrLen;
        if (pos + bytes > m.Length)
        {
            return false;
        }
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadCString(m, ref pos, cstrLen);
        }
        list = result;
        return true;
    }

    private static string ReadCString(ReadOnlySpan<byte> m, ref int pos, int fixedLen)
    {
        var span = m.Slice(pos, fixedLen);
        pos += fixedLen;
        int nul = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? span[..nul] : span);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> m, ref int pos)
    {
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(m[pos..]);
        pos += 2;
        return v;
    }

    private static ulong ReadU64(ReadOnlySpan<byte> m, ref int pos)
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(m[pos..]);
        pos += 8;
        return v;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> m, ref int pos, out uint value)
    {
        if (pos + 4 > m.Length)
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(m[pos..]);
        pos += 4;
        return true;
    }

    private static void ReadExactly(Stream s, Span<byte> dest)
    {
        try
        {
            s.ReadExactly(dest);
        }
        catch (EndOfStreamException)
        {
            throw new DbnFormatException("Unexpected end of stream while reading DBN header/metadata.");
        }
    }

    public void Dispose()
    {
        if (_disposeStream)
        {
            _stream.Dispose();
        }
    }
}
