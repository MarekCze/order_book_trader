using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Global filters 1–6 (rulebook "Global filters"). Filter numbering:
/// 1 session (RTH, minus first 2 min and news windows), 2 location (≤ 4 ticks from LOI),
/// 3 volatility (ATR pctl within [20th, 95th]), 4 spread (= 1 tick), 5 exposure
/// (one position; ≤ 3 attempts per LOI per session; 2 consecutive stop-outs kill the
/// level for the day), 6 sizing (fixed fractional ≤ 0.5%).
/// </summary>
public class RiskManagerTests
{
    /// <summary>10:00 EDT mid-RTH on 2026-06-01 (EDT = UTC−4).</summary>
    private static readonly Timestamp MidRth =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero));

    private static Timestamp AtUtc(int hour, int minute, int second = 0) =>
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, hour, minute, second, TimeSpan.Zero));

    private const long Level = 5_000_000_000_000; // 5000.00 raw nano

    private static CandidateContext Ok(Timestamp? ts = null) => new(
        Ts: ts ?? MidRth,
        LevelKey: Level,
        LoiDistanceTicks: 1,
        AtrPercentile: 0.50,
        NewsWindow: false,
        SpreadTicks: 1,
        StopDistanceTicks: 4);

    private static RiskManager NewRisk(RiskOptions? opts = null) => new(opts ?? new RiskOptions());

    // ----- filter 1: session -----

    [Fact]
    public void Blocks_OutsideRth()
    {
        var risk = NewRisk();
        var verdict = risk.Evaluate(Ok(AtUtc(11, 0))); // 07:00 ET pre-market
        Assert.Equal(RiskBlock.Session, verdict.Block);
    }

    [Fact]
    public void Blocks_FirstTwoMinutesAfterOpen()
    {
        var risk = NewRisk();
        Assert.Equal(RiskBlock.Session, risk.Evaluate(Ok(AtUtc(13, 31, 30))).Block); // 09:31:30 ET
        Assert.True(risk.Evaluate(Ok(AtUtc(13, 32, 0))).Approved);                   // 09:32:00 ET
    }

    [Fact]
    public void Blocks_NewsWindow()
    {
        var risk = NewRisk();
        Assert.Equal(RiskBlock.Session, risk.Evaluate(Ok() with { NewsWindow = true }).Block);
    }

    // ----- filter 2: location -----

    [Fact]
    public void Blocks_WhenFarFromLoi_OrLoiUnknown()
    {
        var risk = NewRisk();
        Assert.Equal(RiskBlock.Location, risk.Evaluate(Ok() with { LoiDistanceTicks = 5 }).Block);
        Assert.Equal(RiskBlock.Location, risk.Evaluate(Ok() with { LoiDistanceTicks = -5 }).Block);
        Assert.Equal(RiskBlock.Location, risk.Evaluate(Ok() with { LoiDistanceTicks = null }).Block);
        Assert.True(risk.Evaluate(Ok() with { LoiDistanceTicks = -4 }).Approved);
    }

    // ----- filter 3: volatility -----

    [Fact]
    public void Blocks_AtrOutsideBand_OrUnknown()
    {
        var risk = NewRisk();
        Assert.Equal(RiskBlock.Volatility, risk.Evaluate(Ok() with { AtrPercentile = 0.10 }).Block);
        Assert.Equal(RiskBlock.Volatility, risk.Evaluate(Ok() with { AtrPercentile = 0.99 }).Block);
        Assert.Equal(RiskBlock.Volatility, risk.Evaluate(Ok() with { AtrPercentile = null }).Block);
        Assert.True(risk.Evaluate(Ok() with { AtrPercentile = 0.20 }).Approved);
        Assert.True(risk.Evaluate(Ok() with { AtrPercentile = 0.95 }).Approved);
    }

    // ----- filter 4: spread -----

    [Fact]
    public void Blocks_SpreadNotOneTick()
    {
        var risk = NewRisk();
        Assert.Equal(RiskBlock.Spread, risk.Evaluate(Ok() with { SpreadTicks = 2 }).Block);
        Assert.Equal(RiskBlock.Spread, risk.Evaluate(Ok() with { SpreadTicks = null }).Block);
    }

    // ----- filter 5: exposure -----

    [Fact]
    public void Blocks_WhileReserved_UntilReleased()
    {
        var risk = NewRisk();
        Assert.True(risk.Evaluate(Ok()).Approved);
        risk.Reserve(Level);
        Assert.Equal(RiskBlock.PositionOpen, risk.Evaluate(Ok()).Block);
        risk.Release(Level, TradeResult.NoFill);
        Assert.True(risk.Evaluate(Ok()).Approved);
    }

    [Fact]
    public void Blocks_FourthAttemptAtSameLevel_OtherLevelsUnaffected()
    {
        var risk = NewRisk();
        for (int i = 0; i < 3; i++)
        {
            risk.Reserve(Level);
            risk.Release(Level, TradeResult.NoFill);
        }
        Assert.Equal(RiskBlock.AttemptsExhausted, risk.Evaluate(Ok()).Block);
        Assert.True(risk.Evaluate(Ok() with { LevelKey = Level + 250_000_000 }).Approved);
    }

    [Fact]
    public void TwoConsecutiveStopOuts_KillTheLevelForTheSession()
    {
        var risk = NewRisk();
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.StopOut);
        Assert.True(risk.Evaluate(Ok()).Approved); // one stop-out: still allowed
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.StopOut);
        Assert.Equal(RiskBlock.LevelDead, risk.Evaluate(Ok()).Block);
    }

    [Fact]
    public void WinBetweenStopOuts_ResetsTheConsecutiveCount()
    {
        var risk = NewRisk();
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.StopOut);
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.Win);
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.StopOut);
        Assert.Equal(RiskBlock.AttemptsExhausted, risk.Evaluate(Ok()).Block); // 3 attempts used, but not dead
    }

    [Fact]
    public void SessionRollover_ResetsAttemptsAndDeadLevels()
    {
        var risk = NewRisk();
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.StopOut);
        risk.Reserve(Level);
        risk.Release(Level, TradeResult.StopOut);
        Assert.Equal(RiskBlock.LevelDead, risk.Evaluate(Ok()).Block);
        risk.OnSessionRollover();
        Assert.True(risk.Evaluate(Ok()).Approved);
    }

    // ----- filter 6: sizing -----

    [Fact]
    public void Sizing_FixedFractional_FloorOfRiskBudgetOverPerContractRisk()
    {
        // 100k × 0.5% = $500 budget; 4 ticks × $12.50 = $50/contract → 10 contracts.
        var verdict = NewRisk().Evaluate(Ok());
        Assert.Equal(10, verdict.Quantity);
    }

    [Fact]
    public void Sizing_RespectsContractCap_AndBlocksZeroSize()
    {
        var capped = NewRisk(new RiskOptions { MaxPositionContracts = 4 }).Evaluate(Ok());
        Assert.Equal(4, capped.Quantity);
        var tiny = NewRisk(new RiskOptions { AccountEquity = 1_000m }).Evaluate(Ok());
        Assert.Equal(RiskBlock.ZeroSize, tiny.Block); // $5 budget < $50/contract
    }
}
