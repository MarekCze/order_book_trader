using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>Setup 4 (LVN vacuum) transition guards as pure functions over direction-normalized
/// inputs: "continuation" is the trade direction (down for the stated short, up for the long
/// mirror); directional delta is signed positive when aggressors push the continuation way.</summary>
public class LvnVacuumGuardTests
{
    private static readonly LvnVacuumOptions O = new();

    [Fact]
    public void D1_RoomToPay_RequiresHvnFarEnough()
    {
        Assert.True(LvnVacuumGuards.D1_RoomToPay(8, O));   // exactly the room threshold
        Assert.True(LvnVacuumGuards.D1_RoomToPay(12, O));
        Assert.False(LvnVacuumGuards.D1_RoomToPay(7, O));  // HVN too close — no room to pay
    }

    [Fact]
    public void D2_Pulling_RequiresDepthDeclineAndCancelsDominating()
    {
        Assert.True(LvnVacuumGuards.D2_Pulling(-0.40, 1.6, O));  // 40% decline, pull ratio > 1.5
        Assert.True(LvnVacuumGuards.D2_Pulling(-0.55, 2.0, O));
        Assert.False(LvnVacuumGuards.D2_Pulling(-0.30, 2.0, O)); // only 30% decline
        Assert.False(LvnVacuumGuards.D2_Pulling(-0.50, 1.4, O)); // adds keep pace (pull ≤ 1.5)
        Assert.False(LvnVacuumGuards.D2_Pulling(null, 2.0, O));  // no depth history
        Assert.False(LvnVacuumGuards.D2_Pulling(-0.50, null, O)); // nothing added → no ratio
    }

    [Fact]
    public void D3_AggressorAlignment_RequiresAlignedFlow()
    {
        Assert.True(LvnVacuumGuards.D3_AggressorAlignment(100, O));  // exactly 100 contracts the trade way
        Assert.True(LvnVacuumGuards.D3_AggressorAlignment(250, O));
        Assert.False(LvnVacuumGuards.D3_AggressorAlignment(80, O));  // too weak
        Assert.False(LvnVacuumGuards.D3_AggressorAlignment(-300, O)); // aggression against the trade
    }
}
