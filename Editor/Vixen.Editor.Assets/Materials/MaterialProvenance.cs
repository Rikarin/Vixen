// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vixen.Core;

namespace Vixen.Editor.Assets.Materials;

/// <summary>What a bake was, said in enough detail to run it again or to distrust it.</summary>
/// <remarks>
///     ⚠ <b><see cref="Adapter" /> is recorded and never compared</b>, which doc 48 § D4 states as a
///     decision rather than as a limitation: a re-bake on the same machine is byte-identical and that
///     <i>is</i> asserted, a re-bake on a different card is not, and pretending otherwise would make
///     the first artist with a different GPU a bug report.
/// </remarks>
public sealed record MaterialBakeRecord {
    /// <summary>Where the maps came from, as a path a person can follow.</summary>
    /// <remarks>
    ///     The graph asset's path when a graph produced them, and the input folder when the command
    ///     line did. Written relative to the project where it is inside one, because an absolute path
    ///     off somebody's machine is a fact about that machine.
    /// </remarks>
    public required string Source { get; init; }

    /// <summary>Which asset that was, where it is one.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the set's identity and the file name is not.</b> Two graphs both called
    ///     <c>Material</c> produce the same seven file names, and a writer that keyed on those
    ///     overwrote the first one's maps with the second's and handed back the first one's GUIDs —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/681">#681</a>, on the mesh-map baker, for
    ///     exactly this reason. Empty means "nobody said", which the next bake may adopt.
    /// </remarks>
    public AssetId SourceAsset { get; init; }

    /// <summary>The graph's exposed parameters, as they stood.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Which adapter ran the bake. Recorded, never asserted.</summary>
    public string Adapter { get; init; } = string.Empty;
}

/// <summary>The <c>texturing:</c> block in a baked material's sidecar, and the check it buys.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § D4, which is doc 40 § D7's provenance block reused in shape.</b> Source,
///         outputs, resolution, parameters, adapter, a digest and a timestamp — everything needed to
///         say what produced these bytes, on the one file that is not itself an output.
///     </para>
///     <para>
///         ⚠ <b>Flat dotted keys rather than the nested mapping § D4 sketches.</b> A sidecar's
///         extensions are a <c>Dictionary&lt;string, string&gt;</c>, which is what
///         <c>meshMap.usage</c> and every other extension in the tree already writes into; a nested
///         block would need a second shape in the meta format for one consumer. What the sketch is
///         about is which facts are recorded, and all of them are.
///     </para>
///     <para>
///         ⚠ <b>The digest covers the maps and not the <c>.vxmat</c>.</b> The point of it is that a
///         file somebody painted over is detected — and a material an artist edited in the inspector
///         is not that. Including the material would make raising its emissive intensity look
///         identical to painting on a normal map, and the bake would refuse to run for a reason
///         nobody could act on.
///     </para>
/// </remarks>
public static class MaterialProvenance {
    /// <summary>The key naming where the maps came from.</summary>
    public const string SourceKey = "texturing.source";

    /// <summary>The key naming the asset that produced them, where there is one.</summary>
    public const string SourceAssetKey = "texturing.sourceAsset";

    /// <summary>The key listing which files were written.</summary>
    public const string OutputsKey = "texturing.outputs";

    /// <summary>The key naming what size they were baked at.</summary>
    public const string ResolutionKey = "texturing.resolution";

    /// <summary>The prefix each exposed parameter is written under.</summary>
    public const string ParameterPrefix = "texturing.parameter.";

    /// <summary>The key naming the adapter that ran the bake.</summary>
    public const string AdapterKey = "texturing.adapter";

    /// <summary>The prefix each output's own digest is written under.</summary>
    public const string DigestPrefix = "texturing.digest.";

    /// <summary>The key holding the digest over all of them.</summary>
    public const string WrittenDigestKey = "texturing.writtenDigest";

    /// <summary>The key holding when the bake ran.</summary>
    public const string AtKey = "texturing.at";

    /// <summary>The key on a map's own sidecar saying which of the seven it is.</summary>
    public const string MapKey = "texturing.map";

    /// <summary>And which material's set it belongs to.</summary>
    public const string MaterialKey = "texturing.material";

    /// <summary>What separates the entries of <see cref="OutputsKey" />.</summary>
    public const string Separator = ", ";

