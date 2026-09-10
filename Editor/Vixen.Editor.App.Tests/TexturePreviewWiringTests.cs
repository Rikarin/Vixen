// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.Assets.Tests;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 11's channel viewer and mip inspector, joined — <c>TextureImportView.ViewChanged</c> had
///     no subscriber and <c>Image.Texture</c> had no writer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both halves were finished and the panel's own suite was green.</b>
///         <c>ViewTests.ChannelsAreAState</c> asserts that pressing a channel button moves the state
///         and raises the event, which is exactly what happened; what nothing asserted is that
///         anything downstream of the event existed. The preview was not stale — it was empty, on
///         every texture the editor had ever opened.
///         <see href="https://github.com/Rikarin/Vixen/issues/610">#610</see>.
///     </para>
///     <para>
///         ⚠ <b>Asserted on the uploaded texels rather than on <c>Image.Texture</c> being non-zero.</b>
///         A number arriving proves a subscriber ran; it does not prove the subscriber prepared the
///         picture that was asked for, and "the alpha shown as a grey" is a claim about bytes. The
///         recording surface is the same shape <c>ThumbnailTests</c> uses and needs no device.
///     </para>
/// </remarks>
public class TexturePreviewWiringTests {
    /// <summary>How big the fixture is, which is what makes its chain more than one level.</summary>
    const int Width = 8;

    const int Height = 8;

    /// <summary>A surface that keeps what it was handed instead of making a texture out of it.</summary>
    sealed class Recording : IThumbnailSurface {
        readonly List<ulong> released = [];

        ulong next = 1;

        public List<(int Width, int Height, byte[] Pixels)> Uploads { get; } = [];

        public IReadOnlyList<ulong> Released => released;

        public ulong Upload(int width, int height, ReadOnlySpan<byte> rgba) {
            Uploads.Add((width, height, rgba.ToArray()));

            return next++;
        }

        public bool Update(ulong image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) => false;

        public void Release(ulong image) => released.Add(image);
    }

