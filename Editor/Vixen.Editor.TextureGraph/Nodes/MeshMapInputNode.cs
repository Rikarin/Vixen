// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>What a baked mesh map measures, as a graph asks for one.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D12's ten, spelled the way the bake spells them.</b> These are the same
///         strings <c>Vixen.Editor.Assets.MeshMaps.MeshMapNaming.Suffix</c> writes into a map's
///         sidecar under <c>meshMap.usage</c>, and the whole binding is that the two agree.
///     </para>
///     <para>
///         ⚠ <b>They are written down twice, and that is forced by the assembly graph rather than
///         chosen.</b> The vocabulary's home is <c>Vixen.Editor.Assets</c>, which references Assimp,
///         <c>Vixen.Engine</c> and every importer in the tree; this assembly's csproj spends four
///         paragraphs keeping its closure to twenty-nine projects, and referencing the asset
///         database from a compiler that runs on every edit would undo all of it. The reverse edge —
///         <c>Vixen.Editor.Assets</c> → here — is the one that is allowed and is how the resolver
///         reads what this node emits. So the duplication is one direction of a cycle that must not
///         exist, and what keeps it honest is <c>MeshMapNodeVocabularyTests</c>: it walks
///         <c>MeshMapNaming.Every</c>, drives this node once per usage through the real compiler, and
///         asserts the reference that comes out parses back to the usage it went in as. Renaming a
///         suffix on either side is red there, which is the failure
///         <c>MeshMapNaming</c>'s own remarks say silently unbinds every shipped generator.
///     </para>
/// </remarks>
static class TextureMeshMaps {
    /// <summary>The scheme a mesh-map reference is written under.</summary>
    /// <remarks>
    ///     ⚠ <b>A scheme rather than a path, because a mesh map is the one external image a graph
    ///     asks for <em>by what it is</em> rather than by which file it is.</b>
    ///     <c>TextureGraphExternal.Asset</c> carries a project-relative path for a
    ///     <c>Source/Bitmap</c> — <c>Assets/Textures/rust.png</c> — and a host resolves it by
    ///     opening that file. A generator names no file at all, so what crosses is
    ///     <c>meshmap:curvature</c>, which no project path can collide with and which a host that
    ///     has not been told which mesh it is baking can refuse with a sentence instead of a
    ///     missing-file exception.
    /// </remarks>
    public const string Scheme = "meshmap:";

    /// <summary>The nine a <c>Mesh Map</c> node may name.</summary>
    public static IReadOnlyList<string> Known { get; } = [
        "normal",
        "height",
        "ao",
        "bent",
        "curvature",
        "thickness",
        "position",
        "world",
        "id"
    ];

    /// <summary>The four that are one measurement per texel, and are therefore grey.</summary>
    /// <remarks>
    ///     ⚠ <b>The split is the map's, not a taste.</b> A bent normal or a world normal read as grey
    ///     would be thrown away at the first thing that touched it — <c>NoiseNode</c>'s Worley
    ///     argument exactly — and a curvature read as colour arrives at a port that <em>measures</em>
    ///     as a type error naming that port. Both halves are silent in different directions, so the
    ///     classification is here and the node has no setting for it.
    /// </remarks>
    public static IReadOnlyList<string> Grey { get; } = ["height", "ao", "curvature", "thickness"];

    /// <summary>The canonical spelling of a usage, or empty when it is not one of the nine.</summary>
    /// <param name="usage">What the author typed.</param>
    /// <returns>The spelling <see cref="Known" /> holds, or an empty string.</returns>
    public static string Canonical(string usage) {
        foreach (var known in Known) {
            if (string.Equals(known, usage, StringComparison.OrdinalIgnoreCase)) {
                return known;
            }
        }

        return "";
    }

    /// <summary>What a graph writes to ask for one map, whichever mesh the bake turns out to be for.</summary>
    /// <param name="usage">A canonical usage — one of <see cref="Known" />.</param>
    /// <returns>The reference a host resolves.</returns>
    public static string Reference(string usage) => Scheme + usage;
}

