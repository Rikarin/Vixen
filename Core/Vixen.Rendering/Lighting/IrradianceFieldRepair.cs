// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>One brick to repair, laid out as <c>IrradianceRepair.rvn</c>'s <c>IrradianceRepairJob</c>.</summary>
/// <remarks>
///     The same first three members as <see cref="IrradianceFillJob" /> and one more, which is why they
///     are two types rather than one: the size is what tells a same-size neighbour from a coarser one,
///     and a fill has no use for it. It lands in the padding std430 left after the spacing, so both are
///     forty-eight bytes — a coincidence worth *not* relying on, which is why a test asserts each
///     against its own reflection.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = Stride)]
public struct IrradianceRepairJob {
    /// <summary>How many bytes from one job to the next.</summary>
    public const int Stride = 48;

    /// <summary>Where the brick's footprint starts in the pool, in texels.</summary>
    [FieldOffset(0)]
    public Int3 Origin;

    /// <summary>Where its probe (0, 0, 0) stands, in world space.</summary>
    [FieldOffset(16)]
    public Vector3 Position;

    /// <summary>How far apart its probes are.</summary>
    [FieldOffset(32)]
    public Vector3 Spacing;

    /// <summary>How many finest cells across it is. Zero is padding, which the shader skips.</summary>
    [FieldOffset(44)]
    public float Size;
}

/// <summary>Dilation and the border sync, on the device — the other half of doc 19 § L2's filler A.</summary>
/// <remarks>
///     <para>
///         <b>Owed the moment the pool is a storage image.</b> With
///         <see cref="IrradianceFieldTexture.PoolIsWritten" /> set, the CPU field is no longer uploaded, so
///         <see cref="IrradianceField.Dilate" /> and <see cref="IrradianceField.SyncBorders" /> repair an
///         array nothing reads. Without these a device-filled field has a rind of unlit probes around every
///         wall and a seam at every brick boundary — the two artefacts the whole leak-mitigation section
///         exists to remove.
///     </para>
///     <para>
///         <b>Every brick, every frame, not the fill's budget.</b> That is what the CPU does and it is not
///         an oversight in either: a brick the budget did not refill still has neighbours that were, and a
///         border is a copy of a probe that may have just changed. Repairing only the refilled bricks would
///         leave a seam that moves around the field as the round robin walks it, which is far harder to
///         recognise than a static one. Restricting this to the dirty bricks and their neighbours is a real
///         optimisation and is not this.
///     </para>
///     <para>
///         <b>The order is fixed and each step is a dispatch with a barrier after it.</b> Dilate, promote,
///         then the borders coarsest first — and within each size class faces, then edges, then the corner.
///         A border is a copy, so copying before the original is repaired copies the hole; a fine brick
///         borrows across a size boundary by interpolating the coarse one's own field, including its border
///         plane — so the coarse brick's borders have to be right first; and a border texel can read a
///         border texel of its own class, so the one it reads has to belong to a dispatch that has finished.
///         All three facts are <see cref="IrradianceField.SyncBorders" />'s, restated as a dispatch order.
///     </para>
///     <para>
///         <b>What replaces the CPU's deferred write list is a sign for the dilation, and an order for the
///         borders.</b> <see cref="IrradianceField.Dilate" /> applies its repairs after the whole sweep so
///         that a repair cannot feed the probe beside it in the same pass. A device has nowhere to put a
///         list every invocation can also read past, so a repair is written with its validity negated — a
///         value no filler produces and every reader already rejects, because <c>validity &lt;= 0</c> was
///         already the test for "do not borrow from this". The promote pass flips it back. The border phase
///         cannot use the sign — a copy needs the value, and a sign only marks one — which is why its
///         deferral is the rank order above rather than a mark.
///     </para>
/// </remarks>
public sealed class IrradianceFieldRepair : IDisposable {
    /// <summary>The shader this dispatches, in three variants.</summary>
    public const string ShaderName = IrradianceRepairKeys.ShaderName;

