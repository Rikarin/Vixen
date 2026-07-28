// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     The frame's own descriptor set: the environment, the probes, the shadow atlas, the sun.
/// </summary>
/// <remarks>
///     <para>
///         Set 0's counterpart to <see cref="ViewConstants" />, and the piece that was missing once
///         <c>ForwardPlus.rvn</c> started saying which set each of its bindings was in. Everything
///         here is true of the whole frame — one sky, one sun, one set of probes, one shadow atlas —
///         so it is written once and bound once, where a per-material set is written per material and
///         a per-draw block per object.
///     </para>
///     <para>
///         <strong>Unlike set 1, this one holds resources as well as a block</strong>, which is why it
///         resolves through the effect rather than being configured like <see cref="ViewConstants" />
///         is. Set 1 is a contract between shaders — its layout must be identical everywhere or the
///         shared set cannot survive a pipeline change — while set 0 belongs to whichever pass is
///         drawing, so taking its shape from that pass's own binding plan is both possible and right.
///     </para>
///     <para>
///         The names are the shader's and the indices are its too: a host sets
///         <c>ForwardPlusKeys.Environment</c> and <see cref="EffectSetWriter" /> finds where
///         <c>environment</c> goes. Adding a texture above it in the <c>.rvn</c> renumbers the binding
///         and changes nothing here.
///     </para>
/// </remarks>
public sealed class SceneConstants(IGraphicsDevice device, string name = "Scene") : IDisposable {
    readonly EffectConstants constants = new(device, name);
    readonly List<DescriptorWrite> writes = [];

    bool disposed;

    /// <summary>Where the descriptor sets come from. Without one, nothing is bound.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>Which of the four conventional sets this is.</summary>
    public DescriptorSetSlot Slot { get; set; } = DescriptorSetSlot.PerFrame;

    /// <summary>
    ///     The values and resources the frame binds, by the names the generator interned.
    /// </summary>
    /// <remarks>
    ///     One collection for both, because <see cref="ParameterCollection" /> already holds a texture
    ///     handle as happily as a float and the shader's plan is what decides which a name is. A host
    ///     fills it from whatever it has — <c>EnvironmentLight.Apply</c>, a probe's
    ///     <c>Apply</c>, its own sun — and never says where anything goes.
    /// </remarks>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>
    ///     The scene's lighting, written into <see cref="Parameters" /> on every bind.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Optional, and the reason it is here rather than in a host's frame loop is the effect:
    ///         the probe array's length is the shader's, and this is where the shader is known.
    ///         A host that would rather write the names itself leaves it null and nothing changes.
    ///     </para>
    ///     <para>
    ///         On every bind rather than once, because it is the frame's answer to "where are the
    ///         probes now" and a probe moves. It costs a comparison per value when nothing did —
    ///         <see cref="ParameterCollection.Version" /> is what decides whether the block is
    ///         re-uploaded, and re-asserting a value does not change it.
    ///     </para>
    /// </remarks>
    public Lighting.SceneLighting? Lighting { get; set; }

    /// <summary>How many times the set has been written, which settles once the frame stops changing.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Whether the last <see cref="Bind" /> found everything it needed.</summary>
    /// <remarks>
    ///     False after a bind that found a binding nothing filled — a probe array with no cubes in it,
    ///     an environment nobody set. The set is not bound in that case, so the pass draws with
    ///     whatever set 0 held before, which is the failure a host wants to see rather than a
    ///     validation error from a driver.
    /// </remarks>
    public bool IsComplete { get; private set; }

    /// <summary>
    ///     Fills the frame's set from an effect's plan and binds it.
    /// </summary>
    /// <param name="commands">Where to bind.</param>
    /// <param name="effect">The pass being drawn, whose set 0 this is.</param>
    /// <returns>Whether anything was bound.</returns>
    /// <remarks>
    ///     Takes the effect rather than a layout because the layout <em>is</em> the effect's — one
    ///     pass's set 0, not a shape shared across the frame. A second pass with a different set 0
    ///     wants its own instance of this, which is the honest answer: they are different sets.
    /// </remarks>
    public bool Bind(ICommandList commands, Effect effect) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(effect);
        ObjectDisposedException.ThrowIf(disposed, this);

        var slot = (int)Slot;

        if (Descriptors is null || effect.SetLayouts.Length <= slot || !effect.SetLayouts[slot].IsValid) {
            IsComplete = false;
            return false;
        }

        // Before the block is filled, because what it writes is half of what goes in it: the probe
        // volumes are members of this block and the cubes beside them are bindings of this set.
        Lighting?.Extract(Parameters, effect);

        // The block is the effect's own — its size and its member offsets come from the reflection —
        // so unlike a per-view block there is nothing for a host to declare.
        var block = Constants(effect);

        IsComplete = EffectSetWriter.TryWrite(effect, Slot, Parameters, block, writes);

        if (!IsComplete) {
            return false;
        }

        var set = Descriptors.Allocate(effect.SetLayouts[slot], System.Runtime.InteropServices.CollectionsMarshal.AsSpan(writes));

        commands.BindDescriptorSet(Slot, set);
        WriteCount++;
        return true;
    }

    /// <summary>The frame's block, refilled only when a value changed.</summary>
    EffectConstants? Constants(Effect effect) {
        var declared = effect.BlockOf(Slot);

        return declared.Exists && constants.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)
            ? constants
            : null;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        constants.Dispose();
    }
}
