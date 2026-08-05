// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     One disturbance, two consumers — [docs/plan/35 § D12]'s wake and splash hooks.
/// </summary>
/// <remarks>
///     ⚠ <b>What is asserted is the sharing, because that is the part with a decision in it.</b> A
///     ripple field turns a disturbance into an injection and <c>Vixen.Vfx</c> turns it into a burst
///     of spray; two producers would be a wake whose spray is not where the ripple is, and the frame
///     they stop agreeing on is the frame something changed in only one of them.
/// </remarks>
public sealed class WaterDisturbanceTests {
    /// <summary>
    ///     ⚠ Draining does not empty, which is what lets there be two consumers.
    /// </summary>
    /// <remarks>
    ///     A queue that emptied itself on the first read would give whichever system was added second
    ///     nothing at all — a wake with no spray, or spray with no wake, depending on an ordering
    ///     nobody chose. The step that produced them is what clears it.
    /// </remarks>
    [Fact]
    public void Two_consumers_each_see_every_disturbance() {
        var queue = new WaterDisturbances();
        var ripples = new WaterRipples(WaterRippleSettings.Default with { Extent = 16f, Resolution = 33 });

        queue.Add(new(new(8f, 8f), 1f, -3f, WaterDisturbanceKind.Splash, 0f));
        queue.Add(new(new(6f, 9f), 0.5f, -1f, WaterDisturbanceKind.Wake, 0f));

        Assert.Equal(2, ripples.Apply(queue));

        // The second consumer — a particle system, here standing in for one — sees the same two.
        var spray = 0;

        foreach (var disturbance in queue.Queued) {
            _ = disturbance;
            spray++;
        }

        Assert.Equal(2, spray);
        Assert.Equal(2, queue.Count);

        queue.Clear();

        Assert.Equal(0, queue.Count);
    }

    /// <summary>The budget is a bound and what does not fit is counted, not dropped in silence.</summary>
    /// <remarks>
    ///     ⚠ § D12's own wording: an unbounded number of sources is how this feature becomes a
    ///     frame-time cliff, and a budget nobody can see is one nobody raises before shipping.
    /// </remarks>
    [Fact]
    public void What_does_not_fit_is_counted() {
        var queue = new WaterDisturbances(budget: 2);

        Assert.True(queue.Add(new(Vector2.Zero, 1f, -1f, WaterDisturbanceKind.Wake, 0f)));
        Assert.True(queue.Add(new(Vector2.Zero, 1f, -1f, WaterDisturbanceKind.Wake, 0f)));
        Assert.False(queue.Add(new(Vector2.Zero, 1f, -1f, WaterDisturbanceKind.Wake, 0f)));

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.Overflowed);
    }

    /// <summary>
    ///     ⚠ A disturbance is a rate, not a displacement — a boat sitting still makes no hole.
    /// </summary>
    /// <remarks>
    ///     <c>WaterRipples.Inject</c>'s own rule, asserted through the queue because that is the path
    ///     a wake actually takes: a source that pushed the height down would carve a permanent dent in
    ///     the lake, where one that pushes the rate down makes a depression that springs back.
    /// </remarks>
    [Fact]
    public void A_disturbance_pushes_the_rate_and_the_surface_springs_back() {
        var settings = WaterRippleSettings.Default with { Extent = 16f, Resolution = 33, Speed = 2f };
        var queue = new WaterDisturbances();
        var ripples = new WaterRipples(settings);

        queue.Add(new(new(8f, 8f), 1.5f, -8f, WaterDisturbanceKind.Splash, 0f));
        ripples.Apply(queue);
        queue.Clear();

        // The first step turns the rate into a dent.
        ripples.Step(1f / 60f);

        var dented = ripples.At(16, 16);

        Assert.True(dented < -0.001f, $"the injection did not push the surface down: {dented}");

        // And with nothing more injected it comes back past its own start, which is the whole
        // difference: a rate pushed in is a depression that rebounds, where a height pushed in is a
        // hole that only ever fills.
        //
        // ⚠ Four seconds of headroom, because the rebound is a wave crossing the window and back and
        // its timing is the *window's* rather than the injection's. A loop sized by eye passed at one
        // extent and would have failed at the next.
        var rebounded = false;

        for (var step = 0; step < 240 && !rebounded; step++) {
            ripples.Step(1f / 60f);

            rebounded = ripples.At(16, 16) > 0f;
        }

        Assert.True(rebounded, $"the surface never sprang back from {dented}, which is a hole rather than a splash");
    }
}
