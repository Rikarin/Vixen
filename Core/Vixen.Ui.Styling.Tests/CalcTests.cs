// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>The arithmetic <c>calc()</c> folds, and — the larger half — the arithmetic it refuses.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both halves are asserted here on purpose, because each is the other's failure mode.</b>
///         A <c>calc()</c> that folds nothing is the behaviour this parser already had, and every
///         "it folds" case below is red against it. A <c>calc()</c> that folds too much is worse and
///         is invisible: <c>calc(100% - 10px)</c> answering <c>90</c> of something is a rectangle at a
///         plausible wrong size, and <c>calc(2px -1px)</c> answering <c>2px</c> is CSS's own
///         whitespace rule quietly not applied. Those are the cases a value-only test cannot see, so
///         they are written as refusals and they are what a widened evaluator goes red on.
///     </para>
///     <para>
///         <b>What this prints on the day nothing runs.</b> <c>Parse</c> is pure and takes no device,
///         no document and no frame, so there is no arrangement in which these can skip. The one
///         instrument risk is the assertion itself being unfalsifiable — hence no test below asserts
///         only <c>Kind</c>, and every folding case pins the number <i>and</i> the unit.
///     </para>
/// </remarks>
public class CalcTests {
    const float Tolerance = 1e-4f;

    static StyleValueParser Parser() => new(new NameTable(), new NameTable());

    /// <summary>Two lengths in one unit add, which is the shape <c>ring-offset-*</c> is written in.</summary>
    /// <remarks>
    ///     ⚠ This is the case doc 43 records as a blocker on three roots. Tailwind v4 writes the outer
    ///     ring's spread as <c>calc(var(--tw-ring-offset-width) + var(--tw-ring-width))</c>, and the
    ///     two widths come from two independent classes — so no generator can do the addition at build
    ///     time, and substitution has already run by the time this parser sees the text. It arrives
    ///     here as ordinary arithmetic over two pixel lengths.
    /// </remarks>
    [Theory]
    [InlineData("calc(2px + 2px)", 4f, StyleUnit.Pixels)]
    [InlineData("calc(2px - 5px)", -3f, StyleUnit.Pixels)]
    [InlineData("calc(1rem + 0.5rem)", 1.5f, StyleUnit.Rem)]
    [InlineData("calc(2px * 3)", 6f, StyleUnit.Pixels)]
    [InlineData("calc(3 * 2px)", 6f, StyleUnit.Pixels)]
    [InlineData("calc(10px / 4)", 2.5f, StyleUnit.Pixels)]

    // ⚠ The asymmetry with `+` and `-`, and it is CSS's rather than an oversight here: neither `*`
    // nor `/` can begin a number, so there is no sign to confuse an operator with and Values 4 asks
    // for no space. `calc(2px -1px)` two theories down is the same rule seen from the other side.
    [InlineData("calc(2px* 3)", 6f, StyleUnit.Pixels)]
    [InlineData("calc(100% - 10%)", 90f, StyleUnit.Percent)]
    [InlineData("calc(1s / 2)", 0.5f, StyleUnit.Seconds)]
    public void Lengths_in_one_unit_fold_to_a_length(string css, float expected, StyleUnit unit) {
        var value = Parser().Parse(css);

        Assert.Equal(StyleValueKind.Length, value.Kind);
        Assert.Equal(expected, value.Number, Tolerance);
        Assert.Equal(unit, value.Unit);
    }

    /// <summary>Bare numbers fold to a number, which is what the shipped theme's type scale is made of.</summary>
    /// <remarks>
    ///     <c>vixen.default.vcss</c> writes every line height as a ratio — <c>--text-xs--line-height:
    ///     calc(1 / 0.75)</c> — because that is how Tailwind v4 states them. Read through a
    ///     <c>var()</c> those arrived as <see cref="StyleValueKind.Unknown" />.
    /// </remarks>
    [Theory]
    [InlineData("calc(1 / 0.75)", 1.333333f)]
    [InlineData("calc(1.25 / 0.875)", 1.428571f)]
    [InlineData("calc(2 + 3)", 5f)]
    [InlineData("calc(2 * 3 + 4)", 10f)]
    [InlineData("calc(2 + 3 * 4)", 14f)]
    [InlineData("calc((2 + 3) * 4)", 20f)]
    [InlineData("calc(calc(2 + 3) * 4)", 20f)]
    [InlineData("calc(-2 + 3)", 1f)]
    [InlineData("calc(1e2 / 4)", 25f)]
    [InlineData("calc(2e-2 * 100)", 2f)]
    public void Numbers_fold_to_a_number(string css, float expected) {
        var value = Parser().Parse(css);

        Assert.Equal(StyleValueKind.Number, value.Kind);
        Assert.Equal(expected, value.Number, Tolerance);
    }

