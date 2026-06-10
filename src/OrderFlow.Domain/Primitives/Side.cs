namespace OrderFlow.Domain.Primitives;

/// <summary>Book side / aggressor side. Maps DBN side chars: 'B' = Bid, 'A' = Ask, 'N' = None.</summary>
public enum Side : byte
{
    None = 0,
    Bid = 1,
    Ask = 2,
}

public static class SideExtensions
{
    public static Side Opposite(this Side side) => side switch
    {
        Side.Bid => Side.Ask,
        Side.Ask => Side.Bid,
        _ => Side.None,
    };
}
