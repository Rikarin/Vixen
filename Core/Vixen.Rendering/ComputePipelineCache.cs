// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     Compute pipelines, created once per effect.
/// </summary>
/// <remarks>
///     <para>
///         Keyed by the effect alone, where <see cref="PipelineCache" /> needs four parts — and the
///         difference is the whole reason there are two caches rather than one generalised one. A
///         graphics pipeline depends on what it draws into, what state it draws with and how a vertex
///         is read; a compute pipeline is the shader and its layout, and nothing else exists to vary.
///     </para>
///     <para>
///         Never evicts, for the same reason: a project's distinct compute shaders are authored, and
///         dropping one only to recreate it is the stall the cache exists to remove.
///     </para>
/// </remarks>
public sealed class ComputePipelineCache(IGraphicsDevice device) {
    readonly Dictionary<Effect, PipelineHandle> pipelines = [];
    readonly Dictionary<Effect, ShaderHandle> modules = [];

    /// <summary>How many distinct pipelines have been created.</summary>
    public int Count => pipelines.Count;

    /// <summary>The pipeline for an effect, compiling it the first time.</summary>
    /// <returns>The pipeline, or an invalid handle when the effect has no compute stage.</returns>
    /// <remarks>
    ///     An invalid handle rather than a throw for a missing stage, because that is what a wrongly
    ///     addressed effect looks like — a graphics variant asked for as a compute one — and the node
    ///     that asked can skip a dispatch far more usefully than a frame can catch an exception.
    /// </remarks>
    public PipelineHandle GetOrCreate(Effect effect) {
        ArgumentNullException.ThrowIfNull(effect);

        if (pipelines.TryGetValue(effect, out var existing)) {
            return existing;
        }

        var module = Module(effect);
        var created = default(PipelineHandle);

        if (module.IsValid) {
            created = device.CreateComputePipeline(new(module, effect.Layout, effect.Key.ShaderName));
        }

        pipelines[effect] = created;
        return created;
    }

    /// <summary>Forgets every pipeline and module, for a device loss or a shader reload.</summary>
    public void Clear() {
        pipelines.Clear();
        modules.Clear();
    }

    ShaderHandle Module(Effect effect) {
        if (modules.TryGetValue(effect, out var existing)) {
            return existing;
        }

        var handle = ShaderHandle.Null;

        foreach (var compiled in effect.Stages) {
            if (compiled.Stage != ShaderStage.Compute) {
                continue;
            }

            handle = device.CreateShader(
                ShaderStage.Compute,
                compiled.Bytecode.AsSpan(),
                $"{effect.Key.ShaderName}.Compute"
            );

            break;
        }

        modules[effect] = handle;
        return handle;
    }
}
