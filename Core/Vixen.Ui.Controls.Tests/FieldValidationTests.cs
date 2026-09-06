// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A field that knows whether what it holds is acceptable.</summary>
/// <remarks>
///     ⚠ <b>Half of these assertions are about the accessibility tree rather than about the
///     control's own properties, and that is the half that matters.</b>
///     <see cref="AccessibleStates.Required" /> and <see cref="AccessibleStates.Invalid" /> had been
///     declared for as long as the tree had existed and <i>nothing in the repository produced
///     either</i> — so a form's mandatory fields were announced exactly like its optional ones and a
///     rejected value exactly like an accepted one. A suite that only checked <c>IsValid</c> would
///     pass against a control that kept the verdict entirely to itself, which is what a screen reader
///     would then see.
/// </remarks>
public class FieldValidationTests {
    /// <summary>What the seam being a no-op by default has to look like.</summary>
    [Fact]
    public void A_field_with_no_rule_on_it_is_valid_and_says_nothing() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();

        Assert.True(field.IsValid);
        Assert.Null(field.ValidationMessage);
        Assert.False(field.HasClass("invalid"));
        Assert.Equal(AccessibleStates.None, field.AccessibleState & Reported);
    }

    /// <summary>
    ///     ⚠ Required and invalid are two separate flags and an empty required field carries both.
    ///     Filling it in drops one of them and keeps the other: a field does not stop being mandatory
    ///     because somebody has typed in it, and a form that forgot that would announce its
    ///     requirements only while they were unmet.
    /// </summary>
    [Fact]
    public void A_required_field_is_invalid_while_it_is_empty_and_required_either_way() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();

        field.Required = true;

        Assert.False(field.IsValid);
        Assert.Equal(ControlStrings.FieldRequired.Text, field.ValidationMessage);
        Assert.Equal(AccessibleStates.Required | AccessibleStates.Invalid, field.AccessibleState & Reported);

        field.Value = "Ada";

        Assert.True(field.IsValid);
        Assert.Null(field.ValidationMessage);
        Assert.Equal(AccessibleStates.Required, field.AccessibleState & Reported);
    }

    /// <summary>
    ///     The application supplies the words and this assembly supplies the state, which is the
    ///     whole division of labour: nothing here knows what the field is for.
    /// </summary>
    [Fact]
    public void A_rule_supplies_the_reason_and_the_control_supplies_the_state() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();

        field.Validator = value => value?.Contains('@') == true ? null : "Needs an at-sign";
        field.Value = "ada";

        Assert.False(field.IsValid);
        Assert.Equal("Needs an at-sign", field.ValidationMessage);
        Assert.Equal(AccessibleStates.Invalid, field.AccessibleState & Reported);

        field.Value = "ada@example.com";

        Assert.True(field.IsValid);
        Assert.Null(field.ValidationMessage);
        Assert.Equal(AccessibleStates.None, field.AccessibleState & Reported);
    }

    /// <summary>
    ///     ⚠ A rule attached to a field that has already been filled in does not wait for the next
    ///     keystroke. A form built from a saved document assigns its values first and its rules
    ///     second, and a control that only validated on an edit would show every restored value as
    ///     acceptable until it was touched.
    /// </summary>
    [Fact]
    public void Attaching_a_rule_to_a_field_that_already_holds_something_validates_at_once() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();

        field.Value = "ada";

        Assert.True(field.IsValid);

        field.Validator = _ => "No";

        Assert.False(field.IsValid);
        Assert.Equal("No", field.ValidationMessage);
    }

    /// <summary>
    ///     ⚠ Validity can turn on something that is not the value, which is why
    ///     <see cref="TextField.Revalidate" /> is public. Nothing about a name checked against a list
    ///     changes when a keystroke lands in <i>this</i> field, so a control that only revalidated on
    ///     its own edits would sit there green after the list arrived.
    /// </summary>
    [Fact]
    public void Revalidate_notices_a_condition_that_is_not_the_value() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();
        var taken = false;

        field.Validator = _ => taken ? "Already taken" : null;
        field.Value = "ada";

        Assert.True(field.IsValid);

        taken = true;

        Assert.True(field.IsValid);

        field.Revalidate();

        Assert.False(field.IsValid);
        Assert.Equal(AccessibleStates.Invalid, field.AccessibleState & Reported);
    }

    /// <summary>
    ///     The theme's half. A verdict nothing draws is a verdict a sighted user never learns, and
    ///     the class is the only thing a stylesheet can hang a picture on.
    /// </summary>
    [Fact]
    public void The_invalid_class_arrives_and_leaves_with_the_verdict() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();

        field.Required = true;
        fixture.Update();

        Assert.True(field.HasClass("invalid"));

        field.Value = "Ada";
        fixture.Update();

        Assert.False(field.HasClass("invalid"));
    }

    /// <summary>
    ///     ⚠ The default rule asks whether anything was supplied, not whether it was meaningful. A
    ///     space is a character and a control that decided otherwise would be rejecting a value some
    ///     other field legitimately wants — trimming is a rule about what the field is for, so it
    ///     belongs in a <see cref="TextField.Validator" />.
    /// </summary>
    [Fact]
    public void The_required_rule_counts_characters_and_does_not_judge_them() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<TextBox>();

        field.Required = true;
        field.Value = " ";

        Assert.True(field.IsValid);
    }

    /// <summary>A required multi-select wants one, and one is what "required" means on a set.</summary>
    /// <remarks>
    ///     ⚠ <b>The verdict is over the <i>set</i>, which is why this could not be a flag on
    ///     <c>SelectBase</c>.</b> A <see cref="Select" /> asks whether <c>Value</c> is null; this
    ///     asks whether anything is in the chosen set, and the two questions have no shared shape to
    ///     put on a base class beyond the word. Unchoosing the last one has to take the verdict back,
    ///     which is the half a writer hooked only to "something was added" gets wrong.
    /// </remarks>
    [Fact]
    public void A_required_multi_select_is_invalid_until_something_is_chosen() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<MultiSelect>();

        field.AddOption("red", "Red");
        field.AddOption("green", "Green");

        Assert.True(field.IsValid);
        Assert.Equal(AccessibleStates.None, field.AccessibleState & Reported);

        field.Required = true;

        Assert.False(field.IsValid);
        Assert.Equal(AccessibleStates.Required | AccessibleStates.Invalid, field.AccessibleState & Reported);

        field.Select("red", true);

        Assert.True(field.IsValid);
        Assert.Equal(AccessibleStates.Required, field.AccessibleState & Reported);

        // ⚠ Two chosen and then both taken away. One is enough and the second changes nothing, but
        // removing the last one has to put the verdict back — which is the direction a writer that
        // only listened for an addition never runs.
        field.Select("green", true);
        Assert.True(field.IsValid);

        field.Select("red", false);
        Assert.True(field.IsValid);

        field.Select("green", false);
        Assert.False(field.IsValid);
        Assert.Equal(AccessibleStates.Required | AccessibleStates.Invalid, field.AccessibleState & Reported);
    }

    /// <summary>A combo box's verdict is its editor's, and both halves are told the right thing.</summary>
    /// <remarks>
    ///     ⚠ <b>The split is the decision, and it is not the same answer on both sides.</b> ARIA
    ///     puts <c>role="combobox"</c> on the <i>input</i>, so <c>aria-required</c> and
    ///     <c>aria-invalid</c> belong to the editor and the box itself reports
    ///     <see cref="AccessibleRole.None" /> — a second set of states here would be a second node
    ///     standing for the same field. The <i>cascade</i> is the other way round: what somebody
    ///     writes a rule against is <c>combo-box:invalid</c>, because the border, the chevron and the
    ///     field are one box on screen. So the tree stays on the editor and the state bits are
    ///     mirrored out.
    /// </remarks>
    [Fact]
    public void A_combo_boxs_verdict_is_its_editors_and_reaches_both() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<ComboBox>();

        Assert.True(field.IsValid);
        Assert.True(field.State.HasFlag(ElementState.Valid));
        Assert.False(field.State.HasFlag(ElementState.Invalid));

        field.Required = true;

        Assert.True(field.Editor.Required);
        Assert.False(field.IsValid);

        // The box, for the stylesheet.
        Assert.True(field.State.HasFlag(ElementState.Required));
        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.False(field.State.HasFlag(ElementState.Valid));

        // The editor, for the tree — and nothing on the box, which has no node.
        Assert.Equal(
            AccessibleStates.Required | AccessibleStates.Invalid,
            field.Editor.AccessibleState & Reported
        );

        field.Value = "ada";

        Assert.True(field.IsValid);
        Assert.True(field.State.HasFlag(ElementState.Valid));
        Assert.False(field.State.HasFlag(ElementState.Invalid));
        Assert.Equal(AccessibleStates.Required, field.Editor.AccessibleState & Reported);
    }

    /// <summary>A rule that is not the value moves the box as well as the editor.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this method exists for: <c>Editor.Revalidate()</c> alone leaves the box
    ///     stale.</b> The editor is where the rules live, so it is the object an application reaches
    ///     for — and a mirror that is only refreshed by a value change is a mirror that is right
    ///     until the first time the answer moves for any other reason.
    /// </remarks>
    [Fact]
    public void Revalidating_a_combo_box_moves_the_box_and_not_only_the_editor() {
        using var fixture = new ControlFixture();
        var field = fixture.Add<ComboBox>();
        var taken = false;

        field.Editor.Validator = _ => taken ? "Already taken" : null;
        field.Value = "ada";

        Assert.True(field.State.HasFlag(ElementState.Valid));

        taken = true;
        field.Revalidate();

        Assert.False(field.IsValid);
        Assert.True(field.State.HasFlag(ElementState.Invalid));
        Assert.False(field.State.HasFlag(ElementState.Valid));
    }

    /// <summary>Every state this file is about, so an unrelated flag cannot make an assertion pass.</summary>
    const AccessibleStates Reported = AccessibleStates.Required | AccessibleStates.Invalid;
}
