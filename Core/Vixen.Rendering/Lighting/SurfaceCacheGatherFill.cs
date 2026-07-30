// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>The bounce over the cards as a compute dispatch — doc 19 § L4's radiosity pass.</summary>
/// <remarks>
///     <para>
///         <b>The device half of <c>CardRadiosity.Gather</c>, and checked against it.</b> Every
///         valid texel casts the reference's own Hammersley rays; a hit asks the composed
///         <c>surfaceCache</c> slot what the surface radiates — <c>SurfaceCacheSource</c> reading
///         the front of the double buffer while the dispatch writes the back — and the host swaps
///         with <see cref="SurfaceCacheTexture.SwapGather" /> after the queue settles, the mirror of
///         <c>SurfaceCacheStore.SwapGathered</c>.
///     </para>
///     <para>
///         <b>Iterating this to a fixed point is the infinite-bounce look</b>, and each pass is one
///         recorded dispatch: light once, then gather-and-swap until the change the readback shows
///         drops below whatever the caller calls converged — the same loop the Cornell test runs on
///         the CPU, pass for pass.
///     </para>
/// </remarks>
public sealed class SurfaceCacheGatherFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = SurfaceCacheGatherKeys.ShaderName;

    /// <summary>The slot the gather rays march through.</summary>
    const string FieldSlot = "distanceField";

    /// <summary>The slot a hit is answered through.</summary>
    const string CacheSlot = "surfaceCache";

    /// <summary>The kernel's own bindings, by the names the reflection interned.</summary>
    const string CardsName = "cards";

    const string AlbedoName = "albedoDepth";
    const string NormalName = "normalValid";
    const string TargetName = "gatherAtlas";

    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants frameBlock;
    readonly EffectConstants materialBlock;

    bool disposed;

    /// <summary>Creates a bounce pass on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public SurfaceCacheGatherFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        frameBlock = new(device, "SurfaceCacheGather.Frame");
        materialBlock = new(device, "SurfaceCacheGather.Material");
    }

    /// <summary>Where the variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The shader behind the field slot — what the gather rays actually march.</summary>
    public string Source { get; set; } = MaterialCompiler.EmptyFieldShader;

    /// <summary>The shader behind the cache slot — what a hit answers with.</summary>
    /// <remarks><c>NoSurfaceCache</c> by default — every hit black, the tracers' answer before § L4
    ///     existed, and the composition a gather-under-open-sky closed form runs under. A frame with
    ///     a cache sets <see cref="MaterialCompiler.SurfaceCacheShader" /> and applies its mirror
    ///     under <c>SurfaceCacheGather.SurfaceCacheSource</c>.</remarks>
    public string CacheSource { get; set; } = MaterialCompiler.EmptySurfaceCacheShader;

    /// <summary>What the two sets are filled from, by the names the reflection interned.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>How many rays each texel's gather casts.</summary>
    public int Rays { get; set; } = 32;

    /// <summary>How far a gather ray looks before deciding it escaped.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a ray starts, in world units.</summary>
    public float Bias { get; set; } = 0.01f;

    /// <summary>What an escaping ray sees. Black is a closed scene.</summary>
    public Vector3 SkyColour { get; set; }

    /// <summary>How many cards have been dispatched over.</summary>
    public int Gathered { get; private set; }

    /// <summary>How many dispatches have been recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    public string? Skipped { get; private set; }

    /// <summary>Records one bounce over every resident card.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="texture">The cache mirror whose back gather plane the dispatch writes.</param>
    /// <returns>How many cards were dispatched over, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or texture.</exception>
    /// <remarks>⚠ The dispatch writes the back plane; nothing reads it until the caller calls
    ///     <see cref="SurfaceCacheTexture.SwapGather" /> — after the submit, because the swap decides
    ///     what the <i>next</i> recorded pass reads and which plane <c>Apply</c> publishes.</remarks>
    public int Record(ICommandList commands, SurfaceCacheTexture texture) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the pass has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the cache's textures do not exist yet, so there is nothing to gather over");
        }

        var count = texture.CardCount;

        if (count == 0) {
            return 0;
        }

        var key = EffectKey.Of(ShaderName)
            .With(MaterialCompiler.PassComposition((FieldSlot, Source), (CacheSlot, CacheSource)));

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        Parameters.Set(SurfaceCacheGatherKeys.Rays, Rays);
        Parameters.Set(SurfaceCacheGatherKeys.MaxDistance, MaxDistance);
        Parameters.Set(SurfaceCacheGatherKeys.Bias, Bias);
        Parameters.Set(SurfaceCacheGatherKeys.SkyColor, SkyColour);

        var frame = default(DescriptorSetHandle);

        if (Declares(effect, DescriptorSetSlot.PerFrame) && !TryFrameSet(effect, out frame)) {
            return Skip(
                $"set 0 of '{key}' has bindings nothing filled — the composed sources' resources are "
                + $"written under '{ShaderName}.{Source}.*' and '{ShaderName}.{CacheSource}.*', "
                + "and something has to put them there"
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

        var groups = Groups(texture, out var depth);

        // ⚠ The back plane is not a graph resource, so this is the only thing that will move it.
        texture.TransitionGatherBack(commands, ResourceState.ShaderRead, SurfaceCacheTexture.PlaneIsBeingWritten);
        commands.Dispatch(groups.X, groups.Y, depth);
        texture.TransitionGatherBack(commands, SurfaceCacheTexture.PlaneIsBeingWritten, ResourceState.ShaderRead);

        Gathered += count;
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

    /// <summary>Fills set 0 from the names the composed sources' owners wrote.</summary>
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

        writes.Add(DescriptorWrite.StorageImage(target.Binding, texture.GatherBackView));

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
