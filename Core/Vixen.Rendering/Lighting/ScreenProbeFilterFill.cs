// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>Spreads each accumulated probe over its plane-sharing neighbours, on the device.</summary>
/// <remarks>
///     The device half of <see cref="ScreenProbes.ScreenProbeHistory.Filter" />, checked against it —
///     one invocation per probe over the history's front set, written into the mirror's separate
///     filtered planes so the history stays raw and next frame blends against an unfiltered past.
///     Runs after the accumulation's swap, which is what makes "the front set" this frame's answer.
/// </remarks>
public sealed class ScreenProbeFilterFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = ScreenProbeFilterKeys.ShaderName;

    static readonly string[] HistoryNames = [
        "historyL0", "historyL1R", "historyL1G", "historyL1B", "historySurface", "historyNormal"
    ];

    static readonly string[] OutNames = ["outL0", "outL1R", "outL1G", "outL1B"];

    readonly IGraphicsDevice device;
    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants materialBlock;

    bool disposed;

    /// <summary>Creates a filter on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public ScreenProbeFilterFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        materialBlock = new(device, "ScreenProbeFilter.Material");
    }

    /// <summary>Where the variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The parameters the uniform block is filled from.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>A lattice neighbour's share relative to the probe's own, 0 to 1.</summary>
    public float Strength { get; set; } = 0.5f;

    /// <summary>How far off the probe's plane a neighbour may stand and still blend, in world units.</summary>
    public float Tolerance { get; set; } = 0.05f;

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    public string? Skipped { get; private set; }

    /// <summary>Records one filter pass over the history's front set into the filtered planes.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="history">The history whose front set is read — created and primed already.</param>
    /// <returns>How many probes were dispatched over, zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">An argument is missing.</exception>
    public int Record(ICommandList commands, ScreenProbeHistoryTexture history) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(history);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the filter has no effect system, pipeline cache or descriptor allocator");
        }

        if (!history.IsCreated) {
            return Skip("the history does not exist yet, so there is nothing to filter");
        }

        var key = EffectKey.Of(ShaderName).With(MaterialCompiler.PassComposition());

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        var layout = history.Layout;

        Parameters.Set(ScreenProbeFilterKeys.GridSize, new Core.Mathematics.Vector2(layout.GridSize.X, layout.GridSize.Y));
        Parameters.Set(ScreenProbeFilterKeys.Strength, Strength);
        Parameters.Set(ScreenProbeFilterKeys.Tolerance, Tolerance);

        if (!TryMaterialSet(effect, history, out var material)) {
            return Skip($"set 2 of '{key}' could not be filled, so the dispatch would write nowhere");
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, material);

        history.TransitionFiltered(commands, ResourceState.ShaderRead, ResourceState.ShaderWrite | ResourceState.ShaderRead);

        commands.Dispatch(
            (layout.GridSize.X + 7) / 8,
            (layout.GridSize.Y + 7) / 8,
            1
        );

        history.TransitionFiltered(commands, ResourceState.ShaderWrite | ResourceState.ShaderRead, ResourceState.ShaderRead);

        return layout.ProbeCount;
    }

    bool TryMaterialSet(Effect effect, ScreenProbeHistoryTexture history, out DescriptorSetHandle set) {
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

        for (var plane = 0; plane < ScreenProbeHistoryTexture.Planes; plane++) {
            if (effect.BindingOf(HistoryNames[plane]) is not { } from) {
                return false;
            }

            writes.Add(new DescriptorWrite(from.Binding, DescriptorKind.SampledTexture, TextureView: history.FrontView(plane)));
        }

        for (var plane = 0; plane < ScreenProbeHistoryTexture.FilteredPlanes; plane++) {
            if (effect.BindingOf(OutNames[plane]) is not { } to) {
                return false;
            }

            writes.Add(new DescriptorWrite(to.Binding, DescriptorKind.StorageTexture, TextureView: history.FilteredView(plane)));
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
        materialBlock.Dispose();
    }
}
