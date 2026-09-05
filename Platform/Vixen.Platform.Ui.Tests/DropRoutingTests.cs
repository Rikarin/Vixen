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
}
