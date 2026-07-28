// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Time;
using Xunit;

namespace Vixen.Net.Tests;

/// <summary>Tick arithmetic, and the wrap it is built to survive.</summary>
public sealed class TickTests {
    [Fact]
    public void OrderIsWhatItLooksLikeInTheMiddleOfTheRange() {
        var earlier = new Tick(100);
        var later = new Tick(140);

        Assert.True(later.IsAfter(earlier));
        Assert.True(earlier.IsBefore(later));
        Assert.False(later.IsBefore(earlier));
        Assert.Equal(40, later - earlier);
        Assert.Equal(-40, earlier - later);
    }

    [Fact]
    public void ATickIsNeitherBeforeNorAfterItself() {
        var tick = new Tick(7);

        Assert.False(tick.IsAfter(tick));
        Assert.False(tick.IsBefore(tick));
        Assert.Equal(0, tick - tick);
    }

    [Fact]
    public void OrderSurvivesTheWrap() {
        // Two ticks either side of the point where a uint runs out. Compared as numbers, the one
        // that came second is smaller by four billion; compared as ticks, it is three later —
        // MaxValue - 1, MaxValue, 0, 1.
        var beforeWrap = new Tick(uint.MaxValue - 1);
        var afterWrap = new Tick(1);

        Assert.True(afterWrap.IsAfter(beforeWrap));
        Assert.True(beforeWrap.IsBefore(afterWrap));
        Assert.Equal(3, afterWrap - beforeWrap);
        Assert.Equal(-3, beforeWrap - afterWrap);
    }

    [Fact]
    public void CountingPastTheEndComesBackToTheStart() {
        Assert.Equal(new Tick(0), new Tick(uint.MaxValue).Next);
        Assert.Equal(new Tick(uint.MaxValue), new Tick(0).Previous);
        Assert.Equal(new Tick(1), new Tick(uint.MaxValue).Add(2));
        Assert.Equal(new Tick(uint.MaxValue), new Tick(1).Subtract(2));
    }

    [Fact]
    public void AddingAndSubtractingAreTheOperatorsToo() {
        var tick = new Tick(10);

        Assert.Equal(new Tick(13), tick + 3);
        Assert.Equal(new Tick(7), tick - 3);
        Assert.Equal(new Tick(7), tick + -3);
    }

    [Fact]
    public void ATickRateKnowsHowLongATickIs() {
        var rate = new TickRate(50);

        Assert.Equal(TimeSpan.FromMilliseconds(20), rate.Duration);
        Assert.Equal(3, rate.ToTicks(TimeSpan.FromMilliseconds(60)));
        Assert.Equal(TimeSpan.FromMilliseconds(60), rate.ToTime(3));

        // Rounded to nearest, not truncated: 29 ms is closer to one tick and a half than to one, and
        // a lead computed from a round trip that always rounded down would always be a tick short.
        Assert.Equal(1, rate.ToTicks(TimeSpan.FromMilliseconds(29)));
        Assert.Equal(2, rate.ToTicks(TimeSpan.FromMilliseconds(31)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void ATickRateOutsideItsRange_Throws(int ticksPerSecond) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickRate(ticksPerSecond));

    [Fact]
    public void ADefaultConstructedTickRate_SaysSoRatherThanDividingByZero() {
        var rate = default(TickRate);

        Assert.False(rate.IsValid);
        Assert.Throws<InvalidOperationException>(() => rate.Duration);
    }
}
