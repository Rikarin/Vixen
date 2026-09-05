// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The controls that put an element into a state a selector can ask about.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half of a selector-only variant that a variant test cannot reach, and the half
///         that made doc 43's A13 expensive.</b>
///         <c>Vixen.Ui.Styling.Utilities.Tests.VariantCoverageTests</c> puts an element into a state
///         by hand and checks that the variant reaches it — which proves the compiler, the index and
///         the matcher, and proves nothing at all about whether anything in a real document ever
///         enters that state. A bit no control writes compiles, indexes, matches nothing, and passes
///         every row in that file. This is the writer's side.
///     </para>
///     <para>
///         ⚠ <b>Both directions on every bit</b>, because a control that sets a flag and never clears
///         it is the commoner defect: the positive case is the one somebody tries by hand, and the
///         negative case is the one a user hits by editing the field.
///     </para>
/// </remarks>
public class ElementStateBitTests {
    static UiTest Opened() => ControlHarness.Open(200f, 120f);

    /// <summary>A read-only field is <c>:read-only</c> and stops being it.</summary>
    /// <remarks>
    ///     ⚠ The <c>read-only</c> class is asserted beside the bit, because the editor's themes select
    ///     on it and the bit was added <i>beside</i> it rather than in place of it. A commit that
    ///     tidied the class away would restyle every inspector field, and this is what would say so.
    /// </remarks>
    [Fact]
    public void A_read_only_field_carries_the_state_and_the_class() {
        using var ui = Opened();

        var field = ui.Add<TextBox>();

        Assert.False(field.State.HasFlag(ElementState.ReadOnly));

        field.ReadOnly = true;

        Assert.True(field.State.HasFlag(ElementState.ReadOnly));
        Assert.True(field.HasClass("read-only"));

        field.ReadOnly = false;

        Assert.False(field.State.HasFlag(ElementState.ReadOnly));
        Assert.False(field.HasClass("read-only"));
    }

    /// <summary>An empty field with a placeholder is showing it; an empty one without is not.</summary>
    /// <remarks>
    ///     ⚠ <b>The distinction the <c>empty</c> class cannot make, and the reason
    ///     <c>:placeholder-shown</c> is not that class renamed.</b> Selectors 4 § 10.4 matches a field
    ///     that is <i>currently displaying</i> placeholder text — so a field with no value and nothing
    ///     to show in its place does not match, while its <c>empty</c> class is set either way. A
    ///     variant compiled against the class would have reached every empty field in the document.
    /// </remarks>
    [Fact]
    public void A_placeholder_is_only_shown_when_there_is_one_and_no_value() {
        using var ui = Opened();

        var field = ui.Add<TextBox>();

        // ⚠ Written and cleared rather than read on a fresh control, because `Restate` runs on a
        // change and a field that has never had either is in neither condition — which is
        // unobservable, since a field with no placeholder shows nothing whatever its class says.
        field.Value = "Ada";
        field.Value = string.Empty;

        // Empty, but with nothing to show: `empty` yes, `:placeholder-shown` no. This is the pair the
        // class alone cannot tell apart.
        Assert.True(field.HasClass("empty"));
        Assert.False(field.State.HasFlag(ElementState.PlaceholderShown));

        field.Placeholder = "Name";

        Assert.True(field.State.HasFlag(ElementState.PlaceholderShown));

        field.Value = "Ada";

        Assert.False(field.State.HasFlag(ElementState.PlaceholderShown));

        field.Value = string.Empty;

        Assert.True(field.State.HasFlag(ElementState.PlaceholderShown));

        // And it goes when the placeholder does, not only when a value arrives.
        field.Placeholder = null;

        Assert.False(field.State.HasFlag(ElementState.PlaceholderShown));
    }

