// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The form: a set of fields submitted as one, refused while any of them is wrong.</summary>
/// <remarks>
///     ⚠ <b>The subject is the submission, not the picture</b> — a form draws nothing of its own. What
///     each test pins is one of HTML's rules: implicit submission from a field, the default button,
///     refusal with the focus on the first field that said no, the form owner, and the bar on
///     disabled fields.
/// </remarks>
public class FormTests {
    sealed class Built : IDisposable {
        public ControlFixture Fixture { get; } = new();

        public Form Form { get; }

        public TextBox Name { get; }

        public TextBox Email { get; }

        public Button Ok { get; }

        public int Submissions { get; private set; }

        public Built() {
            Form = Fixture.Add<Form>();
            Form.AccessibleName = "Account";
            Form.Submitted += _ => Submissions++;

            Name = Form.Add<TextBox>();
            Name.Required = true;

            Email = Form.Add<TextBox>();
            Email.Required = true;

            Ok = Form.Add<Button>();
            Ok.Label = "OK";
            Ok.IsDefault = true;

            Fixture.Update();
        }

        public void Dispose() => Fixture.Dispose();
    }

    /// <summary>A form is a landmark of its own kind, and the first thing in the tree to report one.</summary>
    [Fact]
    public void A_form_is_a_form_landmark_and_not_a_stop() {
        using var built = new Built();

        Assert.Equal(AccessibleRole.Form, built.Form.Role);
        Assert.Equal("Account", built.Form.AccessibleName);
        Assert.False(built.Form.Focusable);
    }

    /// <summary>
    ///     ⚠ <b>A submission with a field that will not take its value is refused, and the keyboard is
    ///     put in the first such field.</b>
    /// </summary>
    /// <remarks>
    ///     Both fields are empty and required, so both are wrong; the focus goes to the first in
    ///     document order, and every field is marked as having been through a submission — which is
    ///     what lets <c>:user-invalid</c> show the second field, one the user never reached.
    /// </remarks>
    [Fact]
    public void A_submission_with_a_wrong_field_is_refused_and_focuses_the_first() {
        using var built = new Built();

        Assert.False(built.Email.State.HasFlag(ElementState.UserInteracted));

        Assert.False(built.Form.Submit());

        Assert.Equal(0, built.Submissions);
        Assert.Same(built.Name, built.Fixture.Document.Focused);
        Assert.True(built.Email.State.HasFlag(ElementState.UserInteracted));
        Assert.True(built.Email.State.HasFlag(ElementState.Invalid));

        built.Name.Value = "Ada";
        Assert.False(built.Form.Submit());
        Assert.Same(built.Email, built.Fixture.Document.Focused);

        built.Email.Value = "ada@example.org";
        Assert.True(built.Form.Submit());
        Assert.Equal(1, built.Submissions);
    }

    /// <summary>
    ///     ⚠ <b>Enter in a field submits the form around it, once</b> — implicit submission, heard as
    ///     the routed event the field raises.
    /// </summary>
    /// <remarks>
    ///     Once and not twice: the default button is in the same form, and a submission heard both
    ///     from the field and from a click the key equivalent sent to the button would call the
    ///     application's handler twice for one key.
    /// </remarks>
    [Fact]
    public void Enter_in_a_field_submits_the_form_once() {
        using var built = new Built();

        built.Name.Value = "Ada";
        built.Email.Value = "ada@example.org";

        built.Fixture.Document.Focus(built.Email);
        built.Fixture.Type(InputKey.Enter);

        Assert.Equal(1, built.Submissions);
    }

    /// <summary>The default button submits; any other button in the form does not.</summary>
    [Fact]
    public void The_default_button_submits_and_another_does_not() {
        using var built = new Built();

        built.Name.Value = "Ada";
        built.Email.Value = "ada@example.org";

        var help = built.Form.Add<Button>();
        help.Label = "Help";
        built.Fixture.Update();

        built.Fixture.Click(help);
        Assert.Equal(0, built.Submissions);

        built.Fixture.Click(built.Ok);
        Assert.Equal(1, built.Submissions);
    }

    /// <summary>
    ///     ⚠ <b>A disabled field is barred from validation</b>, as a disabled <c>&lt;input&gt;</c> is —
    ///     and so is every field under a disabled control.
    /// </summary>
    [Fact]
    public void A_disabled_field_does_not_block_a_submission() {
        using var built = new Built();

        built.Name.Value = "Ada";
        built.Email.Disabled = true;

        var box = built.Form.Add<GroupBox>();
        box.Disabled = true;
        box.Content.Add<TextBox>().Required = true;
        built.Fixture.Update();

        Assert.True(built.Form.Submit());
        Assert.Equal([built.Name], built.Form.Fields);
    }

    /// <summary>
    ///     ⚠ <b>A field belongs to the nearest form above it.</b> The outer form does not validate the
    ///     inner one's fields, and Enter in an inner field submits the inner form only.
    /// </summary>
    [Fact]
    public void A_field_belongs_to_the_nearest_form() {
        using var built = new Built();

        built.Name.Value = "Ada";
        built.Email.Value = "ada@example.org";

        var inner = built.Form.Add<Form>();
        var address = inner.Add<TextBox>();
        address.Value = "1 Analytical Row";

        var innerSubmissions = 0;
        inner.Submitted += _ => innerSubmissions++;

        // A wrong field in the inner form is none of the outer form's business.
        var postcode = inner.Add<TextBox>();
        postcode.Required = true;
        built.Fixture.Update();

        Assert.DoesNotContain(postcode, built.Form.Fields);
        Assert.True(built.Form.Submit());
        Assert.Equal(1, built.Submissions);

        postcode.Value = "N1";
        built.Fixture.Document.Focus(address);
        built.Fixture.Type(InputKey.Enter);

        Assert.Equal(1, innerSubmissions);
        Assert.Equal(1, built.Submissions);
    }

    /// <summary>
    ///     A form is a column: its one theme rule, since an element nothing styles lays its children
    ///     out in a row and would put every field beside the one before it.
    /// </summary>
    [Fact]
    public void A_form_stacks_its_fields() {
        using var built = new Built();

        Assert.Equal(built.Name.AbsoluteLeft, built.Email.AbsoluteLeft);
        Assert.True(
            built.Email.AbsoluteTop >= built.Name.AbsoluteTop + built.Name.Height,
            $"the second field starts at {built.Email.AbsoluteTop}, inside the first ({built.Name.AbsoluteTop} + {built.Name.Height})"
        );
    }

    /// <summary>Every validating control is a field, and a combo box answers for its own editor.</summary>
    [Fact]
    public void Every_validating_control_is_a_field_once() {
        using var fixture = new ControlFixture();

        var form = fixture.Add<Form>();
        var text = form.Add<TextBox>();
        var select = form.Add<Select>();
        var multi = form.Add<MultiSelect>();
        var combo = form.Add<ComboBox>();
        var check = form.Add<CheckBox>();
        var radios = form.Add<RadioGroup>();

        // Not fields: a button, and a row whose field is the one inside it.
        form.Add<Button>();
        var row = form.Add<LabeledContent>();
        var inRow = row.Content.Add<NumericInput>();
        fixture.Update();

        Assert.Equal([text, select, multi, combo, check, radios, inRow], form.Fields);

        // And a required tick box nobody ticked refuses on its own, with the focus on it.
        check.Required = true;
        Assert.False(form.Submit());
        Assert.Same(check, fixture.Document.Focused);
    }
}
