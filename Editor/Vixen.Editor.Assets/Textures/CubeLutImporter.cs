// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Textures;

/// <summary>What a grading table needs said about it, which is almost nothing.</summary>
/// <remarks>
///     A settings type with no settings, because the file already says its own size and domain and
///     everything else about a LUT is fixed: no mips — a mip of a colour transform is a different
///     colour transform — no compression, and no sRGB flag, because the values are a mapping rather
///     than a picture. It exists because <c>AssetImporter&lt;T&gt;</c> takes one, and because the day
///     somebody wants a contribution slider it goes here.
/// </remarks>
[DataContract("CubeLutImporter")]
public sealed record CubeLutImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Turns a colourist's <c>.cube</c> into the 3D texture the tonemapper samples.</summary>
/// <remarks>
///     <para>
///         <b>The missing half of a grading pipeline that was otherwise complete.</b>
///         <c>Tonemap.rvn</c> has sampled a <c>Texture3D</c> with the correct half-texel inset since
///         it was written, and no project could author one — the importer list was models, textures,
///         audio, video, scenes, navmeshes and palettes. A host had to build the texture in C# by
///         hand, so nobody did.
///     </para>
///     <para>
///         ⚠ <b>No mip chain.</b> Every other texture importer builds one and this must not: a
///         lookup table is sampled at exactly one level, and a half-size mip of a colour transform is
///         a <em>different</em> colour transform — averaging two entries of a grade is not the grade
///         halfway between them. A sampler that fell to a lower level would silently apply it.
///     </para>
///     <para>
///         ⚠ <b>No compression either.</b> Block compression works because neighbouring texels in a
///         picture are similar and the eye forgives the error. Neither is true here: neighbouring
///         entries are a gradient the grade exists to control, and the error lands on every pixel of
///         the frame that indexes through it.
///     </para>
/// </remarks>
[Importer(".cube")]
public sealed class CubeLutImporter : AssetImporter<CubeLutImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        CubeLutImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var table = CubeLut.Parse(text);

        context.Report(
            ImportSeverity.Information,
            $"A {table.Width}³ grading table, in {table.Format}. It ships uncompressed and without mips: a mip of "
            + "a colour transform is a different colour transform."
        );

        // Written under "Texture", the same type name every other image ships as, because what comes
        // out the other end is a texture and the tonemapper binds it as one. A type of its own would
        // need a loader of its own to say the same thing.
        context.Write(SubAssetId.Main, "Texture", Ktx2.Write(table));

        return context.Finish();
    }
}
