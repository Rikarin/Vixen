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

    readonly Dictionary<string, string> missingByEffect = new(StringComparer.Ordinal);

    /// <summary>How many times the set has been written, which settles once the frame stops changing.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Whether every effect that binds through this found everything it needed.</summary>
    /// <remarks>
    ///     <para>
    ///         False while any effect's last bind found a binding nothing filled — a probe array with
    ///         no cubes in it, an environment nobody set. The set is not bound in that case, so the
    ///         pass draws with whatever set 0 held before, which is the failure a host wants to see
    ///         rather than a validation error from a driver.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Across every effect, not the last one.</b> This used to report the most recent
    ///         <see cref="Bind" /> alone, and a frame binds set 0 once per pass — so a Main pass short
    ///         of a binding was overwritten by a later, smaller pass that bound fine, and the one log
    ///         line a black frame gets said "complete". Each effect's answer is kept until its next
    ///         bind updates it.
    ///     </para>
    /// </remarks>
    public bool IsComplete { get; private set; }

    /// <summary>
    ///     The shader's names for every binding nobody filled, per effect, or null when none.
    /// </summary>
    /// <remarks>
    ///     The one fact a black frame is otherwise missing. <see cref="IsComplete" /> says a set did
    ///     not bind; this says <em>which</em> effect and which of a dozen resources had nobody to
    ///     fill them, which is the difference between reading a shader's binding plan by hand and
    ///     reading one log line.
    /// </remarks>
    public string? MissingBinding { get; private set; }

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

        if (Descriptors is null) {
            Record(effect.Key.ShaderName, "(no descriptor allocator)");
            return false;
        }

        // An effect with no such set at all is the ordinary case, not a failure — the same
        // distinction the TryWrite path below draws for an empty set. It used to be recorded as
        // incomplete, which under per-effect tracking would flag every ShadowCaster-shaped pass
        // forever.
        if (effect.SetLayouts.Length <= slot || !effect.SetLayouts[slot].IsValid) {
            Record(effect.Key.ShaderName, null);
            return false;
        }

        // Before the block is filled, because what it writes is half of what goes in it: the probe
        // volumes are members of this block and the cubes beside them are bindings of this set.
        Lighting?.Extract(Parameters, effect);

        // The block is the effect's own — its size and its member offsets come from the reflection —
        // so unlike a per-view block there is nothing for a host to declare.
        var block = Constants(effect);

        var written = EffectSetWriter.TryWrite(effect, Slot, Parameters, block, writes, out var missing);

        // ⚠ <b>A set the effect declares nothing in is not a set anybody failed to fill.</b>
        // `TryWrite` answers false for it — there is nothing to write, so nothing was written — and
        // names no missing binding, which is the distinction. A shader that reads nothing per frame is
        // the ordinary case rather than a mistake: `ParticleSprite` is one, and `ShadowCaster` is
        // another. Left conflated, one such pass in a frame makes `IsComplete` say "this frame binds
        // no set 0", which is the black-screen diagnostic, about a frame that is drawing.
        Record(effect.Key.ShaderName, written || missing is null ? null : missing);

        if (!written) {
            return false;
        }

        var set = Descriptors.Allocate(effect.SetLayouts[slot], System.Runtime.InteropServices.CollectionsMarshal.AsSpan(writes));

        commands.BindDescriptorSet(Slot, set);
        WriteCount++;
        return true;
    }

    /// <summary>One effect's completeness, folded into the whole's.</summary>
    /// <remarks>
    ///     Keyed by shader name and kept until that effect's next bind updates it, so a pass that
    ///     went short stays visible however many complete passes bind after it. A document reload
    ///     that removes an effect leaves its last entry behind until the next process start — a
    ///     stale name in a diagnostic string, which is cheaper than being wrong the other way.
    /// </remarks>
    void Record(string effect, string? missing) {
        if (missing is null) {
            missingByEffect.Remove(effect);
        } else {
            missingByEffect[effect] = missing;
        }

        IsComplete = missingByEffect.Count == 0;

        MissingBinding = missingByEffect.Count == 0
            ? null
            : string.Join("; ", missingByEffect.Select(entry => $"{entry.Key}: {entry.Value}"));
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
