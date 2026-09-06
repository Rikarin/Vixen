// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>A texture set's stack, compiled.</summary>
/// <param name="Plan">The plan, or <see langword="null" /> when something refused.</param>
/// <param name="Graph">The graph it was compiled from — what <c>Explode</c> writes out.</param>
/// <param name="Problems">What building the graph had to say, about layers.</param>
/// <param name="Diagnostics">What compiling the graph had to say, about nodes.</param>
/// <param name="Outputs">Which image is which map, by usage.</param>
/// <param name="Externals">The imported images this plan needs supplied, per bitmap node.</param>
/// <remarks>
///     ⚠ <b>Two problem lists and not one, because they are two different readers' problems.</b> A
///     <see cref="LayerStackProblem" /> names a layer an artist can select in a layers panel; a
///     <c>NodeDiagnostic</c> names a node in the exploded graph, which for a stack that has not been
///     exploded is a node nobody can see. Flattening them into one list would mean either losing the
///     layer identity or inventing one for every node the builder emitted.
/// </remarks>
sealed record LayerStackCompilation(
    TexturePlan? Plan,
    NodeGraphModel Graph,
    ImmutableArray<LayerStackProblem> Problems,
    ImmutableArray<NodeDiagnostic> Diagnostics,
    ImmutableArray<TextureGraphOutput> Outputs,
    ImmutableArray<TextureGraphExternal> Externals
) {
    /// <summary>Which layer emitted each node — <see cref="LayerStackBuild.Layers" />, carried on.</summary>
    /// <remarks>
    ///     ⚠ <b>The bridge between the two lists above, and the reason they can stay two</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a>. A reader that has this can
    ///     say which layer a node's diagnostic is about without the diagnostic having had to carry a
    ///     layer, which is a field <c>NodeDiagnostic</c> has no business growing: it is the node
    ///     graph's type and a node graph has no layers.
    /// </remarks>
    public ImmutableDictionary<NodeId, string> Layers { get; init; } =
        ImmutableDictionary<NodeId, string>.Empty;

    /// <summary>Whether there is a plan to bake.</summary>
    public bool Succeeded => Plan is not null;
}

/// <summary>A layer stack, as a <see cref="TexturePlan" />.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 48 § D1: the stack does not get an evaluator of its own, and it does not get a
///         plan emitter of its own either.</b> This class is thirty lines because all it does is
///         hand <see cref="LayerStackGraph" />'s graph to <c>TextureGraphCompiler</c> — the same
///         public compiler a hand-wired <c>.vxtexgraph</c> goes through, with the same node classes
///         and the same kernels behind them. There is no arithmetic here for the graph compiler to
///         disagree with.
///     </para>
///     <para>
///         ⚠ <b>And that is what makes exit criterion 6 measurable rather than tautological.</b> If
///         this emitted ops directly, "a stack and its explosion bake byte-identical outputs" would
///         be comparing two compilers and would be a much harder promise; because it does not, the
///         differential's real content is the <em>round trip</em> — the explosion is written as
///         YAML, read back, and compiled, and every setting, value and wire has to survive that for
///         the bytes to match. <c>LayerStackExplodeTests</c> is where that is asserted.
///     </para>
/// </remarks>
static class LayerStackCompiler {
    /// <summary>Compiles one texture set's stack.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <param name="registry">The node types, or <see langword="null" /> for this build's.</param>
    /// <param name="bakeLevelOffset">
    ///     How much bigger this bake is than the resolution the stack was authored at: <c>0</c> at
    ///     the authoring resolution, <c>-2</c> to bake a 1K stack at 4K.
    /// </param>
    /// <param name="assets">
    ///     The project's <c>Assets/</c> folder, whose <c>Compounds</c> subfolder is published beside
    ///     the shipped ones, or <see langword="null" /> for the shipped compounds alone.
    /// </param>
    /// <returns>The plan, the graph it came from, and everything either half had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack or the set is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A parameter rather than a project, because this is a static method with no
    ///     document</b> — which is exactly how the two panels came to publish two different libraries
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/858">#858</a>). What a caller has to know
    ///     is that leaving it null means a fill or a mask effect naming a project compound compiles
    ///     in the graph panel and is <c>TG0001</c> — "nothing inlined it" — here.
    /// </remarks>
    public static LayerStackCompilation Compile(
        LayerStackAsset stack,
        TextureSetAsset set,
        NodeTypeRegistry? registry = null,
        int bakeLevelOffset = 0,
        string? assets = null
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        ISubGraphSource? subGraphs = null;

        // ⚠ Built once and used twice. The build needs the registry to know which port of a generator
        // carries its image; the compile needs the same registry *and* the sub-graph source that came
        // with it, or every compound node in the graph is a type the compiler does not know.
        registry ??= Library(out subGraphs, assets);

        var build = LayerStackGraph.Build(stack, set, registry);

        return Compile(stack, build, registry, bakeLevelOffset, subGraphs);
    }