    /// <summary>A half-ticked box is indeterminate and is <i>not</i> checked.</summary>
    /// <remarks>
    ///     ⚠ <b>The second assertion is the one worth having.</b> CSS matches <c>:indeterminate</c>
    ///     and not <c>:checked</c> on a box showing a dash, so the two bits have to be independent —
    ///     a control that folded the third appearance into <see cref="ElementState.Checked" /> would
    ///     make every <c>checked:</c> rule in a stylesheet apply to it.
    /// </remarks>
    [Fact]
    public void A_half_ticked_box_is_indeterminate_rather_than_checked() {
        using var ui = Opened();

        var box = ui.Add<CheckBox>();
        box.IsIndeterminate = true;

        Assert.True(box.State.HasFlag(ElementState.Indeterminate));
        Assert.False(box.State.HasFlag(ElementState.Checked));

        // ⚠ Activating it resolves the flag, which the control does deliberately — the state the flag
        // describes has just stopped being true. So this is also the clearing half.
        box.IsIndeterminate = false;

        Assert.False(box.State.HasFlag(ElementState.Indeterminate));
    }

    /// <summary>A progress bar of unknown length carries the same bit a half-ticked box does.</summary>
    /// <remarks>
    ///     ⚠ Deliberately the same bit rather than one of its own: Selectors 4 § 10.9 gives
    ///     <c>:indeterminate</c> to both, so a stylesheet writes one rule and gets both. A second bit
    ///     meaning the same word is a second thing to keep in step.
    /// </remarks>
    [Fact]
    public void An_indeterminate_progress_bar_carries_the_same_bit() {
        using var ui = Opened();

        var bar = ui.Add<ProgressBar>();

        Assert.False(bar.State.HasFlag(ElementState.Indeterminate));

        bar.IsIndeterminate = true;

        Assert.True(bar.State.HasFlag(ElementState.Indeterminate));

        bar.IsIndeterminate = false;

        Assert.False(bar.State.HasFlag(ElementState.Indeterminate));
    }

    /// <summary>And the cascade answers the pseudo-class, which is what all of it is for.</summary>
    /// <remarks>
    ///     ⚠ <b>End to end rather than on the bit, because everything above this is a claim about a
    ///     field on an element.</b> The stylesheet is the consumer, and a bit nothing selects on is
    ///     the same as no bit at all — which is the failure this whole item is about. Both halves are
    ///     asserted: the property arrives when the field is read-only and is gone when it is not.
    /// </remarks>
    [Fact]
    public void A_stylesheet_can_select_on_the_state_it_was_put_into() {
        using var ui = ControlHarness.Open(200f, 120f, "textbox:read-only { opacity: 0.4 }");

        var field = ui.Add<TextBox>();

        Assert.Null(ui.Document.NumberOf(field.Style, ui.Document.PropertyId("opacity")));

        field.ReadOnly = true;
        ui.Frame();

        Assert.Equal(0.4f, ui.Document.NumberOf(field.Style, ui.Document.PropertyId("opacity")) ?? 0f, 3);

        field.ReadOnly = false;
        ui.Frame();

        Assert.Null(ui.Document.NumberOf(field.Style, ui.Document.PropertyId("opacity")));
    }

    /// <summary><c>:read-write</c> is the absence of <c>:read-only</c>, and matches the other way round.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A negation rather than a bit of its own</b>, on <c>:enabled</c>'s terms: Selectors 4
    ///         § 10.2 defines it as the elements <c>:read-only</c> does not match, so a second bit
    ///         would be a second thing to keep in step — and the two would disagree the first time a
    ///         control set one and forgot the other. The two rows below are the other way round from
    ///         the test above, which is exactly what a compiler that had given it a state of its own
    ///         would fail.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a stated divergence: a plain element here is read-write.</b> A browser calls a
    ///         non-editable element read-only, so a <c>div</c> matches <c>:read-only</c> there and
    ///         <c>:read-write</c> here. That is the divergence <c>:enabled</c> already carries for an
    ///         element that is not a control, and it is written down rather than discovered.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Read_write_is_the_absence_of_read_only() {
        using var ui = ControlHarness.Open(200f, 120f, "textbox:read-write { opacity: 0.6 }");

        var field = ui.Add<TextBox>();
        var opacity = ui.Document.PropertyId("opacity");

        Assert.Equal(0.6f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);

        field.ReadOnly = true;
        ui.Frame();

