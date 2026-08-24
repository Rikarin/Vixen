// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     The two panels in this assembly whose capture-leg key handler moved from <c>OnComposed</c>
///     into <c>&lt;self /&gt;</c>, driven by real dispatched keys.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Neither panel had a key press in its suite before this.</b> <c>PaletteTests</c>
///         drives the palette by writing <c>Field.Value</c> and calling <c>Move</c> and
///         <c>Accept</c>; <c>KeyBindingsViewTests</c> calls <c>Capture</c> and <c>Rebind</c>. Both
///         are the legs that cannot fail — the handler could have been deleted outright and every
///         one of those tests would still pass. The port changed <i>how the handler is registered</i>
///         and nothing else, so a test that does not press a key says nothing about it.
///     </para>
///     <para>
///         ⚠ <b>And <c>&lt;self /&gt;</c> is what makes the press arrive at all.</b>
///         <c>Keyboard.Dispatch</c> routes to <c>Focused ?? Root</c>, and both panels focus their
///         search box on open — so the capture route runs root → panel → box. A handler moved onto
///         the first markup root instead of the host would sit <i>beside</i> the box rather than
///         above it and would never see the key. That is the behaviour change the four pickers
///         stayed hand-written to avoid, and these are the tests that would catch it.
///     </para>
///     <para>
///         ⚠ <b>The <c>.handled</c> pair is the point of the last two tests.</b> Two of the five
///         capture-leg panels want <c>handledEventsToo</c> and three must not have it. The palette
///         is one of the three, <c>KeyBindingsView</c> is one of the two, and they are asserted
///         against the same arrangement in opposite directions — because a port that gave the
///         modifier to all five, or to none, would pass every other test in both suites.
///     </para>
/// </remarks>
public sealed class SelfHandlerKeyTests {
    static StringId Title(string text) => new("test." + text, text);

    // ── CommandPalette: capture, and deliberately not `handled` ──────────────

    /// <summary>
    ///     Down moves the highlight, and it does so before the search box treats it as caret
    ///     movement.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The focus is on the <c>SearchBox</c>, which is the whole difficulty.</b>
    ///     <c>OnOpened</c> focuses it, so a bubble-leg handler would get Down only after the box had
    ///     already moved its caret, and a handler on the first markup root would not be on the route
    ///     at all. Both are invisible to a test that calls <c>Move(1)</c>.
    /// </remarks>
    [Fact]
    public void The_palette_takes_the_arrows_before_the_box_it_is_focused_on() {
        using var shell = Shell(out var ui);

        Assert.Equal(0, shell.Palette.Highlighted);
        Assert.True(shell.Palette.Results.Count > 1, "the fixture should offer more than one row");

        ui.PressKey(InputKey.Down);
        ui.Frame();

        Assert.Equal(1, shell.Palette.Highlighted);

        ui.PressKey(InputKey.Up);
        ui.Frame();

        Assert.Equal(0, shell.Palette.Highlighted);
    }

    /// <summary>
    ///     ⚠ <b>The assertion that separates the host from the first markup root, which none of the
    ///     other palette tests can make.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Checked rather than assumed, and the first attempt was wrong.</b> Because
    ///     <c>OnOpened</c> focuses the <c>SearchBox</c>, the capture route runs root → palette →
    ///     box — so a handler written on <c>&lt;SearchBox&gt;</c> instead of on <c>&lt;self /&gt;</c>
    ///     is still on that route, as its target, and hears every key the tests above press. Moving
    ///     the attribute there leaves them all green; it was tried.
    ///
    ///     What tells them apart is a key that arrives elsewhere in the panel, which is the case
    ///     the four pickers stayed hand-written for: "a key arriving while the focus is on the
    ///     result list would never reach it". <c>palette-list</c> is not under the box, so raising
    ///     there is on the host's route and not on the box's.
    /// </remarks>
    [Fact]
    public void A_key_arriving_over_the_list_reaches_the_host_and_not_a_root_beside_it() {
        using var shell = Shell(out var ui);

        Assert.Equal(0, shell.Palette.Highlighted);

        var args = new KeyEvent { Key = InputKey.Down, Action = KeyAction.Pressed };

        shell.Palette.List.Raise(args);
        ui.Frame();

        Assert.Equal(1, shell.Palette.Highlighted);
        Assert.True(args.Handled);
    }

