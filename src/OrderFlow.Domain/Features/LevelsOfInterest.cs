using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>LOI taxonomy from the rulebook. Only RoundNumber is computable in M2; the
/// profile-derived types arrive with M3's LOI engine.</summary>
public enum LoiType : byte
{
    RoundNumber = 1,
    PriorDayHigh = 2,
    PriorDayLow = 3,
    OvernightHigh = 4,
    OvernightLow = 5,
    PriorPoc = 6,
    Vah = 7,
    Val = 8,
    Lvn = 9,
    NakedPoc = 10,
}

/// <summary>A concrete LOI relative to a query price. Positive distance = price above the LOI.</summary>
public readonly record struct LoiReference(Price Price, LoiType Type, long SignedDistanceTicks);

/// <summary>
/// Setup 5 swing-divergence sample: a new 30-minute extreme (<paramref name="NewExtreme"/>)
/// and the prior swing extreme it followed (<paramref name="PriorExtreme"/>), each tagged with
/// the session cumulative delta at the moment it printed. <paramref name="Ts"/> is the event
/// that produced the new extreme, so a detector can distinguish a fresh sample from a stale one.
/// </summary>
public readonly record struct SwingDivergence(
    Price PriorExtreme, Price NewExtreme, long CumDeltaPrior, long CumDeltaNew, Timestamp Ts);

public interface ILoiProvider
{
    bool TryGetNearest(Price price, TickSize tick, out LoiReference loi);
}

/// <summary>
/// Round-number LOIs (multiples of 25.00 for ES) — the one LOI source computable without
/// M3's profile engine. Ties between two equidistant round numbers resolve to the higher
/// one (round half away from zero), deterministically.
/// </summary>
public sealed class RoundNumberLoiProvider : ILoiProvider
{
    private readonly long _intervalRaw;

    public RoundNumberLoiProvider(decimal intervalPoints)
    {
        if (intervalPoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalPoints));
        }
        _intervalRaw = Price.FromDecimal(intervalPoints).RawNano;
    }

    public bool TryGetNearest(Price price, TickSize tick, out LoiReference loi)
    {
        long half = _intervalRaw / 2;
        long shifted = price.RawNano >= 0 ? price.RawNano + half : price.RawNano - half;
        long nearestRaw = shifted / _intervalRaw * _intervalRaw;
        var nearest = new Price(nearestRaw);
        loi = new LoiReference(nearest, LoiType.RoundNumber, price.TicksFrom(nearest, tick));
        return true;
    }
}
