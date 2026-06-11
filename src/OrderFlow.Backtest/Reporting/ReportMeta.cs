namespace OrderFlow.Backtest.Reporting;

/// <summary>Context shown in the report header — sources replayed and the cost assumptions
/// the P&L is net of (CLAUDE.md: all journal P&L is net; surface the knobs that produced it).</summary>
public sealed record ReportMeta(
    string Title,
    IReadOnlyList<string> Sources,
    decimal CommissionPerRoundTurn,
    decimal TickValue);
