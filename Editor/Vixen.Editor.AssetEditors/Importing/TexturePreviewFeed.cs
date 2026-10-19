// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Plugin;
using Vixen.Graphics;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>What puts pixels in a texture editor's preview, for the channels and level it asks for.</summary>
/// <remarks>
///     <para>
///         <b>The subscriber <c>TextureImportView.ViewChanged</c> never had.</b> The panel decodes
///         the file, offers four channel buttons and a mip ladder, and raises an event when either
///         moves — and nothing listened, so pressing <b>A</b> moved a button and left the picture
///         alone. Worse, <c>Image.Texture</c> was never written by anything at all: the preview was
///         not stale, it was empty, on every texture the editor has ever opened.
///         <see href="https://github.com/Rikarin/Vixen/issues/610">#610</see>.
///     </para>
///     <para>
///         ⚠ <b>Beside the panel and not in it, and through <see cref="IEditorGraphics" /> rather than
///         a device.</b> <c>Image.Texture</c> is a number the host's renderer hands out, which needs a
///         texture, which needs the device — and this assembly has no device and must not acquire
///         one. So the view says what it wants to see and this says it in pixels through the
///         graphics the plugin contract lends, which is the same surface the browser's grid uploads
///         through. ⚠ <b>It was the application's until #1297</b>, holding that surface directly and
///         naming <see cref="TextureImportView" /> to do it — one of the names doc 36 § P3 counted.
///     </para>
///     <para>
///         ⚠ <b>The isolate is a CPU pass and not a tint.</b> A draw-list tint multiplies; showing
///         the alpha as a grey is a swizzle. The draw list did gain a
///         <c>UiImageView</c> for that in
///         <see href="https://github.com/Rikarin/Vixen/issues/611">#611</see> — but it carries one
///         choice of channel, where this panel's <see cref="TextureChannels" /> is a set, and it
///         cannot answer "which mip" at all. A prepared upload answers both with one mechanism.
///     </para>
///     <para>
///         ⚠ <b>The chain is generated with the import's own filter</b>, from
///         <see cref="TextureContent" /> — a colour texture averaged in linear light, a normal map
///         renormalised. A mip inspector showing a chain filtered differently from the one that will
///         ship is an inspector that cannot be used to decide anything.
///     </para>
///     <para>
///         ⚠ <b>Followers are dropped by <see cref="UiElement.IsRemoved" />, which is the only
///         honest test</b> — a docked panel is torn down by the workspace and tells nobody here. It
///         is the same sweep <c>AssetEditorsModule.Follow</c> keeps, and without it a closed texture
///         editor holds a GPU texture for the life of the session.
///     </para>
/// </remarks>
sealed class TexturePreviewFeed(IEditorGraphics graphics) {
    readonly IEditorGraphics graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
    readonly List<Followed> followers = [];

    /// <summary>How many editors are being kept fed.</summary>
    internal int Count => followers.Count;

    /// <summary>Starts feeding a freshly opened texture editor, and draws it once now.</summary>
    /// <param name="view">The panel.</param>
    /// <param name="document">The document it was opened over, which is where the mip filter is written.</param>
    /// <remarks>
    ///     ⚠ <b>Once now as well as on every change</b>, because the first picture is the one nobody
    ///     asked for: the panel is opened, nothing has been pressed, and a subscriber that only
    ///     reacted would leave the preview empty until somebody happened to click a channel.
    /// </remarks>
    public void Follow(TextureImportView view, TextureImportDocument document) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(document);

        Sweep();

        var followed = new Followed(view, document);

        followers.Add(followed);
        view.ViewChanged += _ => Draw(followed);

