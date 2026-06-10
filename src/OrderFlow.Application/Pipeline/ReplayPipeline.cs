using System.Threading.Channels;
using OrderFlow.Domain.Events;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Wires source → channel → book builder. This is the pipeline spine; later milestones
/// hang the feature engine, detectors and execution off <see cref="IBookEventObserver"/>.
/// </summary>
public static class ReplayPipeline
{
    public const int DefaultChannelCapacity = 1 << 16;

    public static async Task RunAsync(
        IMarketEventSource source,
        BookBuilderStage bookBuilder,
        IBookEventObserver observer,
        int channelCapacity = DefaultChannelCapacity,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<MarketEvent>(new BoundedChannelOptions(channelCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var consumer = bookBuilder.RunAsync(channel.Reader, observer, cancellationToken);
        await source.PumpAsync(channel.Writer, cancellationToken).ConfigureAwait(false);
        await consumer.ConfigureAwait(false);
    }
}
