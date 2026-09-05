// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The measurement the four <c>*-reverse</c> refusals were left waiting on.</summary>
/// <remarks>
///     <para>
///         <b>#240 refused <c>space-x-reverse</c>, <c>space-y-reverse</c>, <c>divide-x-reverse</c>
///         and <c>divide-y-reverse</c> for one stated reason in two halves</b>: each multiplies an
///         edge by a <c>--tw-*-reverse</c> flag, <c>StyleValueParser</c> had no <c>calc()</c>, and
///         "the flag would be a custom property nobody reads". ⚠ The first half expired when
///         <c>calc()</c> landed. The second was never measured, and #599 asked for the measurement
///         before the refusal moved either way — because a refusal lifted without one is the failure
///         doc 43 exists to eliminate, and so is a refusal left standing on an expired reason.
///     </para>
///     <para>
///         ⚠ <b>This is the measurement, and it comes out positive on every count.</b> A flag one
///         class writes is read by another class's declaration on the same element, it inherits to
///         the descendants the child-scoped rule actually matches, and both halves of v4's
///         arithmetic fold — <c>calc(w * var(--f))</c> and <c>calc(w * calc(1 - var(--f)))</c>, at
///         both values of the flag. So the mechanism is not what keeps these four out.
///     </para>
///     <para>
///         ⚠ <b>What keeps them out is a decision one file away, and it is unrelated to
///         <c>calc()</c>.</b> <c>UtilityFamilies</c> has <c>divide-x</c> and <c>space-x</c> write
///         <i>one</i> edge, deliberately, because writing the leading edge as well would
///         out-specify a child's own <c>border-s-2</c> and silently erase it. v4's reverse flag
///         works by flipping <i>which of two written edges</i> carries the width, so it needs the
///         edge this table refuses to write — and writing it as <c>calc(w * var(--f, 0))</c> rather
///         than as a literal <c>0</c> changes nothing about that: it is the declaration that
///         out-specifies, not the value. See <c>UtilityFamilies</c> and doc 43 § F9.
///     </para>
/// </remarks>
public class ReverseFlagTests {
    /// <summary>v4's own shape for <c>divide-x</c>, hand-written so the mechanism can be measured.</summary>
    /// <remarks>
    ///     ⚠ <b>Hand-written on purpose, and it is what makes this a measurement rather than a
    ///     test of something registered.</b> Nothing emits these classes; what is under test is the
    ///     cascade underneath them, so the fixture writes the rules v4 would emit and asks the
    ///     engine what it makes of them. Registering the family first and then measuring it would
    ///     be the order this document forbids.
    /// </remarks>
    const string DivideX = """
        root { width: 400px; height: 200px; }
        holder { flex-direction: row; }
        kid { width: 40px; }

        .divide-x-2 > kid {
            border-left-width: calc(2px * calc(1 - var(--tw-divide-x-reverse, 0)));
            border-right-width: calc(2px * var(--tw-divide-x-reverse, 0));
        }

        .divide-x-reverse { --tw-divide-x-reverse: 1; }
        """;

    static (float? Left, float? Right) Edges(params string[] holderClasses) {
        using var document = new UiDocument(400f, 200f);
        document.Load(DivideX);

        var holder = document.Root.Add("holder", null, holderClasses);
        var kid = holder.Add("kid");

        document.Update();

        return (
            document.LengthOf(kid.Style, document.PropertyId("border-left-width")),
            document.LengthOf(kid.Style, document.PropertyId("border-right-width"))
        );
    }

    /// <summary>A flag one class writes is read by another class's declaration, at both values.</summary>
    /// <remarks>
    ///     ⚠ <b>Per value, not merely "it resolves".</b> A flag that resolved to the same edge
    ///     whatever it said would satisfy a one-sided assertion and be exactly as useless as one
    ///     nothing read — so both values are asserted, and each is asserted on <i>both</i> edges:
    ///     the width has to arrive on one and be absent from the other, which is what "reverse"
    ///     means.
    /// </remarks>
    [Fact]
    public void A_reverse_flag_written_by_one_class_is_read_by_another() {
        var forward = Edges("divide-x-2");

        Assert.Equal(2f, forward.Left);
        Assert.Equal(0f, forward.Right);

        var reversed = Edges("divide-x-2", "divide-x-reverse");

        Assert.Equal(0f, reversed.Left);
        Assert.Equal(2f, reversed.Right);
    }

    /// <summary>And it reaches the children, which is the only elements the rule matches.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that would have been easy to declare settled by the same-element
    ///     measurement.</b> v4 writes the flag on the <i>container</i> and reads it in a rule
    ///     scoped to <c>&gt; :not(:last-child)</c>, so the value has to travel one edge down the
    ///     tree. Custom properties inherit, which is what makes that work — and a cascade that
    ///     resolved <c>var()</c> only against the element's own declarations would pass the test
    ///     above and fail this one.
    /// </remarks>
    [Fact]
    public void The_flag_inherits_from_the_container_that_declares_it() {
        using var document = new UiDocument(400f, 200f);
        document.Load(DivideX);

        // The flag two levels above the element whose edges read it, so that nothing but
        // inheritance can carry it: the rule matches the kid, and the kid declares nothing.
        var outer = document.Root.Add("holder", null, "divide-x-reverse");
        var holder = outer.Add("holder", null, "divide-x-2");
        var kid = holder.Add("kid");

        document.Update();

        Assert.Equal(0f, document.LengthOf(kid.Style, document.PropertyId("border-left-width")));
        Assert.Equal(2f, document.LengthOf(kid.Style, document.PropertyId("border-right-width")));
    }

    /// <summary>The four spellings are still not utilities, and this is what says the decision stands.</summary>
    /// <remarks>
    ///     ⚠ <b>A refusal with no test rots into an oversight.</b> The reason they are absent is no
    ///     longer <c>calc()</c> and no longer "nobody reads the flag" — both are measured above —
    ///     it is <c>UtilityFamilies</c>' decision that <c>divide-x</c> and <c>space-x</c> write one
    ///     edge. This fails the day somebody registers them, which is the day that decision has to
    ///     be revisited rather than worked around: the reverse flag is only meaningful over a family
    ///     that writes both edges.
    /// </remarks>
    [Theory]
    [InlineData("space-x-reverse")]
    [InlineData("space-y-reverse")]
    [InlineData("divide-x-reverse")]
    [InlineData("divide-y-reverse")]
    public void The_four_reverse_spellings_are_still_absent(string candidate) {
        var fixture = new UtilityFixture();

        Assert.Null(fixture.Declarations(candidate));
    }
}