    /// <summary>Enter runs the highlighted command and closes the palette.</summary>
    [Fact]
    public void Enter_runs_the_highlighted_command() {
        using var shell = Shell(out var ui);

        var ran = 0;

        shell.Commands.Add("test.only", Title("Zzz Unique"), () => ran++);
        shell.Palette.Field.Value = "Zzz Unique";
        shell.Palette.Refresh();
        ui.Frame();

        Assert.Equal("Zzz Unique", shell.Palette.Results[0].Title);

        ui.PressKey(InputKey.Enter);
        ui.Frame();

        Assert.Equal(1, ran);
        Assert.False(shell.Palette.IsOpen);
    }

    /// <summary>
    ///     ⚠ <b>And a key something else has already claimed does <i>not</i> move the palette.</b>
    ///     This is the assertion that says the palette was not given <c>.handled</c> along with the
    ///     two panels that want it. A palette that ran on handled events would move its highlight —
    ///     and, on Enter, <i>run a command</i> — on a keystroke another handler had taken, which is
    ///     the concrete harm behind "a handler that runs on already-handled events is not a free
    ///     upgrade". The mirror of this test is
    ///     <c>InputActionsViewDumpTests.A_key_another_handler_has_already_claimed_is_still_recorded</c>,
    ///     which asserts the opposite about the panel that does want it.
    /// </summary>
    [Fact]
    public void A_key_something_else_has_claimed_does_not_move_the_palette() {
        using var shell = Shell(out var ui);

        var claimed = 0;

        ui.Document.Root.AddHandler<KeyEvent>(
            (_, args) => {
                claimed++;
                args.Handled = true;
            },
            RoutingStrategy.Capture
        );

        ui.PressKey(InputKey.Down);
        ui.Frame();

        Assert.True(claimed > 0, "the root handler should have seen the key first");
        Assert.Equal(0, shell.Palette.Highlighted);
    }

    // ── KeyBindingsView: capture *and* handled ───────────────────────────────

    /// <summary>
    ///     ⚠ <b>Recording a chord sees the key even though the dispatcher has already run a
    ///     command for it, and that is what <c>.handled</c> buys.</b> Without the modifier the
    ///     router skips a handler once something downstream has marked the event handled — so
    ///     pressing Ctrl+S to <i>bind</i> Ctrl+S would save the scene and record nothing. The root
    ///     handler here stands in for <c>CommandDispatcher</c>, which is attached to the document
    ///     and is above this panel on the capture leg exactly as this is.
    /// </summary>
    [Fact]
    public void Recording_hears_a_chord_the_dispatcher_has_already_claimed() {
        using var document = new UiDocument(900f, 600f);
        using var ui = UiTest.Adopt(document);

        ControlTheme.Install(document);
        EditorTheme.Install(document);

        var commands = new CommandRegistry();
        var keys = new KeyMap();

        commands.Add("scene.frame-all", Title("Frame All"), static () => { });

        var view = document.Root.Add<KeyBindingsView>();

        view.Show(commands, keys);
        ui.Frames(2);

        var row = view.Grid.Items
            .Select((item, at) => (Row: item as KeyBindingRow, At: at))
            .First(entry => entry.Row?.Id == "scene.frame-all")
            .At;

        view.Grid.Select(row);
        ui.Frames(2);

        view.Capture(true);
        ui.Frames(2);

        Assert.True(view.IsCapturing);

        var claimed = 0;

        document.Root.AddHandler<KeyEvent>(
            (_, args) => {
                claimed++;
                args.Handled = true;
            },
            RoutingStrategy.Capture
        );

        document.Focus(view);
        ui.PressKey(InputKey.F);
        ui.Frames(2);

        Assert.True(claimed > 0, "the stand-in dispatcher should have seen the key first");

        // The chord landed, which is only possible because the binding carries `handled`.
        Assert.False(view.IsCapturing);
        Assert.Equal(new KeyChord(InputKey.F, ModifierKeys.None), keys.ChordFor("scene.frame-all"));
    }

    // ── The shell around the palette ─────────────────────────────────────────

    /// <summary>An open palette with three commands in it, and a harness over its document.</summary>
    /// <remarks>
    ///     <c>OpenPalette</c> focuses the field, so the arrangement under test is the real one: the
    ///     focus is on a control that wants the arrows itself.
    /// </remarks>
    static EditorShell Shell(out UiTest ui) {
        var shell = new EditorShell(900f, 600f);

        shell.Commands.Add("file.save", Title("Save"), static () => { });
        shell.Commands.Add("file.save-all", Title("Save All"), static () => { });
        shell.Commands.Add("file.save-as", Title("Save As"), static () => { });

        shell.Palette.OpenPalette();
        shell.Palette.Field.Value = "save";
        shell.Palette.Refresh();

        ui = UiTest.Adopt(shell.Document);
        ui.Frames(2);

        return shell;
    }
}
