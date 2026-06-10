using System.Buffers.Binary;
using OrderFlow.Backtest;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using OrderFlow.Infrastructure.Dbn;

namespace OrderFlow.Tests;

public class DbnCodecTests
{
    private static MemoryStream WriteFile(IReadOnlyList<MarketEvent> events, DbnWriterOptions? options = null)
    {
        var ms = new MemoryStream();
        using (var writer = new DbnMbp10Writer(ms, options, leaveOpen: true))
        {
            foreach (var e in events)
            {
                writer.WriteEvent(in e);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static List<MarketEvent> DecodeAll(Stream s, out DbnDecoder decoderState)
    {
        var decoder = DbnDecoder.Open(s, leaveOpen: true);
        var result = new List<MarketEvent>();
        while (decoder.TryReadEvent(out var e))
        {
            result.Add(e);
        }
        decoderState = decoder;
        return result;
    }

    [Theory]
    [InlineData((byte)2, true)]
    [InlineData((byte)2, false)]
    [InlineData((byte)3, true)]
    [InlineData((byte)3, false)]
    public void RoundTrip_PreservesEveryField(byte version, bool zstd)
    {
        var events = new SyntheticMbp10Generator(seed: 5).Generate(2_000).ToArray();
        using var ms = WriteFile(events, new DbnWriterOptions { Version = version, ZstdCompress = zstd });

        var decoded = DecodeAll(ms, out var decoder);
        Assert.Equal(events, decoded); // record-struct equality: all fields incl. the 10-level array
        Assert.Equal(version, decoder.Metadata.Version);
        Assert.True(decoder.Metadata.IsMbp10Schema);
        Assert.Equal("GLBX.MDP3", decoder.Metadata.Dataset);
        Assert.Equal(71, decoder.Metadata.SymbolCstrLen);
        Assert.Equal(new[] { "ES.c.0" }, decoder.Metadata.Symbols);
    }

    [Fact]
    public void RoundTrip_WithTsOutTail_IgnoresAppendedTimestamp()
    {
        var events = new SyntheticMbp10Generator(seed: 6).Generate(500).ToArray();
        using var ms = WriteFile(events, new DbnWriterOptions { TsOut = true, ZstdCompress = false });

        var decoded = DecodeAll(ms, out var decoder);
        Assert.True(decoder.Metadata.TsOut);
        Assert.Equal(events, decoded);
    }

    [Fact]
    public void Decoder_SkipsForeignRecordTypes_WithoutCrashing()
    {
        var events = new SyntheticMbp10Generator(seed: 7).Generate(100).ToArray();
        var ms = new MemoryStream();
        using (var writer = new DbnMbp10Writer(ms, new DbnWriterOptions { ZstdCompress = false }, leaveOpen: true))
        {
            for (int i = 0; i < events.Length; i++)
            {
                writer.WriteEvent(in events[i]);
                if (i == 50)
                {
                    // Inject a fake SymbolMappingMsg-style record: rtype 0x16, 24 bytes total.
                    var alien = new byte[24];
                    alien[0] = 24 / 4;
                    alien[1] = 0x16;
                    BinaryPrimitives.WriteUInt32LittleEndian(alien.AsSpan(4), 1);
                    writer.WriteRawRecord(alien);
                }
            }
        }
        ms.Position = 0;

        var decoded = DecodeAll(ms, out var decoder);
        Assert.Equal(events, decoded);
        Assert.Equal(1, decoder.SkippedByRtype[0x16]);
    }

    [Fact]
    public void Decoder_SynthesizesSnapshotEnd_OnFlagTransition()
    {
        // The generator flags exactly its seed records (the initial book build) as snapshot.
        var all = new SyntheticMbp10Generator(seed: 8, seedFlaggedAsSnapshot: true)
            .Generate(SyntheticMbp10Generator.SeedRecordCount + 50).ToArray();
        int snapCount = SyntheticMbp10Generator.SeedRecordCount;
        Assert.All(all.Take(snapCount), e => Assert.True(e.IsSnapshot));
        Assert.All(all.Skip(snapCount), e => Assert.False(e.IsSnapshot));

        using var ms = WriteFile(all);
        var decoded = DecodeAll(ms, out _);

        Assert.Equal(all.Length + 1, decoded.Count);
        Assert.Equal(MarketEventKind.SnapshotEnd, decoded[snapCount].Kind);
        Assert.Equal(all.Take(snapCount), decoded.Take(snapCount));
        Assert.Equal(all.Skip(snapCount), decoded.Skip(snapCount + 1));
    }

    [Fact]
    public void Decoder_ThrowsDbnFormat_OnTruncatedRecord()
    {
        var events = new SyntheticMbp10Generator(seed: 10).Generate(20).ToArray();
        using var full = WriteFile(events, new DbnWriterOptions { ZstdCompress = false });
        var truncated = new MemoryStream(full.ToArray(), 0, (int)full.Length - 10);

        var decoder = DbnDecoder.Open(truncated, leaveOpen: true);
        Assert.Throws<DbnFormatException>(() =>
        {
            while (decoder.TryReadEvent(out _))
            {
            }
        });
    }

    [Fact]
    public void Decoder_RejectsNonDbnStream()
    {
        var junk = new MemoryStream("definitely not a DBN file"u8.ToArray());
        Assert.Throws<DbnFormatException>(() => DbnDecoder.Open(junk, leaveOpen: true));
    }

    [Fact]
    public void Decoder_PreservesPriceTimestampAndLevelPrecision()
    {
        // Exercise the exact wire encoding: nanoprice, nanosecond timestamps, and the full
        // 10-level array (including unpopulated UNDEF slots) survive intact.
        var levels = TestEvents.Levels(
            bids: [(5023.50m, 17, 3), (5023.25m, 250, 12)],
            asks: [(5023.75m, 9, 1)]);
        var e = new MarketEvent(
            MarketEventKind.Trade, BookCause.None, InstrumentId: 12345,
            TsEvent: new Timestamp(1_767_621_600_123_456_789UL),
            TsRecv: new Timestamp(1_767_621_600_123_456_999UL),
            Sequence: 4_000_000_001,
            Price: Price.FromDecimal(5023.75m),
            Size: 17,
            Side: Side.Bid,
            Flags: MarketEventFlags.Last | MarketEventFlags.TopOfBook,
            Depth: 0,
            Levels: levels);
        using var ms = WriteFile(new[] { e });

        var decoded = DecodeAll(ms, out _);
        var d = Assert.Single(decoded);
        Assert.Equal(e, d);
        Assert.Equal(5_023_750_000_000L, d.Price.RawNano);
        Assert.Equal(Price.Undefined, d.Levels[2].BidPrice); // empty slots round-trip as UNDEF
        Assert.Equal(Price.Undefined, d.Levels[1].AskPrice);
    }

    [Fact]
    public void Decoder_MapsActionsToKindsAndCauses()
    {
        var state = TestEvents.Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 4, 1)]);
        var events = new[]
        {
            TestEvents.BookChanged(BookCause.Add, Side.Bid, 4999.75m, 10, 0, state),
            TestEvents.BookChanged(BookCause.Cancel, Side.Bid, 4999.75m, 2, 0, state),
            TestEvents.BookChanged(BookCause.Modify, Side.Ask, 5000.00m, 4, 0, state),
            TestEvents.Trade(Side.Ask, 4999.75m, 3, state),
            TestEvents.Clear(),
        };
        using var ms = WriteFile(events);

        var decoded = DecodeAll(ms, out _);
        Assert.Equal(events.Length, decoded.Count);
        Assert.Equal(
            new[] { (MarketEventKind.BookChanged, BookCause.Add), (MarketEventKind.BookChanged, BookCause.Cancel),
                    (MarketEventKind.BookChanged, BookCause.Modify), (MarketEventKind.Trade, BookCause.None),
                    (MarketEventKind.BookClear, BookCause.None) },
            decoded.Select(d => (d.Kind, d.Cause)).ToArray());
        Assert.Equal(Side.Ask, decoded[3].Side); // aggressor side preserved on trades
    }
}
