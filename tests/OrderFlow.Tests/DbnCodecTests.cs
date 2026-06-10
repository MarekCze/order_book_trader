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
        using (var writer = new DbnMboWriter(ms, options, leaveOpen: true))
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
        var events = new SyntheticMboGenerator(seed: 5).Generate(2_000).ToArray();
        using var ms = WriteFile(events, new DbnWriterOptions { Version = version, ZstdCompress = zstd });

        var decoded = DecodeAll(ms, out var decoder);
        Assert.Equal(events, decoded); // record-struct equality: all fields
        Assert.Equal(version, decoder.Metadata.Version);
        Assert.True(decoder.Metadata.IsMboSchema);
        Assert.Equal("GLBX.MDP3", decoder.Metadata.Dataset);
        Assert.Equal(71, decoder.Metadata.SymbolCstrLen);
        Assert.Equal(new[] { "ES.c.0" }, decoder.Metadata.Symbols);
    }

    [Fact]
    public void RoundTrip_WithTsOutTail_IgnoresAppendedTimestamp()
    {
        var events = new SyntheticMboGenerator(seed: 6).Generate(500).ToArray();
        using var ms = WriteFile(events, new DbnWriterOptions { TsOut = true, ZstdCompress = false });

        var decoded = DecodeAll(ms, out var decoder);
        Assert.True(decoder.Metadata.TsOut);
        Assert.Equal(events, decoded);
    }

    [Fact]
    public void Decoder_SkipsForeignRecordTypes_WithoutCrashing()
    {
        var events = new SyntheticMboGenerator(seed: 7).Generate(100).ToArray();
        var ms = new MemoryStream();
        using (var writer = new DbnMboWriter(ms, new DbnWriterOptions { ZstdCompress = false }, leaveOpen: true))
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
        var snapshotAdds = new SyntheticMboGenerator(seed: 8, seedFlaggedAsSnapshot: true)
            .Generate(120).ToArray(); // all 120 are seed adds → snapshot-flagged
        Assert.All(snapshotAdds, e => Assert.True(e.IsSnapshot));
        var live = new SyntheticMboGenerator(seed: 9).Generate(50)
            .Where(e => !e.IsSnapshot).ToArray();

        using var ms = WriteFile(snapshotAdds.Concat(live).ToArray());
        var decoded = DecodeAll(ms, out _);

        Assert.Equal(120 + 1 + live.Length, decoded.Count);
        Assert.Equal(MarketEventKind.SnapshotEnd, decoded[120].Kind);
        Assert.Equal(snapshotAdds, decoded.Take(120));
        Assert.Equal(live, decoded.Skip(121));
    }

    [Fact]
    public void Decoder_ThrowsDbnFormat_OnTruncatedRecord()
    {
        var events = new SyntheticMboGenerator(seed: 10).Generate(20).ToArray();
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
    public void Decoder_PreservesPriceAndTimestampPrecision()
    {
        // Exercise the exact wire encoding: nanoprice and nanosecond timestamps survive intact.
        var e = new MarketEvent(
            MarketEventKind.AddOrder, InstrumentId: 12345,
            TsEvent: new Timestamp(1_767_621_600_123_456_789UL),
            TsRecv: new Timestamp(1_767_621_600_123_456_999UL),
            Sequence: 4_000_000_001,
            OrderId: 0xDEADBEEFCAFE,
            Price: Price.FromDecimal(5023.75m),
            Size: 17,
            Side: Side.Ask,
            Flags: MarketEventFlags.Last | MarketEventFlags.TopOfBook);
        using var ms = WriteFile(new[] { e });

        var decoded = DecodeAll(ms, out _);
        Assert.Equal(e, Assert.Single(decoded));
        Assert.Equal(5_023_750_000_000L, decoded[0].Price.RawNano);
    }
}
