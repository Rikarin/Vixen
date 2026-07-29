// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

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
}