    /// <summary>Precedence is arithmetic's, and the two spellings that prove it disagree.</summary>
    /// <remarks>
    ///     ⚠ A left-to-right evaluator answers 20 for both of these, so the pair is the assertion and
    ///     neither one alone is. <c>2 + 3 * 4</c> is the half that goes red.
    /// </remarks>
    [Fact]
    public void A_product_binds_tighter_than_a_sum() {
        var parser = Parser();

        Assert.Equal(14f, parser.Parse("calc(2 + 3 * 4)").Number, Tolerance);
        Assert.Equal(20f, parser.Parse("calc((2 + 3) * 4)").Number, Tolerance);
    }

    /// <summary>What does not fold is refused whole, and is not folded to its first term.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>calc(100% - 10px)</c> is the one that is valid CSS and refused anyway.</b> A
    ///         <see cref="StyleValue" /> is one number and one unit, so there is nothing for a
    ///         percentage-minus-a-pixel to be; resolving it needs the containing block, which is the
    ///         context <see cref="StyleUnit" />'s own remark forbids this assembly from reaching for.
    ///         Refusing is what it did before and what it must keep doing — the failure to avoid is
    ///         folding it to <c>90</c> of one of the two units.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>calc(2px -1px)</c> is the whitespace rule</b> — Values 4 § 10.1 makes
    ///         <c>+</c> and <c>-</c> operators only with space on both sides, because otherwise the
    ///         sign of a term and the operator are the same character. An evaluator that shrugged and
    ///         returned the first term would answer <c>2px</c>.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("calc(100% - 10px)")]
    [InlineData("calc(1rem + 4px)")]
    [InlineData("calc(100px + 0)")]
    [InlineData("calc(2px -1px)")]
    [InlineData("calc(2px * 3px)")]
    [InlineData("calc(4 / 0)")]
    [InlineData("calc(4px / 2px)")]
    [InlineData("calc(2px +)")]
    [InlineData("calc(2px + )")]
    [InlineData("calc()")]
    [InlineData("calc((2 + 3)")]
    [InlineData("calc(2 + 3))")]
    [InlineData("calc(red + 2px)")]
    [InlineData("calc(2px 3px)")]
    public void What_does_not_fold_is_Unknown_rather_than_its_first_term(string css) {
        var value = Parser().Parse(css);

        Assert.Equal(StyleValueKind.Unknown, value.Kind);
    }

    /// <summary>A folded <c>calc()</c> is indistinguishable from the value written out.</summary>
    /// <remarks>
    ///     The point of folding rather than carrying an expression: everything downstream — the
    ///     animator, <c>LengthContext</c>, <c>DrawListBuilder</c> — reads one number and one unit and
    ///     needs no notion that a <c>calc()</c> was ever there. Equality on the struct is the strongest
    ///     way to say so, and it covers the unit, which a number comparison does not.
    /// </remarks>
    [Fact]
    public void A_folded_expression_equals_the_value_written_out() {
        var parser = Parser();

        Assert.Equal(parser.Parse("4px"), parser.Parse("calc(2px + 2px)"));
        Assert.Equal(parser.Parse("1.5rem"), parser.Parse("calc(3rem / 2)"));
        Assert.NotEqual(parser.Parse("4"), parser.Parse("calc(2px + 2px)"));
    }

    /// <summary>A <c>calc()</c> inside a list is one item, not several.</summary>
    /// <remarks>
    ///     ⚠ <c>Parse</c> splits a value on top-level whitespace, and a <c>calc()</c> body is full of
    ///     it. The split counts brackets, so this holds — but it holds by an invariant a change to the
    ///     splitter could break silently, and the symptom would be a four-item shadow becoming a
    ///     seven-item one.
    /// </remarks>
    [Fact]
    public void A_calc_inside_a_whitespace_list_stays_one_item() {
        var value = Parser().Parse("0 0 0 calc(2px + 2px)");

        Assert.Equal(StyleValueKind.List, value.Kind);
        Assert.Equal(4, value.Items.Length);
        Assert.Equal(StyleValueKind.Length, value.Items[3].Kind);
        Assert.Equal(4f, value.Items[3].Number, Tolerance);
        Assert.Equal(StyleUnit.Pixels, value.Items[3].Unit);
    }
}
