// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>One probe's placement, laid out as <c>ScreenProbeAccumulate.rvn</c>'s <c>ScreenProbeSurface</c>.</summary>
/// <remarks>Explicit offsets, asserted against the reflection by a test, for every job mirror's reason.</remarks>
[StructLayout(LayoutKind.Explicit, Size = Stride)]
public struct ScreenProbeSurfaceJob {
    /// <summary>How many bytes from one entry to the next.</summary>
    public const int Stride = 32;

    /// <summary>Where the probe's surface is, in world space.</summary>
    [FieldOffset(0)]
    public Vector3 Position;

    /// <summary>One for a probe standing on a surface; zero for one with nothing under it.</summary>
    [FieldOffset(12)]
    public int Valid;

    /// <summary>Which way that surface faces, normalised.</summary>
    [FieldOffset(16)]
    public Vector3 Normal;
}

/// <summary>Folds each frame's resolved probes into their history, on the device.</summary>
/// <remarks>
///     <para>
///         <b>The device half of <see cref="ScreenProbeHistory" />, and checked against it.</b> The
///         same capped running mean, the same reprojection through last frame's camera, the same
///         plane test for disocclusion — one invocation per probe, reading the resolve's planes and
///         last frame's history set, writing this frame's.
///     </para>
///     <para>
///         <b>The camera bookkeeping is owned here, as the CPU history owns its own.</b>
///         <see cref="ViewProjection" /> is the camera the surfaces being recorded stand under; after
///         each record it becomes the previous one, which is what the next frame reprojects through.
///         Pairing this frame's camera with last frame's surfaces reconstructs history that exists
///         nowhere, on either side of the comparison.
///     </para>
///     <para>
///         Every binding index comes off the compiled effect and the set is filled by hand, for the
///         reasons every compute driver here shares. <see cref="ScreenProbeHistoryTexture.Swap" />
///         runs inside <see cref="Record" /> — the written set is the front set by the time the
///         caller binds planes for the upsample.
///     </para>
/// </remarks>
public sealed class ScreenProbeAccumulateFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = ScreenProbeAccumulateKeys.ShaderName;

    static readonly string[] HistoryNames = [
        "historyL0", "historyL1R", "historyL1G", "historyL1B", "historySurface", "historyNormal"
    ];

    static readonly string[] OutNames = ["outL0", "outL1R", "outL1G", "outL1B", "outSurface", "outNormal"];

    static readonly string[] CurrentNames = ["currentL0", "currentL1R", "currentL1G", "currentL1B"];

    readonly IGraphicsDevice device;
    readonly UploadBuffer<ScreenProbeSurfaceJob> surfaces = new("ScreenProbeAccumulate.Surfaces");
    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants materialBlock;

    Matrix4x4 previous = Matrix4x4.Identity;
    bool disposed;

    /// <summary>Creates an accumulator on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public ScreenProbeAccumulateFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        surfaces.Device = device;
        materialBlock = new(device, "ScreenProbeAccumulate.Material");
    }

    /// <summary>Where the variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The parameters the uniform block is filled from.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>The camera the surfaces being recorded stand under — this frame's, forward.</summary>
    public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>How many frames a probe's history may weigh at most.</summary>
    public int MaxFrames { get; set; } = 16;

    /// <summary>How far off a history probe's plane this frame's surface may stand, in world units.</summary>
    public float Tolerance { get; set; } = 0.05f;

    /// <summary>How many accumulations have been recorded.</summary>
    public int Frames { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    public string? Skipped { get; private set; }

    /// <summary>Records one frame's accumulation and swaps the history.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="atlas">The placement the frame traced from — where the surfaces come from.</param>
    /// <param name="texture">The mirror whose resolved planes are this frame's input.</param>
    /// <param name="history">The ping-ponged history. Created here on first use.</param>
    /// <returns>How many probes were dispatched over, zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">An argument is missing.</exception>
    public int Record(
        ICommandList commands,
        ScreenProbeAtlas atlas,
        ScreenProbeTexture texture,
        ScreenProbeHistoryTexture history
    ) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(history);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the accumulator has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the resolved planes do not exist yet, so there is nothing to accumulate");
        }

        var key = EffectKey.Of(ShaderName).With(MaterialCompiler.PassComposition());

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        history.EnsureCreated(device);
        history.Prime(commands);

        var layout = atlas.Layout;

        Stage(atlas);

        Parameters.Set(ScreenProbeAccumulateKeys.PreviousViewProjection, previous);
        Parameters.Set(ScreenProbeAccumulateKeys.Viewport, new Vector2(layout.Viewport.X, layout.Viewport.Y));
        Parameters.Set(ScreenProbeAccumulateKeys.TileSize, (float)layout.TileSize);
        Parameters.Set(ScreenProbeAccumulateKeys.GridSize, new Vector2(layout.GridSize.X, layout.GridSize.Y));
        Parameters.Set(ScreenProbeAccumulateKeys.MaxFrames, (float)MaxFrames);
        Parameters.Set(ScreenProbeAccumulateKeys.Tolerance, Tolerance);
        Parameters.Set(ScreenProbeAccumulateKeys.Frames, Frames);

        if (!TryMaterialSet(effect, texture, history, layout.ProbeCount, out var material)) {
            return Skip($"set 2 of '{key}' could not be filled, so the dispatch would write nowhere");
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, material);

        history.TransitionBack(commands, ResourceState.ShaderRead, ResourceState.ShaderWrite | ResourceState.ShaderRead);

        commands.Dispatch(
            (layout.GridSize.X + 7) / 8,
            (layout.GridSize.Y + 7) / 8,
            1
        );

        history.TransitionBack(commands, ResourceState.ShaderWrite | ResourceState.ShaderRead, ResourceState.ShaderRead);
        history.Swap();

        previous = ViewProjection;
        Frames++;

        return layout.ProbeCount;
    }

    /// <summary>One entry per probe, straight off the atlas's placement.</summary>
    void Stage(ScreenProbeAtlas atlas) {
        surfaces.Begin();

        var layout = atlas.Layout;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var valid = atlas.TrySurface(new(x, y), out var position, out var normal);

                surfaces.Add(
                    [
                        new ScreenProbeSurfaceJob {
                            Position = position,
                            Valid = valid ? 1 : 0,
                            Normal = normal
                        }
                    ]
                );
            }
        }

        surfaces.Upload();
    }

    bool TryMaterialSet(
        Effect effect,
        ScreenProbeTexture texture,
        ScreenProbeHistoryTexture history,
        int count,
        out DescriptorSetHandle set
    ) {
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

        if (effect.BindingOf("surfaces") is not { } jobs) {
            return false;
        }

        writes.Add(
            DescriptorWrite.Storage(jobs.Binding, surfaces.Buffer, surfaces.Offset, (long)count * ScreenProbeSurfaceJob.Stride)
        );

        for (var plane = 0; plane < CurrentNames.Length; plane++) {
            if (effect.BindingOf(CurrentNames[plane]) is not { } current) {
                return false;
            }

            writes.Add(new DescriptorWrite(current.Binding, DescriptorKind.SampledTexture, TextureView: texture.ProbeView(plane)));
        }

        for (var plane = 0; plane < ScreenProbeHistoryTexture.Planes; plane++) {
            if (effect.BindingOf(HistoryNames[plane]) is not { } from) {
                return false;
            }

            writes.Add(new DescriptorWrite(from.Binding, DescriptorKind.SampledTexture, TextureView: history.FrontView(plane)));

            if (effect.BindingOf(OutNames[plane]) is not { } to) {
                return false;
            }

            writes.Add(new DescriptorWrite(to.Binding, DescriptorKind.StorageTexture, TextureView: history.BackView(plane)));
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

        surfaces.Dispose();
        materialBlock.Dispose();
    }
}
