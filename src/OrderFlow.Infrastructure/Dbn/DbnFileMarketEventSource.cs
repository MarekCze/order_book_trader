using System.Threading.Channels;
using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Events;

namespace OrderFlow.Infrastructure.Dbn;

/// <summary>Pumps a DBN file (optionally zstd-compressed) into the pipeline channel.</summary>
public sealed class DbnFileMarketEventSource : IMarketEventSource
{
    private readonly string _path;

    public DbnFileMarketEventSource(string path)
    {
        _path = path;
    }

    public DbnMetadata? Metadata { get; private set; }

    public long EventsRead { get; private set; }

    public long IgnoredMboActionCount { get; private set; }

    public IReadOnlyList<long> SkippedByRtype { get; private set; } = Array.Empty<long>();

    public async Task PumpAsync(ChannelWriter<MarketEvent> writer, CancellationToken cancellationToken = default)
    {
        try
        {
            using var decoder = DbnDecoder.Open(_path);
            Metadata = decoder.Metadata;
            while (decoder.TryReadEvent(out var e))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!writer.TryWrite(e))
                {
                    await writer.WriteAsync(e, cancellationToken).ConfigureAwait(false);
                }
                EventsRead++;
            }
            SkippedByRtype = decoder.SkippedByRtype.ToArray();
            IgnoredMboActionCount = decoder.IgnoredMboActionCount;
            writer.Complete();
        }
        catch (Exception ex)
        {
            writer.Complete(ex);
            throw;
        }
    }
}
