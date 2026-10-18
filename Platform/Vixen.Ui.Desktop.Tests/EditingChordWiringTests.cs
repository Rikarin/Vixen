// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>That a plain application's ⌘C and ⌘Z reach the responder chain with no keymap built.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The application with no editor in it, which is the one that had nothing.</b> The
///         editor and <c>Samples/02-HelloUi</c> each build a <c>KeyMap</c> and a
///         <c>CommandDispatcher</c> by hand; a <see cref="UiApplication" /> built from options did
///         not, so the Undo it installs on the root answered Edit ▸ Undo and never ⌘Z unless a text
///         field with history happened to have the focus (#642, #650).
///     </para>
///     <para>
///         <b>Driven by platform key events through the loop</b>, the path a keyboard takes, and not
///         by <see cref="UiDocument.Dispatch(KeyEvent)" />: what was missing was never the
///         mechanism, it was that this host did not turn it on.
///     </para>
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class EditingChordWiringTests {
    sealed class Probe : Component {
        public TextBox Field { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Field = ctx.Element(Root, "probe-panel").Add<TextBox>();
            Field.Value = "hello";
        }
    }

    sealed class Clipboard : IUiClipboard {
        public string? Text { get; private set; }

        public bool HasText => Text is not null;

        public bool TryGetText([NotNullWhen(true)] out string? text) {
            text = Text;
            return text is not null;
        }

        public bool SetText(string text) {
            Text = text;
            return true;
        }
    }

    /// <summary>Runs an application that presses one chord after <paramref name="started" /> has set it up.</summary>
    static void Run(Probe probe, Action<UiApplication> started, Key key) {
        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = 3,
            InstallSystemFont = false,
            Content = () => probe,

            // The Windows table on every machine, so the chord below means the same thing on a Mac.
            Configure = document => document.EditingKeymap = EditingKeymap.Windows,
            Started = application => {
                started(application);

                platform.Post(PlatformEvent.Keyboard(
                    PlatformEventKind.KeyDown,
                    window.Id,
                    Stopwatch.GetTimestamp(),
                    key,
                    KeyModifiers.LeftControl
                ));
            }
        };

        using var application = new UiApplication(options, platform, window);
        application.Run();
    }

    /// <summary>The nearer half: the field's own Copy, and the window's untouched.</summary>
    /// <remarks>
    ///     ⚠ <b>Green without the routing as well, and that is what it is for.</b> The field takes
    ///     ⌘C itself while it has a selection, so this is the control for the test below it: turning
    ///     the chord into a command must not let the window's Copy outrank the caret.
    /// </remarks>
    [Fact]
    public void A_focused_text_field_answers_copy_and_the_window_is_not_asked() {
        var probe = new Probe();
        var clipboard = new Clipboard();
        var window = 0;

        Run(probe, application => {
            application.Document.Clipboard = clipboard;

            var responder = new CommandResponder();
            responder.Add("edit.copy", () => window++);
            application.Document.CommandResponder = responder;

            application.Document.Focus(probe.Field);
            probe.Field.SelectAll();
        }, Key.C);

        Assert.True(probe.Field.IsFocused, "the field never took the focus, so the assertion below proves nothing.");
        Assert.Equal("hello", clipboard.Text);
        Assert.Equal(0, window);
    }

    [Fact]
    public void With_nothing_focused_copy_falls_through_to_the_window() {
        var window = 0;
        UiDocument? document = null;

        Run(new Probe(), application => {
            document = application.Document;

            var responder = new CommandResponder();
            responder.Add("edit.copy", () => window++);
            document.CommandResponder = responder;
        }, Key.C);

        Assert.Null(document!.Focused);
        Assert.Equal(1, window);
    }

    [Fact]
    public void Undo_reaches_the_application_s_manager_from_outside_a_field() {
        var value = 2;

        Run(new Probe(), application => {
            var manager = application.Document.UndoManager!;
            manager.Register("Set", () => value = 1, () => value = 2);
        }, Key.Z);

        Assert.Equal(1, value);
    }
}
