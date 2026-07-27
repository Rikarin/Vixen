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
///         <see cref="Stage" /> stands in for the pass's own state — its blend and depth intent and
///         its attachment formats — because a stage is what a compositor configures and a pass is
///         what it configures <em>into</em>. When the compositor asset exists this becomes the pass's
///         own identity; the shape of the key does not change.
///     </para>
/// </remarks>
public readonly record struct PipelineKey(Effect Effect, int Stage, int VertexLayout);

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