    /// <summary>Opening a texture puts its pixels in the preview, with no button having been pressed.</summary>
    [Fact]
    public void Opening_a_texture_uploads_its_pixels_into_the_preview() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);

        // ⚠ Two, and the second one is the sprite pane's: opening a texture opens two views over one
        // document, and the ladder's chosen level is not the sheet the slicer draws its rects over.
        // See `Opening_a_texture_puts_the_sheet_under_the_sprite_overlay`.
        Assert.Equal(2, surface.Uploads.Count);

        var uploaded = surface.Uploads[0];

        Assert.Equal(Width, uploaded.Width);
        Assert.Equal(Height, uploaded.Height);
        Assert.NotEqual(0ul, view.Preview.Texture);

        // The fixture's own texels, unaltered: every channel is being shown.
        Assert.Equal(Red, uploaded.Pixels[0]);
        Assert.Equal(Green, uploaded.Pixels[1]);
        Assert.Equal(Blue, uploaded.Pixels[2]);
        Assert.Equal(Alpha, uploaded.Pixels[3]);
    }

    /// <summary>Asking for the alpha alone re-uploads it as a grey, which is what makes it visible.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole of the defect in one assertion.</b> A texture drawn with only its alpha
    ///     left in place is invisible on an opaque quad, so "show me the alpha" has to mean the
    ///     numbers spread across R, G and B — which is a swizzle and not a tint, and is why the
    ///     preview cannot answer this without a prepared upload.
    /// </remarks>
    [Fact]
    public void Asking_for_the_alpha_alone_shows_it_as_a_grey() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);

        view.SetChannel(TextureChannels.Red, shown: false);
        view.SetChannel(TextureChannels.Green, shown: false);
        view.SetChannel(TextureChannels.Blue, shown: false);

        session.Frames(1);

        var uploaded = surface.Uploads[^1];

        Assert.Equal(TextureChannels.Alpha, view.Channels);
        Assert.Equal(Alpha, uploaded.Pixels[0]);
        Assert.Equal(Alpha, uploaded.Pixels[1]);
        Assert.Equal(Alpha, uploaded.Pixels[2]);
        Assert.Equal(byte.MaxValue, uploaded.Pixels[3]);

        // ⚠ The previous image is given up as the new one arrives. Without this a click is a leaked
        // GPU texture, and nothing about the picture would say so.
        Assert.NotEmpty(surface.Released);
    }

    /// <summary>Turning the alpha off shows the colour that is stored, rather than the colour in use.</summary>
    [Fact]
    public void Turning_the_alpha_off_leaves_the_colour_where_it_is() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);

        view.SetChannel(TextureChannels.Alpha, shown: false);
        session.Frames(1);

        var uploaded = surface.Uploads[^1];

        Assert.Equal(TextureChannels.Colour, view.Channels);
        Assert.Equal(Red, uploaded.Pixels[0]);
        Assert.Equal(Green, uploaded.Pixels[1]);
        Assert.Equal(Blue, uploaded.Pixels[2]);
        Assert.Equal(byte.MaxValue, uploaded.Pixels[3]);
    }

    /// <summary>Choosing a level of the chain uploads that level, which is a different extent.</summary>
    /// <remarks>
    ///     ⚠ <b>A decoded file has one level, so the chain is generated rather than read.</b> The
    ///     extent is the assertion because it cannot be produced by accident: level two of an 8×8 is
    ///     2×2, and an implementation that re-uploaded level zero under a new number would pass every
    ///     assertion about the picture arriving.
    /// </remarks>
    [Fact]
    public void Choosing_a_mip_level_uploads_that_level() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);

        Assert.True(view.Levels.Count > 2, "an 8×8 source has a chain to inspect");

        view.SetMipLevel(2);
        session.Frames(1);

        var uploaded = surface.Uploads[^1];

        Assert.Equal(2, view.MipLevel);
        Assert.Equal(2, uploaded.Width);
        Assert.Equal(2, uploaded.Height);
    }

    /// <summary>A texture editor opened before the device arrives is drawn when it does.</summary>
    /// <remarks>
    ///     The window has to exist before there is a surface to upload through, and a session restore
    ///     opens documents before the first frame — so this is the ordinary order rather than an edge.
    /// </remarks>
    [Fact]
    public void A_texture_opened_before_the_surface_arrives_is_drawn_when_it_does() {
        using var session = EditorSession.Start();
        var view = Open(session);

        Assert.Equal(0ul, view.Preview.Texture);

        var surface = Attach(session);

        Assert.NotEmpty(surface.Uploads);
        Assert.NotEqual(0ul, view.Preview.Texture);
    }

    /// <summary>A closed editor's image is given back, rather than held for the life of the session.</summary>
    [Fact]
    public void A_closed_editor_releases_its_image() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session, out var id);

        var image = view.Preview.Texture;

        Assert.NotEqual(0ul, image);

        session.Close(id);
        session.Frames(2);

        Assert.True(view.IsRemoved);

        // The sweep runs when the next editor is followed or the surface is restated, which is what
        // a session that keeps opening textures does.
        session.Editor.ThumbnailSurface = surface;

        Assert.Contains(image, surface.Released);
    }

    /// <summary>The sprite editor's overlay gets the sheet under it, which it never had.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same defect as this file's own subject, one pane down.</b>
    ///         <c>SpriteSheetView.Preview.Texture</c> had no writer anywhere in the repository, so
    ///         every rect an author dragged was dragged over an empty box — and it survived because
    ///         <c>SpriteEditorTests</c> asserts the rects, the modes and the sub-assets, which are the
    ///         half that worked. <see href="https://github.com/Rikarin/Vixen/issues/1031">#1031</see>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The intrinsic size is the assertion that the boxes will land on the frames.</b>
    ///         The overlay positions its rects in texels times the zoom while the <c>Image</c> fits
    ///         its picture with <c>object-fit</c>, so the two agree only when the picture's own shape
    ///         is the shape of the box <c>Restate</c> sized — a preview letterboxed inside its box
    ///         would put every rect somewhere the frame is not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_a_texture_puts_the_sheet_under_the_sprite_overlay() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);
        var sprites = view.Sprites;

        Assert.NotEqual(0ul, sprites.Preview.Texture);

        // Its own picture rather than the ladder's, because the two panes are asked different
        // questions and only one of them can be answered with a mip.
        Assert.NotEqual(view.Preview.Texture, sprites.Preview.Texture);

        var uploaded = surface.Uploads[^1];

        Assert.Equal(Width, uploaded.Width);
        Assert.Equal(Height, uploaded.Height);
        Assert.Equal(new Vector2(Width, Height), sprites.Preview.IntrinsicSize);

        // Level zero with every channel, whatever the pane above is showing.
        Assert.Equal(Red, uploaded.Pixels[0]);
        Assert.Equal(Green, uploaded.Pixels[1]);
        Assert.Equal(Blue, uploaded.Pixels[2]);
        Assert.Equal(Alpha, uploaded.Pixels[3]);
    }

    /// <summary>A channel button moves the pane above and leaves the sheet alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that says the second picture is a second picture.</b> An implementation that
    ///     pointed both previews at whatever the ladder last uploaded would pass every assertion about
    ///     the sheet arriving, and then show the sprite editor a 2×2 mip of the alpha the moment
    ///     anybody touched the inspector above it.
    /// </remarks>
    [Fact]
    public void Moving_the_ladder_does_not_disturb_the_sheet() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);

        var sheet = view.Sprites.Preview.Texture;

        Assert.NotEqual(0ul, sheet);

        view.SetChannel(TextureChannels.Alpha, shown: false);
        view.SetMipLevel(2);
        session.Frames(1);

        Assert.Equal(2, view.MipLevel);
        Assert.Equal(sheet, view.Sprites.Preview.Texture);
        Assert.DoesNotContain(sheet, surface.Released);
    }

    /// <summary>
    ///     Issue 1207: repainting the file the editor is open on and refreshing shows the new
    ///     texels, in both panes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The oracle is the uploaded bytes, because the preview is drawn correctly the
    ///         whole time — just of the wrong pixels.</b> <c>TextureImportView.Show</c> was the only
    ///         thing that ever decoded, and it runs when the tab is created, so a repainted
    ///         <c>.png</c> kept the picture it had when it was opened until the tab was closed and
    ///         reopened. A test that asserted a texture id or that something was drawn would have
    ///         passed against every version of that defect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The middle assertion asserts the defect, which is what makes the last one
    ///         meaningful.</b> Nothing has told the panel yet, so it is still showing the old shade —
    ///         and the shades are two values neither of which a decoder produces by accident, so a
    ///         path that uploaded nothing at all could not read back as either.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Refreshing_shows_a_repainted_file_rather_than_the_pixels_the_tab_opened_on() {
        using var session = EditorSession.Start();
        var surface = Attach(session);
        var view = Open(session);

        // ⚠ Read back through the image the pane is *pointing at* rather than by position in the
        // list. The same surface feeds the browser's thumbnails, so a decode landing on a pool
        // thread can arrive between these assertions — and what is being asked is what this pane
        // shows, which is a question the id answers exactly.
        Assert.Equal(Red, Shown(surface, view.Preview.Texture));
        Assert.Equal(Red, Shown(surface, view.Sprites.Preview.Texture));

        Repaint(session, Repainted);

        // The file on disk has moved and nothing has said so: both panes are still the old bytes.
        Assert.Equal(Red, Shown(surface, view.Preview.Texture));
        Assert.Equal(Red, view.Source!.Level(0)[0]);

        session.Run("assets.refresh");
        session.Frames(1);

        // Both panes now show the shade that is on disk, which is a second upload of each.
        Assert.Equal(Repainted, Shown(surface, view.Preview.Texture));
        Assert.Equal(Repainted, Shown(surface, view.Sprites.Preview.Texture));

        // And the panel's own decodes moved with them, which is what the mip ladder and the
        // slicer read rather than the pictures above.
        Assert.Equal(Repainted, view.Source!.Level(0)[0]);
        Assert.Equal(Repainted, view.Sprites.Source!.Level(0)[0]);
    }

    /// <summary>
    ///     ⚠ And the settings the author was typing survive it — a repaint is not an edit to how the
    ///     file is imported.
    /// </summary>
    /// <remarks>
    ///     <b>The instrument check on the fix rather than on the defect.</b> The obvious way to make
    ///     the assertion above pass is to re-run <c>Show</c>, which rebinds the inspector, re-reads
    ///     the sidecar and resets the sprite selection — turning somebody else's Ctrl+S into a
    ///     discard of unsaved settings. The decode is the only thing that may move.
    /// </remarks>
    [Fact]
    public void A_repaint_does_not_discard_the_settings_being_edited() {
        using var session = EditorSession.Start();

        Attach(session);

        var view = Open(session);
        var document = (TextureImportDocument) session.Project.Documents.OfType<TextureImportDocument>().Single();

        document.Texture.MaxSize = 512;
        document.Texture.GenerateMips = false;

        Repaint(session, Repainted);
        session.Run("assets.refresh");
        session.Frames(1);

        Assert.Equal(512, document.Texture.MaxSize);
        Assert.False(document.Texture.GenerateMips);
        Assert.Equal(Repainted, view.Source!.Level(0)[0]);
    }

    /// <summary>
    ///     ⚠ The other route in: a watcher naming the file, which is what an external program's
    ///     Ctrl+S becomes — and an unrelated file must not cost a decode.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The path is the whole of the filter, and the negative half is the instrument.</b>
    ///         <c>EditorProject.AnnounceFileChanged</c> is called once per drained change per open
    ///         document, so a handler that redecoded on every announcement would pass the positive
    ///         assertion here and make somebody else's save cost one PNG decode per open texture
    ///         editor. The two halves are asserted with the same mechanism in the same test.
    ///     </para>
    ///     <para>
    ///         ⚠ Asserted on <c>Source</c> being the <em>same object</em> rather than on the shade,
    ///         because a redecode of an unchanged file produces identical texels — the whole failure
    ///         being guarded against is invisible in the pixels.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_watched_change_to_the_file_redecodes_and_one_to_another_file_does_not() {
        using var session = EditorSession.Start();

        Attach(session);

        var view = Open(session);
        var before = view.Source;

        Assert.NotNull(before);

        Repaint(session, Repainted);

        // Something else in the project moved. Nothing about this file changed, so nothing decodes.
        session.Project.AnnounceFileChanged("Assets/somebody-else.png");
        session.Frames(1);

        Assert.Same(before, view.Source);

        // And now the file itself, named the way `ExternalEdits` names it — project-relative.
        session.Project.AnnounceFileChanged("Assets/hero.png");
        session.Frames(1);

        Assert.NotSame(before, view.Source);
        Assert.Equal(Repainted, view.Source!.Level(0)[0]);
    }

    // ── The fixture ──────────────────────────────────────────────────────────

    /// <summary>The shade the file is given when it is repainted, which no decoder invents.</summary>
    const byte Repainted = 33;

    /// <summary>The first red byte of whatever was uploaded under an image id.</summary>
    /// <remarks>
    ///     The recording surface hands out ids from one upward, so the id a pane is pointing at is
    ///     the position in the list plus one — which is what makes "what is this pane showing"
    ///     answerable without depending on how many other things uploaded in between.
    /// </remarks>
    static byte Shown(Recording surface, ulong image) {
        Assert.NotEqual(0ul, image);
        Assert.InRange(image, 1ul, (ulong) surface.Uploads.Count);

        return surface.Uploads[(int) image - 1].Pixels[0];
    }

    /// <summary>Writes the same file again in one flat shade, as another program would.</summary>
    static void Repaint(EditorSession session, byte shade) {
        var absolute = session.Project.Paths.Absolute("Assets/hero.png");
        var pixels = new byte[Width * Height * 4];

        Array.Fill(pixels, shade);

        File.WriteAllBytes(absolute, MinimalPng.Write(Width, Height, pixels));
    }


    const byte Red = 200;
    const byte Green = 100;
    const byte Blue = 50;
    const byte Alpha = 25;

    /// <summary>Gives the editor somewhere to upload to, and redraws what is already open.</summary>
    static Recording Attach(EditorSession session) {
        var surface = new Recording();

        session.Editor.ThumbnailSurface = surface;

        return surface;
    }

    static TextureImportView Open(EditorSession session) => Open(session, out _);

    /// <summary>Writes a real PNG and opens it the way a double-click in the browser does.</summary>
    static TextureImportView Open(EditorSession session, out string id) {
        var relative = "Assets/hero.png";
        var absolute = session.Project.Paths.Absolute(relative);
        var pixels = new byte[Width * Height * 4];

        for (var texel = 0; texel < pixels.Length; texel += 4) {
            pixels[texel] = Red;
            pixels[texel + 1] = Green;
            pixels[texel + 2] = Blue;
            pixels[texel + 3] = Alpha;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, MinimalPng.Write(Width, Height, pixels));

        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath(relative, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        id = "asset." + entry.Guid;

        var view = Descendants(session.Document.Root).OfType<TextureImportView>().FirstOrDefault();

        Assert.NotNull(view);

        return view;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }
}