    /// <summary>The shader's name for the job buffer.</summary>
    const string JobsName = "jobs";

    /// <summary>The shader's names for the four pool volumes, in the order they are packed.</summary>
    static readonly string[] PoolNames = ["repairL0", "repairL1R", "repairL1G", "repairL1B"];

    /// <summary>The shader's name for the index volume.</summary>
    const string IndirectionName = "irradianceIndirection";

    /// <summary>The shader's name for how the index volume is sampled.</summary>
    const string PointSamplerName = "irradiancePointSampler";

    /// <summary>
    ///     How many jobs a size class has to start at a multiple of.
    /// </summary>
    /// <remarks>
    ///     A storage binding's offset must be a multiple of <c>minStorageBufferOffsetAlignment</c>, which
    ///     <see cref="UploadBuffer{T}.Alignment" /> takes as 256 — the largest any target reports. A job is
    ///     forty-eight bytes, and the first multiple of forty-eight that is also a multiple of 256 is
    ///     sixteen jobs. So each class is padded up to that, and the padding is jobs of size zero, which
    ///     the shader skips.
    /// </remarks>
    const int JobAlignment = 16;

    readonly IGraphicsDevice device;
    readonly UploadBuffer<IrradianceRepairJob> jobs = new("IrradianceRepair.Jobs");
    readonly List<DescriptorWrite> writes = [];
    readonly List<(int Start, int Count)> classes = [];
    readonly List<int> sizes = [];
    readonly PipelineHandle[] borderPipelines = new PipelineHandle[3];
    readonly EffectConstants block;

    bool disposed;

