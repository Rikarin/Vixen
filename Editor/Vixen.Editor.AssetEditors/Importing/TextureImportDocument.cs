// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Rendering.Sprites;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>The editable mirror of <see cref="TextureImportSettings" />.</summary>
/// <remarks>
///     Member for member, and <c>ImportSettingsMirrorTests</c> is what keeps it that way. The
///     documentation is deliberately not copied across: what each setting <i>means</i> lives on the
///     record, and two prose descriptions of one knob is how they come to disagree. What is here is
///     the one-line tooltip a row shows.
/// </remarks>
[DataContract("TextureImportEdits")]
public sealed class TextureImportEdits {
    /// <summary>What the bytes mean.</summary>
    [Inspector]
    [Tooltip("A normal map, an albedo map and a packed mask are all RGBA. Nothing in the file says which.")]
    public TextureContent Content { get; set; } = TextureContent.Colour;

    /// <summary>Which compressed format to ship in.</summary>
    [Inspector]
    [Tooltip("Automatic picks from the content. See TextureImporter for what it picks and why.")]
    public TextureCompression Compression { get; set; } = TextureCompression.Automatic;

    /// <summary>Whether to build a mip chain.</summary>
    [Inspector]
    [Tooltip("Off costs bandwidth and aliases; on costs a third more memory.")]
    public bool GenerateMips { get; set; } = true;

    /// <summary>Whether the alpha channel is transparency rather than packed data.</summary>
    [Inspector]
    [Tooltip("Only consulted for colour. Weights the mip filter, which stops a cut-out's edge bleeding.")]
    public bool AlphaIsTransparency { get; set; } = true;

    /// <summary>The largest the texture may ship at, or zero for no limit.</summary>
    [Inspector]
    [Tooltip("Halving, not resampling: a 2048 with a limit of 1000 ships at 512.")]
    public int MaxSize { get; set; }

    /// <summary>Whether this texture produces sprites, and one or many.</summary>
    [Inspector]
    [Tooltip("Multiple produces a sub-asset per rect. Slice them in the sprite editor.")]
    public SpriteMode SpriteMode { get; set; } = SpriteMode.None;

    /// <summary>How many texels make one world unit.</summary>
    [Inspector]
    [Tooltip("A hundred is the usual. A background painted at a quarter of the resolution wants 25.")]
    public float PixelsPerUnit { get; set; } = Sprite.DefaultPixelsPerUnit;

    /// <summary>Where each sprite is, in texels of the source image.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <c>[Inspector]</c>.</b> Every other member here is a knob, and a
    ///     generic drawer for an array of eleven-field records would be a hundred rows of numbers
    ///     describing rectangles somebody wants to see on the picture. <see cref="SpriteSheetView" />
    ///     is the drawer for this one, and <see cref="TextureImportDocument" /> is what it edits it
    ///     through so the edits reach the same undo stack the knobs do.
    /// </remarks>
    public SpriteRect[] Sprites { get; set; } = [];
}

/// <summary>A texture's import settings, open for editing.</summary>
/// <remarks>
///     What doc 11 asks a texture editor for is import settings, a channel viewer, a mip inspector
///     and the platform-override matrix. The first and the last are this document's;
///     <see cref="TextureImportView" /> is the other two, and it is a view over the file's own pixels
///     rather than over anything this holds — see there for why the preview decodes the source rather
///     than the artefact.
/// </remarks>
public sealed class TextureImportDocument : ImportSettingsDocument {
    /// <summary>The settings, typed.</summary>
    public TextureImportEdits Texture => (TextureImportEdits) Settings;

    /// <summary>The sprites cut out of this texture, in the order they were sliced.</summary>
    public IReadOnlyList<SpriteRect> Sprites => Texture.Sprites;

    /// <summary>Raised whenever the set of sprites or any one of them changes.</summary>
    /// <remarks>
    ///     One signal for all of it, because the sprite panel redraws the overlay either way: a rect
    ///     that moved and a rect that appeared are the same amount of work, and two events would only
    ///     let a view be subtly wrong about one of them.
    /// </remarks>
    public event Action<TextureImportDocument>? SpritesChanged;

    /// <inheritdoc />
    protected override Type SettingsType => typeof(TextureImportEdits);

    /// <inheritdoc />
    protected override string ImporterTag => "TextureImporter";

