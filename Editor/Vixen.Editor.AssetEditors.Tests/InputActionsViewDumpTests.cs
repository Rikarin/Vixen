// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Input;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The input editor's port, held to the chrome it replaced and the keys it takes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A committed test rather than a wave note.</b> Wave 6 found "byte-identical in N
///         dumped states" claimed by nine ledger rows and gated by three test files. This panel had
///         <b>no view test at all</b> before the port — <c>AuthoringTests</c> covers the document
///         and never builds the view — so there was nothing to regress against and this is it.
///     </para>
///     <para>
///         ⚠ <b>Two dumps, because a tree dump is blind.</b> <c>UiTest.Tree</c> prints tags,
///         classes, rectangles and text; a button's <c>Label</c> and a toggle's <c>IsChecked</c>
///         live in parts the control owns, and the Listen latch is the whole state of this panel's
///         one mode. <c>UiTest.Flags</c> is the other half.
///     </para>
///     <para>
///         ⚠ <b>And the key handling is exercised rather than assumed.</b> The point of the port is
///         that <c>AddHandler&lt;KeyEvent&gt;(…, Capture, handledEventsToo: true)</c> became
///         <c>&lt;self on:keydown.capture.handled /&gt;</c>. A dump that never pressed a key would
///         say nothing about it, so three of the tests below press one — including one that
///         another handler has already marked handled, which is the only assertion that can tell
///         <c>.handled</c> from its absence.
///     </para>
/// </remarks>
public sealed class InputActionsViewDumpTests {
    // ── The chrome, which is what the port moved ─────────────────────────────

