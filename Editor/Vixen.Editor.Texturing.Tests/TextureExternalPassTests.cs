// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>One evaluation sees one version of each file it reads, however many channels read it.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/981">#981</a>, and the syscalls it
///         saves are not what is asserted here.</b> The count is in
///         <see cref="PaintCanvasStoreWiringTests" />, exactly, over a scripted drag — a pointer-up
///         asks the store three times where it asked fifteen. What is here is the property that
///         count is a consequence of, and the one a smaller number cannot demonstrate.
///     </para>
///     <para>
///         ⚠ <b>Every case rewrites the file <em>between</em> two asks of the same pass, which is
///         the only arrangement that can tell a memo from a fast store.</b> A <c>.vxpaint</c> is a
///         file other tools touch, and <see cref="PaintCanvasStore" /> is deliberately honest about
///         that: asked twice across a write it answers with two different canvases, correctly. So a
///         pass that forwarded every ask would build one map out of both — and would look identical
///         to this one on any fixture where the file holds still.
///     </para>
///     <para>
///         ⚠ <b>Each case also asks the bare store afterwards, and that assertion is the
///         instrument.</b> Without it a fixture whose rewrite failed to move the stamp — the file
///         system's timestamp resolution is the usual way — would pass by answering the same canvas
///         for the reason the test is not about. The rewrites here change the canvas's
///         <em>size</em>, so the length moves whatever the clock did.
///     </para>
/// </remarks>
public class TextureExternalPassTests : IDisposable {
    readonly string folder = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Makes the throwaway folder the canvases are written into.</summary>
    public TextureExternalPassTests() => Directory.CreateDirectory(folder);

    /// <inheritdoc />
    public void Dispose() {
        GC.SuppressFinalize(this);

        try {
            Directory.Delete(folder, recursive: true);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // A test that cannot tidy its own temporary folder has still said what it had to say.
        }
    }

    /// <summary>A canvas rewritten mid-pass is still the canvas the rest of that pass reads.</summary>
    /// <remarks>
    ///     <b>The seven channels of an unrestricted paint layer are seven externals of one file</b>,
    ///     and before the pass each of them asked the store afresh. The rewrite below is what an
    ///     artist's other editor, or a version-control checkout, does between two of those asks.
    /// </remarks>
    [Fact]
    public void A_canvas_rewritten_between_two_asks_of_one_pass_is_answered_from_the_first() {
        var file = Written("Hull.vxpaint", 16, 16);

        PaintCanvasStore store = new();
        TextureExternalPass pass = new(store);

        var first = pass.Canvas(file).Canvas;

        Assert.NotNull(first);
        Assert.Equal(16, first.Width);

        Written("Hull.vxpaint", 32, 32);

        var second = pass.Canvas(file).Canvas;

        Assert.Same(first, second);

        // ⚠ The instrument: the rewrite really did move the stamp, so the sameness above is the
        // memo rather than a store that failed to notice. This is also the behaviour the store is
        // supposed to have, and the pass does not change it — it only stops one evaluation
        // straddling it.
        var afterwards = store.Open(file);

        Assert.NotNull(afterwards);
        Assert.NotSame(first, afterwards);
        Assert.Equal(32, afterwards.Width);
    }

    /// <summary>And a picture is decoded once for the pass, not once per channel that names it.</summary>
    [Fact]
    public void A_picture_is_decoded_once_for_the_pass_however_many_channels_name_it() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        PaintCanvasStore store = new();
        TextureExternalPass pass = new(store);

        var decodes = 0;

        TextureData Decode(string path) {
            decodes++;

            using var stream = File.OpenRead(path);

            return new(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1);
        }

        var first = pass.Picture(file, Decode).Picture;

        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);

        var second = pass.Picture(file, Decode).Picture;

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, decodes);

        // The instrument again: the store, asked directly, decodes the rewritten file.
        Assert.NotSame(first, store.Picture(file, Decode));
        Assert.Equal(2, decodes);
    }

    /// <summary>⚠ And a file that would not read is refused once, with the same sentence both times.</summary>
    /// <remarks>
    ///     <b>An unmemoised failure is a question re-asked, which splits an evaluation exactly as a
    ///     rewrite does.</b> A file that appears between two channels of the same fill would give
    ///     one of them a refusal and the other a picture — so what is held is the answer whichever
    ///     way it came out, and the caller composes its own sentence from the message.
    /// </remarks>
    [Fact]
    public void A_refusal_is_held_for_the_pass_the_way_an_answer_is() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        PaintCanvasStore store = new();
        TextureExternalPass pass = new(store);

        var decodes = 0;

        TextureData Decode(string path) {
            decodes++;

            throw new InvalidDataException("this is not a picture this build can decode.");
        }

        var (picture, why) = pass.Picture(file, Decode);

        Assert.Null(picture);
        Assert.Equal("this is not a picture this build can decode.", why);

        // The file appears — a half-written import that finished — and the rest of the pass must
        // not see it, or one fill answers two ways about one file.
        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);

        var (again, sameWhy) = pass.Picture(file, Decoded);

        Assert.Null(again);
        Assert.Equal(why, sameWhy);
        Assert.Equal(1, decodes);

        // The instrument: the store, asked directly, reads the file that is there now.
        Assert.NotNull(store.Picture(file, Decoded));
    }

    /// <summary>Decodes a two-by-two picture, opening the file the way a real decoder would.</summary>
    static TextureData Decoded(string path) {
        using var stream = File.OpenRead(path);

        return new(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1);
    }

    /// <summary>Writes a one-channel canvas into the throwaway folder and returns its absolute path.</summary>
    string Written(string name, int width, int height) {
        var file = Path.Combine(folder, name);

        PaintCanvas canvas = new(width, height);

        canvas.Channel("baseColor");

        using var stream = File.Create(file);

        canvas.Write(stream);

        return file;
    }
}