/// <summary>
///     A baked mesh map, bound by what it measures rather than by which file it is.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § 4.8's Mesh Map Input, and the reason one generator compound works on every
///         mesh.</b> A Curvature Edge Wear graph asks for <c>curvature</c> and is handed whichever
///         file this project's bake produced for the mesh being textured. That is Painter's automatic
///         connection, and it is the difference between a library of generators and a library of
///         graphs each hard-wired to one barrel.
///     </para>
///     <para>
///         ⚠ <b>The design question this node answers is "how does a graph name the mesh it is for",
///         and the answer is that it does not and must not.</b> A texture graph carries a name, nodes,
///         edges, an interface and § D8's settings — there is nowhere to put a mesh, and putting one
///         there would be the bug: a generator that named a mesh would be a generator that works on
///         one. So the mesh enters where <c>BakeLevelOffset</c> enters, at the <em>evaluation</em>,
///         and the consequence worth stating plainly is that **the compiled plan is the same plan for
///         every mesh** — two bakes of one generator differ only in their external table. That is why
///         "one compound, two meshes, no rewiring" is not a feature this node has to implement; it is
///         the only thing this shape can do, and the test that proves it has to check the two bakes
///         bind <em>different</em> files rather than merely that both compile.
///     </para>
///     <para>
///         ⚠ <b>What is on the other side of the reference is a host, and until one walks
///         <c>TextureGraphCompiler.Externals</c> a graph containing this node compiles and does not
///         bake</b> — which is <c>BitmapNode</c>'s state and the same sentence.
///         <c>Vixen.Editor.Assets.MeshMaps.MeshMapBinding</c> is the resolver
///         (<a href="https://github.com/Rikarin/Vixen/issues/702">#702</a>); the panel and the CLI
///         verb that would call it are
///         <a href="https://github.com/Rikarin/Vixen/issues/573">#573</a>'s.
///     </para>
///     <para>
///         ⚠ <b>A quantized map arrives quantized, and the node cannot undo it.</b> Displacement and
///         curvature are stored as <c>0.5 + 0.5·v/range</c> in eight bits with the range in the
///         sidecar — so <c>curvature</c> here is a map whose <em>0.5 is zero curvature</em>, edges
///         above it and creases below. Decoding would mean reading a sidecar, which is an asset
///         database, which a compilation must not touch; and a node that silently rescaled by a
///         guessed range would be a generator whose threshold means something different on every
///         mesh. So a <c>Colour/Levels</c> picking the half it wants is what a generator does, and
///         every shipped one does exactly that.
///     </para>
/// </remarks>
[Node(
    "Source/Mesh Map",
    Preview = true,
    Summary = "A baked mesh map, bound by what it measures — the same graph works on every mesh."
)]
sealed partial class MeshMapInputNode : TextureNode {
    /// <summary>Which map: one of <c>TextureMeshMaps.Known</c>.</summary>
    [Setting(
        Name = "Map",
        Summary = "What the map measures: normal, height, ao, bent, curvature, thickness, position, world or id."
    )]
    public string Map = "curvature";

    /// <summary>The map, resampled into the graph's resolution.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var usage = TextureMeshMaps.Canonical(emitter.Text(nameof(Map)).Trim());

        if (usage.Length == 0) {
            // ⚠ Refused rather than defaulted, for `TextureSettings.Enum`'s reason and one more of
            // its own: a misspelt usage that fell back to `normal` would be a generator reading the
            // wrong measurement, and every mesh map is a plausible-looking picture, so nothing
            // downstream and nobody looking at the result would ever say so.
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                $"'{nameof(Map)}' is '{emitter.Text(nameof(Map)).Trim()}', which is not one of "
                + $"{string.Join(", ", TextureMeshMaps.Known)}. A mesh map is bound by what it measures, so a "
                + "name nothing bakes binds nothing.",
                nameof(Map)
            );

            return;
        }

        var channels = TextureMeshMaps.Grey.Contains(usage, StringComparer.Ordinal)
            ? TextureChannels.Grey
            : TextureChannels.Colour;

        // Rgba8 because a baked map is a PNG — `MeshMapNaming.Extension` says so and says why — and
        // because a plan may *read* a format no kernel can write.
        var source = emitter.External(TextureFormat.Rgba8, TextureChannels.Colour, TextureMeshMaps.Reference(usage));
        var target = emitter.Write("Out", channels);

        emitter.Dispatch(
            TextureSources.Bitmap(
                target,
                source,
                // ⚠ Never. Every one of the nine is a measurement rather than a picture — a direction,
                // a distance, an occluded fraction, an index — and running the sRGB curve over one
                // would bend it into a plausible wrong number. `Source/Bitmap` has the setting
                // because an imported asset's space is a fact about the asset; a mesh map's is a fact
                // about the bake, and the bake writes linear.
                srgb: false,
                // ⚠ Point for the id map, and this is § D12's own sentence made mechanical: an id is
                // "nearest-sampled and never filtered", because interpolating two material indices
                // produces a third that belongs to no material — the same hairline the gutter
                // dilation is excluded from, arriving instead from the resample into the graph's
                // resolution. Everything else is a continuous measurement and is interpolated.
                bilinear: !string.Equals(usage, "id", StringComparison.Ordinal)
            )
        );
    }
}
