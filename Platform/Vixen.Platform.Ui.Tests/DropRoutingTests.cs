// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>A file dragged onto the window reaches the document, which it never did.</summary>
/// <remarks>
///     <para>
///         ⚠ <c>PlatformEventKind.DropFile</c> and <c>DropText</c> are produced by
///         <c>DesktopPlatform</c> from SDL and by <c>WebPlatform</c> from the browser's drop
///         handler, both backends assert their own translation, and <c>PlatformInput.Dispatch</c>
///         had no arm for either — so both fell through its <c>default</c> and dragging a file onto
///         a Vixen window was inert on every platform. This is <c>TextCompositionRoutingTests</c>'
///         gap one event kind over, and it is a test on the seam for the same reason: both halves
///         were tested and correct, and the join was neither.
///     </para>
/// </remarks>
public class DropRoutingTests {
    /// <summary>What selecting five textures in a file manager and dragging them once looks like.</summary>
    static readonly string[] Paths = [
        "/tmp/a.png", "/tmp/b.png", "/tmp/c.png", "/tmp/d.png", "/tmp/e.png"
    ];

    static UiDocument Laid() {
        var document = new UiDocument(200f, 100f);
        document.Load("root { width: 200px; height: 100px; }");
        document.Update();
        return document;
    }

    /// <summary>A dropped file arrives as a path, at the point it was dropped.</summary>
    [Fact]
    public void A_dropped_file_reaches_the_document() {
        using var document = Laid();
        DropEvent? seen = null;
        document.Root.AddHandler<DropEvent>((_, args) => seen = args);

        var handled = PlatformInput.Dispatch(
            document,
            PlatformEvent.Drop(PlatformEventKind.DropFile, 1, 0, "/tmp/scene.vxscene", new Vector2(30f, 40f))
        );

        Assert.True(handled);
        Assert.NotNull(seen);
        Assert.Equal("/tmp/scene.vxscene", Assert.Single(seen.Files));
        Assert.Null(seen.Text);
        Assert.Equal(30f, seen.X);
        Assert.Equal(40f, seen.Y);
    }

    /// <summary>Dropped text arrives as text and not as a path.</summary>
    /// <remarks>
    ///     ⚠ <b>The load-bearing half.</b> The two kinds carry their payload in the same
    ///     <c>PlatformEvent.Text</c> field, so the arm that forwards them decides which one it is —
    ///     and a bridge that put both in <see cref="DropEvent.Files" /> would hand a handler a
    ///     dragged sentence as a filename, which fails at the first <c>File.OpenRead</c> with an
    ///     error about a path that was never a path.
    /// </remarks>
    [Fact]
    public void Dropped_text_is_not_delivered_as_a_file() {
        using var document = Laid();
        DropEvent? seen = null;
        document.Root.AddHandler<DropEvent>((_, args) => seen = args);

        PlatformInput.Dispatch(
            document,
            PlatformEvent.Drop(PlatformEventKind.DropText, 1, 0, "some dragged words", new Vector2(5f, 5f))
        );

        Assert.NotNull(seen);
        Assert.Empty(seen.Files);
        Assert.Equal("some dragged words", seen.Text);
    }

    /// <summary>Five files selected together arrive as one drop with five paths.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The defect this closes is not a missing event but an excess of them.</b> SDL
    ///         posts one <c>SDL_DROPFILE</c> per path and brackets the run; the brackets were
    ///         discarded, so a handler that opened a document per drop opened five windows for one
    ///         gesture and a handler that started an import job started five. Nothing failed and
    ///         nothing logged.
    ///     </para>
    ///     <para>
    ///         The count is the assertion. "It received the five paths" is also true of five
    ///         separate events, so what is checked is that the document was dispatched to
    ///         <i>once</i>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bracketed_run_of_files_is_one_drop() {
        using var document = Laid();
        var drops = new List<DropEvent>();
        document.Root.AddHandler<DropEvent>((_, args) => drops.Add(args));

        Assert.True(Dispatch(PlatformEventKind.DropBegin, string.Empty));

        foreach (var path in Paths) {
            Assert.True(Dispatch(PlatformEventKind.DropFile, path));

            // Nothing is delivered until the group closes — a handler that saw the first file
            // before the last one arrived would be back to one event per file with extra steps.
            Assert.Empty(drops);
        }

        Assert.True(Dispatch(PlatformEventKind.DropComplete, string.Empty));