        Assert.Null(ui.Document.NumberOf(field.Style, opacity));
    }

    /// <summary>A mandatory field carries <c>:required</c>, and <c>:optional</c> is its absence.</summary>
    /// <remarks>
    ///     ⚠ <b>The declaration and not the verdict.</b> <c>:required</c> is true of a field that has
    ///     been filled in perfectly well, which is what separates it from <c>:invalid</c> — a test
    ///     that only ever looked at an empty required field would pass with the two folded into one
    ///     bit. So the value is set before the flag is read back.
    /// </remarks>
    [Fact]
    public void A_required_field_carries_the_state_and_an_optional_one_does_not() {
        using var ui = Opened();

        var field = ui.Add<TextBox>();

        Assert.False(field.State.HasFlag(ElementState.Required));

        field.Required = true;
        field.Value = "Ada";

        Assert.True(field.State.HasFlag(ElementState.Required));

        field.Required = false;

        Assert.False(field.State.HasFlag(ElementState.Required));
    }

    /// <summary>Exactly one of <c>:valid</c> and <c>:invalid</c>, always, from the moment it exists.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The first assertion is the one a two-bit implementation gets wrong.</b> A field
    ///         nobody has touched has never been through a value change, so nothing but the call in
    ///         <c>OnCreated</c> writes a verdict — and without it the field carries neither bit, which
    ///         a selector cannot tell apart from a container that does not validate at all.
    ///     </para>
    ///     <para>
    ///         ⚠ And both bits are asserted at every step, not just the one being claimed. The
    ///         failure worth catching here is <i>both at once</i>, which no single positive
    ///         assertion can see.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_field_is_valid_or_invalid_and_never_both_nor_neither() {
        using var ui = Opened();

        var field = ui.Add<TextBox>();

        Assert.True(field.State.HasFlag(ElementState.Valid));
        Assert.False(field.State.HasFlag(ElementState.Invalid));

        // An empty required field is invalid from the moment it is marked, which is what `TextField`
        // documents and what makes the verdict independent of a submit.
        field.Required = true;

        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.False(field.State.HasFlag(ElementState.Valid));

        field.Value = "Ada";

        Assert.True(field.State.HasFlag(ElementState.Valid));
        Assert.False(field.State.HasFlag(ElementState.Invalid));

        // And a rule the application attaches moves it the same way, so the bit follows `Validate`
        // rather than following `Required`.
        field.Validator = value => value == "Ada" ? "taken" : null;

        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.False(field.State.HasFlag(ElementState.Valid));
    }

    /// <summary>And the cascade answers <c>:invalid</c>, which is what all of it is for.</summary>
    /// <remarks>
    ///     ⚠ End to end rather than on the bit, on the same terms as the <c>:read-only</c> test
    ///     above: a bit nothing selects on is the same as no bit at all. Both directions, so a rule
    ///     that applied unconditionally would fail.
    /// </remarks>
    [Fact]
    public void A_stylesheet_can_select_on_invalidity() {
        using var ui = ControlHarness.Open(200f, 120f, "textbox:invalid { opacity: 0.4 }");

        var field = ui.Add<TextBox>();
        var opacity = ui.Document.PropertyId("opacity");

        Assert.Null(ui.Document.NumberOf(field.Style, opacity));

        field.Required = true;
        ui.Frame();

        Assert.Equal(0.4f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);

        field.Value = "Ada";
        ui.Frame();

        Assert.Null(ui.Document.NumberOf(field.Style, opacity));
    }

    /// <summary><c>:optional</c> is the absence of <c>:required</c>, and matches the other way round.</summary>
    /// <remarks>
    ///     ⚠ <b>A negation rather than a bit of its own</b>, on <c>:read-write</c>'s terms — so its
    ///     rows run backwards from every other test in this file, which is exactly what a compiler
    ///     that had given it a state of its own would fail. It carries the same stated divergence:
    ///     everything that never said it was required is optional here, where a browser would only
    ///     say it of a form control.
    /// </remarks>
    [Fact]
    public void Optional_is_the_absence_of_required() {
        using var ui = ControlHarness.Open(200f, 120f, "textbox:optional { opacity: 0.6 }");

        var field = ui.Add<TextBox>();
        var opacity = ui.Document.PropertyId("opacity");

        Assert.Equal(0.6f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);

        field.Required = true;
        ui.Frame();

        Assert.Null(ui.Document.NumberOf(field.Style, opacity));
    }
}
