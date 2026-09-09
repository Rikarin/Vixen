// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Materials;
using Vixen.Rendering.Materials;

namespace Vixen.Cli;

/// <summary>`vixen texture bake --parallax` — the ask a first bake had no way to make.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>'s remainder, and it is
///         an authoring route rather than a rendering change.</b> A bake writes a height map for
///         whatever wants it and composes no <see cref="ParallaxOcclusionFeature" /> for it, because
///         composing one would put a per-pixel march on <em>every</em> material any graph ever
///         emitted a height output from. What it does instead is preserve and re-seat a feature the
///         material already carries — which by construction cannot help a material that does not
///         exist yet. So the route was two steps, bake and add the feature and bake again, and since
///         the last batch the bake at least says so in a warning naming the tag to paste. A route a
///         tool describes is not a route the tool offers; this is the offer.
///     </para>
///     <para>
///         ⚠ <b>Nothing here decides where the feature goes, and that is the whole of why it is
///         safe.</b> <see cref="ParallaxOcclusionFeature" /> declares
///         <see cref="MaterialFeatureStage.Coordinate" /> and <c>MaterialCompiler</c> refuses a
///         coordinate feature listed behind one that samples — so a naive "append it" writes a
///         <c>.vxmat</c> the bake itself produced and the importer then rejects. This asks
///         <see cref="MaterialBake.Material" /> to compose the material a second time with the
///         feature present, and <em>that</em> is what seats it at index 0, re-points its
///         <c>HeightMap</c> at the name <c>WorldRenderer.Paired</c> keys on, and drops it again if
///         this bake wrote no height map. One rule, in one place, with a flag in front of it.
///     </para>
///     <para>
///         ⚠ <b>Applied after the write rather than seeded before it.</b> A seed <c>.vxmat</c> would
///         have to be written at the path the bake is going to choose, and that path is
///         <c>ProjectMaterialBaker</c>'s: a name already owned by a different source becomes
///         <c>Name_2</c>, so a seed guessing the name either lands beside the set or on somebody
///         else's material. Re-composing what the bake actually wrote needs no guess.
///     </para>
///     <para>
///         ⚠ <b>A material that did not ask still does not get one.</b> The flag is the ask, it is
///         off by default, and a bake without it is byte-identical to the bake before this file
///         existed.
///     </para>
/// </remarks>
static class BakeParallax {
    /// <summary>The YAML tag an author would paste, and what the bake's own warning names.</summary>
    /// <remarks>
    ///     ⚠ <b>Matched against <see cref="MaterialBakeSet.Warnings" /> because that warning becomes
    ///     false here, and there is no shared constant to match on.</b>
    ///     <c>ProjectMaterialBaker.Overpaint</c> exists for exactly this reason one refusal over — a
    ///     caller has to tell one of that type's messages from the others — and the unfed-height
    ///     warning has no such anchor yet, so this matches on the actionable half of its text.
    ///     <c>TextureCommandTests.The_bakers_unfed_height_warning_still_names_the_tag</c> is
    ///     what turns a reword into a red test rather than into a sentence telling an artist to do
    ///     something this verb already did.
    /// </remarks>
    public const string Tag = "!ParallaxOcclusion";

    /// <summary>Puts a parallax feature on the material a bake just wrote.</summary>
    /// <param name="set">What the bake put in the project.</param>
    /// <param name="error">Where to say why it could not.</param>
    /// <returns>The bake's warnings that are still true of the material now on disk.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> Requested(MaterialBakeSet set, TextWriter error) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(error);

        var file = set.Files.FirstOrDefault(written =>
            written.EndsWith(MaterialImporter.Extension, StringComparison.Ordinal)
        );

        if (file is null) {
            return set.Warnings;
        }

        if (!set.Maps.ContainsKey(MaterialMapTarget.Height)) {
            // ⚠ Said rather than passed over, and it is the one thing an author can act on here: a
            // march with no height field is not a smaller effect, it is a feature whose map index
            // stays at nought and which marches the bindless table's fallback checker. What is
            // missing is upstream — an Output node naming the height usage.
            error.WriteLine(
                "--parallax was given and this bake wrote no height map, so no parallax was turned on. A "
                + "parallax march reads a height field, and an Output node naming the height usage is what "
                + "writes one."
            );

            return set.Warnings;
        }

        MaterialContent written;

        try {
            written = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(file));
        } catch (Exception failure)
            when (failure is YamlParseException or YamlBindingException or NotSupportedException or IOException) {
            error.WriteLine($"--parallax could not re-read the material this bake wrote: {failure.Message}");

            return set.Warnings;
        }

        if (Array.Exists(written.Features, feature => feature is ParallaxOcclusionFeature)) {
            // A re-bake of a material whose author already asked. The bake kept and fed it, so there
            // is nothing to add and no warning to drop.
            return set.Warnings;
        }

        var asked = written with { Features = [new ParallaxOcclusionFeature(), .. written.Features] };

        File.WriteAllText(file, YamlSerializer.ToYaml(MaterialBake.Material(set.Maps, asked)));

        return [.. set.Warnings.Where(warning => !warning.Contains(Tag, StringComparison.Ordinal))];
    }
}
