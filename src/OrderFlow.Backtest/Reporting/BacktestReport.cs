using OrderFlow.Domain.Trading;

namespace OrderFlow.Backtest.Reporting;

/// <summary>
/// One journal candidate flattened for reporting. Outcome fields are null for candidates
/// that never traded (blocked / expired / cancelled). <see cref="Id"/> is the journal
/// primary key, which is assigned in event order — so ordering rows by Id is chronological
/// and gives a deterministic equity curve.
/// </summary>
public sealed record ReportRow
{
    public required long Id { get; init; }
    public required SetupId Setup { get; init; }
    public required TradeDirection Direction { get; init; }

    /// <summary>Risk-filter that blocked the candidate, or "None".</summary>
    public required string Block { get; init; }

    /// <summary>Null only if the journal row carries no disposition at all.</summary>
    public required CandidateDisposition? Disposition { get; init; }

    public required ExitReason ExitReason { get; init; }
    public required bool T1Filled { get; init; }
    public required long? MaeTicks { get; init; }
    public required long? MfeTicks { get; init; }
    public required decimal? GrossPnl { get; init; }
    public required decimal? Commission { get; init; }
    public required decimal? NetPnl { get; init; }

    public bool Traded => Disposition == CandidateDisposition.Traded;
}

/// <summary>Aggregate stats for a slice of candidates (overall, per setup, or per setup+direction).</summary>
public sealed record SetupStats
{
    public required SetupId? Setup { get; init; }            // null = the overall row
    public required TradeDirection? Direction { get; init; } // null = aggregated across directions
    public required int Candidates { get; init; }
    public required int Blocked { get; init; }
    public required int Expired { get; init; }
    public required int Cancelled { get; init; }
    public required int Trades { get; init; }
    public required int Wins { get; init; }
    public required int Losses { get; init; }
    public required int Scratches { get; init; }
    public required double HitRate { get; init; }       // wins / trades
    public required decimal GrossPnl { get; init; }
    public required decimal Commission { get; init; }
    public required decimal NetPnl { get; init; }
    public required decimal Expectancy { get; init; }   // net / trades
    public required decimal AvgWin { get; init; }
    public required decimal AvgLoss { get; init; }      // negative or zero
    public required double? ProfitFactor { get; init; } // gross win / |gross loss|; null when no losses
    public required double AvgMaeTicks { get; init; }
    public required double AvgMfeTicks { get; init; }
    public required IReadOnlyDictionary<ExitReason, int> ExitReasons { get; init; }
}

public sealed record EquityPoint(int Index, long CandidateId, decimal CumulativeNet);

public sealed record EquityCurve(
    IReadOnlyList<EquityPoint> Points,
    decimal Peak,
    decimal MaxDrawdown,
    decimal FinalEquity);

/// <summary>Order-statistics summary of a tick-valued distribution (MAE or MFE) across trades.</summary>
public sealed record DistributionSummary(
    int Count,
    double Min,
    double P25,
    double Median,
    double P75,
    double P90,
    double Max,
    double Mean);

/// <summary>
/// M6 backtest report: the pure, deterministic aggregation of journal rows into the
/// figures the rulebook/CLAUDE.md call for — per-setup expectancy net of costs, hit rate,
/// MAE/MFE distributions, invalidation-vs-stop exit breakdown and an equity curve. No I/O,
/// no SQLite, no wall clock; identical input rows always yield an identical report.
/// </summary>
public sealed record BacktestReport
{
    public required SetupStats Overall { get; init; }
    public required IReadOnlyList<SetupStats> PerSetup { get; init; }
    public required IReadOnlyList<SetupStats> PerSetupDirection { get; init; }
    public required EquityCurve Equity { get; init; }
    public required DistributionSummary Mae { get; init; }
    public required DistributionSummary Mfe { get; init; }

    /// <summary>Risk-filter name → count of candidates it blocked (excludes "None").</summary>
    public required IReadOnlyDictionary<string, int> BlockedByFilter { get; init; }

    /// <summary>Traded rows in chronological (Id) order — backing the equity-curve / per-trade CSVs.</summary>
    public required IReadOnlyList<ReportRow> Trades { get; init; }