    /// <summary>
    ///     The tree the hand-written <c>OnCreated</c> built: a bar of six controls, and a body of a
    ///     tree beside a column of two.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>&lt;self /&gt;</c> creates nothing, and this is where that is checked.</b> It
    ///     names the host rather than making an element, so the panel has exactly the two children
    ///     the C# gave it — a root that had become three would be a layout change hiding inside a
    ///     handler port.
    /// </remarks>
    [Fact]
    public void The_chrome_is_the_tree_OnCreated_built() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);
        var tree = harness.Ui.Tree(view);
        var flags = harness.Ui.Flags(view);

        Assert.Equal("input-editor", view.Tag);
        Assert.Equal(["input-bar", "input-body"], view.Children.Select(child => child.Tag));

        Assert.Contains("<input-bar>", tree, StringComparison.Ordinal);
        Assert.Contains("<input-body>", tree, StringComparison.Ordinal);
        Assert.Contains("<input-side>", tree, StringComparison.Ordinal);
        Assert.Contains("<input-fields>", tree, StringComparison.Ordinal);
        Assert.Contains("<analysis-list>", tree, StringComparison.Ordinal);

        // The `ref`s are the parts the C# assigned in `OnCreated`, and every caller reads them.
        // `input-bar` holds the six controls; `input-body` holds the tree and `Side` beside it,
        // with `Fields` and `Diagnostics` stacked inside that — which is `body.Add("input-side")`
        // followed by two `Side.Add`s, exactly as the hand-written `OnCreated` had it.
        Assert.Same(view.Children[0], view.AddMap.Parent);
        Assert.Same(view.Children[1], view.Tree.Parent);
        Assert.Same(view.Children[1], view.Side.Parent);
        Assert.Equal([view.Tree, view.Side], view.Children[1].Children);
        Assert.Equal([view.Fields, view.Diagnostics], view.Side.Children);

        // The six labels, none of which a tree dump can see.
        foreach (var label in new[] { "Add Map", "Add Action", "Add Binding", "Add Scheme", "Remove", "Listen" }) {
            Assert.Contains($"Label=\"{label}\"", flags, StringComparison.Ordinal);
        }

        // The one control whose size and variant were set by hand, five times over.
        Assert.False(view.Tree.MultiSelect);
        Assert.All(
            new[] { view.AddMap, view.AddAction, view.AddBinding, view.AddScheme, view.Delete },
            button => {
                Assert.Equal(ControlSize.Small, button.Size);
                Assert.Equal(ControlVariant.Subtle, button.Variant);
            }
        );
    }

    // ── The key handling, which is what the port re-registered ───────────────

    /// <summary>
    ///     Listening on, a key pressed, and the control written into the selected binding.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A real dispatched key rather than a call to <c>Record</c>.</b> The whole of what
    ///     changed is how the handler is registered, so a test that called the method behind it
    ///     would pass with no handler registered at all. The focus is put on the panel because
    ///     <c>Keyboard.Dispatch</c> routes to <c>Focused ?? Root</c> — with the focus elsewhere the
    ///     capture leg never reaches this element, which is the very failure
    ///     <c>&lt;self /&gt;</c> exists to avoid.
    /// </remarks>
    [Fact]
    public void Listening_records_the_key_that_was_actually_pressed() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();

        Assert.True(view.IsListening);

        harness.Ui.Document.Focus(view);
        harness.Ui.PressKey(InputKey.J);
        harness.Ui.Frame();

        Assert.Equal("<Keyboard>/j", document.Actions.Maps[0].Actions[0].Bindings[0].Path);

        // A plain binding takes one control and the mode ends, which is also the flags half.
        Assert.False(view.IsListening);
        Assert.DoesNotContain("IsChecked=True", harness.Ui.Flags(view), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The assertion that tells <c>.handled</c> from its absence, and the only one that
    ///     can.</b> Two of the five capture-leg pickers want <c>handledEventsToo</c> and three must
    ///     not have it, so a port that dropped the flag — or spread it to all five — would pass
    ///     every other test in this file. Here something above the panel marks the key handled on
    ///     the way down, which is exactly what <c>CommandDispatcher</c> does to a chord that is
    ///     already bound: without the modifier the router would skip this panel and pressing S to
    ///     bind S would save the scene instead.
    /// </summary>
    [Fact]
    public void A_key_another_handler_has_already_claimed_is_still_recorded() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();

        // On the capture leg at the root, so it runs *before* the panel's — the dispatcher's
        // position. A bubble-leg handler would run after and prove nothing.
        var claimed = 0;

        harness.Ui.Document.Root.AddHandler<KeyEvent>(
            (_, args) => {
                claimed++;
                args.Handled = true;
            },
            RoutingStrategy.Capture
        );

        harness.Ui.Document.Focus(view);
        harness.Ui.PressKey(InputKey.K);
        harness.Ui.Frame();

        // The claim really happened, so the recording below is not a test that forgot to arm itself.
        Assert.True(claimed > 0, "the root handler should have seen the key first");
        Assert.Equal("<Keyboard>/k", document.Actions.Maps[0].Actions[0].Bindings[0].Path);
    }

    /// <summary>Escape is the way out of the mode and is the one control it will not record.</summary>
    [Fact]
    public void Escape_leaves_the_mode_and_binds_nothing() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();

        harness.Ui.Document.Focus(view);
        harness.Ui.PressKey(InputKey.Escape);
        harness.Ui.Frame();

        Assert.False(view.IsListening);
        Assert.Equal(string.Empty, document.Actions.Maps[0].Actions[0].Bindings[0].Path);
    }

    /// <summary>And a key pressed while the mode is off reaches the panel and is ignored.</summary>
    /// <remarks>
    ///     ⚠ <b>The negative half, because a handler that fired unconditionally would pass all
    ///     three tests above.</b> <c>Keyed</c> opens with <c>if (!IsListening)</c>, so this is the
    ///     branch that says the panel is not quietly eating every keystroke in the editor.
    /// </remarks>
    [Fact]
    public void A_key_pressed_while_the_mode_is_off_records_nothing() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        Assert.False(view.IsListening);

        harness.Ui.Document.Focus(view);

        var args = new KeyEvent { Key = InputKey.L, Action = KeyAction.Pressed };

        harness.Ui.Document.Dispatch(args);
        harness.Ui.Frame();

        Assert.Equal(string.Empty, document.Actions.Maps[0].Actions[0].Bindings[0].Path);

        // And it was left for whatever else wanted it, which is what an idle mode owes the editor.
        Assert.False(args.Handled);
    }

    // ── Chords, which the mode could not record at all ───────────────────────

    /// <summary>
    ///     ⚠ <b>Ctrl+S is a <c>buttonWithModifiers</c> composite of two parts, and the mode used to
    ///     be unable to record any chord whatever.</b> The issue's stated symptom — an author gets
    ///     <c>&lt;Keyboard&gt;/s</c> and the modifier is discarded — is <b>refuted</b>: holding
    ///     Control produces a <c>KeyEvent</c> whose key <i>is</i> Control, which the old
    ///     <c>Keyed</c> recorded as <c>&lt;Keyboard&gt;/leftCtrl</c> before dropping out of the
    ///     mode, so S never arrived. Worse than the filed symptom, and the same fix (#468).
    /// </summary>
    [Fact]
    public void A_chord_records_the_composite_the_format_has_always_had() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();
        harness.Ui.Document.Focus(view);

        harness.Ui.Hold(ModifierKeys.Control);
        harness.Ui.KeyDown(InputKey.LeftControl);
        harness.Ui.Frame();

        // The modifier alone recorded nothing and left the mode running — which is the half that
        // makes a chord reachable at all.
        Assert.True(view.IsListening);
        Assert.Equal(InputCompositeKind.None, Binding(document).Composite);

        harness.Ui.PressKey(InputKey.S);
        harness.Ui.Frame();

        var binding = Binding(document);

        Assert.Equal(InputCompositeKind.ButtonWithModifiers, binding.Composite);
        Assert.Equal(string.Empty, binding.Path);
        Assert.Equal(2, binding.Parts.Count);
        Assert.Equal("modifier", binding.Parts[0].Part);
        Assert.Equal("<Keyboard>/leftControl", binding.Parts[0].Path);
        Assert.Equal("button", binding.Parts[1].Part);
        Assert.Equal("<Keyboard>/s", binding.Parts[1].Path);

        Assert.False(view.IsListening);
    }

    /// <summary>
    ///     ⚠ <b>And the composite the panel writes is one the runtime reads back.</b> A recorder
    ///     that produced a shape only it understood would pass every assertion above and bind
    ///     nothing in a game, so the YAML goes through <c>InputActionAssetWriter</c> and
    ///     <c>InputActionAssetReader</c> — the pair a <c>.vxinput</c> actually travels through.
    /// </summary>
    [Fact]
    public void The_recorded_chord_round_trips_through_the_reader() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();
        harness.Ui.Document.Focus(view);

        harness.Ui.Hold(ModifierKeys.Control | ModifierKeys.Shift);
        harness.Ui.KeyDown(InputKey.LeftControl);
        harness.Ui.KeyDown(InputKey.RightShift);
        harness.Ui.PressKey(InputKey.S);
        harness.Ui.Frame();

        var text = InputActionAssetWriter.Write(document.Actions);

        Assert.Contains("buttonWithModifiers", text, StringComparison.Ordinal);

        var result = InputActionAssetReader.Read(text);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Asset);

        var read = result.Asset!.Maps[0].Actions[0].Bindings[0];

        Assert.Equal(InputCompositeKind.ButtonWithModifiers, read.Composite);
        Assert.Equal(
            ["<Keyboard>/leftControl", "<Keyboard>/rightShift", "<Keyboard>/s"],
            read.Parts.Select(part => part.Path)
        );

        // The side actually held, not a left-hand guess: `ModifierKeys` says "either Shift" and a
        // part has to name one control.
        Assert.Equal(["modifier", "modifier", "button"], read.Parts.Select(part => part.Part));
    }

    /// <summary>
    ///     ⚠ <b>A modifier let go with nothing under it still binds the modifier, and refusing
    ///     modifier keys outright would have taken that away.</b> Sprint on Shift is a binding
    ///     people write.
    /// </summary>
    [Fact]
    public void A_modifier_released_on_its_own_binds_the_modifier() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();
        harness.Ui.Document.Focus(view);

        harness.Ui.Hold(ModifierKeys.Shift);
        harness.Ui.KeyDown(InputKey.LeftShift);
        harness.Ui.Frame();

        Assert.Equal(string.Empty, Binding(document).Path);

        harness.Ui.Hold(ModifierKeys.None);
        harness.Ui.KeyUp(InputKey.LeftShift);
        harness.Ui.Frame();

        var binding = Binding(document);

        Assert.Equal(InputCompositeKind.None, binding.Composite);
        Assert.Equal("<Keyboard>/leftShift", binding.Path);
    }

    /// <summary>
    ///     And letting a modifier up <i>after</i> the chord landed does not record a second time.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The predicate that could not be false otherwise.</b> Every real chord ends with the
    ///     modifier coming up, so a release arm that did not know a chord had been recorded would
    ///     overwrite every chord with its own modifier the instant the author's hand left the
    ///     keyboard — and the test above would still pass.
    /// </remarks>
    [Fact]
    public void Letting_the_modifier_up_after_a_chord_does_not_overwrite_it() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, document);

        view.Listen.IsChecked = true;
        harness.Ui.Frame();
        harness.Ui.Document.Focus(view);

        harness.Ui.Hold(ModifierKeys.Control);
        harness.Ui.KeyDown(InputKey.LeftControl);
        harness.Ui.PressKey(InputKey.S);

        harness.Ui.Hold(ModifierKeys.None);
        harness.Ui.KeyUp(InputKey.LeftControl);
        harness.Ui.Frame();

        var binding = Binding(document);

        Assert.Equal(InputCompositeKind.ButtonWithModifiers, binding.Composite);
        Assert.Equal("<Keyboard>/s", binding.Parts[^1].Path);
    }

    // ── The one binding this port introduced ─────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The diagnostics list is the only markup binding in the file, and this is its signal
    ///     audit.</b> Wave 7 shipped <c>@if (QualityRows == 0)</c> over a plain <c>int</c> — no
    ///     dependency, evaluated once — and six dumped states matched byte-for-byte while the
    ///     binding could not fire. <c>reported</c> is a <c>Signal</c>, so this shows two documents
    ///     with different complaints through the same panel and asserts the rows follow. A plain
    ///     field would leave the first load's rows standing for ever and pass any single-state test.
    /// </summary>
    [Fact]
    public void The_diagnostics_rows_follow_the_signal_rather_than_the_first_load() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);

        // A well-formed asset complains about nothing.
        Assert.Empty(Rows(view));

        var broken = Document(harness, "Broken.vxinput", ": not : valid : yaml :\n  - [\n");

        view.Show(broken);
        harness.Ui.Frames(2);

        var complaints = Rows(view);

        Assert.NotEmpty(complaints);
        Assert.All(complaints, row => Assert.True(row.HasClass("error")));

        // The part wave 6 hoisted, standing exactly where `Report`'s hand-built rows stood.
        Assert.All(complaints, row => Assert.Same(view.Diagnostics, row.Parent));
        Assert.Contains("<analysis-stage>", harness.Ui.Tree(view.Diagnostics), StringComparison.Ordinal);
        Assert.Contains("<analysis-message>", harness.Ui.Tree(view.Diagnostics), StringComparison.Ordinal);

        // And back to a clean document, because a binding that only ever grows is the other defect.
        view.Show(Document(harness, "Clean.vxinput", string.Empty));
        harness.Ui.Frames(2);

        Assert.Empty(Rows(view));
    }

    // ── The panel around them ────────────────────────────────────────────────

    static InputActionsView Open(ViewHarness harness, out InputActionsDocument document) {
        document = Document(harness, "Controls.vxinput", string.Empty);

        var view = harness.Ui.Document.Root.Add<InputActionsView>();

        view.Show(document);
        harness.Ui.Frames(2);

        return view;
    }

    static InputActionsDocument Document(ViewHarness harness, string name, string text) =>
        new(harness.Project.Project, AssetId.New(), harness.Project.Write("Assets/" + name, text));

    /// <summary>Adds a binding to the sample action and selects its row, through the tree.</summary>
    /// <remarks>
    ///     ⚠ <b>Driven through <c>TreeView.Select</c> rather than by writing the panel's field</b>,
    ///     so the <c>SelectionChanged</c> subscription <c>OnComposed</c> still owns is part of what
    ///     is under test — and so is the <c>Changed</c> → <c>Reload</c> leg, since adding the
    ///     binding is what rebuilds the tree the selection is then made in. A new document opens
    ///     with one map and one action and no bindings at all, which is the state
    ///     <c>InputActionsDocument</c>'s constructor writes for an empty file.
    /// </remarks>
    static void Choose(ViewHarness harness, InputActionsView view, InputActionsDocument document) {
        document.AddBinding("Player", "Move", new(string.Empty));
        harness.Ui.Frames(2);

        var action = view.Tree.Root.Children[0].Children[0];

        Assert.NotEmpty(action.Children);

        view.Tree.Select(action.Children[0]);
        harness.Ui.Frames(2);

        // The panel read the row's tag, which is what every branch of `Record` then keys off.
        Assert.Equal(0, view.Selected.Binding);
    }

    static InputBindingData Binding(InputActionsDocument document) =>
        document.Actions.Maps[0].Actions[0].Bindings[0];

    static IReadOnlyList<UiElement> Rows(InputActionsView view) =>
        [.. view.Diagnostics.Children.Where(child => child.Tag == "analysis-row")];
}
