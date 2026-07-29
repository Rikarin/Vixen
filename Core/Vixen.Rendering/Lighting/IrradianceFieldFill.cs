// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>One brick to fill, laid out as <c>IrradianceFill.rvn</c>'s <c>IrradianceFillJob</c>.</summary>
/// <remarks>
///     <para>
///         <b>Explicit offsets, copied from the reflection rather than left to the runtime.</b> The
///         shader's struct is std430: three <c>float3</c>-shaped members, each rounded up to sixteen
///         bytes, forty-eight in all. Sequential layout of three twelve-byte fields is thirty-six, and
///         every job after the first would then be read from the middle of the one before it — which
///         reads as a field where one brick is right and the rest are lit from nowhere.
///     </para>
///     <para>
///         The offsets are asserted against <c>IrradianceFill.reflect.json</c> by a test, because a
///         struct that agrees with a comment is a struct that agrees with nothing.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = Stride)]
public struct IrradianceFillJob {
    /// <summary>How many bytes from one job to the next.</summary>
    public const int Stride = 48;

    /// <summary>Where the brick's footprint starts in the pool, in texels.</summary>
    [FieldOffset(0)]
    public Int3 Origin;

    /// <summary>Where its probe (0, 0, 0) stands, in world space.</summary>
    [FieldOffset(16)]
    public Vector3 Position;

    /// <summary>How far apart its probes are. A quarter of the brick, not a fifth.</summary>
    [FieldOffset(32)]
    public Vector3 Spacing;
}

