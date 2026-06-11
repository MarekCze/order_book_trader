using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

public class ConditionFunnelTests
{
    [Fact]
    public void Check_CountsEvaluationsAndPasses_AndPassesVerdictThrough()
    {
        var funnel = new ConditionFunnel("E1", "E2");
        Assert.True(funnel.Check(0, true));
        Assert.False(funnel.Check(0, false));
        Assert.True(funnel.Check(1, true));

        Assert.Equal(2, funnel.Evaluated(0));
        Assert.Equal(1, funnel.Passed(0));
        Assert.Equal(1, funnel.Evaluated(1));
        Assert.Equal(1, funnel.Passed(1));
    }

    [Fact]
    public void NameLookups_MatchIntLookups()
    {
        var funnel = new ConditionFunnel("A1", "A2");
        funnel.Check(1, true);
        Assert.Equal(0, funnel.Passed("A1"));
        Assert.Equal(1, funnel.Passed("A2"));
        Assert.Equal(1, funnel.Evaluated("A2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => funnel.Passed("nope"));
    }

    [Fact]
    public void Summary_RendersChainInOrder()
    {
        var funnel = new ConditionFunnel("E1", "E2", "E3");
        funnel.Check(0, true);
        funnel.Check(0, false);
        funnel.Check(1, true);

        Assert.Equal("E1 1/2 → E2 1/1 → E3 0/0", funnel.Summary());
    }
}
