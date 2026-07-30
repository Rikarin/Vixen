// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>Resolves each screen probe's radiance map into spherical harmonics — doc 19 § L3's "resolve to SH".</summary>
/// <remarks>
///     <para>
///         <b>The device half of <see cref="ScreenProbeAtlas.Resolve" />, and checked against it probe
///         by probe.</b> One workgroup per probe, walking the map in the exact order the CPU walks it,
///         with the solid angles uploaded from <see cref="OctahedralMap.SolidAngles" /> — the same
///         exact table, not a second derivation, because a texel's weight is a property of the map and
///         computing it twice is how two sides drift by a percent nobody can attribute.
///     </para>
///     <para>
///         The output is the mirror's four resolved-probe planes, in the irradiance pool's colour-major
///         packing, with validity in the constant plane's alpha. Whatever upsamples these interpolates
///         coefficients, exactly as the field's sampler does — which is the property the whole storage
///         layer rests on, restated per screen.
///     </para>
/// </remarks>
public sealed class ScreenProbeResolve : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = ScreenProbeResolveKeys.ShaderName;

    /// <summary>The shader's name for the atlas it reads.</summary>
    const string AtlasName = "radianceAtlas";

    /// <summary>The shader's name for the solid-angle table.</summary>
    const string WeightsName = "solidAngles";

    /// <summary>The shader's names for the four probe planes, in the order they are packed.</summary>
    static readonly string[] PlaneNames = ["probeL0", "probeL1R", "probeL1G", "probeL1B"];

    readonly UploadBuffer<float> weights = new("ScreenProbeResolve.SolidAngles");
    readonly List<DescriptorWrite> writes = [];

    bool disposed;

    /// <summary>Creates a resolve on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public ScreenProbeResolve(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        weights.Device = device;
    }

    /// <summary>Where the resolve variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>How many dispatches have been recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    public string? Skipped { get; private set; }

    /// <summary>Records a dispatch that resolves every probe of an atlas.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="texture">The mirror whose atlas is read and whose probe planes are written.</param>
    /// <returns>How many probes were dispatched, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or texture.</exception>
    /// <remarks>
    ///     ⚠ The atlas must hold what should be resolved — traced this frame or uploaded — and is read
    ///     in <see cref="ResourceState.ShaderRead" />, which is where both the trace and the upload
    ///     leave it. The probe planes are bracketed here, because nothing else knows they exist.
    /// </remarks>
    public int Record(ICommandList commands, ScreenProbeTexture texture) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the resolve has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the atlas texture does not exist yet, so there is nothing to resolve");
        }

        // A composition for a shader that composes nothing — the rule is about the compilation, and
        // this package's trace declares two slots.
        var key = EffectKey.Of(ShaderName, [], MaterialCompiler.PassComposition());

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        var layout = texture.Probes.Layout;
        var table = OctahedralMap.SolidAngles(layout.MapResolution);

        weights.Begin();
        weights.Add(table.Span);
        weights.Upload();

        if (!TrySet(effect, texture, table.Length, out var set)) {
            return Skip($"set 2 of '{key}' could not be filled, so the dispatch would write nowhere");
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);

        texture.TransitionProbes(commands, ResourceState.ShaderRead, ScreenProbeTexture.AtlasIsBeingWritten);

        // One group per probe: the shader takes its probe from the group id, in a grid-shaped dispatch.
        commands.Dispatch(layout.GridSize.X, layout.GridSize.Y, 1);

        texture.TransitionProbes(commands, ScreenProbeTexture.AtlasIsBeingWritten, ResourceState.ShaderRead);

        Dispatches++;

        return layout.ProbeCount;
    }

    /// <summary>Fills set 2 — this frame's run of the weight ring, the atlas, and the four planes.</summary>
    bool TrySet(Effect effect, ScreenProbeTexture texture, int count, out DescriptorSetHandle set) {
        const DescriptorSetSlot Slot = DescriptorSetSlot.PerMaterial;

        set = default;
        writes.Clear();

        var index = (int)Slot;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        if (effect.BindingOf(WeightsName) is not { } table) {
            return false;
        }

        writes.Add(DescriptorWrite.Storage(table.Binding, weights.Buffer, weights.Offset, (long)count * sizeof(float)));

        if (effect.BindingOf(AtlasName) is not { } atlas) {
            return false;
        }

        writes.Add(DescriptorWrite.Texture(atlas.Binding, texture.AtlasView));

        for (var plane = 0; plane < PlaneNames.Length; plane++) {
            if (effect.BindingOf(PlaneNames[plane]) is not { } target) {
                return false;
            }

            writes.Add(
                new DescriptorWrite(target.Binding, DescriptorKind.StorageTexture, TextureView: texture.ProbeView(plane))
            );
        }

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
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

        weights.Dispose();
    }
}