/// <summary>Fills an irradiance field's probes with a compute dispatch — doc 19 § L2's filler A.</summary>
/// <remarks>
///     <para>
///         <b>The device half of <see cref="TracedIrradianceFiller" />, and checked against it.</b>
///         One workgroup per brick and one invocation per probe: sixty-four threads marching
///         sixty-four rays each, with the brick a group is filling read out of a job buffer indexed by
///         the group. That is what lets one dispatch refill N bricks rather than N dispatches, and N
///         is <see cref="Compositor.IrradianceFieldRenderer.Budget" />'s number.
///     </para>
///     <para>
///         <b>It is not a <c>ComputeRenderer</c>, and cannot be.</b> That node binds resources by
///         graph name; the pool volumes are not graph resources — they are named into a descriptor set
///         — so nothing in the graph can see them and nothing else will transition them. This is the
///         same shape <see cref="HiZPyramid" /> is, for the same reason.
///     </para>
///     <para>
///         <b>Every binding index comes off the compiled effect rather than the generated
///         constants.</b> Those describe one variant — the reflection checked in beside the shader was
///         produced with <c>GlobalDistanceField</c> filling the slot — and a different implementation
///         behind <c>distanceField</c> may declare resources of its own, which renumbers everything
///         after them. The <i>names</i> are safe and are what this asks by; the numbers are the
///         compilation's.
///     </para>
///     <para>
///         <b>The dispatch writes the sixty-four probes a brick owns and nothing else</b>, which is
///         exactly what the reference filler writes — so the two can be compared to the last
///         coefficient. The border plane and the repair of invalid probes belong to
///         <see cref="IrradianceFieldRepair" />, which this owns and runs after every fill, in the
///         order <see cref="IrradianceField.Dilate" /> and <see cref="IrradianceField.SyncBorders" />
///         insist on. One call rather than two, because a fill without its repair leaves a rind and a
///         seam that read as lighting bugs rather than as a missing line.
///     </para>
/// </remarks>
public sealed class IrradianceFieldFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = IrradianceFillKeys.ShaderName;

    /// <summary>The slot it marches through.</summary>
    const string FieldSlot = "distanceField";

    /// <summary>The shader's names for the four pool volumes, in the order they are packed.</summary>
    static readonly string[] PoolNames = ["fillL0", "fillL1R", "fillL1G", "fillL1B"];

    /// <summary>The shader's name for the job buffer.</summary>
    const string JobsName = "jobs";

    readonly IGraphicsDevice device;
    readonly UploadBuffer<IrradianceFillJob> jobs = new("IrradianceFill.Jobs");
    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants frameBlock;
    readonly EffectConstants materialBlock;

    IrradianceBrickCursor cursor;
    IrradianceBrick[] taken = [];
    bool disposed;

    /// <summary>Creates a filler on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public IrradianceFieldFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        jobs.Device = device;
        frameBlock = new(device, "IrradianceFill.Frame");
        materialBlock = new(device, "IrradianceFill.Material");
        Repair = new(device);
    }

    /// <summary>The dilation and border sync that run after every fill.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Owned rather than optional, which is the whole reason it is here.</b> A fill leaves
    ///         probes nothing could fill and borders nothing has copied; running the repair afterwards is
    ///         cheap, easy to forget, and forgetting it produces a rind and a seam that read as lighting
    ///         bugs rather than as a missing call. That is the same argument
    ///         <see cref="Compositor.IrradianceFieldRenderer" /> makes for owning the CPU pair, and it is
    ///         the same pair.
    ///     </para>
    ///     <para>
    ///         Exposed so a caller can set <see cref="IrradianceFieldRepair.DilationPasses" /> and read
    ///         <see cref="IrradianceFieldRepair.Skipped" />; its effect system, pipelines and allocator
    ///         are this one's, forwarded on every record so the two cannot be configured apart.
    ///     </para>
    /// </remarks>
    public IrradianceFieldRepair Repair { get; }

    /// <summary>Where the fill variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The shader behind the field slot — what the rays actually march.</summary>
    /// <remarks>
    ///     <c>NoDistanceField</c> by default, which reports that nothing is near and makes every ray
    ///     reach the sky. That is not a placeholder: it is the closed form the reference filler is
    ///     held against, so it is the composition the two can be compared under.
    ///     <c>GlobalDistanceField</c> is what a frame with a clipmap in it sets, and the names its
    ///     volumes are written under are then <c>IrradianceFill.GlobalDistanceField.*</c> — the slot's
    ///     name qualified by the shader, which is what <see cref="Parameters" /> has to be filled with.
    /// </remarks>
    public string Source { get; set; } = MaterialCompiler.EmptyFieldShader;

    /// <summary>
    ///     What the two sets are filled from, by the names the reflection interned.
    /// </summary>
    /// <remarks>
    ///     The fill's own numbers are set here by the properties below. Set 0 is whatever the composed
    ///     source declares, and belongs to whoever owns it — a clipmap writes its volumes in with
    ///     <c>GlobalDistanceFieldTexture.Apply(fill.Parameters, "IrradianceFill.GlobalDistanceField")</c>.
    /// </remarks>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>How many directions each probe looks in.</summary>
    public int RayCount { get; set; } = 64;

    /// <summary>How far a ray looks before deciding it hit nothing.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>What a ray that hit nothing sees.</summary>
    public Vector3 SkyColour { get; set; } = Vector3.One;

    /// <summary>Which way the sun is, in world space.</summary>
    public Vector3 SunDirection { get; set; } = new(0f, 1f, 0f);

    /// <summary>The reciprocal of the sun's angular radius. Larger is a sharper shadow.</summary>
    public float SunSoftness { get; set; } = 8f;

    /// <summary>How much of the previous answer survives a fill, from zero to one.</summary>
    /// <remarks>
    ///     Zero takes the new answer whole, which is what makes a single fill checkable against a
    ///     closed form — and against the reference filler, whose default is zero for the same reason.
    ///     A filler running every frame wants something near nine-tenths.
    /// </remarks>
    public float Hysteresis { get; set; }

    /// <summary>Which indirection cell the next dispatch starts at.</summary>
    public int Cursor => cursor.Position;

    /// <summary>How many bricks have been dispatched since this was made.</summary>
    /// <remarks>
    ///     The same number, and the same reason, as <see cref="Compositor.IrradianceFieldRenderer.Filled" />: a
    ///     field that looks unlit and this sitting still is a scheduling failure, while the same field
    ///     with this climbing is a filler that is not finding any light.
    /// </remarks>
    public int Filled { get; private set; }

    /// <summary>How many dispatches have been recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    /// <remarks>
    ///     ⚠ Carried rather than thrown. Every reason here is a frame that is not yet ready — a shader
    ///     still compiling, a device with no compute — and a frame that threw on one of those would
    ///     take the application down for a condition that resolves itself. But a dispatch that
    ///     silently does not happen is indistinguishable from a filler that finds no light, which is
    ///     the confusion this exists to end.
    /// </remarks>
    public string? Skipped { get; private set; }

    /// <summary>Records a dispatch that refills up to <paramref name="budget" /> bricks.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="field">The field whose bricks to fill.</param>
    /// <param name="texture">Its mirror, whose pool the dispatch writes.</param>
    /// <param name="budget">How many bricks at most.</param>
    /// <returns>How many bricks were dispatched, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list, field or texture.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative budget.</exception>
    /// <remarks>
    ///     ⚠ <paramref name="texture" /> must have been created — <c>Upload</c> called at least once —
    ///     and must have <see cref="IrradianceFieldTexture.PoolIsWritten" /> set. Without the second
    ///     the pool is not a storage image and the next upload would copy the CPU field over whatever
    ///     this wrote, one frame after it wrote it.
    /// </remarks>
    public int Record(ICommandList commands, IrradianceField field, IrradianceFieldTexture texture, int budget) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the filler has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the field's textures do not exist yet, so there is no pool to write");
        }

        if (!texture.PoolIsWritten) {
            return Skip("the mirror uploads its pool, so a dispatch into it would be overwritten");
        }

        if (budget == 0) {
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

        if (taken.Length < budget) {
            taken = new IrradianceBrick[budget];
        }

        var count = cursor.Take(field, taken.AsSpan(0, budget));

        if (count == 0) {
            return 0;
        }

        Stage(field, count);

        Parameters.Set(IrradianceFillKeys.RayCount, RayCount);
        Parameters.Set(IrradianceFillKeys.MaxDistance, MaxDistance);
        Parameters.Set(IrradianceFillKeys.SkyColor, SkyColour);
        Parameters.Set(IrradianceFillKeys.SunDirection, SunDirection);
        Parameters.Set(IrradianceFillKeys.SunSoftness, SunSoftness);
        Parameters.Set(IrradianceFillKeys.Hysteresis, Hysteresis);

        // Everything that can fail does so before anything is recorded, so a list is never left
        // holding a barrier into ShaderWrite with no dispatch to justify it.
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

        // ⚠ The pool is not a graph resource, so this is the only thing that will move it. It is left
        // in ShaderRead because that is where both the mirror's upload and the sampling pass expect
        // to find it — and the repair runs inside the same pair, because it writes the same images.
        texture.TransitionPool(commands, ResourceState.ShaderRead, IrradianceFieldTexture.PoolIsBeingWritten);

        // One group per brick along Z, because that is what the shader indexes its job buffer by.
        commands.Dispatch(1, 1, count);
        texture.OrderPool(commands);

        Repair.Effects = Effects;
        Repair.Pipelines = Pipelines;
        Repair.Descriptors = Descriptors;
        Repair.Record(commands, field, texture);

        texture.TransitionPool(commands, IrradianceFieldTexture.PoolIsBeingWritten, ResourceState.ShaderRead);

        Filled += count;
        Dispatches++;

        return count;
    }

    /// <summary>Writes the bricks this dispatch covers into the job buffer.</summary>
    void Stage(IrradianceField field, int count) {
        jobs.Begin();

        for (var index = 0; index < count; index++) {
            var brick = taken[index];

            jobs.Add(
                [
                    new IrradianceFillJob {
                        Origin = field.Pool.OriginOf(brick.Slot),
                        Position = field.ProbePosition(brick, 0, 0, 0),
                        Spacing = field.ProbeSpacingOf(brick.Size)
                    }
                ]
            );
        }

        jobs.Upload();
    }

    /// <summary>Fills set 0 from the names the composed source's owner wrote.</summary>
    /// <remarks>
    ///     Through <see cref="EffectSetWriter" /> rather than by hand, because what is in this set is
    ///     not this type's to know: it belongs to whatever fills <c>distanceField</c>, and a clipmap's
    ///     four volumes and a ray-traced scene's acceleration structure are not the same shape.
    /// </remarks>
    bool TryFrameSet(Effect effect, out DescriptorSetHandle set) =>
        TryAllocate(effect, DescriptorSetSlot.PerFrame, frameBlock, out set);

    /// <summary>Fills set 2 — the fill's own block, its jobs and the four pool volumes.</summary>
    /// <remarks>
    ///     By hand rather than through <see cref="EffectSetWriter" />, for one reason: the job buffer
    ///     is a ring, and what has to be bound is <i>this frame's region of it</i> rather than the
    ///     buffer. A name-driven write has nowhere to put an offset, and binding at zero reads the
    ///     bytes an unfinished frame is still using.
    /// </remarks>
    bool TryMaterialSet(Effect effect, IrradianceFieldTexture texture, int count, out DescriptorSetHandle set) {
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
            DescriptorWrite.Storage(job.Binding, jobs.Buffer, jobs.Offset, (long)count * IrradianceFillJob.Stride)
        );

        for (var channel = 0; channel < PoolNames.Length; channel++) {
            if (effect.BindingOf(PoolNames[channel]) is not { } volume) {
                return false;
            }

            writes.Add(
                new DescriptorWrite(
                    volume.Binding,
                    DescriptorKind.StorageTexture,
                    TextureView: texture.PoolView(channel)
                )
            );
        }

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
    }

    /// <summary>One set, filled by name and allocated, or false when a binding had nothing in it.</summary>
    bool TryAllocate(Effect effect, DescriptorSetSlot slot, EffectConstants constants, out DescriptorSetHandle set) {
        set = default;

        var index = (int)slot;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        var declared = effect.BlockOf(slot);

        var block = declared.Exists
            && constants.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)
                ? constants
                : null;

        if (!EffectSetWriter.TryWrite(effect, slot, Parameters, block, writes)) {
            return false;
        }

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
    }

    /// <summary>Whether a variant has anything at all in one of its sets.</summary>
    /// <remarks>
    ///     A set with no bindings still has a layout — set indices are positional — so its layout
    ///     being valid says nothing about whether there is a set to bind. Composing
    ///     <c>NoDistanceField</c> leaves set 0 empty, and binding an empty set is a descriptor nobody
    ///     needs rather than an error, but filling one is a write of nothing that reads as a failure.
    /// </remarks>
    static bool Declares(Effect effect, DescriptorSetSlot slot) {
        foreach (var binding in effect.Bindings) {
            if (binding.Set == slot) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records why nothing was dispatched, and says so.</summary>
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
        Repair.Dispose();
    }
}
