// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § D4's <c>AddPreview</c> row, asserted on the grid rather than on the registry.</summary>
/// <remarks>
///     <para>
///         <b>P2's rule about how to test a contribution, applied.</b> A registry entry proves
///         nothing; what is asserted here is a tile in the content browser drawing a picture of a file
///         nothing in the engine can decode, and the same file drawing a type glyph when the
///         contribution is not there.
///     </para>
///     <para>
///         ⚠ <b>The refusal set is why one of these exists at all.</b> <c>ThumbnailCache</c> refuses
///         an extension permanently — deliberately, so a folder of two hundred <c>.fbx</c> files is
///         not two hundred background tasks per scroll — and a plugin always activates after the grid
///         has already refused everything it owns. Without the subscription its files would show
///         glyphs until the editor was restarted, which is the same failure
///         <c>RefreshSettingsPages</c> exists to prevent one kind along.
///     </para>
/// </remarks>
public class AssetPreviewContributionTests {
    const string Owned = "Assets/curve.sample";

    /// <summary>A surface that hands out numbers and remembers the pixels.</summary>
    sealed class Recording : IThumbnailSurface {
        public List<(int Width, int Height, byte[] Pixels)> Uploads { get; } = [];

        ulong next = 1;

        public ulong Upload(int width, int height, ReadOnlySpan<byte> rgba) {
            Uploads.Add((width, height, rgba.ToArray()));

            return next++;
        }

        public bool Update(ulong image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) => false;

        public void Release(ulong image) { }
    }

    /// <summary>
    ///     The content browser draws a picture for a file no decoder in the engine can read, because
    ///     something contributed one — and draws the type glyph for it when nothing has.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, in one test, because either alone is satisfiable by the wrong code.</b> A
    ///     grid that drew a picture for everything would pass the first; a registry nothing reads
    ///     would pass the second. What is under test is the difference between them.
    /// </remarks>
    [Fact]
    public void The_grid_draws_a_contributed_picture_for_a_file_no_decoder_can_read() {
        var registry = new EditorRegistry();
        using var editor = EditorSession.Start(new() { Extensions = registry });

        var surface = new Recording();

        editor.Editor.ThumbnailSurface = surface;

        var asset = Write(editor);

        editor.Open("project");
        editor.Settle();

        var grid = Grid(editor);

        // The instrument: with nothing contributed the file is refused, so no picture is ever asked
        // for. If this were already a picture the test below would prove nothing.
        Assert.False(
            Until(editor, () => surface.Uploads.Count > 0, frames: 40),
            "a file no decoder claims was decoded anyway."
        );
        Assert.Contains(grid.Items, item => item.Guid == asset);

        using var scope = registry.Add(Flat(0x20, 0x80, 0xC0));

        Assert.True(
            Until(editor, () => grid.Tiles.Any(tile => tile.Node?.Guid == asset && tile.Picture.Texture != 0)),
            "the contributed preview never reached the grid."
        );

        var tile = Assert.Single(grid.Tiles, candidate => candidate.Node?.Guid == asset);

        Assert.False(tile.Picture.HasClass("hidden"));
        Assert.True(tile.Glyph.HasClass("hidden"));

        // And what was uploaded is the contributor's own colour, reduced by the editor's own filter
        // rather than by the plugin — a box filter over one colour is that colour.
        var uploaded = Assert.Single(surface.Uploads);

        Assert.Equal(ThumbnailCache.Size, uploaded.Width);
        Assert.Equal(ThumbnailCache.Size, uploaded.Height);
        Assert.Equal(0x20, uploaded.Pixels[0]);
        Assert.Equal(0x80, uploaded.Pixels[1]);
        Assert.Equal(0xC0, uploaded.Pixels[2]);
    }

    /// <summary>
    ///     A contributor claiming an extension the built-in decoders also read is asked first, which
    ///     is what "asks the registry first and falls back" has to mean to be worth anything.
    /// </summary>
    [Fact]
    public void A_contributed_preview_is_asked_before_the_built_in_decoders() {
        var registry = new EditorRegistry();
        using var editor = EditorSession.Start(new() { Extensions = registry });

        var surface = new Recording();

        editor.Editor.ThumbnailSurface = surface;

        using var scope = registry.Add(Flat(0x11, 0x22, 0x33) with { Extension = ".png" });

        var painted = Paint(editor);

        editor.Open("project");
        editor.Settle();

        var grid = Grid(editor);

        Assert.True(
            Until(editor, () => grid.Tiles.Any(tile => tile.Node?.Guid == painted && tile.Picture.Texture != 0)),
            "the .png never got a picture at all."
        );

        var uploaded = Assert.Single(surface.Uploads);

        // The contributor's colour, not the 0xF0 grey the file actually contains.
        Assert.Equal(0x11, uploaded.Pixels[0]);
        Assert.Equal(0x22, uploaded.Pixels[1]);
        Assert.Equal(0x33, uploaded.Pixels[2]);
    }

