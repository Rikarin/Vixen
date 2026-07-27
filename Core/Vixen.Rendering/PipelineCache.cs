// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     What a pipeline depends on beyond its shader.
/// </summary>
/// <remarks>
///     <para>
///         The reason <see cref="Effect" /> holds bytecode and layout rather than a pipeline: the
///         same shader compiled once is drawn with different blend states into different attachment
///         formats by different stages. Keying pipelines by effect alone hands back an object drawn
///         with the wrong blend mode — a bug that looks like a material problem and is not.
///     </para>
///     <para>
///         Four parts, because four things decide what a driver compiles: the shader, how its objects
///         are drawn (<see cref="Stage" /> — blend, depth, raster), what they are drawn into
///         (<see cref="Output" /> — formats and samples) and how a vertex is read
///         (<see cref="VertexLayout" />). <see cref="Compositor.EffectPipelineDescriber" /> takes
///         exactly these four and nothing else, which is what makes the key complete rather than
///         merely sufficient so far.
///     </para>
///     <para>
///         The output is <em>formats</em>, not textures — see <see cref="RenderOutput" />. That is
///         what lets the swapchain hand out a different image every frame, and the render graph alias
///         transient targets, without invalidating a single pipeline.
///     </para>
/// </remarks>
public readonly record struct PipelineKey(Effect Effect, int Stage, int VertexLayout, RenderOutput Output);

/// <summary>How a pipeline description is built for one effect drawn by one stage into one output.</summary>
/// <remarks>
///     An interface rather than a callback on the feature, because it is one decision shared by every
///     feature that draws — and because the compositor is where it belongs: the blend and depth state
///     come from the stage it configures, and the formats from the pass it configures that stage
///     into.
/// </remarks>
public interface IPipelineDescriber {
    /// <summary>The description to compile a pipeline from.</summary>
    /// <param name="effect">The shader variant being drawn with.</param>
    /// <param name="stage">The stage drawing it, which carries the blend, depth and raster state.</param>
    /// <param name="output">The formats of the pass it is drawn into.</param>
    /// <param name="vertexLayout">Which vertex layout the geometry uses.</param>
    GraphicsPipelineDescription Describe(
        Effect effect,
        RenderStage stage,
        in RenderOutput output,
        int vertexLayout
    );
}

/// <summary>
///     Pipelines, created once per distinct <see cref="PipelineKey" />.
/// </summary>
/// <remarks>
///     <para>
///         Creating a pipeline is the most expensive thing a frame can do — a driver compiles and
///         optimises the shader for the exact state — so doing it in a draw call is the classic
///         cause of a first-run stutter that profiling attributes to the wrong thing. Cached here,
///         and asked for by a key that names everything the driver was given.
///     </para>
///     <para>
///         The cache never evicts. A project's distinct pipelines are bounded by its materials and
///         its compositor, which are both authored rather than generated, and dropping one only to
///         recreate it is the stutter this exists to remove.
///     </para>
/// </remarks>
public sealed class PipelineCache(IGraphicsDevice device) {
    readonly Dictionary<PipelineKey, PipelineHandle> pipelines = [];

    /// <summary>How many distinct pipelines have been created.</summary>
    public int Count => pipelines.Count;

    /// <summary>The pipeline for a key, if there already is one.</summary>
    /// <remarks>
    ///     What a draw loop asks first. <see cref="GetOrCreate" /> takes a closure, and a closure is
    ///     an allocation per call whether or not it is ever invoked — so the hit path asks this and
    ///     the miss path, which is about to compile a shader, can afford the closure.
    /// </remarks>
    public bool TryGet(in PipelineKey key, out PipelineHandle pipeline) =>
        pipelines.TryGetValue(key, out pipeline);

    /// <summary>
    ///     The pipeline for a key, creating it from <paramref name="describe" /> the first time.
    /// </summary>
    /// <remarks>
    ///     The description is built by a callback rather than passed in, so the common case — a hit —
    ///     costs a dictionary lookup and does not build a <see cref="GraphicsPipelineDescription" />
    ///     with its arrays only to throw it away.
    /// </remarks>
    public PipelineHandle GetOrCreate(in PipelineKey key, Func<GraphicsPipelineDescription> describe) {
        ArgumentNullException.ThrowIfNull(describe);

        if (pipelines.TryGetValue(key, out var existing)) {
            return existing;
        }

        var created = device.CreateGraphicsPipeline(describe());
        pipelines[key] = created;
        return created;
    }

    /// <summary>Forgets every pipeline, for a device loss or a shader reload.</summary>
    /// <remarks>
    ///     Does not destroy them: the handles belong to the device, and a caller that reloaded
    ///     shaders is about to drop the device or the effects behind them. Destroying here would
    ///     mean deciding whether a pipeline still in flight is safe to free, which is the device's
    ///     question rather than the cache's.
    /// </remarks>
    public void Clear() => pipelines.Clear();
}