    /// <summary>Compiles one texture set's stack against a library somebody else already published.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <param name="library">The node types and what inlines the compounds in them.</param>
    /// <param name="bakeLevelOffset">How much bigger this bake is than the authoring resolution.</param>
    /// <returns>The plan, the graph it came from, and everything either half had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack, the set or the library is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The overload a caller that compiles more than once should use, and every interactive
    ///     one does.</b> The <c>assets</c> parameter above publishes the library on the spot, which
    ///     is a recursive directory walk plus a <c>File.ReadAllText</c> and a YAML parse per project
    ///     compound — affordable for a bake and not for
    ///     <c>LayerStackPreview.Evaluate</c>, which runs once per frame of an opacity drag
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/956">#956</a>).
    ///     <para>
    ///         <b>A <see cref="TextureLibrary" /> rather than a registry and a source, for that
    ///         type's own reason</b>: a registry holding a published node type the compiler cannot
    ///         resolve is worse than one without it, so the two are never passed apart.
    ///     </para>
    /// </remarks>
    public static LayerStackCompilation Compile(
        LayerStackAsset stack,
        TextureSetAsset set,
        TextureLibrary library,
        int bakeLevelOffset = 0
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(library);

        var build = LayerStackGraph.Build(stack, set, library.Registry);

        return Compile(stack, build, library.Registry, bakeLevelOffset, library.SubGraphs);
    }

    /// <summary>This build's node types, with the compounds published into them.</summary>
    /// <param name="subGraphs">What a compiler needs to inline those compounds.</param>
    /// <param name="assets">
    ///     The project's <c>Assets/</c> folder, or <see langword="null" /> for the shipped compounds
    ///     alone.
    /// </param>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The call to <c>TextureCompoundLibrary.Publish</c> that
    ///         <a href="https://github.com/Rikarin/Vixen/issues/799">#799</a> says nothing in this
    ///         tree makes.</b> Without it the three shipped generator compounds are embedded in the
    ///         assembly, loadable, compilable and unreachable — a mask naming <c>Generators/Dirt</c>
    ///         resolves to no node type, and a graph containing one reaches a compiler with no
    ///         <c>SubGraphSource</c> and fails on every node it cannot inline. That is why doc 48
    ///         § D10's "a generator authored once works on two meshes" had never been shown: there
    ///         was no way to author one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This used to read the shipped compounds and only those, and that made one node
    ///         type mean two different things depending on which panel an author was in</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/858">#858</a>.
    ///         <c>TextureGraphDocument</c> publishes <c>Assets/Compounds</c> beside the shipped four,
    ///         so a fill or a mask effect naming a project compound compiled in the graph panel and
    ///         was <c>TG0001</c> in the layer stack. Both documents go through
    ///         <see cref="TextureNodeLibrary.Publish" /> now, so the two sets cannot drift; what is
    ///         left is that a caller with a project has to say so, because a static method has none.
    ///     </para>
    /// </remarks>
    public static NodeTypeRegistry Library(out ISubGraphSource subGraphs, string? assets = null) {
        var library = TextureNodeLibrary.Publish(assets);

        subGraphs = library.SubGraphs;

        return library.Registry;
    }

    /// <summary>Compiles a graph a stack already produced, or one read back off a file.</summary>
    /// <param name="stack">The document the resolution and the seed come from.</param>
    /// <param name="build">The graph and its layer problems.</param>
    /// <param name="registry">The node types, or <see langword="null" /> for this build's.</param>
    /// <param name="bakeLevelOffset">How much bigger this bake is than the authoring resolution.</param>
    /// <returns>The compilation.</returns>
    /// <exception cref="ArgumentNullException">The stack or the build is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The overload the differential needs.</b> Exit criterion 6 compiles a graph that came
    ///     back off a YAML round trip rather than one this process built, and it has to reach the
    ///     compiler with the <em>same</em> base resolution, seed and bake level or the comparison
    ///     would be measuring the caller rather than the explosion.
    /// </remarks>
    /// <param name="subGraphs">
    ///     What the compiler inlines a compound with, or <see langword="null" /> for a graph that
    ///     contains none.
    /// </param>
    public static LayerStackCompilation Compile(
        LayerStackAsset stack,
        LayerStackBuild build,
        NodeTypeRegistry? registry = null,
        int bakeLevelOffset = 0,
        ISubGraphSource? subGraphs = null
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(build);

        if (build.HasErrors) {
            // ⚠ Refused before the node walk rather than after it. A stack whose paint layer did not
            // compile still produces a graph — the layers under it are all there — and compiling it
            // would hand back a plan that bakes a picture missing one layer, with the refusal filed
            // under "problems" where a caller looking at `Plan is not null` never reads it.
            return new(null, build.Graph, build.Problems, [], [], []) { Layers = build.Layers };
        }

        if (registry is null) {
            registry = Library(out var published);

            subGraphs ??= published;
        }

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = stack.BaseWidth,
            BaseHeight = stack.BaseHeight,
            BakeLevelOffset = bakeLevelOffset,
            Seed = stack.Seed,
            SubGraphSource = subGraphs
        };

        var compilation = compiler.Compile(build.Graph);

        return new(
            compilation.Artefact,
            build.Graph,
            build.Problems,
            compilation.Diagnostics,
            compiler.Outputs,
            compiler.Externals
        ) {
            Layers = build.Layers
        };
    }
}