    /// <summary>
    ///     ⚠ A contributor that throws is a type glyph, not a dead editor. The delegate is a plugin's
    ///     and it runs on a pool thread nobody is watching.
    /// </summary>
    [Fact]
    public void A_preview_that_throws_leaves_the_grid_standing() {
        var registry = new EditorRegistry();
        using var editor = EditorSession.Start(new() { Extensions = registry });

        var surface = new Recording();

        editor.Editor.ThumbnailSurface = surface;

        var asset = Write(editor);
        var asked = 0;

        using var scope = registry.Add(
            new AssetPreview(".sample", _ => {
                Interlocked.Increment(ref asked);

                throw new InvalidOperationException("the plugin is unhappy");
            })
        );

        editor.Open("project");
        editor.Settle();

        var grid = Grid(editor);

        // ⚠ The instrument, and without it this test passes on an editor that never consults the
        // registry at all: what is under test is surviving the throw, so the throw has to happen.
        Assert.True(Until(editor, () => Volatile.Read(ref asked) > 0), "the contributor was never asked.");

        Assert.False(
            Until(editor, () => surface.Uploads.Count > 0, frames: 40),
            "a throwing contributor uploaded something."
        );

        // Still a live grid, still showing the file, and showing it the way it shows anything with no
        // picture.
        var tile = Assert.Single(grid.Tiles, candidate => candidate.Node?.Guid == asset);

        Assert.True(tile.Picture.HasClass("hidden"));
        Assert.False(tile.Glyph.HasClass("hidden"));
    }

    /// <summary>A flat square of one colour, which is a picture a box filter cannot change.</summary>
    static AssetPreview Flat(byte r, byte g, byte b) =>
        new(".sample", _ => {
            var pixels = new byte[32 * 32 * 4];

            for (var at = 0; at < pixels.Length; at += 4) {
                pixels[at] = r;
                pixels[at + 1] = g;
                pixels[at + 2] = b;
                pixels[at + 3] = 0xFF;
            }

            return new AssetPreviewImage(32, 32, pixels);
        });

    /// <summary>Puts a file of a kind nothing in the engine can decode into the project.</summary>
    static AssetId Write(EditorSession editor) {
        var absolute = Path.Combine(editor.ProjectRoot, Owned.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "not an image, and no decoder claims the extension");

        editor.Run("assets.refresh");

        return editor.Project.Assets.TryGetByPath(Owned, out var entry)
            ? entry.Guid
            : throw editor.Fail($"'{Owned}' is not in the index");
    }

    /// <summary>
    ///     And a real PNG — <c>ThumbnailTests</c>'s own fixture, because a file the built-in decoder
    ///     cannot read would make "the contributor was asked first" indistinguishable from "the
    ///     contributor was the fallback".
    /// </summary>
    static AssetId Paint(EditorSession editor) =>
        ThumbnailTests.Paint(editor, "Assets/plate.png", 16, 16, static (_, _) => 0xF0);

    static AssetGrid Grid(EditorSession editor) => editor.Control<AssetGrid>("project");

    /// <summary>Pumps frames until something a pool thread does has happened, or gives up.</summary>
    /// <remarks>
    ///     ⚠ <b>A frame count and a boolean answer, because two of these tests are waiting for
    ///     <i>nothing</i> to happen.</b> An assertion inside would make "no picture was ever decoded"
    ///     unsayable; the caller says which way round it wants the answer — and asks for far fewer
    ///     frames when the answer it wants is "no", since that arm always spends all of them.
    /// </remarks>
    static bool Until(EditorSession editor, Func<bool> done, int frames = 200) {
        for (var frame = 0; frame < frames && !done(); frame++) {
            editor.Frame();
            Thread.Sleep(1);
        }

        return done();
    }
}
