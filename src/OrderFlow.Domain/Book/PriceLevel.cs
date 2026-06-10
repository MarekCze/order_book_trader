using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Book;

/// <summary>Aggregated (MBP-style) view of one price level, derived on demand from tracked orders.</summary>
public readonly record struct PriceLevel(Price Price, long DisplayedSize, int OrderCount);
