// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>What one element's overlay costs while other elements are transitioning.</summary>
/// <remarks>
///     ⚠ <b>A count and not a clock</b>, by this repository's rule on timing assertions: the property
///     is that the work done for one element does not grow with the number of <i>other</i> elements
///     that are transitioning, and that is a statement about how many entries were looked at.
///     <c>UiDocument.Accumulate</c> calls <see cref="Animator.Apply" /> for every element in every
///     style walk, so a per-element cost that scales with the whole table is a per-restyle cost of
///     O(elements × running transitions) (#1383).
/// </remarks>
public class OverlayCostTests {
    static Animator Animator(CascadeFixture fixture) =>
        new(fixture.Engine.Properties, fixture.Engine.Values, fixture.Engine.Names, fixture.Engine.Keyframes);

    /// <summary>
    ///     Starts a <c>margin-left</c> transition on each of <paramref name="others" /> elements plus the
    ///     one returned, and reports how many entries overlaying the measured one looked at.
    /// </summary>
    static (long Measured, long Idle, bool IdleUntouched, int Running) Overlay(int others) {
        var fixture = new CascadeFixture();
        fixture.Load("""
            .a { transition-property: margin-left; transition-duration: 1s; transition-timing-function: linear }
            .a.moved { margin-left: 40px }
            """);

        var animator = Animator(fixture);

        StyleNodeId Moved() {
            var element = fixture.Tree.CreateElement("div", classNames: ["a"]);
            var before = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
            animator.Observe(element, null, before, 0f);

            fixture.Tree.AddClass(element, "moved");

            var after = fixture.Engine.Resolver.Resolve(fixture.Tree, element);
            animator.Observe(element, before, after, 0f);

            return element;
        }

        var measured = Moved();

        for (var i = 0; i < others; i++) {
            Moved();
        }

        var idle = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var idleStyle = fixture.Engine.Resolver.Resolve(fixture.Tree, idle);
        animator.Observe(idle, null, idleStyle, 0f);

        var measuredStyle = fixture.Engine.Resolver.Resolve(fixture.Tree, measured);

        var start = animator.WithdrawingVisits;
        animator.Apply(measured, measuredStyle, 0.5f);
        var afterMeasured = animator.WithdrawingVisits;

        var overlaid = animator.Apply(idle, idleStyle, 0.5f);
        var afterIdle = animator.WithdrawingVisits;

        return (afterMeasured - start, afterIdle - afterMeasured, ReferenceEquals(overlaid, idleStyle), animator.RunningCount);
    }

    /// <summary>
    ///     ⚠ <b>One transition's element looks at one entry, beside one other element or sixty-four.</b>
    /// </summary>
    /// <remarks>
    ///     The running count is the instrument: it shows the other transitions really were running
    ///     when the overlay was measured, so a table that stopped holding them could not pass this by
    ///     having nothing to walk. With <c>Withdrawing</c> walking the whole table again the measured
    ///     count is <c>others + 1</c>.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void An_elements_overlay_does_not_walk_other_elements_transitions(int others) {
        var (measured, idle, idleUntouched, running) = Overlay(others);

        Assert.Equal(others + 1, running);
        Assert.Equal(1, measured);
        Assert.Equal(0, idle);
        Assert.True(idleUntouched);
    }
}
