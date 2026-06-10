using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Pure book-shape feature calculators over a top-10 snapshot (rulebook 2.1, F1–F4 plus
/// the F5 depth input). Stateless and allocation-free; null when the book cannot answer
/// (one-sided / empty / fewer than two levels for a slope).
/// </summary>
public static class BookShapeFeatures
{
    /// <summary>F1: (ask − bid) / tick; null when either side is empty.</summary>
    public static long? SpreadTicks(in BookLevels levels, TickSize tick)
    {
        var top = levels[0];
        if (!top.HasBid || !top.HasAsk)
        {
            return null;
        }
        return (top.AskPrice.RawNano - top.BidPrice.RawNano) / tick.RawNano;
    }

    /// <summary>F2: (Σbid_k − Σask_k) / (Σbid_k + Σask_k) over the top k populated levels.</summary>
    public static double? DepthImbalance(in BookLevels levels, int k)
    {
        long bid = 0, ask = 0;
        for (int i = 0; i < Math.Min(k, BookLevels.Depth); i++)
        {
            var lvl = levels[i];
            bid += lvl.HasBid ? lvl.BidSize : 0;
            ask += lvl.HasAsk ? lvl.AskSize : 0;
        }
        long total = bid + ask;
        return total > 0 ? (double)(bid - ask) / total : null;
    }

    /// <summary>F3: OLS slope of ln(size) against level index over populated levels; null below 2 levels.</summary>
    public static double? BookSlope(in BookLevels levels, Side side)
    {
        Span<double> y = stackalloc double[BookLevels.Depth];
        Span<int> x = stackalloc int[BookLevels.Depth];
        int n = 0;
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var lvl = levels[i];
            long size = side == Side.Bid ? (lvl.HasBid ? lvl.BidSize : 0) : (lvl.HasAsk ? lvl.AskSize : 0);
            if (size > 0)
            {
                x[n] = i;
                y[n] = Math.Log(size);
                n++;
            }
        }
        if (n < 2)
        {
            return null;
        }
        double xMean = 0, yMean = 0;
        for (int i = 0; i < n; i++)
        {
            xMean += x[i];
            yMean += y[i];
        }
        xMean /= n;
        yMean /= n;
        double cov = 0, varX = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - xMean;
            cov += dx * (y[i] - yMean);
            varX += dx * dx;
        }
        return cov / varX;
    }

    /// <summary>F4: ln(displayed(bid₁) / displayed(ask₁)); null when either side is empty.</summary>
    public static double? BboSizeRatio(in BookLevels levels)
    {
        var top = levels[0];
        if (!top.HasBid || !top.HasAsk)
        {
            return null;
        }
        return Math.Log((double)top.BidSize / top.AskSize);
    }

    /// <summary>F5 input: combined displayed size over the top 5 levels of both sides.</summary>
    public static long Top5Depth(in BookLevels levels)
    {
        long total = 0;
        for (int i = 0; i < 5; i++)
        {
            var lvl = levels[i];
            total += lvl.HasBid ? lvl.BidSize : 0;
            total += lvl.HasAsk ? lvl.AskSize : 0;
        }
        return total;
    }
}