    /// <summary>Creates a repair on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public IrradianceFieldRepair(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        jobs.Device = device;
        block = new(device, "IrradianceRepair.Constants");
    }

    /// <summary>Where the repair variants are resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipelines come from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>
    ///     Where the field is, by the names the reflection interned.
    /// </summary>
    /// <remarks>
    ///     Filled from <see cref="IrradianceFieldTexture.Apply" />, which writes the same struct the
    ///     sampling pass reads it through — because it is the same struct, and the repair locates a
    ///     neighbour with exactly the arithmetic a shaded pixel does.
    /// </remarks>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>How far a repair may travel into invalid probes, in probes.</summary>
    /// <remarks>
    ///     One, and the reasons are <see cref="IrradianceField.Dilate" />'s: only invalid probes are
    ///     written, so each face of a wall repairs inward from its own side and the two meet without
    ///     mixing. More passes reach further into solid geometry, where nothing samples anyway. The knob
    ///     that decides whether light walks through a wall is how thick the wall is in probes, not this.
    /// </remarks>
    public int DilationPasses { get; set; } = 1;

    /// <summary>How many dispatches the last repair recorded, or zero when it recorded none.</summary>
    /// <remarks>
    ///     Two per dilation pass plus three per size class — the border sync is a dispatch per rank. A
    ///     field that still has seams and this sitting at zero is a repair that never ran; the same field
    ///     with this climbing is a repair that ran and disagrees with the reference, which are different
    ///     bugs in different files.
    /// </remarks>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    public string? Skipped { get; private set; }

    /// <summary>Records the dilation and the border sync over every brick of a field.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="field">The field whose bricks to repair.</param>
    /// <param name="texture">Its mirror, whose pool the dispatches read and write.</param>
    /// <returns>How many bricks were repaired, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list, field or texture.</exception>
    /// <remarks>
    ///     ⚠ The pool must already be in <see cref="IrradianceFieldTexture.PoolIsBeingWritten" /> and is
    ///     left there. Bracketing the whole fill-and-repair with one pair of transitions is the caller's,
    ///     because the fill dispatch is inside the same pair.
    /// </remarks>
    public int Record(ICommandList commands, IrradianceField field, IrradianceFieldTexture texture) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;
        Dispatches = 0;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the repair has no effect system, pipeline cache or descriptor allocator");
        }

        if (!texture.IsCreated) {
            return Skip("the field's textures do not exist yet, so there is no pool to repair");
        }

        var staged = Stage(field);

        if (staged == 0) {
            return 0;
        }

        if (!TryPipelines(out var dilate, out var promote, out var borders, out var effect)) {
            return Skip($"'{ShaderName}' has not compiled all five of its variants yet");
        }

        texture.Apply(Parameters, ShaderName);

        // Every dilation pass is two dispatches: the gather, which writes a negated validity, and the
        // promote that believes it. Splitting them is what makes the answer independent of the order the
        // scheduler happened to run the invocations in.
        for (var pass = 0; pass < Math.Max(0, DilationPasses); pass++) {
            if (!Dispatch(commands, texture, effect, dilate, 0, staged)) {
                return Skip("set 2 of the dilation variant could not be filled");
            }

            if (!Dispatch(commands, texture, effect, promote, 0, staged)) {
                return Skip("set 2 of the promote variant could not be filled");
            }
        }

        // Coarsest first, because a fine brick borrows across a size boundary by interpolating the coarse
        // one's field — including the border plane the coarse brick has only just been given. Within a
        // class faces, then edges, then the corner, because an edge texel's copy can reach a face texel of
        // its own class and the read has to be of a finished dispatch — see the shader's `Rank`.
        foreach (var (start, count) in classes) {
            foreach (var pipeline in borders) {
                if (!Dispatch(commands, texture, effect, pipeline, start, count)) {
                    return Skip("set 2 of a border variant could not be filled");
                }
            }
        }

        return field.BrickCount;
    }

    /// <summary>The five variants and the effect whose plan says where their bindings go.</summary>
    /// <remarks>
    ///     One effect for the layout because all five share it — the permutations decide what an
    ///     invocation does, not what it binds — and five pipelines because that is the point of a
    ///     permutation: the promote pass is four instructions rather than a dilation kernel switched off.
    ///     The borders come back in rank order — faces, edges, corner — which is the order they dispatch.
    /// </remarks>
    bool TryPipelines(
        out PipelineHandle dilate,
        out PipelineHandle promote,
        out PipelineHandle[] borders,
        out Effect effect
    ) {
        dilate = default;
        promote = default;
        borders = borderPipelines;
        effect = null!;

        if (!TryPipeline(false, false, 1, out dilate, out effect)) {
            return false;
        }

        if (!TryPipeline(false, true, 1, out promote, out _)) {
            return false;
        }

        for (var rank = 1; rank <= 3; rank++) {
            if (!TryPipeline(true, false, rank, out borders[rank - 1], out _)) {
                return false;
            }
        }

        return true;
    }

    bool TryPipeline(bool borders, bool promote, int rank, out PipelineHandle pipeline, out Effect resolved) {
        pipeline = default;
        resolved = null!;

        // ⚠ A composition, for a shader that composes nothing. The rule is about the compilation:
        // IrradianceFill sits in the same package and declares `distanceField`, so a source set holding
        // both is refused unless this names a filler for a slot it never reaches.
        var key = EffectKey.Of(
            ShaderName,
            [
                new(IrradianceRepairKeys.Borders.Name, borders ? "true" : "false"),
                new(IrradianceRepairKeys.Promote.Name, promote ? "true" : "false"),
                new(IrradianceRepairKeys.Rank.Name, rank.ToString(CultureInfo.InvariantCulture))
            ],
            MaterialCompiler.PassComposition()
        );

        if (Effects!.Resolve(key) is not { IsPlaceholder: false } effect) {
            return false;
        }

        resolved = effect;
        pipeline = Pipelines!.GetOrCreate(effect);

        return pipeline.IsValid;
    }

    /// <summary>Binds one variant over a run of jobs and dispatches it, then orders what it wrote.</summary>
    bool Dispatch(
        ICommandList commands,
        IrradianceFieldTexture texture,
        Effect effect,
        PipelineHandle pipeline,
        int start,
        int count
    ) {
        if (count <= 0) {
            return true;
        }

        if (!TrySet(effect, texture, start, count, out var set)) {
            return false;
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
        commands.Dispatch(1, 1, count);

        // Between every pair, because each pass reads what the one before it wrote — and the pool is not
        // a graph resource, so nothing else will do it.
        texture.OrderPool(commands);
        Dispatches++;

        return true;
    }

    /// <summary>Fills set 2 — the shader's block, a run of the job buffer, and the four pool volumes.</summary>
    /// <remarks>
    ///     By hand from <see cref="Effect.BindingOf" /> rather than through <see cref="EffectSetWriter" />,
    ///     for the reason <see cref="IrradianceFieldFill" /> gives: what has to be bound is a <i>run</i> of
    ///     the job buffer, and a name-driven write has nowhere to put an offset. Here it earns its keep
    ///     twice over, because the border phase's run is one size class rather than the whole buffer.
    /// </remarks>
    bool TrySet(Effect effect, IrradianceFieldTexture texture, int start, int count, out DescriptorSetHandle set) {
        const DescriptorSetSlot Slot = DescriptorSetSlot.PerMaterial;

        set = default;
        writes.Clear();

        var index = (int)Slot;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        var declared = effect.BlockOf(Slot);

        if (declared.Exists) {
            if (!block.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)) {
                return false;
            }

            writes.Add(DescriptorWrite.Uniform(declared.Binding, block.Buffer, block.Offset, block.Size));
        }

        if (effect.BindingOf(JobsName) is not { } job) {
            return false;
        }

        writes.Add(
            DescriptorWrite.Storage(
                job.Binding,
                jobs.Buffer,
                jobs.Offset + ((long)start * IrradianceRepairJob.Stride),
                (long)count * IrradianceRepairJob.Stride
            )
        );

        if (effect.BindingOf(IndirectionName) is not { } grid) {
            return false;
        }

        writes.Add(DescriptorWrite.Texture(grid.Binding, texture.IndirectionView));

        if (effect.BindingOf(PointSamplerName) is not { } point) {
            return false;
        }

        writes.Add(DescriptorWrite.SamplerAt(point.Binding, texture.PointSampler));

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

    /// <summary>
    ///     Lays every brick into the job buffer, grouped by size, coarsest class first.
    /// </summary>
    /// <returns>How many jobs were staged, padding included.</returns>
    /// <remarks>
    ///     Grouped because the border phase dispatches one class at a time and a dispatch reads a
    ///     contiguous run. Padded because that run has to start at an aligned offset — see
    ///     <see cref="JobAlignment" />. The dilation phase reads the whole buffer including the padding,
    ///     which the shader skips on the size being zero.
    /// </remarks>
    int Stage(IrradianceField field) {
        jobs.Begin();
        classes.Clear();
        sizes.Clear();

        foreach (var brick in field.Bricks) {
            if (!sizes.Contains(brick.Size)) {
                sizes.Add(brick.Size);
            }
        }

        sizes.Sort(static (left, right) => right.CompareTo(left));

        var staged = 0;

        foreach (var size in sizes) {
            while (staged % JobAlignment != 0) {
                jobs.Add([default]);
                staged++;
            }

            var start = staged;

            foreach (var brick in field.Bricks) {
                if (brick.Size != size) {
                    continue;
                }

                jobs.Add(
                    [
                        new IrradianceRepairJob {
                            Origin = field.Pool.OriginOf(brick.Slot),
                            Position = field.ProbePosition(brick, 0, 0, 0),
                            Spacing = field.ProbeSpacingOf(brick.Size),
                            Size = brick.Size
                        }
                    ]
                );

                staged++;
            }

            classes.Add((start, staged - start));
        }

        jobs.Upload();

        return staged;
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
        block.Dispose();
    }
}
