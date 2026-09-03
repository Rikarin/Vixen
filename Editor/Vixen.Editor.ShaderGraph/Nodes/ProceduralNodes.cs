// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph.Nodes;

/// <summary>The graph's procedural vocabulary, over the library that was written for it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Raven/Library/Material/ComputeColor.rvn</c>'s procedural and UV sections were
///         written for this and had no caller of any kind.</b> Its own header says so — "the shader-graph
///         node vocabulary: the primitives a visual material graph compiles down to" — and
///         <c>ValueNoise</c>, <c>FractalNoise</c>, <c>Checker</c>, <c>RotateUv</c> and
///         <c>FlipbookUv</c> were reachable from no node in this assembly. The nodes below are the
///         calling, and they add no shader code: each one is a call to a function that already ships,
///         is already compiled by <c>CheckShaders</c>, and is already in both emitters' reach.
///     </para>
///     <para>
///         ⚠ <b>They are also the first nodes that need an <c>import</c>, and the two shapes disagreed
///         about imports.</b> A surface graph emits the four <c>Vixen.Shaders.*</c> packages
///         unconditionally; a standalone graph emitted none at all. So each of these asks —
///         <see cref="RavenEmitter.Import" /> — rather than the preamble importing the library into
///         every graph, because a standalone graph is what the node preview compiles and that renderer
///         refuses a variant reflecting anything but its one uniform block.
///     </para>
///     <para>
///         ⚠ <b>None of them declares <c>Preview</c>, and the reason is a real limit rather than an
///         oversight.</b> <see cref="ShaderGraphPreviewRenderer" /> compiles the emitted preview
///         through <c>RavenEffectCompiler.FromSources</c> with <em>one</em> source — the preview
///         itself — so nothing in the shipped library is in scope and any node that calls into it
///         fails to bind. That is a property of the preview's compilation and not of these nodes: the
///         same graph compiles as a material, because <c>EditorEffects</c> and the shader build both
///         hand Raven the library's import closure. So the choice is a node with no preview against a
///         node that draws a red square, and the second one teaches an author the wrong thing.
///     </para>
///     <para>
///         What is <em>not</em> here is Perlin, simplex and voronoi. Each is a function
///         <c>ComputeColor</c> does not have, so a node for one is a change to a published
///         <c>.rvn</c> — a regeneration and a <c>CheckShaders</c> run — rather than a node. Value
///         noise is what the library chose and the file says why: a hash and a smoothstep, fed into a
///         ramp, where the difference in gradient quality does not survive.
///     </para>
/// </remarks>
static class ProceduralNodes {
    /// <summary>Where the procedural and UV helpers live.</summary>
    internal const string Library = "Vixen.Shaders.Material";
}

/// <summary>Value noise over a coordinate.</summary>
[Node("Procedural/Noise", Summary = "Smoothed value noise on a grid, in 0..1.")]
public sealed partial class NoiseNode : ShaderNode {
    /// <summary>Where to sample. Defaults to the mesh's own coordinate.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>How many cells across. One cell over the whole UV square is a very slow gradient.</summary>
    [Input(Name = "Scale", Default = [8f])]
    public Scalar Scale;

    /// <summary>The value, in 0..1.</summary>
    [Output(Name = "Out")]
    public Scalar Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Import(ProceduralNodes.Library);

        emitter.Assign(Out.Expression, $"ComputeColor.ValueNoise({Coordinate(emitter, Binding, Uv)} * {Scale})");
    }

    /// <summary>The wired coordinate, or the stage's own when nothing is wired.</summary>
    /// <remarks>
    ///     ⚠ <b>An unconnected port carries the literal its default made, which is not a
    ///     coordinate</b> — the same trap <c>Texture/Sample 2D</c> documents, and the reason every
    ///     UV-taking node has to ask rather than read. A node that read the port would sample noise at
    ///     one point and shade the whole surface in one flat value, which looks like a broken hash.
    /// </remarks>
    internal static string Coordinate(RavenEmitter emitter, NodeBinding binding, Float2 uv) =>
        binding.IsConnected("UV") ? uv.Expression : emitter.Stage(ShaderStageInput.Uv);
}

/// <summary>Value noise summed over octaves.</summary>
/// <remarks>
///     What an author means by "noise" for a cloud, a rust map or a wind field: one octave is visibly
///     a grid, and three are not.
/// </remarks>
[Node("Procedural/Fractal Noise", Summary = "Octaves of value noise, in 0..1.")]
public sealed partial class FractalNoiseNode : ShaderNode {
    /// <summary>Where to sample. Defaults to the mesh's own coordinate.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>How many cells across at the first octave.</summary>
    [Input(Name = "Scale", Default = [4f])]
    public Scalar Scale;