    /// <summary>Opens a texture's import settings.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public TextureImportDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, path) {
    }

    /// <summary>Replaces every sprite, undoably. This is what a slice does.</summary>
    /// <param name="sprites">The new set, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sprites" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>One command for the whole set, and that is the point of slicing being a single
    ///     action.</b> A slice that produced sixty rects as sixty commands would take sixty undos to
    ///     take back — and the author's mental model is "I sliced it, that was wrong, undo".
    /// </remarks>
    public void SetSprites(IReadOnlyList<SpriteRect> sprites) {
        ArgumentNullException.ThrowIfNull(sprites);

        Replace("Slice sprites", [.. sprites]);
    }

    /// <summary>Adds one sprite, undoably.</summary>
    /// <param name="sprite">The rect.</param>
    /// <returns>Where it landed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sprite" /> is null.</exception>
    public int AddSprite(SpriteRect sprite) {
        ArgumentNullException.ThrowIfNull(sprite);

        Replace("Add sprite", [.. Texture.Sprites, sprite]);

        return Texture.Sprites.Length - 1;
    }

    /// <summary>Removes one sprite, undoably.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>Whether there was one there.</returns>
    public bool RemoveSprite(int index) {
        if ((uint) index >= (uint) Texture.Sprites.Length) {
            return false;
        }

        var updated = new List<SpriteRect>(Texture.Sprites);
        updated.RemoveAt(index);

        Replace("Remove sprite", [.. updated]);

        return true;
    }

    /// <summary>Replaces one sprite, undoably.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="sprite">What it becomes.</param>
    /// <returns>Whether there was one there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sprite" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Left unsealed and merging on the index, so dragging a rect is one undo step rather
    ///     than sixty.</b> That is the whole reason this is a command type of its own rather than a
    ///     <c>DelegateCommand</c>: two closures cannot tell whether they are the same edit twice, and
    ///     a drag across a canvas is three hundred edits of one rect. Whoever ends the drag calls
    ///     <c>Stack.Seal</c>, exactly as the shell does for a slider.
    /// </remarks>
    public bool UpdateSprite(int index, SpriteRect sprite) {
        ArgumentNullException.ThrowIfNull(sprite);

        if ((uint) index >= (uint) Texture.Sprites.Length) {
            return false;
        }

        var updated = Texture.Sprites.ToArray();
        updated[index] = sprite;

        Stack.Execute(new SpriteEditCommand(this, "Edit sprite", updated, index));

        return true;
    }

    /// <summary>Puts a whole new set of sprites in place, undoably and as one step.</summary>
    void Replace(string name, SpriteRect[] sprites) {
        Stack.Execute(new SpriteEditCommand(this, name, sprites, merge: -1));
        Stack.Seal();
    }

    void Apply(SpriteRect[] sprites) {
        Texture.Sprites = sprites;
        SpritesChanged?.Invoke(this);
    }

    /// <summary>One change to a texture's sprite list.</summary>
    /// <remarks>
    ///     The whole array either way, rather than a patch. A sprite list is tens of records of eleven
    ///     numbers, so a copy is nothing next to the picture it describes — and a command that held a
    ///     patch would need one shape per operation and would have to be right about all of them.
    /// </remarks>
    sealed class SpriteEditCommand : IEditorCommand {
        readonly TextureImportDocument document;
        readonly SpriteRect[] previous;
        readonly SpriteRect[] next;
        readonly int merge;

        public string Name { get; }

        public SpriteEditCommand(TextureImportDocument document, string name, SpriteRect[] next, int merge) {
            this.document = document;
            this.next = next;
            this.merge = merge;

            Name = name;
            previous = document.Texture.Sprites;
        }

        public void Do(EditorContext context) => document.Apply(next);

        public void Undo(EditorContext context) => document.Apply(previous);

        /// <inheritdoc />
        /// <remarks>
        ///     ⚠ <b>Same document and same rect, and the merged command undoes to what the <i>older</i>
        ///     one would have.</b> That is the contract's own wording and it is the part that is easy
        ///     to get backwards: undoing a finished drag has to go back to where the rect was before
        ///     the drag started, not to where it was one mouse-move ago.
        /// </remarks>
        public bool TryMergeWith(IEditorCommand earlier, [NotNullWhen(true)] out IEditorCommand? merged) {
            merged = null;

            if (merge < 0
                || earlier is not SpriteEditCommand before
                || !ReferenceEquals(before.document, document)
                || before.merge != merge) {
                return false;
            }

            merged = new SpriteEditCommand(document, Name, next, merge, before.previous);

            return true;
        }

        SpriteEditCommand(
            TextureImportDocument document,
            string name,
            SpriteRect[] next,
            int merge,
            SpriteRect[] previous
        ) {
            this.document = document;
            this.next = next;
            this.merge = merge;
            this.previous = previous;

            Name = name;
        }
    }
}