    /// <summary>The digest of some bytes, as the sidecar writes it.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns><c>sha256:</c> and sixty-four lower-case hexadecimal digits.</returns>
    public static string Digest(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The digest over a whole set, which is the digests of its parts.</summary>
    /// <param name="images">The files, in the order they were written.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Over the per-file digests and their names, not over the concatenated bytes.</b> Two
    ///     sets whose files are the same bytes in a different arrangement — a bake that stopped
    ///     writing an opacity map whose pixels the mask map now carries — hash the same under
    ///     concatenation and differently under this.
    /// </remarks>
    public static string Written(IReadOnlyList<MaterialMapImage> images) {
        ArgumentNullException.ThrowIfNull(images);

        var text = new StringBuilder();

        foreach (var image in images) {
            text.Append(MaterialMapNaming.Suffix(image.Target))
                .Append(' ')
                .Append(Digest(image.Bytes))
                .Append('\n');
        }

        return Digest(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>The block a bake writes into the material's sidecar.</summary>
    /// <param name="record">What the bake was.</param>
    /// <param name="images">The files it wrote.</param>
    /// <param name="at">When it ran.</param>
    /// <returns>The extension entries, ready to be merged into a sidecar.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="images" /> is empty.</exception>
    public static Dictionary<string, string> Describe(
        MaterialBakeRecord record,
        IReadOnlyList<MaterialMapImage> images,
        DateTimeOffset at
    ) {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(images);

        if (images.Count == 0) {
            throw new ArgumentException("A bake that wrote no files has no provenance.", nameof(images));
        }

        var written = new Dictionary<string, string>(StringComparer.Ordinal) {
            [SourceKey] = record.Source,
            [OutputsKey] = string.Join(Separator, images.Select(image => MaterialMapNaming.Suffix(image.Target))),
            [ResolutionKey] = Resolution(images[0].Width, images[0].Height),
            [WrittenDigestKey] = Written(images),

            // Round-trippable and in UTC, because the two readers of a timestamp are a person
            // wondering when this ran and a diff — and a local time makes the second one lie every
            // time somebody in another office re-bakes.
            [AtKey] = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };

        if (!record.SourceAsset.IsEmpty) {
            written[SourceAssetKey] = record.SourceAsset.ToString();
        }

        // ⚠ Written even when it is empty is not the rule here: an adapter nobody recorded is absent
        // rather than blank, so that "this bake did not say" and "this bake ran on a card called
        // nothing" are not the same sidecar.
        if (record.Adapter.Length > 0) {
            written[AdapterKey] = record.Adapter;
        }

        foreach (var (name, value) in record.Parameters) {
            written[ParameterPrefix + name] = value;
        }

        foreach (var image in images) {
            written[DigestPrefix + MaterialMapNaming.Suffix(image.Target)] = Digest(image.Bytes);
        }

        return written;
    }

    /// <summary>Which of a set's outputs no longer match what the bake wrote.</summary>
    /// <param name="recorded">The material sidecar's extensions, as they stand.</param>
    /// <param name="present">The bytes each output has on disk now.</param>
    /// <returns>The outputs that differ, in <see cref="MaterialMapNaming.EveryTarget" /> order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is what makes "a file an artist has painted over is never silently
    ///         regenerated" a check rather than a hope.</b> § D4 is explicit that the most common
    ///         reason a baked file's bytes have changed is that somebody painted on it, and a bake
    ///         that overwrote it would be destroying the work in the one moment it looked like
    ///         success.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An output with no recorded digest is not painted over.</b> That is what a set
    ///         written before this block existed looks like, and what a file the last bake did not
    ///         write looks like; calling either one painted would make the check fire on projects
    ///         where nobody has painted anything, which is how a guard gets turned off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And an output that has been deleted is not painted over either.</b> A missing
    ///         file is a file to rewrite; only bytes that are there and disagree are a person's work.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<MaterialMapTarget> Painted(
        IReadOnlyDictionary<string, string> recorded,
        IReadOnlyDictionary<MaterialMapTarget, byte[]> present
    ) {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(present);

        var painted = new List<MaterialMapTarget>();

        foreach (var target in MaterialMapNaming.EveryTarget) {
            if (!recorded.TryGetValue(DigestPrefix + MaterialMapNaming.Suffix(target), out var digest)) {
                continue;
            }

            if (present.TryGetValue(target, out var bytes)
                && !string.Equals(Digest(bytes), digest, StringComparison.Ordinal)) {
                painted.Add(target);
            }
        }

        return painted;
    }

    /// <summary>What size a set was baked at, said the way § D4's example says it.</summary>
    static string Resolution(int width, int height) =>
        width == height
            ? width.ToString(CultureInfo.InvariantCulture)
            : width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
}
