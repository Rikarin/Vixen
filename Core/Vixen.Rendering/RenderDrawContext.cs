// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     What a feature needs in order to record: somewhere to record to, and what to resolve
///     shaders through.
/// </summary>
/// <remarks>
///     <para>
///         Passed rather than held, because recording is the phase that is meant to run on several
///         threads at once: each gets its own <see cref="CommandList" /> and they share everything
///         else. A feature that reached for a command list on the system would be a feature that
///         cannot be parallelised, and it would compile.
///     </para>
///     <para>
///         The device is here too, and only for the one thing a recording feature legitimately needs
///         it for: creating a pipeline it discovers it is missing. Everything else it wants —
///         buffers, textures, descriptor sets — belongs to a resource it was given rather than to
///         the moment of drawing.
///     </para>
/// </remarks>
public sealed class RenderDrawContext(ICommandList commandList, EffectSystem effects) {
    /// <summary>Where this thread records.</summary>
    public ICommandList CommandList { get; } = commandList;

    /// <summary>Where a shader variant comes from.</summary>
    public EffectSystem Effects { get; } = effects;

    /// <summary>The device, for a feature that has to create a pipeline it did not have.</summary>
    public IGraphicsDevice? Device { get; init; }

    /// <summary>The view being recorded.</summary>
    public RenderView? View { get; internal set; }

    /// <summary>The stage being recorded.</summary>
    public RenderStage? Stage { get; internal set; }

    /// <summary>The formats of the pass currently open.</summary>
    /// <remarks>
    ///     Set by whatever opened the pass — <see cref="Compositor.RenderPassRenderer" /> in a
    ///     composed frame — and read by any feature that has to build a pipeline. It is on the
    ///     context rather than on the stage because a stage is drawn into more than one pass, and a
    ///     pipeline built for the wrong formats is one the validation layers reject and a driver
    ///     silently mis-renders.
    /// </remarks>
    public RenderOutput Output { get; set; }
}