    public static BacktestReport Compute(IReadOnlyList<ReportRow> rows)
    {
        var ordered = rows.OrderBy(r => r.Id).ToList();
        var trades = ordered.Where(r => r.Traded).ToList();

        var perSetup = ordered
            .GroupBy(r => r.Setup)
            .OrderBy(g => (byte)g.Key)
            .Select(g => Aggregate(g.Key, direction: null, g.ToList()))
            .ToList();

        var perSetupDirection = ordered
            .GroupBy(r => (r.Setup, r.Direction))
            .OrderBy(g => (byte)g.Key.Setup).ThenBy(g => (byte)g.Key.Direction)
            .Select(g => Aggregate(g.Key.Setup, g.Key.Direction, g.ToList()))
            .ToList();

        return new BacktestReport
        {
            Overall = Aggregate(setup: null, direction: null, ordered),
            PerSetup = perSetup,
            PerSetupDirection = perSetupDirection,
            Equity = BuildEquity(trades),
            Mae = Summarize(trades.Where(t => t.MaeTicks is not null).Select(t => (double)t.MaeTicks!.Value)),
            Mfe = Summarize(trades.Where(t => t.MfeTicks is not null).Select(t => (double)t.MfeTicks!.Value)),
            BlockedByFilter = ordered
                .Where(r => r.Disposition == CandidateDisposition.Blocked && r.Block != "None")
                .GroupBy(r => r.Block)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            Trades = trades,
        };
    }

    private static SetupStats Aggregate(SetupId? setup, TradeDirection? direction, IReadOnlyList<ReportRow> rows)
    {
        var traded = rows.Where(r => r.Traded).ToList();
        var wins = traded.Where(t => t.NetPnl > 0m).ToList();
        var losses = traded.Where(t => t.NetPnl < 0m).ToList();
        var scratches = traded.Count - wins.Count - losses.Count;

        decimal grossWins = wins.Sum(w => w.NetPnl ?? 0m);
        decimal grossLosses = losses.Sum(l => l.NetPnl ?? 0m); // <= 0
        decimal net = traded.Sum(t => t.NetPnl ?? 0m);

        var exitReasons = new Dictionary<ExitReason, int>();
        foreach (var t in traded)
        {
            exitReasons[t.ExitReason] = exitReasons.GetValueOrDefault(t.ExitReason) + 1;
        }

        return new SetupStats
        {
            Setup = setup,
            Direction = direction,
            Candidates = rows.Count,
            Blocked = rows.Count(r => r.Disposition == CandidateDisposition.Blocked),
            Expired = rows.Count(r => r.Disposition == CandidateDisposition.Expired),
            Cancelled = rows.Count(r => r.Disposition == CandidateDisposition.CancelledInvalidated),
            Trades = traded.Count,
            Wins = wins.Count,
            Losses = losses.Count,
            Scratches = scratches,
            HitRate = traded.Count > 0 ? (double)wins.Count / traded.Count : 0.0,
            GrossPnl = traded.Sum(t => t.GrossPnl ?? 0m),
            Commission = traded.Sum(t => t.Commission ?? 0m),
            NetPnl = net,
            Expectancy = traded.Count > 0 ? net / traded.Count : 0m,
            AvgWin = wins.Count > 0 ? grossWins / wins.Count : 0m,
            AvgLoss = losses.Count > 0 ? grossLosses / losses.Count : 0m,
            ProfitFactor = losses.Count > 0 ? (double)(grossWins / -grossLosses) : (double?)null,
            AvgMaeTicks = Mean(traded.Where(t => t.MaeTicks is not null).Select(t => (double)t.MaeTicks!.Value)),
            AvgMfeTicks = Mean(traded.Where(t => t.MfeTicks is not null).Select(t => (double)t.MfeTicks!.Value)),
            ExitReasons = exitReasons,
        };
    }

    private static EquityCurve BuildEquity(IReadOnlyList<ReportRow> trades)
    {
        var points = new List<EquityPoint>(trades.Count);
        decimal cum = 0m, peak = 0m, maxDd = 0m;
        for (int i = 0; i < trades.Count; i++)
        {
            cum += trades[i].NetPnl ?? 0m;
            if (cum > peak)
            {
                peak = cum;
            }
            decimal dd = peak - cum;
            if (dd > maxDd)
            {
                maxDd = dd;
            }
            points.Add(new EquityPoint(i, trades[i].Id, cum));
        }
        return new EquityCurve(points, peak, maxDd, cum);
    }

    private static double Mean(IEnumerable<double> values)
    {
        double sum = 0; int n = 0;
        foreach (var v in values)
        {
            sum += v;
            n++;
        }
        return n > 0 ? sum / n : 0.0;
    }

    private static DistributionSummary Summarize(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return new DistributionSummary(0, 0, 0, 0, 0, 0, 0, 0);
        }
        return new DistributionSummary(
            Count: sorted.Count,
            Min: sorted[0],
            P25: Percentile(sorted, 0.25),
            Median: Percentile(sorted, 0.50),
            P75: Percentile(sorted, 0.75),
            P90: Percentile(sorted, 0.90),
            Max: sorted[^1],
            Mean: sorted.Average());
    }

    /// <summary>Linear-interpolation percentile on a pre-sorted list (deterministic).</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }
        double rank = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        double frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}
