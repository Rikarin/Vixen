// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>
///     Between a compiled graph's external images and the textures an evaluation reads them from.
/// </summary>
/// <remarks>
///     <para>
///         <b>The step that would otherwise be written out at every bake, and there is going to be
///         more than one.</b> A compilation says which images the caller supplies and what fills them
///         — <see cref="TextureGraphCompiler.Externals" /> — and <see cref="TextureUploads" /> puts
///         bytes on a device; joining the two is a four-line loop, and a four-line loop copied into a
///         CLI verb, a panel and a content build is three places to forget an entry.
///     </para>
///     <para>
///         ⚠ <b>What it deliberately cannot do is resolve an asset.</b> An entry naming one is handed
///         back rather than skipped, because skipping it would produce a plan that is missing exactly
///         one texture and an exception at <c>Evaluate</c> about an image index. A host with an asset
///         database reads what came back, loads each picture and uploads it — and a host without one
///         can see, before it starts, that this graph is not one it can bake.
///     </para>
/// </remarks>
public static class TextureGraphExternals {
    /// <summary>Uploads every external image whose bytes the compilation carries.</summary>
    /// <param name="uploads">Where the textures are made, and what owns them.</param>
    /// <param name="plan">The plan the images belong to.</param>
    /// <param name="externals">What the compiler said fills each of them.</param>
    /// <returns>The entries this could not fill: the ones naming an asset a host has to resolve.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">
    ///     An entry names an image the plan does not mark external, or a byte count the size and
    ///     format do not imply — both of which are compiler bugs rather than an author's mistake.
    /// </exception>
    public static ImmutableArray<TextureGraphExternal> Upload(
        TextureUploads uploads,
        TexturePlan plan,
        ImmutableArray<TextureGraphExternal> externals
    ) {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(plan);

        var owed = ImmutableArray.CreateBuilder<TextureGraphExternal>();

        foreach (var external in externals) {
            if (external.Asset.Length > 0) {
                owed.Add(external);

                continue;
            }

            uploads.Add(plan, external.Image, external.Width, external.Height, external.Texels.AsSpan());
        }

        return owed.ToImmutable();
    }
}