    /// <summary>How many octaves. Rounded down, and at least one.</summary>
    [Input(Name = "Octaves", Default = [3f])]
    public Scalar Octaves;

    /// <summary>What each octave multiplies the frequency by.</summary>
    [Input(Name = "Lacunarity", Default = [2f])]
    public Scalar Lacunarity;

    /// <summary>What each octave multiplies the amplitude by.</summary>
    [Input(Name = "Gain", Default = [0.5f])]
    public Scalar Gain;

    /// <summary>The value, in 0..1.</summary>
    [Output(Name = "Out")]
    public Scalar Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Import(ProceduralNodes.Library);

        // ⚠ The octave count is a port like the others and the function takes an `int`, so the cast is
        // here rather than in the library: a port is a float because a wire carries one, and a
        // library function that took a float would have to round it somewhere an author cannot see.
        emitter.Assign(
            Out.Expression,
            $"ComputeColor.FractalNoise({NoiseNode.Coordinate(emitter, Binding, Uv)} * {Scale}, int({Octaves}), {Lacunarity}, {Gain})"
        );
    }
}

/// <summary>A checkerboard.</summary>
/// <remarks>
///     ⚠ <b>The first thing anybody reaches for to find out whether their UVs are what they think.</b>
///     It is in the library for that reason and is worth a node for the same one — a graph author
///     debugging a coordinate should not have to write a <c>floor</c> and a <c>mod</c>.
/// </remarks>
[Node("Procedural/Checker", Summary = "Alternating 0 and 1 over a grid.")]
public sealed partial class CheckerNode : ShaderNode {
    /// <summary>Where to sample. Defaults to the mesh's own coordinate.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>How many squares across and down.</summary>
    [Input(Name = "Scale", Default = [8f, 8f])]
    public Float2 Scale;

    /// <summary>Nought or one.</summary>
    [Output(Name = "Out")]
    public Scalar Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Import(ProceduralNodes.Library);

        emitter.Assign(Out.Expression, $"ComputeColor.Checker({NoiseNode.Coordinate(emitter, Binding, Uv)}, {Scale})");
    }
}

/// <summary>A coordinate turned about a pivot.</summary>
[Node("Vector/Rotate UV", Summary = "Turns a coordinate about a pivot, in radians.")]
public sealed partial class RotateUvNode : ShaderNode {
    /// <summary>The coordinate. Defaults to the mesh's own.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>What to turn about. The middle of the square, unless the art says otherwise.</summary>
    [Input(Name = "Pivot", Default = [0.5f, 0.5f])]
    public Float2 Pivot;

    /// <summary>How far, in radians.</summary>
    [Input(Name = "Rotation", Default = [0f])]
    public Scalar Rotation;

    /// <summary>The turned coordinate.</summary>
    [Output(Name = "Out")]
    public Float2 Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Import(ProceduralNodes.Library);

        emitter.Assign(
            Out.Expression,
            $"ComputeColor.RotateUv({NoiseNode.Coordinate(emitter, Binding, Uv)}, {Pivot}, {Rotation})"
        );
    }
}

/// <summary>One cell of a sprite sheet.</summary>
/// <remarks>
///     ⚠ <b>The row is counted from the top</b>, matching the engine's top-left UV origin. The library
///     function does the flip and says so, which is the part that is easy to get wrong and invisible
///     until the animation plays backwards vertically.
/// </remarks>
[Node("Vector/Flipbook", Summary = "A sprite-sheet cell's coordinate.")]
public sealed partial class FlipbookNode : ShaderNode {
    /// <summary>The coordinate. Defaults to the mesh's own.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>How many cells across and down.</summary>
    [Input(Name = "Grid", Default = [4f, 4f])]
    public Float2 Grid;

    /// <summary>Which cell, counted along the top row first. Rounded down.</summary>
    [Input(Name = "Frame", Default = [0f])]
    public Scalar Frame;

    /// <summary>The cell's coordinate.</summary>
    [Output(Name = "Out")]
    public Float2 Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Import(ProceduralNodes.Library);

        emitter.Assign(
            Out.Expression,
            $"ComputeColor.FlipbookUv({NoiseNode.Coordinate(emitter, Binding, Uv)}, {Grid}, {Frame})"
        );
    }
}