        Draw(followed);
    }

    /// <summary>Redraws every open editor, for a host that has only just acquired a device.</summary>
    /// <remarks>
    ///     The application is built before the window has a Vulkan surface, and a session restore
    ///     opens documents early — so the editors that matter most are exactly the ones opened while
    ///     <see cref="IEditorGraphics.Upload" /> had nowhere to put a picture — which is what <see cref="IEditorGraphics.SurfaceChanged" /> calls this for.
    /// </remarks>
    public void Restate() {
        Sweep();

        foreach (var followed in followers) {
            Draw(followed);
        }
    }

    /// <summary>Gives up every image, for a session that is going away.</summary>
    public void Release() {
        foreach (var followed in followers) {
            followed.Image?.Dispose();
            followed.Sliced?.Dispose();
        }

        followers.Clear();
    }

    /// <summary>Drops the editors that have been closed, releasing what they were showing.</summary>
    void Sweep() {
        for (var index = followers.Count - 1; index >= 0; index--) {
            if (!followers[index].View.IsRemoved) {
                continue;
            }

            followers[index].Image?.Dispose();
            followers[index].Sliced?.Dispose();
            followers.RemoveAt(index);
        }
    }

    /// <summary>Uploads what one editor is currently asking to see, if that has changed.</summary>
    /// <remarks>
    ///     ⚠ <b>Two pictures rather than one.</b> The texture editor's two panes are two views over
    ///     one document — the ladder's chosen level and channels above, and the sprite sheet the
    ///     slicer draws its rects over below — and they ask for different texels, so neither can be
    ///     the other's picture. See <see cref="Slices" />.
    /// </remarks>
    void Draw(Followed followed) {
        if (followed.View.IsRemoved) {
            return;
        }

        Ladder(followed, graphics);
        Slices(followed, graphics);
    }

    /// <summary>Puts the level and channels the mip inspector is asking for into the top pane.</summary>
    static void Ladder(Followed followed, IEditorGraphics images) {
        // ⚠ Only the eight-bit form, the same limit `ThumbnailCache` states: an `.hdr` decodes to
        // `Rgba32Float`, and reducing that to something a UI texture can hold is a tone map rather
        // than a copy. The panel already says why there is no preview when nothing decoded at all.
        if (followed.View.Source is not { Format: PixelFormat.Rgba8UNorm } source) {
            return;
        }

        if (!ReferenceEquals(followed.Source, source)) {
            followed.Source = source;
            followed.Chain = Chained(source, followed.Document);

            // Nothing has been shown of this source yet, so the next comparison must not match.
            followed.Level = -1;
        }

        if (followed.Chain is not { } chain) {
            return;
        }

        var level = Math.Clamp(followed.View.MipLevel, 0, chain.LevelCount - 1);
        var channels = followed.View.Channels;

        // ⚠ The comparison is what keeps a settings keystroke from being an upload. `Refresh` runs on
        // every edit in the inspector beside the picture, and re-uploading the same texels for each
        // one would make typing a size limit cost a GPU texture per character.
        if (followed.Image is not null && followed.Level == level && followed.Channels == channels) {
            return;
        }

        var described = chain.Levels[level];
        if (images.Upload(described.Width, described.Height, Prepared(chain.Level(level), channels)) is not { } image) {
            return;
        }

        followed.Image?.Dispose();
        followed.Image = image;
        followed.Level = level;
        followed.Channels = channels;

        followed.View.Preview.Texture = image.Image;

        // The level's own extent, so `object-fit` letterboxes a mip by the shape it actually is.
        followed.View.Preview.IntrinsicSize = new(described.Width, described.Height);
    }

    /// <summary>Puts the sheet itself under the sprite editor's overlay.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>SpriteSheetView.Preview.Texture</c> had no writer either</b>, so every rect an
    ///         author dragged was dragged over an empty box — the same defect as
    ///         <see href="https://github.com/Rikarin/Vixen/issues/610">#610</see> one pane down, and
    ///         it survived because <c>SpriteEditorTests</c> asserts the rects and the modes, which are
    ///         the half that worked. <see href="https://github.com/Rikarin/Vixen/issues/1031">#1031</see>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Level zero, every channel, and the pane's <i>own</i> decode.</b> The overlay
    ///         positions its rects in <c>SpriteSheetView.Source</c>'s texels times its zoom, so a
    ///         picture taken from anywhere else would be a picture the boxes do not describe. That is
    ///         also why the size written here is the source extent: <c>Restate</c> sizes the box to
    ///         exactly those texels times the zoom, so a matching intrinsic size makes <c>object-fit</c>
    ///         land the picture on the box rather than letterbox it — which is what puts a rect over
    ///         the frame it was cut from.
    ///     </para>
    ///     <para>
    ///         <b>Once per source rather than per change</b>, which is what makes it a second upload
    ///         and not a second upload per click: a channel button and the mip ladder move the pane
    ///         above and say nothing about the sheet.
    ///     </para>
    /// </remarks>
    static void Slices(Followed followed, IEditorGraphics images) {
        var sprites = followed.View.Sprites;

        if (sprites.Source is not { Format: PixelFormat.Rgba8UNorm, Width: > 0, Height: > 0 } sheet) {
            return;
        }

        if (followed.Sliced is not null && ReferenceEquals(followed.Sheet, sheet)) {
            return;
        }

        if (images.Upload(sheet.Width, sheet.Height, sheet.Level(0)) is not { } image) {
            return;
        }

        followed.Sliced?.Dispose();
        followed.Sliced = image;
        followed.Sheet = sheet;

        sprites.Preview.Texture = image.Image;
        sprites.Preview.IntrinsicSize = new(sheet.Width, sheet.Height);
    }

    /// <summary>The source with a mip chain under it, filtered the way the import will filter it.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy, because a decoded file has one level.</b> The mip inspector's whole question
    ///     is what the levels below the first look like, and a decoder answers only the first — so
    ///     the chain is generated here rather than read off the file, once per source rather than
    ///     once per click.
    /// </remarks>
    static TextureData? Chained(TextureData source, TextureImportDocument document) {
        if (source.Width <= 0 || source.Height <= 0) {
            return null;
        }

        var chain = new TextureData(source.Format, source.Width, source.Height);

        source.Level(0).CopyTo(chain.LevelSpan(0));

        try {
            MipChain.Generate(chain, Filter(document.Texture));
        } catch (NotSupportedException) {
            // A format `MipChain` will not reduce still has a level zero worth showing, which is what
            // the ladder's first row is. Everything below it stays black rather than the panel
            // refusing to draw anything.
        }

        return chain;
    }

    /// <summary>What the settings say the texture holds, as a filter.</summary>
    static MipOptions Filter(TextureImportEdits settings) => settings.Content switch {
        TextureContent.NormalMap => MipOptions.NormalMap,
        TextureContent.Colour when settings.AlphaIsTransparency => MipOptions.CutoutColour,
        TextureContent.Colour => MipOptions.Colour,
        _ => MipOptions.Linear
    };

    /// <summary>One level's texels with the channels the viewer asked for, and nothing else.</summary>
    /// <param name="level">The level's own bytes, four to a texel.</param>
    /// <param name="channels">What to show.</param>
    /// <returns>The texels to upload.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One channel on its own becomes a grey, and several stay where they are.</b> That
    ///         is the two questions <see cref="TextureChannels" />'s own remarks say a channel viewer
    ///         is asked: "what is in the alpha" wants the numbers spread across R, G and B, because a
    ///         picture drawn only in the alpha of an opaque quad is invisible; "what does this look
    ///         like without its alpha" wants the colour it already is.
    ///     </para>
    ///     <para>
    ///         The alpha is forced opaque unless it was asked for, so a texture's own transparency
    ///         cannot be mistaken for a channel that is empty.
    ///     </para>
    /// </remarks>
    internal static byte[] Prepared(ReadOnlySpan<byte> level, TextureChannels channels) {
        var pixels = level.ToArray();

        if (channels == TextureChannels.All) {
            return pixels;
        }

        var only = Single(channels);

        for (var texel = 0; texel + 3 < pixels.Length; texel += 4) {
            if (only >= 0) {
                var value = pixels[texel + only];

                pixels[texel] = value;
                pixels[texel + 1] = value;
                pixels[texel + 2] = value;
                pixels[texel + 3] = byte.MaxValue;

                continue;
            }

            if ((channels & TextureChannels.Red) == 0) {
                pixels[texel] = 0;
            }

            if ((channels & TextureChannels.Green) == 0) {
                pixels[texel + 1] = 0;
            }

            if ((channels & TextureChannels.Blue) == 0) {
                pixels[texel + 2] = 0;
            }

            if ((channels & TextureChannels.Alpha) == 0) {
                pixels[texel + 3] = byte.MaxValue;
            }
        }

        return pixels;
    }

    /// <summary>Which byte of a texel a lone channel is, or a negative number for a set of them.</summary>
    static int Single(TextureChannels channels) => channels switch {
        TextureChannels.Red => 0,
        TextureChannels.Green => 1,
        TextureChannels.Blue => 2,
        TextureChannels.Alpha => 3,
        _ => -1
    };

    /// <summary>One open texture editor, and what it was last shown.</summary>
    sealed class Followed(TextureImportView view, TextureImportDocument document) {
        public TextureImportView View { get; } = view;

        public TextureImportDocument Document { get; } = document;

        /// <summary>The decoded source the chain below was built from, compared by reference.</summary>
        public TextureData? Source { get; set; }

        /// <summary>That source with every level filled in.</summary>
        public TextureData? Chain { get; set; }

        /// <summary>What the preview is showing, or null.</summary>
        public IEditorImage? Image { get; set; }

        /// <summary>What the sprite pane is showing, or null.</summary>
        public IEditorImage? Sliced { get; set; }

        /// <summary>The sheet that image was made from, compared by reference.</summary>
        public TextureData? Sheet { get; set; }

        /// <summary>Which level that is, or a negative number for "nothing yet".</summary>
        public int Level { get; set; } = -1;

        /// <summary>Which channels it was prepared for.</summary>
        public TextureChannels Channels { get; set; }
    }
}
