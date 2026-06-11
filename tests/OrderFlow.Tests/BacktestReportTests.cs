using OrderFlow.Backtest.Reporting;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// M6 reporting: the pure statistics engine that turns journal rows into per-setup
/// expectancy/hit-rate, exit-reason breakdowns, an equity curve with drawdown, and
/// MAE/MFE distributions. Computation is pure and deterministic — these tests feed
/// hand-crafted rows and assert the aggregates, no SQLite involved.
/// </summary>
public class BacktestReportTests
{
    private static ReportRow Traded(
        long id, SetupId setup, TradeDirection dir, ExitReason exit,
        decimal net, long mae, long mfe, decimal commission = 1.40m) => new()
    {
        Id = id,
        Setup = setup,
        Direction = dir,
        Block = "None",
        Disposition = CandidateDisposition.Traded,
        ExitReason = exit,
        T1Filled = exit == ExitReason.Target,
        MaeTicks = mae,
        MfeTicks = mfe,
        GrossPnl = net + commission,
        Commission = commission,
        NetPnl = net,
    };

    private static ReportRow Blocked(long id, SetupId setup, TradeDirection dir, string block) => new()
    {
        Id = id,
        Setup = setup,
        Direction = dir,
        Block = block,
        Disposition = CandidateDisposition.Blocked,
        ExitReason = ExitReason.None,
        T1Filled = false,
        MaeTicks = null,
        MfeTicks = null,
        GrossPnl = null,
        Commission = null,
        NetPnl = null,
    };

    private static ReportRow NoTrade(long id, SetupId setup, TradeDirection dir, CandidateDisposition disp) => new()
    {
        Id = id,
        Setup = setup,
        Direction = dir,
        Block = "None",
        Disposition = disp,
        ExitReason = ExitReason.None,
        T1Filled = false,
        MaeTicks = null,
        MfeTicks = null,
        GrossPnl = null,
        Commission = null,
        NetPnl = null,
    };

    [Fact]
    public void PerSetup_AggregatesTrades_HitRate_Expectancy_ExitReasons()
    {
        var rows = new[]
        {
            Traded(1, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Target, net: 100m, mae: 2, mfe: 8),
            Traded(2, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Stop, net: -50m, mae: 6, mfe: 1),
            Blocked(3, SetupId.AbsorptionFade, TradeDirection.Long, "Volatility"),
            Traded(4, SetupId.StopRunFade, TradeDirection.Short, ExitReason.Target, net: 30m, mae: 1, mfe: 5),
            NoTrade(5, SetupId.StopRunFade, TradeDirection.Short, CandidateDisposition.Expired),
        };

        var report = BacktestReport.Compute(rows);

        Assert.Equal(3, report.Overall.Trades);
        Assert.Equal(2, report.Overall.Wins);
        Assert.Equal(1, report.Overall.Losses);
        Assert.Equal(80m, report.Overall.NetPnl);

        var s1 = report.PerSetup.Single(s => s.Setup == SetupId.AbsorptionFade);
        Assert.Equal(3, s1.Candidates);
        Assert.Equal(1, s1.Blocked);
        Assert.Equal(2, s1.Trades);
        Assert.Equal(1, s1.Wins);
        Assert.Equal(1, s1.Losses);
        Assert.Equal(50m, s1.NetPnl);
        Assert.Equal(25m, s1.Expectancy);     // 50 / 2 trades
        Assert.Equal(0.5, s1.HitRate, 6);
        Assert.Equal(100m, s1.AvgWin);
        Assert.Equal(-50m, s1.AvgLoss);
        Assert.Equal(2.0, s1.ProfitFactor!.Value, 6); // 100 / 50
        Assert.Equal(1, s1.ExitReasons[ExitReason.Target]);
        Assert.Equal(1, s1.ExitReasons[ExitReason.Stop]);

        var s2 = report.PerSetup.Single(s => s.Setup == SetupId.StopRunFade);
        Assert.Equal(2, s2.Candidates);
        Assert.Equal(1, s2.Expired);
        Assert.Equal(1, s2.Trades);
        Assert.Null(s2.ProfitFactor); // no losses -> undefined
    }

    [Fact]
    public void PerSetupDirection_SplitsByDirection()
    {
        var rows = new[]
        {
            Traded(1, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Target, net: 20m, mae: 1, mfe: 4),
            Traded(2, SetupId.AbsorptionFade, TradeDirection.Short, ExitReason.Stop, net: -10m, mae: 3, mfe: 1),
        };

        var report = BacktestReport.Compute(rows);

        var longs = report.PerSetupDirection.Single(s => s.Setup == SetupId.AbsorptionFade && s.Direction == TradeDirection.Long);
        var shorts = report.PerSetupDirection.Single(s => s.Setup == SetupId.AbsorptionFade && s.Direction == TradeDirection.Short);
        Assert.Equal(20m, longs.NetPnl);
        Assert.Equal(-10m, shorts.NetPnl);
    }

    [Fact]
    public void EquityCurve_IsCumulativeInIdOrder_WithMaxDrawdown()
    {
        var rows = new[]
        {
            Traded(1, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Target, net: 100m, mae: 1, mfe: 5),
            Traded(2, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Stop, net: -60m, mae: 4, mfe: 1),
            Traded(3, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Stop, net: -30m, mae: 5, mfe: 1),
            Traded(4, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Target, net: 50m, mae: 1, mfe: 6),
        };

        var report = BacktestReport.Compute(rows);
        var pts = report.Equity.Points;

        Assert.Equal(4, pts.Count);
        Assert.Equal(100m, pts[0].CumulativeNet);
        Assert.Equal(40m, pts[1].CumulativeNet);
        Assert.Equal(10m, pts[2].CumulativeNet);
        Assert.Equal(60m, pts[3].CumulativeNet);
        Assert.Equal(60m, report.Equity.FinalEquity);
        Assert.Equal(100m, report.Equity.Peak);
        // Peak 100 -> trough 10 = 90 drawdown.
        Assert.Equal(90m, report.Equity.MaxDrawdown);
    }

    [Fact]
    public void MaeMfe_Distribution_SummarizesTradedRows()
    {
        var rows = new[]
        {
            Traded(1, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Target, net: 10m, mae: 2, mfe: 8),
            Traded(2, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Stop, net: -10m, mae: 4, mfe: 2),
            Traded(3, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Stop, net: -10m, mae: 6, mfe: 0),
            Blocked(4, SetupId.AbsorptionFade, TradeDirection.Long, "Spread"),
        };

        var report = BacktestReport.Compute(rows);

        Assert.Equal(3, report.Mae.Count); // blocked row excluded
        Assert.Equal(2.0, report.Mae.Min, 6);
        Assert.Equal(6.0, report.Mae.Max, 6);
        Assert.Equal(4.0, report.Mae.Mean, 6);
        Assert.Equal(4.0, report.Mae.Median, 6);
        Assert.Equal(8.0, report.Mfe.Max, 6);
    }

    [Fact]
    public void EmptyInput_ProducesEmptyButValidReport()
    {
        var report = BacktestReport.Compute(Array.Empty<ReportRow>());

        Assert.Equal(0, report.Overall.Trades);
        Assert.Empty(report.PerSetup);
        Assert.Empty(report.Equity.Points);
        Assert.Equal(0m, report.Equity.MaxDrawdown);
        Assert.Equal(0, report.Mae.Count);
    }
}
