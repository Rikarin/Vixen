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

    /// <summary>A select reaches a verdict, and it is exactly one bit from the moment it exists.</summary>
    /// <remarks>
    ///     ⚠ <b>The three assertions before <c>Required</c> is ever set are the ones a change-driven
    ///     writer fails.</b> A select that is not required is valid and never goes through a change,
    ///     so a control that only reached a verdict from its value's setter would carry neither bit —
    ///     which a stylesheet cannot tell apart from a <c>div</c>. And the choice is read off
    ///     <c>Value</c> rather than off the options, so a value assigned before its list arrives
    ///     counts.
    /// </remarks>
    [Fact]
    public void A_required_select_is_invalid_until_a_choice_is_made() {
        using var ui = Opened();

        var select = ui.Add<Select>();

        Assert.False(select.State.HasFlag(ElementState.Required));
        Assert.True(select.State.HasFlag(ElementState.Valid));
        Assert.False(select.State.HasFlag(ElementState.Invalid));

        select.Required = true;

        Assert.True(select.State.HasFlag(ElementState.Required));
        Assert.True(select.State.HasFlag(ElementState.Invalid));
        Assert.False(select.State.HasFlag(ElementState.Valid));

        // ⚠ Before any option is added, which is the case the verdict is deliberately not derived
        // from `Selected` for: a field bound to a model before its list has been fetched has made a
        // choice, and reading the option would call it empty and then quietly correct itself.
        select.Value = "green";

        Assert.True(select.State.HasFlag(ElementState.Valid));
        Assert.False(select.State.HasFlag(ElementState.Invalid));

        select.Value = null;

        Assert.True(select.State.HasFlag(ElementState.Invalid));
        Assert.False(select.State.HasFlag(ElementState.Valid));

        // And it stops being mandatory without stopping being a control that validates.
        select.Required = false;

        Assert.False(select.State.HasFlag(ElementState.Required));
        Assert.True(select.State.HasFlag(ElementState.Valid));
        Assert.False(select.State.HasFlag(ElementState.Invalid));
    }

    /// <summary>A required box is invalid until it is ticked, and a dash is not a tick.</summary>
    /// <remarks>
    ///     ⚠ <b>The indeterminate step is the one worth having.</b> A half-ticked box is
    ///     <i>not</i> checked — that is why <see cref="ElementState.Indeterminate" /> is a bit of its
    ///     own — so a required box showing a dash has not been answered, and a verdict computed from
    ///     "has the user touched it" rather than from the value would let it through.
    /// </remarks>
    [Fact]
    public void A_required_box_is_invalid_until_it_is_ticked() {
        using var ui = Opened();

        var box = ui.Add<CheckBox>();

        Assert.True(box.State.HasFlag(ElementState.Valid));
        Assert.False(box.State.HasFlag(ElementState.Invalid));

        box.Required = true;

        Assert.True(box.State.HasFlag(ElementState.Required));
        Assert.True(box.State.HasFlag(ElementState.Invalid));
        Assert.False(box.State.HasFlag(ElementState.Valid));

        box.IsChecked = true;

        Assert.True(box.State.HasFlag(ElementState.Valid));
        Assert.False(box.State.HasFlag(ElementState.Invalid));

        box.IsIndeterminate = true;

        Assert.True(box.State.HasFlag(ElementState.Invalid));
        Assert.False(box.State.HasFlag(ElementState.Valid));

        box.IsIndeterminate = false;

        Assert.True(box.State.HasFlag(ElementState.Valid));
        Assert.False(box.State.HasFlag(ElementState.Invalid));

        box.IsChecked = false;

        Assert.True(box.State.HasFlag(ElementState.Invalid));
        Assert.False(box.State.HasFlag(ElementState.Valid));
    }

    /// <summary>A required group is invalid until one of its members is chosen.</summary>
    /// <remarks>
    ///     ⚠ <b>The bits are on the group and on none of the radios, which is the divergence from
    ///     HTML worth asserting.</b> A browser marks each <c>&lt;input type=radio&gt;</c> required
    ///     and re-derives the group from the shared name; here the group is the element that holds
    ///     the answer, so a member carrying <c>:required</c> would be claiming that this one in
    ///     particular has to be the chosen one.
    /// </remarks>
    [Fact]
    public void A_required_group_is_invalid_until_a_member_is_chosen() {
        using var ui = Opened();

        var group = ui.Add<RadioGroup>();
        var red = group.AddOption("red", "Red");
        group.AddOption("green", "Green");

        Assert.True(group.State.HasFlag(ElementState.Valid));
        Assert.False(group.State.HasFlag(ElementState.Invalid));

        group.Required = true;

        Assert.True(group.State.HasFlag(ElementState.Required));
        Assert.True(group.State.HasFlag(ElementState.Invalid));
        Assert.False(group.State.HasFlag(ElementState.Valid));

        // The member is neither, because a radio does not take part in constraint validation on its
        // own — Selectors 4 § 10.6's "neither" case, and the reason `Valid` is a bit rather than the
        // absence of `Invalid`.
        Assert.False(red.State.HasFlag(ElementState.Valid));
        Assert.False(red.State.HasFlag(ElementState.Invalid));
        Assert.False(red.State.HasFlag(ElementState.Required));

        group.Value = "green";

        Assert.True(group.State.HasFlag(ElementState.Valid));
        Assert.False(group.State.HasFlag(ElementState.Invalid));

        group.Value = null;

        Assert.True(group.State.HasFlag(ElementState.Invalid));
        Assert.False(group.State.HasFlag(ElementState.Valid));
    }

    /// <summary>A number outside its bounds is <c>:out-of-range</c> and <c>:invalid</c> with it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertion that could not have been written a commit ago.</b>
    ///         <c>NumericInput</c> clamped in its coerce, so the field arrived at the third line
    ///         below holding ten and this whole condition was unreachable by any route — which is
    ///         why the two variants were refused rather than registered.
    ///     </para>
    ///     <para>
    ///         ⚠ And <c>:invalid</c> is asserted beside it deliberately. Selectors 4 § 10.7 makes a
    ///         range violation a constraint violation, so a stylesheet that only knows how to colour
    ///         an invalid field colours this one too — and a control that wrote the range bit without
    ///         the verdict would look right in every by-hand test and be uncoloured in a real sheet.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_number_outside_its_bounds_is_out_of_range_and_invalid() {
        using var ui = Opened();

        var field = ui.Add<NumericInput>();
        field.Minimum = 0d;
        field.Maximum = 10d;

        Assert.False(field.State.HasFlag(ElementState.OutOfRange));
        Assert.True(field.State.HasFlag(ElementState.Valid));

        field.Number = 50d;

        Assert.True(field.State.HasFlag(ElementState.OutOfRange));
        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.False(field.State.HasFlag(ElementState.Valid));

        field.Number = 5d;

        Assert.False(field.State.HasFlag(ElementState.OutOfRange));
        Assert.True(field.State.HasFlag(ElementState.Valid));
        Assert.False(field.State.HasFlag(ElementState.Invalid));

        // ⚠ The bounds moving under a value that has not moved, which is the case `OnRangeChanged`
        // used to answer by rewriting the number.
        field.Maximum = 4d;

        Assert.True(field.State.HasFlag(ElementState.OutOfRange));
        Assert.True(field.State.HasFlag(ElementState.Invalid));
    }

    /// <summary>And a stylesheet answers <c>:out-of-range</c>, with <c>:in-range</c> its negation.</summary>
    /// <remarks>
    ///     ⚠ The two rules run in opposite directions in one sheet, which is what a pair of
    ///     independent bits would fail: a field is one or the other and never both, because
    ///     <c>:in-range</c> compiles to the negation rather than to a bit of its own.
    /// </remarks>
    [Fact]
    public void A_stylesheet_can_select_on_a_broken_bound() {
        using var ui = ControlHarness.Open(
            200f,
            120f,
            "numeric-input:out-of-range { opacity: 0.4 } numeric-input:in-range { opacity: 0.9 }");

        var field = ui.Add<NumericInput>();
        var opacity = ui.Document.PropertyId("opacity");

        field.Minimum = 0d;
        field.Maximum = 10d;
        ui.Frame();

        Assert.Equal(0.9f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);

        field.Number = 50d;
        ui.Frame();

        Assert.Equal(0.4f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);

        field.Number = 5d;
        ui.Frame();

        Assert.Equal(0.9f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);
    }

    /// <summary>And a stylesheet reaches all three, which is what the bits are for.</summary>
    /// <remarks>
    ///     ⚠ End to end rather than on the bit, on the <c>:read-only</c> test's terms: a bit nothing
    ///     selects on is the same as no bit at all. Three tags in one sheet, because the failure this
    ///     catches is a control that writes the bit onto a part rather than onto itself — which no
    ///     assertion about <c>State</c> can see.
    /// </remarks>
    [Fact]
    public void A_stylesheet_reaches_every_control_that_validates() {
        using var ui = ControlHarness.Open(
            200f,
            160f,
            "select:invalid { opacity: 0.4 } checkbox:invalid { opacity: 0.4 } radio-group:invalid { opacity: 0.4 }"
            + " multi-select:invalid { opacity: 0.4 } combo-box:invalid { opacity: 0.4 }"
            + " switch:invalid { opacity: 0.4 }");

        var opacity = ui.Document.PropertyId("opacity");

        var select = ui.Add<Select>();
        var box = ui.Add<CheckBox>();
        var group = ui.Add<RadioGroup>();
        var many = ui.Add<MultiSelect>();
        var combo = ui.Add<ComboBox>();

        // ⚠ The control that must never match, in the same sheet as the ones that must. A switch
        // deliberately does not take part in constraint validation — see the type's remarks — and a
        // refusal nothing asserts is a refusal the next person deletes by accident.
        var toggle = ui.Add<Switch>();

        Assert.Null(ui.Document.NumberOf(select.Style, opacity));
        Assert.Null(ui.Document.NumberOf(box.Style, opacity));
        Assert.Null(ui.Document.NumberOf(group.Style, opacity));
        Assert.Null(ui.Document.NumberOf(many.Style, opacity));
        Assert.Null(ui.Document.NumberOf(combo.Style, opacity));

        select.Required = true;
        box.Required = true;
        group.Required = true;
        many.Required = true;
        combo.Required = true;
        ui.Frame();

        Assert.Equal(0.4f, ui.Document.NumberOf(select.Style, opacity) ?? 0f, 3);
        Assert.Equal(0.4f, ui.Document.NumberOf(box.Style, opacity) ?? 0f, 3);
        Assert.Equal(0.4f, ui.Document.NumberOf(group.Style, opacity) ?? 0f, 3);

        // ⚠ The two this issue is about. `combo-box:invalid` reached nothing at all before —
        // `combo-box textbox:invalid` reached the editor inside it and the box drawn round the
        // editor and its chevron, which is the thing anybody writes a rule against, matched no
        // selector in any state.
        Assert.Equal(0.4f, ui.Document.NumberOf(many.Style, opacity) ?? 0f, 3);
        Assert.Equal(0.4f, ui.Document.NumberOf(combo.Style, opacity) ?? 0f, 3);

        // Unchanged and unchangeable: there is no property on it that could make the rule apply.
        Assert.Null(ui.Document.NumberOf(toggle.Style, opacity));

        select.Value = "green";
        box.IsChecked = true;
        group.Value = "green";
        many.Select("green", true);
        combo.Value = "green";
        ui.Frame();

        Assert.Null(ui.Document.NumberOf(select.Style, opacity));
        Assert.Null(ui.Document.NumberOf(box.Style, opacity));
        Assert.Null(ui.Document.NumberOf(group.Style, opacity));
        Assert.Null(ui.Document.NumberOf(many.Style, opacity));
        Assert.Null(ui.Document.NumberOf(combo.Style, opacity));
    }

    /// <summary>A switch carries neither verdict bit, in any state, on purpose.</summary>
    /// <remarks>
    ///     ⚠ <b>Selectors 4 § 10.6's "neither" case, and the reason <c>Valid</c> is a bit rather than
    ///     the absence of <c>Invalid</c>.</b> A switch takes effect the moment it is flipped, so
    ///     "this must be on before the form is acceptable" is a sentence about something a switch is
    ///     not — a required switch is a setting the application will not let you turn off, which is a
    ///     disabled control or a confirmation. Asserted rather than left implicit because the whole
    ///     point of the two-bit arrangement is that "does not validate" is expressible, and a
    ///     <c>Required</c> added here later out of symmetry would pass every other test in this file.
    /// </remarks>
    [Fact]
    public void A_switch_does_not_take_part_in_validation_at_all() {
        using var ui = Opened();

        var toggle = ui.Add<Switch>();

        Assert.False(toggle.State.HasFlag(ElementState.Valid));
        Assert.False(toggle.State.HasFlag(ElementState.Invalid));
        Assert.False(toggle.State.HasFlag(ElementState.Required));

        toggle.IsChecked = true;

        Assert.False(toggle.State.HasFlag(ElementState.Valid));
        Assert.False(toggle.State.HasFlag(ElementState.Invalid));

        toggle.IsChecked = false;

        Assert.False(toggle.State.HasFlag(ElementState.Valid));
        Assert.False(toggle.State.HasFlag(ElementState.Invalid));

        // And the checkbox beside it does, so the assertion above is about this control rather than
        // about the harness never writing the bits.
        var box = ui.Add<CheckBox>();

        Assert.True(box.State.HasFlag(ElementState.Valid));
    }

    /// <summary>A field is not <c>:user-invalid</c> until somebody has actually been in it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The blur without an edit is the assertion worth having, and it is the one a
    ///         writer hooked to <c>OnValueChanged</c> fails.</b> Every field in a form loaded from a
    ///         model goes through a value change before anybody has seen it, so a bit set there would
    ///         claim the user had been in all of them — which is exactly the "form turns red before it
    ///         has been filled in" that <c>:user-invalid</c> exists to prevent.
    ///     </para>
    ///     <para>
    ///         ⚠ And the interaction never clears: what changes back is the verdict. The last two
    ///         steps assert that, because a control that reset it on focus loss would make the state
    ///         visible only while the caret was in the field.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_user_has_to_have_had_a_go_before_a_field_is_user_invalid() {
        using var ui = Opened();

        var field = ui.Add<TextBox>();
        var other = ui.Add<TextBox>();

        field.Required = true;

        // Invalid from the moment it is marked — and not yet *user*-invalid, which is the whole
        // distinction.
        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.False(field.State.HasFlag(ElementState.UserInteracted));

        // Focused and blurred with nothing typed. Tabbing through a form must not accuse it.
        ui.Document.Focus(field);
        ui.Document.Focus(other);

        Assert.False(field.State.HasFlag(ElementState.UserInteracted));

        ui.Document.Focus(field);
        ui.TypeText("A");
        ui.Document.Focus(other);

        Assert.True(field.State.HasFlag(ElementState.UserInteracted));
        Assert.True(field.State.HasFlag(ElementState.Valid));

        // ⚠ And it survives the value going back to what it was. The verdict moves; the fact that
        // somebody has been in the field does not.
        field.Value = string.Empty;

        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.True(field.State.HasFlag(ElementState.UserInteracted));
    }

    /// <summary>And a stylesheet can say <c>:user-invalid</c>, which ExCSS cannot parse.</summary>
    /// <remarks>
    ///     ⚠ <b>The end-to-end row is what proves the repair rather than the bit.</b> ExCSS 4.3.2 has
    ///     no literal for the name, so <c>textbox:user-invalid</c> arrives as one
    ///     <c>UnknownSelector</c> covering the whole compound and no pseudo-class code ever runs on
    ///     it; <c>SelectorCompiler.TryRewrite</c> is what re-reads it. The second rule is the
    ///     separation that matters: the field is <c>:invalid</c> for the whole test and
    ///     <c>:user-invalid</c> only after a keystroke.
    /// </remarks>
    [Fact]
    public void A_stylesheet_can_select_on_user_invalidity() {
        using var ui = ControlHarness.Open(
            200f,
            160f,
            "textbox:user-invalid { opacity: 0.4 } textbox:invalid { --shown: 1 }");

        var field = ui.Add<TextBox>();
        var other = ui.Add<TextBox>();
        var opacity = ui.Document.PropertyId("opacity");
        var shown = ui.Document.PropertyId("--shown");

        field.Required = true;
        ui.Frame();

        // Invalid, and saying nothing about it.
        Assert.Equal(1f, ui.Document.NumberOf(field.Style, shown) ?? 0f, 3);
        Assert.Null(ui.Document.NumberOf(field.Style, opacity));

        ui.Document.Focus(field);
        ui.TypeText("A");
        ui.Document.Focus(other);

        // Emptied again, so that the field is invalid *and* has been typed into — which is the only
        // combination the first rule is allowed to reach.
        field.Value = string.Empty;
        ui.Frame();

        Assert.Equal(0.4f, ui.Document.NumberOf(field.Style, opacity) ?? 0f, 3);
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

    /// <summary>An expanded disclosure is <c>:open</c>, and it is the disclosure rather than its header.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>open</c> class is asserted beside the bit for the same reason the read-only
    ///     row above asserts its class: this control has written the class since it was made and the
    ///     bit arrived beside it, not in place of it.</b> The header's <c>:checked</c> is asserted
    ///     too, and it is the row that says these are two statements and not one — a commit that
    ///     moved the new bit onto the header, where the chevron already reads a state, would leave
    ///     every <c>expander:open</c> rule in the tree matching nothing and pass every other
    ///     assertion here.
    /// </remarks>
    [Fact]
    public void An_expanded_disclosure_carries_the_open_state_and_its_header_does_not() {
        using var ui = Opened();

        var expander = ui.Add<Expander>();

        Assert.False(expander.State.HasFlag(ElementState.Open));

        expander.IsExpanded = true;

        Assert.True(expander.State.HasFlag(ElementState.Open));
        Assert.True(expander.HasClass("open"));
        Assert.False(expander.Header.State.HasFlag(ElementState.Open));
        Assert.True(expander.Header.State.HasFlag(ElementState.Checked));

        expander.IsExpanded = false;

        Assert.False(expander.State.HasFlag(ElementState.Open));
        Assert.False(expander.HasClass("open"));
    }

    /// <summary>A select showing its list is <c>:open</c>, however the list came to shut.</summary>
    /// <remarks>
    ///     ⚠ <b>The dismissal is the half worth writing, and an implementation that set the bit in
    ///     <c>Open()</c> and cleared it in <c>CloseList()</c> passes everything before it.</b> The
    ///     popover closes itself on a light dismiss and on Escape without this control being asked,
    ///     so the bit is written from the overlay's own notification; one written at the call sites
    ///     would say open over a list that is gone, which is the stale-state failure rather than a
    ///     missing one.
    /// </remarks>
    [Fact]
    public void A_select_showing_its_list_is_open_and_stops_being_it_when_the_list_dismisses_itself() {
        using var ui = Opened();

        var select = ui.Add<Select>();
        select.AddOption("one");
        select.AddOption("two");

        Assert.False(select.State.HasFlag(ElementState.Open));

        select.Open();
        ui.Frame();

        Assert.True(select.IsOpen);
        Assert.True(select.State.HasFlag(ElementState.Open));

        // ⚠ Through the popover rather than through `CloseList`, which is what a light dismiss and
        // the Escape key both do.
        select.List.Close();
        ui.Frame();

        Assert.False(select.IsOpen);
        Assert.False(select.State.HasFlag(ElementState.Open));
    }

    /// <summary>And a stylesheet can say <c>:open</c>, which ExCSS cannot parse either.</summary>
    /// <remarks>
    ///     ⚠ <b><c>:open</c> was recorded as owed a parser and it was owed a rewrite.</b> ExCSS 4.3.2
    ///     hands <c>expander:open</c> back as one <c>UnknownSelector</c> covering the whole compound,
    ///     exactly as it does <c>:user-valid</c> — and that stopped being a blocker the day the
    ///     repair for the latter landed. This is the end-to-end row: it fails on a bit with no writer
    ///     and it fails on a writer with no repair, which are the two halves that were owed.
    /// </remarks>
    [Fact]
    public void A_stylesheet_can_select_on_openness() {
        using var ui = ControlHarness.Open(200f, 160f, "expander:open { opacity: 0.4 }");

        var expander = ui.Add<Expander>();
        var opacity = ui.Document.PropertyId("opacity");

        ui.Frame();

        Assert.Null(ui.Document.NumberOf(expander.Style, opacity));

        expander.IsExpanded = true;
        ui.Frame();

        Assert.Equal(0.4f, ui.Document.NumberOf(expander.Style, opacity) ?? 0f, 3);
    }
}