        var drop = Assert.Single(drops);

        Assert.Equal(Paths, drop.Files);
        Assert.Null(drop.Text);
        Assert.Equal(30f, drop.X);
        Assert.Equal(40f, drop.Y);

        return;

        bool Dispatch(PlatformEventKind kind, string text) =>
            PlatformInput.Dispatch(document, PlatformEvent.Drop(kind, 1, 0, text, new Vector2(30f, 40f)));
    }

    /// <summary>A backend that sends no brackets still delivers its drops, one each.</summary>
    /// <remarks>
    ///     ⚠ <b>The wrong way to write the coalescing passes every other test here.</b> Deferring
    ///     delivery to <c>DropComplete</c> unconditionally is simpler and reads better, and it makes
    ///     drag-and-drop do nothing at all on any backend that produces <c>DropFile</c> and no
    ///     brackets — a silent failure, because a drop nobody handled looks exactly like a drop that
    ///     was never delivered. So the un-bracketed path is asserted beside the bracketed one rather
    ///     than assumed to be untouched.
    /// </remarks>
    [Fact]
    public void An_unbracketed_file_still_stands_on_its_own() {
        using var document = Laid();
        var drops = new List<DropEvent>();
        document.Root.AddHandler<DropEvent>((_, args) => drops.Add(args));

        foreach (var path in Paths) {
            PlatformInput.Dispatch(
                document,
                PlatformEvent.Drop(PlatformEventKind.DropFile, 1, 0, path, new Vector2(30f, 40f))
            );
        }

        Assert.Equal(Paths.Length, drops.Count);
        Assert.Equal(Paths, drops.Select(drop => Assert.Single(drop.Files)));
    }

    /// <summary>A bracket that carried nothing delivers nothing.</summary>
    /// <remarks>
    ///     A drag whose payload the platform could not turn into a path still brackets, and a
    ///     <see cref="DropEvent" /> with no files and no text is a drop of nothing that a handler
    ///     has no way to refuse — it would read as a drop on every element under the pointer.
    /// </remarks>
    [Fact]
    public void An_empty_bracket_delivers_no_drop() {
        using var document = Laid();
        var drops = 0;
        document.Root.AddHandler<DropEvent>((_, _) => drops++);

        Assert.True(PlatformInput.Dispatch(document, Bracket(PlatformEventKind.DropBegin)));
        Assert.True(PlatformInput.Dispatch(document, Bracket(PlatformEventKind.DropComplete)));

        Assert.Equal(0, drops);
    }

    /// <summary>A group nobody closed does not swallow the next one.</summary>
    /// <remarks>
    ///     ⚠ <b>The accumulator is static, so an abandoned group is a defect that outlives the drag
    ///     that caused it</b> — a window closed mid-drag, or a backend that stops mid-run, would
    ///     otherwise leave every later file joining a group nothing will ever close, and drag-and-drop
    ///     would be dead for the life of the process. A new <c>DropBegin</c> starts a fresh group and
    ///     discards whatever was open, which is the one place that can be recovered from.
    /// </remarks>
    [Fact]
    public void A_new_group_discards_one_that_was_never_closed() {
        using var document = Laid();
        var drops = new List<DropEvent>();
        document.Root.AddHandler<DropEvent>((_, args) => drops.Add(args));

        PlatformInput.Dispatch(document, Bracket(PlatformEventKind.DropBegin));

        PlatformInput.Dispatch(
            document,
            PlatformEvent.Drop(PlatformEventKind.DropFile, 1, 0, "/tmp/abandoned.png", new Vector2(30f, 40f))
        );

        // The drag went away. The next one begins.
        PlatformInput.Dispatch(document, Bracket(PlatformEventKind.DropBegin));

        PlatformInput.Dispatch(
            document,
            PlatformEvent.Drop(PlatformEventKind.DropFile, 1, 0, "/tmp/wanted.png", new Vector2(30f, 40f))
        );

        PlatformInput.Dispatch(document, Bracket(PlatformEventKind.DropComplete));

        var drop = Assert.Single(drops);

        Assert.Equal("/tmp/wanted.png", Assert.Single(drop.Files));
    }

    static PlatformEvent Bracket(PlatformEventKind kind) =>
        PlatformEvent.Drop(kind, 1, 0, string.Empty, new Vector2(30f, 40f));
}
