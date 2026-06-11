using System.Text;

namespace OrderFlow.Domain.Trading;

/// <summary>
/// Per-condition funnel telemetry shared by all setup detectors: for each named rulebook
/// condition in a detector's guard chain, counts how many times the guard was evaluated
/// and how many times it passed, in chain order. Because guards short-circuit, a
/// condition's evaluated count is the number of events that survived everything before
/// it — so the summary shows exactly which condition is the binding constraint (silent
/// setups) and whether any condition filters at all (pass-everything setups).
/// Diagnostic only — never read by trading logic, never journaled (no schema change).
/// </summary>
public sealed class ConditionFunnel
{
    private readonly string[] _names;
    private readonly long[] _evaluated;
    private readonly long[] _passed;

    public ConditionFunnel(params string[] conditionNames)
    {
        _names = conditionNames;
        _evaluated = new long[conditionNames.Length];
        _passed = new long[conditionNames.Length];
    }

    /// <summary>Records one evaluation of <paramref name="condition"/> and passes the
    /// guard's verdict through, so call sites stay single-expression:
    /// <c>if (!Conditions.Check(E1, Setup5Guards.E1_…(…))) return;</c></summary>
    public bool Check(int condition, bool passed)
    {
        _evaluated[condition]++;
        if (passed)
        {
            _passed[condition]++;
        }
        return passed;
    }

    public int Count => _names.Length;

    public string Name(int condition) => _names[condition];

    public long Evaluated(int condition) => _evaluated[condition];

    public long Passed(int condition) => _passed[condition];

    /// <summary>Name-based lookups for tests/tools (the hot path uses the int overloads).</summary>
    public long Evaluated(string name) => _evaluated[IndexOf(name)];

    public long Passed(string name) => _passed[IndexOf(name)];

    private int IndexOf(string name)
    {
        int i = Array.IndexOf(_names, name);
        return i >= 0 ? i : throw new ArgumentOutOfRangeException(nameof(name), name, "unknown condition");
    }

    /// <summary>One-line chain summary, "name passed/evaluated" per condition in chain
    /// order: <c>E1 3/120 → E2 1/3 → …</c>.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _names.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(" → ");
            }
            sb.Append(_names[i]).Append(' ')
              .Append(_passed[i].ToString("N0")).Append('/').Append(_evaluated[i].ToString("N0"));
        }
        return sb.ToString();
    }
}
