// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>That focusing a field turns the operating system's text input on, and tells it where the caret is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two implemented, tested, six-platform capabilities with no caller.</b>
///         <c>ITextInput.Activate</c>'s only caller in the whole repository was the game host's debug
///         console; <c>ITextInput.SetCandidateArea</c> — implemented in <c>DesktopServices</c>,
///         <c>WebTextInput</c>, <c>AndroidServices</c>, <c>IosInput</c> and <c>HeadlessServices</c> —
///         had none at all outside one headless test that called it by hand. So no text control in
///         the framework ever started text input, and no input method was ever told where to draw
///         its candidate list.
///     </para>
///     <para>
///         ⚠ <b>Why nobody noticed on a desktop.</b> SDL leaves text input running, so a focused
///         <c>TextField</c> on Windows, macOS or Linux received characters anyway. On the web and on
///         a phone it receives nothing — and everywhere, the candidate window for Japanese, Chinese
///         or Korean sits at a corner of the screen over whatever is there.
///     </para>
///     <para>
///         <b>Asserted through the loop rather than on <c>PlatformTextInput</c> directly.</b> The
///         defect was never the mechanism; it was that no frame called it. A unit test on the wire
///         would have been green for the whole four years the wire was missing.
///     </para>
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class TextInputWiringTests {
    sealed class Probe : Component {
        public TextBox Field { get; private set; } = null!;

        public bool ReadOnly { get; init; }

        protected override void Build(BuildContext ctx) {
            Field = ctx.Element(Root, "probe-panel").Add<TextBox>();
            Field.Value = "hello";
            Field.ReadOnly = ReadOnly;
        }
    }

    /// <summary>What the platform's text input held on the last frame of the run.</summary>
    /// <remarks>
    ///     ⚠ <b>Sampled inside the loop and not read afterwards.</b> The loop deactivates text input
    ///     on its way out — it is process state and SDL leaves it running past the window that asked
    ///     — so every one of these assertions read after <c>Run</c> would see it off, and three of
    ///     the four would pass for that reason rather than for their own.
    /// </remarks>
    readonly record struct Observed(bool IsActive, Rectangle CandidateArea);

    static (Probe Content, Observed Last) Run(bool focus, bool readOnly = false, int frames = 3) {
        var probe = new Probe { ReadOnly = readOnly };
        var platform = new HeadlessPlatform();
        var last = default(Observed);

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = frames,
            InstallSystemFont = false,
            Content = () => probe,
            Started = app => {
                if (focus) {
                    app.Document.Focus(probe.Field);
                }
            },

            // ⚠ `Frame` runs before this frame's update and therefore before this frame's wire, so
            // what it samples is the previous frame's answer. With three frames that is the answer
            // for a document whose focus was set in `Started`, which is what is being asserted.
            Frame = (_, _) => {
                var input = (HeadlessTextInput)platform.TextInput;
                last = new(input.IsActive, input.CandidateArea);
            }
        };

        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });

        var application = new UiApplication(options, platform, window);
        application.Run();

        return (probe, last);
    }

    /// <summary>A focused field turns text input on and places the candidate window at its caret.</summary>
    /// <remarks>
    ///     ⚠ <b>The rectangle is compared with the field's own, not with a literal.</b> A literal
    ///     would be a second computation of the caret position that could agree with the first while
    ///     both were wrong; the assertion that matters is that what the platform was told is what the
    ///     field draws.
    /// </remarks>
    [Fact]
    public void Focusing_a_field_starts_text_input_at_its_caret() {
        var (probe, last) = Run(focus: true);

        Assert.True(last.IsActive, "text input was never started, so a web or mobile field gets no characters.");
        Assert.Equal(probe.Field.CaretArea, last.CandidateArea);

        // And it is a real rectangle rather than the zero one a wire that never ran would leave.
        Assert.NotEqual(Rectangle.Empty, last.CandidateArea);
    }

    /// <summary>With nothing focused, text input stays off.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that is not symmetry.</b> While text input is on the platform gives
    ///     keystrokes to the input method first, so <c>W</c> may compose rather than reach a binding
    ///     — which is why <c>ITextInput</c> is off by default and why a host that only ever activated
    ///     would break every keyboard-driven game that also draws a chat box.
    /// </remarks>
    [Fact]
    public void With_no_field_focused_text_input_is_left_off() {
        var (_, last) = Run(focus: false);

        Assert.False(last.IsActive);
        Assert.Equal(Rectangle.Empty, last.CandidateArea);
    }

    /// <summary>A read-only field takes the focus and does not start an input method.</summary>
    /// <remarks>
    ///     A read-only field is still focusable and still selectable — selecting and copying is what
    ///     it is for. What it must not do is raise a candidate window over a field that will discard
    ///     everything the input method commits, which is the same distinction
    ///     <c>TextField.ShowsCaret</c> makes about drawing a caret at all.
    /// </remarks>
    [Fact]
    public void A_read_only_field_does_not_start_an_input_method() {
        var (probe, last) = Run(focus: true, readOnly: true);

        Assert.True(probe.Field.IsFocused, "the field never took the focus, so the assertion below proves nothing.");
        Assert.False(last.IsActive);
    }

    /// <summary>Moving the focus to another field moves the candidate window to it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A wire that pushed the caret rectangle once and never again passes every
    ///         assertion above.</b> The candidate window has to follow the focus, or a user who tabs
    ///         from one field to the next types into the second one with the input method's list
    ///         sitting over the first.
    ///     </para>
    ///     <para>
    ///         Two fields stacked in a column, so the rectangles differ in <i>y</i> — measured
    ///         against layout rather than against glyph advances, because
    ///         <see cref="UiApplicationOptions.InstallSystemFont" /> is off here and a caret at the
    ///         start and the end of a zero-width string is at the same <i>x</i>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Moving_the_focus_moves_the_candidate_window() {
        var probe = new TwoFields();
        var platform = new HeadlessPlatform();
        var seen = new List<Rectangle>();

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = 6,
            InstallSystemFont = false,
            Content = () => probe,
            Started = app => app.Document.Focus(probe.First),
            Frame = (app, _) => {
                seen.Add(((HeadlessTextInput)platform.TextInput).CandidateArea);

                if (seen.Count == 3) {
                    app.Document.Focus(probe.Second);
                }
            }
        };

        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });

        var application = new UiApplication(options, platform, window);
        application.Run();

        Assert.Equal(probe.First.CaretArea, seen[2]);
        Assert.Equal(probe.Second.CaretArea, seen[^1]);
        Assert.NotEqual(seen[2], seen[^1]);
    }

    sealed class TwoFields : Component {
        public TextBox First { get; private set; } = null!;

        public TextBox Second { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            var panel = ctx.Element(Root, "probe-panel");

            First = panel.Add<TextBox>();
            Second = panel.Add<TextBox>();
        }
    }
}
