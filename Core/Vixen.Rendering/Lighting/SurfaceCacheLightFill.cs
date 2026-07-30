// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>Lights the surface cache with a compute dispatch — doc 19 § L4's direct pass.</summary>
/// <remarks>
///     <para>
///         <b>The device half of <c>CardRadiosity.Light</c>, and checked against it.</b> One
///         invocation per texel and one group-z per card: the card buffer is the job list, because
///         the radiosity relights every resident card every pass — no cursor, no budget, the way the
///         probe fills have them, since a card missing a pass is a card whose bounce lags the scene.
///     </para>
///     <para>
///         <b>It is not a <c>ComputeRenderer</c>, and cannot be</b> — the atlas planes are not graph
///         resources; they are named into descriptor sets. The same shape
///         <see cref="IrradianceFieldFill" /> is, for the same reason, and every binding index comes
///         off the compiled effect rather than the generated constants for the same reason too: a
///         different source behind <c>distanceField</c> renumbers everything after it.
///     </para>
/// </remarks>
public sealed class SurfaceCacheLightFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = SurfaceCacheLightKeys.ShaderName;

    /// <summary>The slot the shadow rays march through.</summary>
    const string FieldSlot = "distanceField";

    /// <summary>The kernel's own bindings, by the names the reflection interned.</summary>
    const string CardsName = "cards";

    const string AlbedoName = "albedoDepth";
    const string NormalName = "normalValid";
    const string TargetName = "directAtlas";

    readonly IGraphicsDevice device;
    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants frameBlock;
    readonly EffectConstants materialBlock;

    bool disposed;

    /// <summary>Creates a lighting pass on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public SurfaceCacheLightFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        frameBlock = new(device, "SurfaceCacheLight.Frame");
        materialBlock = new(device, "SurfaceCacheLight.Material");
    }

    /// <summary>Where the variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The shader behind the field slot — what the shadow rays actually march.</summary>
    /// <remarks><c>NoDistanceField</c> by default: nothing shadows anything, which is the closed form
    ///     the reference pass is held against and therefore the composition the two are compared
    ///     under. A frame with a clipmap sets <c>GlobalDistanceField</c> and writes its volumes under
    ///     <c>SurfaceCacheLight.GlobalDistanceField.*</c>.</remarks>
    public string Source { get; set; } = MaterialCompiler.EmptyFieldShader;

    /// <summary>What the two sets are filled from, by the names the reflection interned.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>From the surface toward the sun, normalised by the caller.</summary>
    public Vector3 TowardSun { get; set; } = new(0f, 1f, 0f);

    /// <summary>The sun's irradiance on a perpendicular surface.</summary>
    public Vector3 SunIrradiance { get; set; } = Vector3.One;

    /// <summary>How far a shadow ray looks before deciding it escaped.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a shadow ray starts, in world units.</summary>
    public float Bias { get; set; } = 0.01f;

    /// <summary>How many cards have been dispatched over.</summary>
    public int Lit { get; private set; }

    /// <summary>How many dispatches have been recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    /// <remarks>⚠ Carried rather than thrown, the fills' shared contract: every reason is a frame not
    ///     yet ready, and a dispatch that silently does not happen is indistinguishable from a sun
    ///     that finds no surface.</remarks>
    public string? Skipped { get; private set; }

    /// <summary>Records a dispatch that lights every resident card.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="texture">The cache mirror whose direct plane the dispatch writes.</param>
    /// <returns>How many cards were dispatched over, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or texture.</exception>
    public int Record(ICommandList commands, SurfaceCacheTexture texture) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the pass has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the cache's textures do not exist yet, so there is nothing to light");
        }

        var count = texture.CardCount;

        if (count == 0) {
            return 0;
        }

        var key = EffectKey.Of(ShaderName).With(MaterialCompiler.PassComposition(FieldSlot, Source));

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        Parameters.Set(SurfaceCacheLightKeys.TowardSun, TowardSun);
        Parameters.Set(SurfaceCacheLightKeys.SunIrradiance, SunIrradiance);
        Parameters.Set(SurfaceCacheLightKeys.MaxDistance, MaxDistance);
        Parameters.Set(SurfaceCacheLightKeys.Bias, Bias);

        var frame = default(DescriptorSetHandle);

        if (Declares(effect, DescriptorSetSlot.PerFrame) && !TryFrameSet(effect, out frame)) {
            return Skip(
                $"set 0 of '{key}' has bindings nothing filled — the composed source's resources are "
                + $"written under '{ShaderName}.{Source}.*' and something has to put them there"
            );
        }

        if (!TryMaterialSet(effect, texture, out var material)) {
            return Skip($"set 2 of '{key}' could not be filled, so the dispatch would write nowhere");
        }

        commands.BindPipeline(pipeline);

        if (frame.IsValid) {
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, frame);
        }

        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, material);

        // ⚠ The plane is not a graph resource, so this is the only thing that will move it. Left in
        // ShaderRead, where the sampler and the upload both expect to find it.
        var groups = Groups(texture, out var depth);

        texture.TransitionDirect(commands, ResourceState.ShaderRead, SurfaceCacheTexture.PlaneIsBeingWritten);
        commands.Dispatch(groups.X, groups.Y, depth);
        texture.TransitionDirect(commands, SurfaceCacheTexture.PlaneIsBeingWritten, ResourceState.ShaderRead);

        Lit += count;
        Dispatches++;

        return count;
    }

    /// <summary>How many 8×8 groups cover the widest card, and how many cards deep the dispatch is.</summary>
    static Int2 Groups(SurfaceCacheTexture texture, out int depth) {
        var widest = Int2.Zero;

        foreach (var (card, _) in texture.Store.Cards) {
            widest = new(Math.Max(widest.X, card.Resolution.X), Math.Max(widest.Y, card.Resolution.Y));
        }

        depth = texture.CardCount;

        return new((widest.X + 7) / 8, (widest.Y + 7) / 8);
    }

    /// <summary>Fills set 0 from the names the composed source's owner wrote.</summary>
    bool TryFrameSet(Effect effect, out DescriptorSetHandle set) {
        set = default;

        var index = (int)DescriptorSetSlot.PerFrame;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        var declared = effect.BlockOf(DescriptorSetSlot.PerFrame);

        var block = declared.Exists
            && frameBlock.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)
                ? frameBlock
                : null;

        if (!EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, Parameters, block, writes)) {
            return false;
        }

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
    }

    /// <summary>Fills set 2 — the pass's own block, the cards, the two surface planes and the target.</summary>
    bool TryMaterialSet(Effect effect, SurfaceCacheTexture texture, out DescriptorSetHandle set) {
        const DescriptorSetSlot Slot = DescriptorSetSlot.PerMaterial;

        set = default;
        writes.Clear();

        var index = (int)Slot;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        var declared = effect.BlockOf(Slot);

        if (declared.Exists) {
            if (!materialBlock.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)) {
                return false;
            }

            writes.Add(
                DescriptorWrite.Uniform(declared.Binding, materialBlock.Buffer, materialBlock.Offset, materialBlock.Size)
            );
        }

        if (effect.BindingOf(CardsName) is not { } cardsBinding) {
            return false;
        }

        writes.Add(DescriptorWrite.Storage(cardsBinding.Binding, texture.CardsBuffer));

        if (effect.BindingOf(AlbedoName) is not { } albedo || effect.BindingOf(NormalName) is not { } normal) {
            return false;
        }

        writes.Add(DescriptorWrite.Texture(albedo.Binding, texture.AlbedoDepthView));
        writes.Add(DescriptorWrite.Texture(normal.Binding, texture.NormalValidView));

        if (effect.BindingOf(TargetName) is not { } target) {
            return false;
        }

        writes.Add(DescriptorWrite.StorageImage(target.Binding, texture.DirectView));

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
    }

    /// <summary>Whether a variant has anything at all in one of its sets.</summary>
    static bool Declares(Effect effect, DescriptorSetSlot slot) {
        foreach (var binding in effect.Bindings) {
            if (binding.Set == slot) {
                return true;
            }
        }

        return false;
    }

    int Skip(string reason) {
        Skipped = reason;

        return 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        frameBlock.Dispose();
        materialBlock.Dispose();
    }
}
