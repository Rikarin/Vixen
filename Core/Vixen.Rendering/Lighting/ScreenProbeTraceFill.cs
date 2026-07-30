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

/// <summary>One probe to trace, laid out as <c>ScreenProbeTrace.rvn</c>'s <c>ScreenProbeTraceJob</c>.</summary>
/// <remarks>
///     Explicit offsets, asserted against <c>ScreenProbeTrace.reflect.json</c> by a test, for the
///     reason every job mirror here gives: std430 puts the <c>float3</c> at sixteen where sequential
///     layout would put it at eight, and a struct that agrees with a comment agrees with nothing.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = Stride)]
public struct ScreenProbeTraceJob {
    /// <summary>How many bytes from one job to the next.</summary>
    public const int Stride = 32;

    /// <summary>Where the probe's map starts in the atlas, in texels.</summary>
    [FieldOffset(0)]
    public Int2 AtlasOrigin;

    /// <summary>One for a probe standing on a surface; zero for one whose patch is cleared instead.</summary>
    [FieldOffset(8)]
    public int Valid;

    /// <summary>Where its rays start — the probe's surface, already stepped off along its normal.</summary>
    [FieldOffset(16)]
    public Vector3 Origin;
}

/// <summary>Traces screen probes with a compute dispatch — doc 19 § L3's gather, first half.</summary>
/// <remarks>
///     <para>
///         <b>The device half of <see cref="TracedScreenProbeGather" />, and checked against it.</b>
///         One workgroup per probe and one invocation per texel, with the probe a group is tracing
///         read out of a job buffer indexed by the group — the same shape as
///         <see cref="IrradianceFieldFill" />, because it is the same problem one probe kind over.
///     </para>
///     <para>
///         <b>The jobs are staged from the atlas, and only for the probes that have a surface.</b>
///         The atlas is where <see cref="ScreenProbeAtlas.SetSurface" /> and
///         <see cref="ScreenProbeAtlas.Invalidate" /> already recorded what each anchor pixel showed,
///         so the dispatch has no opinion about the screen at all. ⚠ By default a probe with no
///         surface is not dispatched, and on a mirror the dispatch owns its patch is therefore
///         <i>undefined</i> — nothing uploaded it and nothing traced it. A frame sets
///         <see cref="ClearInvalid" /> and such probes get a job that writes the invalid mark
///         instead, because the resolve reads validity out of the atlas.
///     </para>
///     <para>
///         <b>Every binding index comes off the compiled effect rather than the generated
///         constants</b>, and set 2 is filled by hand because the job buffer is a ring and a
///         name-driven write has nowhere to put an offset — both verbatim from the irradiance fill,
///         which learnt them the hard way.
///     </para>
/// </remarks>
public sealed class ScreenProbeTraceFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = ScreenProbeTraceKeys.ShaderName;

    /// <summary>The slot it marches through.</summary>
    const string FieldSlot = "distanceField";

    /// <summary>The slot its long rays terminate in.</summary>
    const string FarSlot = "irradiance";

    /// <summary>The shader's name for the job buffer.</summary>
    const string JobsName = "jobs";

    /// <summary>The shader's name for the atlas it writes.</summary>
    const string AtlasName = "radianceAtlas";

    readonly IGraphicsDevice device;
    readonly UploadBuffer<ScreenProbeTraceJob> jobs = new("ScreenProbeTrace.Jobs");
    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants frameBlock;
    readonly EffectConstants materialBlock;

    bool disposed;

    /// <summary>Creates a tracer on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public ScreenProbeTraceFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        jobs.Device = device;
        frameBlock = new(device, "ScreenProbeTrace.Frame");
        materialBlock = new(device, "ScreenProbeTrace.Material");
    }

    /// <summary>Where the trace variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The shader behind the field slot — what the rays actually march.</summary>
    /// <remarks>
    ///     <c>NoDistanceField</c> by default, exactly as <see cref="IrradianceFieldFill.Source" /> and
    ///     for its reason: every ray reaching the sky is the closed form the reference gather is held
    ///     against, so it is the composition the two can be compared under.
    /// </remarks>
    public string Source { get; set; } = MaterialCompiler.EmptyFieldShader;

    /// <summary>The shader behind the irradiance slot — where a ray that misses terminates.</summary>
    /// <remarks>
    ///     <c>NoIrradiance</c> by default, whose coverage of zero blends every termination back to the
    ///     sky — a project without a field traces exactly the rays it traced before.
    ///     <c>IrradianceFieldProbes</c> is what a frame with a field sets, and the field's volumes are
    ///     then written under <c>ScreenProbeTrace.IrradianceFieldProbes.*</c> — which is
    ///     <see cref="IrradianceFieldTexture.Apply" /> with exactly that prefix, into
    ///     <see cref="Parameters" />.
    /// </remarks>
    public string FarSource { get; set; } = MaterialCompiler.EmptyIrradianceShader;

    /// <summary>What the sets are filled from, by the names the reflection interned.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>How far a ray looks before deciding it hit nothing.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a probe stands, in world units.</summary>
    /// <remarks>
    ///     Applied while staging, so the shader receives an origin rather than a surface and a rule —
    ///     the same single place the reference applies it, which is what keeps the two comparable.
    /// </remarks>
    public float SurfaceBias { get; set; } = 0.01f;

    /// <summary>What a ray that hit nothing sees, straight ahead.</summary>
    public Vector3 SkyColour { get; set; } = Vector3.One;

    /// <summary>How much the sky changes per unit of a direction's y, per channel.</summary>
    /// <remarks>
    ///     Zero is a uniform sky. Nonzero is what lets a device comparison exercise the octahedral
    ///     decode: under a uniform sky every texel is the same number whatever direction it stands
    ///     for, so a mirrored or transposed decode is invisible.
    /// </remarks>
    public Vector3 SkyGradient { get; set; }

    /// <summary>Whether probes with no surface get a job that clears their patch to the invalid mark.</summary>
    /// <remarks>
    ///     <para>
    ///         Off, only the probes with a surface are dispatched, and an unwritten patch on a
    ///         device-owned atlas is <i>undefined</i> — the composition the device comparisons run
    ///         under, where the reference says which texels mean anything.
    ///     </para>
    ///     <para>
    ///         On, every probe gets a job, and one with no surface stores zero alpha across its patch
    ///         instead of tracing. A frame wants this: the resolve reads validity out of the atlas,
    ///         and on an atlas the dispatch owns, a patch nothing wrote holds whatever the allocator
    ///         left there — garbage that can carry a nonzero alpha into a probe the placement already
    ///         said has nothing under it.
    ///     </para>
    /// </remarks>
    public bool ClearInvalid { get; set; }

    /// <summary>How many probes have been dispatched since this was made.</summary>
    public int Traced { get; private set; }

    /// <summary>How many dispatches have been recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    /// <remarks>
    ///     Carried rather than thrown, for <see cref="IrradianceFieldFill.Skipped" />'s reason: every
    ///     cause is a frame that is not ready yet, and a dispatch that silently does not happen is
    ///     indistinguishable from a gather that found no light.
    /// </remarks>
    public string? Skipped { get; private set; }

    /// <summary>Records a dispatch that traces every valid probe of an atlas.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="texture">The atlas mirror whose texture the dispatch writes.</param>
    /// <returns>How many probes were dispatched, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or texture.</exception>
    /// <remarks>
    ///     ⚠ <paramref name="texture" /> must have been created — <c>Upload</c> called at least once —
    ///     and must have <see cref="ScreenProbeTexture.AtlasIsWritten" /> set, or the next upload
    ///     copies the stale CPU atlas over whatever this traced, one frame after it traced it.
    /// </remarks>
    public int Record(ICommandList commands, ScreenProbeTexture texture) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the tracer has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the atlas texture does not exist yet, so there is nothing to write");
        }

        if (!texture.AtlasIsWritten) {
            return Skip("the mirror uploads its atlas, so a dispatch into it would be overwritten");
        }

        var key = EffectKey.Of(ShaderName).With(MaterialCompiler.PassComposition((FieldSlot, Source), (FarSlot, FarSource)));

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        var count = Stage(texture.Probes);

        if (count == 0) {
            return 0;
        }

        Parameters.Set(ScreenProbeTraceKeys.MaxDistance, MaxDistance);
        Parameters.Set(ScreenProbeTraceKeys.SkyColor, SkyColour);
        Parameters.Set(ScreenProbeTraceKeys.SkyGradient, SkyGradient);

        // Everything that can fail does so before anything is recorded, so a list is never left
        // holding a barrier with no dispatch to justify it.
        var frame = default(DescriptorSetHandle);

        if (Declares(effect, DescriptorSetSlot.PerFrame) && !TryFrameSet(effect, out frame)) {
            return Skip(
                $"set 0 of '{key}' has bindings nothing filled — the composed source's resources are "
                + $"written under '{ShaderName}.{Source}.*' and something has to put them there"
            );
        }

        if (!TryMaterialSet(effect, texture, count, out var material)) {
            return Skip($"set 2 of '{key}' could not be filled, so the dispatch would write nowhere");
        }

        commands.BindPipeline(pipeline);

        if (frame.IsValid) {
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, frame);
        }

        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, material);

        // ⚠ The atlas is not a graph resource, so this is the only thing that will move it. Left in
        // ShaderRead, where the upload, the readback and the coming resolve all expect to find it.
        texture.TransitionAtlas(commands, ResourceState.ShaderRead, ScreenProbeTexture.AtlasIsBeingWritten);

        // One group per probe along Z, because that is what the shader indexes its job buffer by.
        commands.Dispatch(1, 1, count);

        texture.TransitionAtlas(commands, ScreenProbeTexture.AtlasIsBeingWritten, ResourceState.ShaderRead);

        Traced += count;
        Dispatches++;

        return count;
    }

    /// <summary>Writes one job per valid probe into the job buffer.</summary>
    /// <returns>How many probes have a surface to trace from.</returns>
    int Stage(ScreenProbeAtlas atlas) {
        jobs.Begin();

        var layout = atlas.Layout;
        var count = 0;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                if (atlas.TrySurface(probe, out var position, out var normal)) {
                    jobs.Add(
                        [
                            new ScreenProbeTraceJob {
                                AtlasOrigin = layout.AtlasOrigin(probe),
                                Valid = 1,
                                Origin = position + (normal * SurfaceBias)
                            }
                        ]
                    );
                } else if (ClearInvalid) {
                    jobs.Add([new ScreenProbeTraceJob { AtlasOrigin = layout.AtlasOrigin(probe) }]);
                } else {
                    continue;
                }

                count++;
            }
        }

        if (count > 0) {
            jobs.Upload();
        }

        return count;
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

    /// <summary>Fills set 2 — the trace's own block, this frame's run of the job ring, and the atlas.</summary>
    bool TryMaterialSet(Effect effect, ScreenProbeTexture texture, int count, out DescriptorSetHandle set) {
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

        if (effect.BindingOf(JobsName) is not { } job) {
            return false;
        }

        writes.Add(
            DescriptorWrite.Storage(job.Binding, jobs.Buffer, jobs.Offset, (long)count * ScreenProbeTraceJob.Stride)
        );

        if (effect.BindingOf(AtlasName) is not { } target) {
            return false;
        }

        writes.Add(new DescriptorWrite(target.Binding, DescriptorKind.StorageTexture, TextureView: texture.AtlasView));

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

        jobs.Dispose();
        frameBlock.Dispose();
        materialBlock.Dispose();
    }
}
