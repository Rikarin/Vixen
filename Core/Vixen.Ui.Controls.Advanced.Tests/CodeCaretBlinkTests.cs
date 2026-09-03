// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The code editor's caret blinks, counted in changed frames rather than timed.</summary>
/// <remarks>
///     <para>
///         <b>The instrument is <see cref="UiTest.Redraws" /> and the number is exact.</b>
///         <c>UiDocument.Draw</c> answers whether the rebuilt draw list differs from the previous
///         frame's, so a settled editor with a blinking caret in it produces exactly one changed
///         frame per flip and none at all in between. That makes "it blinks" a count — six half
///         periods, six changed frames — rather than a screenshot at a moment somebody chose.
///     </para>
///     <para>
///         ⚠ <b>An exact count is what separates this from an assertion that cannot fail.</b>
///         <c>Redraws</c> increasing is satisfied by an editor that rewrites itself every frame for
///         some other reason, which is the failure mode <c>EditorStillnessTests</c> exists to catch;
///         a count that has to be six catches both a caret that does not blink and an editor that
///         never settles.
///     </para>
///     <para>
///         ⚠ <b>Ten frames to a half period.</b> The harness's clock is stepped and set to 10 ms a
///         frame here, so nothing below is measured against a wall clock and the numbers are the same
///         on a loaded machine.
///     </para>
/// </remarks>
public class CodeCaretBlinkTests {
    static readonly FontFace Font = LoadFont();

    static readonly TimeSpan Blink = TimeSpan.FromMilliseconds(100);

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Advanced.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>An editor with both themes over it, on a clock that steps 10 ms a frame.</summary>
    static (UiTest Ui, CodeEditor Editor) Editor(bool focused = true) {
        var ui = UiTest.Create(800f, 600f, new UiTestOptions { FrameDelta = TimeSpan.FromMilliseconds(10) });

        ui.Document.Fonts.Register("Test", Font);

        ControlTheme.Install(ui.Document);
        AdvancedTheme.Install(ui.Document);
        ui.Load("root { width: 800px; height: 600px; }");

        var editor = ui.Document.Root.Add<CodeEditor>();
        editor.Source = "one\ntwo\nthree";
        editor.CaretBlink = Blink;

        ui.Frame();
        editor.Refresh();
        ui.Frame();

        if (focused) {
            ui.Document.Focus(editor);
        }

        return (ui, editor);
    }

    /// <summary>Sixty frames of a focused editor are six changed frames and no others.</summary>
    [Fact]
    public void A_focused_editor_changes_once_per_half_period_and_not_otherwise() {
        var (ui, _) = Editor();
        using var owned = ui;

        // The focus ring appearing is itself a change; the claim is about the frames after it.
        ui.Frames(4);

        var redraws = ui.Redraws;

        ui.Frames(60);

        Assert.Equal(redraws + 6, ui.Redraws);
    }

    /// <summary>And an editor nobody is in changes nothing at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that keeps a shell still.</b> <c>DrawCaret</c> returns before it reads the
    ///     clock when the editor has not got the focus, so an interface with a code pane on it that
    ///     nobody has clicked into costs the same as it did before the caret learned to blink.
    /// </remarks>
    [Fact]
    public void An_unfocused_editor_is_still() {
        var (ui, _) = Editor(focused: false);
        using var owned = ui;

        ui.Frames(4);

        var redraws = ui.Redraws;

        ui.Frames(60);

        Assert.Equal(redraws, ui.Redraws);
    }

    /// <summary>Moving the caret restarts the phase, so a held arrow key does not make it flicker.</summary>
    /// <remarks>
    ///     ⚠ <b>Nine frames, a move, nine frames — nineteen in all, which is most of two half
    ///     periods.</b> A phase measured from a free-running clock would have flipped in the middle
    ///     of that and the count would be one rather than zero.
    /// </remarks>
    [Fact]
    public void Moving_the_caret_restarts_the_phase() {
        var (ui, editor) = Editor();
        using var owned = ui;

        ui.Frames(4);
        ui.Frames(9);

        var redraws = ui.Redraws;

        editor.Move(new TextPosition(1, 0));
        ui.Frames(9);

        // One changed frame — the caret arriving on the second line — and no flip after it.
        Assert.Equal(redraws + 1, ui.Redraws);
    }

    /// <summary>Zero is an editor whose caret never goes out.</summary>
    [Fact]
    public void A_blink_of_zero_leaves_the_editor_still() {
        var (ui, editor) = Editor();
        using var owned = ui;

        editor.CaretBlink = TimeSpan.Zero;
        ui.Frames(4);

        var redraws = ui.Redraws;

        ui.Frames(60);

        Assert.Equal(redraws, ui.Redraws);
    }
}
