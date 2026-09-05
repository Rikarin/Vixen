// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Editor.TextureGraph;

/// <summary>A plan that has been evaluated, and the textures it produced.</summary>
/// <remarks>
///     ⚠ <b>It owns every texture the evaluation created, including the pooled intermediates.</b>
///     Disposing destroys them; not disposing leaks a bake's worth of images, which at 2K is tens of
///     megabytes per plan. An output image and an intermediate are the same kind of thing here — the
///     plan's <see cref="TexturePlan.Outputs" /> is what stops the pool from handing an output's slot
///     to a later op, and nothing about the handle says which it was.
/// </remarks>
public sealed class TextureBake : IDisposable {
    readonly IGraphicsDevice device;
    readonly TextureHandle[] textures;
    readonly List<TextureViewHandle> views;

    bool disposed;

    internal TextureBake(
        IGraphicsDevice device,
        TexturePlan plan,
        TexturePoolSchedule schedule,
        TextureHandle[] textures,
        List<TextureViewHandle> views
    ) {
        this.device = device;
        this.textures = textures;
        this.views = views;
        Plan = plan;
        Schedule = schedule;
    }

    /// <summary>What was evaluated.</summary>
    public TexturePlan Plan { get; }

    /// <summary>Where each image ended up.</summary>
    public TexturePoolSchedule Schedule { get; }

    /// <summary>How many dispatches it took — one per op, and a way to see that none was skipped.</summary>
    public int Dispatches { get; internal set; }

    /// <summary>The texture one image is in.</summary>
    /// <param name="image">Its index in <see cref="TexturePlan.Images" />.</param>
    /// <returns>The handle, or an invalid one for an image the caller supplied.</returns>
    public TextureHandle TextureOf(int image) {
        var slot = Schedule.SlotOf[image];

        return slot < 0 ? default : textures[slot];
    }

    /// <summary>Reads one image back as eight-bit RGBA.</summary>
    /// <param name="image">Its index in <see cref="TexturePlan.Images" />.</param>
    /// <returns>The picture, top row first.</returns>
    /// <exception cref="ArgumentException">The image is one the caller supplied, so this bake has no copy of it.</exception>
    /// <exception cref="ObjectDisposedException">The bake has been disposed.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is an encoder and not a second implementation of anything.</b> Doc 48 § D3
    ///         forbids a CPU twin of a kernel, and the reason is that a parity test against one proves
    ///         the two transcriptions agree rather than that either is right. Turning half-floats into
    ///         bytes on the way to a file is not a kernel — nothing in the graph does it, and there is
    ///         nothing for it to disagree with.
    ///     </para>
    ///     <para>
    ///         A single-channel image comes back as grey with an opaque alpha rather than as red,
    ///         because what is being looked at is a mask and a red mask is unreadable.
    ///     </para>
    /// </remarks>
    public Core.Imaging.Bitmap Read(int image) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var texture = TextureOf(image);

        if (!texture.IsValid) {
            throw new ArgumentException(
                $"Image {image} is supplied by the caller, so this bake does not hold it.",
                nameof(image)
            );
        }

        var size = Plan.SizeOf(image);
        var format = Plan.Images[image].Format;
        var length = size.X * size.Y * TextureFormats.BytesPerTexel(format);

        var readback = device.CreateBuffer(
            new(length, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "texture graph readback")
        );

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "texture graph readback")) {
            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(texture), new(size.X, size.Y, 1), readback, 0);

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.CopySource, ResourceState.ShaderRead)])
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var raw = new byte[length];

        device.Read(readback, 0, raw);
        device.Destroy(readback);

        return TexturePixels.ToBitmap(raw, size.X, size.Y, format);
    }

    /// <summary>Writes one image to a PNG.</summary>
    /// <param name="image">Its index in <see cref="TexturePlan.Images" />.</param>
    /// <param name="path">Where to write it.</param>
    public void Save(int image, string path) => Core.Imaging.PngCodec.Save(path, Read(image));

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        device.WaitIdle();

        foreach (var view in views) {
            if (view.IsValid) {
                device.Destroy(view);
            }
        }

        foreach (var texture in textures) {
            if (texture.IsValid) {
                device.Destroy(texture);
            }
        }

        views.Clear();
    }
}

/// <summary>Runs a <see cref="TexturePlan" /> on a device.</summary>
/// <remarks>
///     <para>
///         <b>The whole of doc 48 § D3's evaluator, and there is exactly one of it.</b> A graph
///         compiles to a plan and so does a layer stack, so both front ends reach a picture through
///         this and neither can develop an opinion of its own about what a blend mode means.
///     </para>
///     <para>
///         ⚠ <b>An evaluation is not a frame.</b> It opens one, submits every op's dispatch in a
///         single command list and waits for the device — so it must not be called between a caller's
///         own <c>BeginFrame</c> and <c>EndFrame</c>. A bake is a modal operation an artist starts;
///         the interactive per-node preview of doc 48 § M4 is a different caller with a different
///         budget, and it will want the recording half of this split out rather than this method
///         called sixty times a second.
///     </para>
///     <para>
///         <b>Variants are compiled once and kept.</b> The cache is keyed on the kernel and the
///         format it writes, which is the only thing that changes between two uses of one kernel —
///         everything else an op varies is a uniform. <see cref="Compilations" /> is public because
///         "a plan of forty ops compiles three shaders" is a claim about that number and nothing
///         else.
///     </para>
/// </remarks>
public sealed class TexturePlanEvaluator : IDisposable {
    /// <summary>The workgroup size every kernel in <c>Shaders/</c> declares.</summary>
    /// <remarks>
    ///     ⚠ <b>Duplicated from the <c>[ComputeShader(8, 8, 1)]</c> in each source, and a kernel that
    ///     disagreed would leave the tail of an image unwritten.</b> Raven puts the size on the stage
    ///     attribute so it cannot be separated from the stage; it does not put it in the reflection,
    ///     so a host still has to know it. <c>TextureKernelTests</c> is what asserts the two agree.
    /// </remarks>
    public const int GroupSize = 8;

    readonly IGraphicsDevice device;
    readonly EffectLoader loader;
    readonly Dictionary<(string Kernel, TextureFormat Output), Variant> variants = [];

    bool disposed;

    /// <summary>Builds an evaluator on a device.</summary>
    /// <param name="device">Where the images and the pipelines live.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public TexturePlanEvaluator(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        loader = new EffectLoader(device);
    }

    /// <summary>How many kernel variants have been compiled.</summary>
    public int Compilations { get; private set; }

    /// <summary>How many dispatches have been recorded, across every evaluation.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Evaluates a plan.</summary>
    /// <param name="plan">What to run.</param>
    /// <param name="externals">
    ///     The textures behind the plan's external images, by image index. Every external image an op
    ///     reads has to be in here, already in <see cref="ResourceState.ShaderRead" />.
    /// </param>
    /// <returns>The bake, which owns the textures until it is disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <exception cref="ArgumentException">The plan is unsound, or an external image was not supplied.</exception>
    /// <exception cref="ObjectDisposedException">The evaluator has been disposed.</exception>
    public TextureBake Evaluate(TexturePlan plan, IReadOnlyDictionary<int, TextureHandle>? externals = null) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(plan);

        var problems = plan.Validate();

        if (!problems.IsEmpty) {
            throw new ArgumentException(
                "This plan cannot be evaluated:" + Environment.NewLine + string.Join(Environment.NewLine, problems),
                nameof(plan)
            );
        }

        var schedule = TexturePoolSchedule.For(plan);
        var textures = new TextureHandle[schedule.Allocations];
        var slotViews = new TextureViewHandle[schedule.Allocations];
        List<TextureViewHandle> owned = [];

        for (var slot = 0; slot < schedule.Allocations; slot++) {
            var shape = schedule.Slots[slot];

            textures[slot] = device.CreateTexture(
                new(
                    TextureFormats.Pixel(shape.Format),
                    shape.Width,
                    shape.Height,
                    // Storage to be written by a kernel, Sampled to be read by the next one, and
                    // CopySource so a bake can become a file. All three at creation, because a
                    // backend wants the full set then and an image's role changes between ops.
                    TextureUsage.Storage | TextureUsage.Sampled | TextureUsage.CopySource,
                    Name: string.Create(CultureInfo.InvariantCulture, $"texture graph slot {slot}")
                )
            );

            slotViews[slot] = device.CreateTextureView(textures[slot]);
            owned.Add(slotViews[slot]);
        }

        var bake = new TextureBake(device, plan, schedule, textures, owned);

        try {
            Run(plan, schedule, bake, slotViews, ExternalViews(plan, externals, owned));
        } catch {
            bake.Dispose();

            throw;
        }

        return bake;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        device.WaitIdle();

        foreach (var variant in variants.Values) {
            device.Destroy(variant.Pipeline);
            device.Destroy(variant.Module);
        }

        variants.Clear();
    }

    /// <summary>A view onto each external image, made once and destroyed with the bake.</summary>
    Dictionary<int, TextureViewHandle> ExternalViews(
        TexturePlan plan,
        IReadOnlyDictionary<int, TextureHandle>? externals,
        List<TextureViewHandle> owned
    ) {
        Dictionary<int, TextureViewHandle> made = [];

        for (var image = 0; image < plan.Images.Length; image++) {
            if (!plan.Images[image].External) {
                continue;
            }

            if (externals is null || !externals.TryGetValue(image, out var supplied) || !supplied.IsValid) {
                throw new ArgumentException(
                    $"Image {image} is external and no texture was supplied for it. An external image is a bitmap "
                    + "input; a plan that has one cannot be evaluated without it.",
                    nameof(externals)
                );
            }

            var view = device.CreateTextureView(supplied);

            made[image] = view;
            owned.Add(view);
        }

        return made;
    }

    void Run(
        TexturePlan plan,
        TexturePoolSchedule schedule,
        TextureBake bake,
        TextureViewHandle[] slotViews,
        Dictionary<int, TextureViewHandle> externals
    ) {
        var state = new ResourceState[schedule.Allocations];
        List<BufferHandle> constants = [];
        List<DescriptorSetHandle> sets = [];

        Array.Fill(state, ResourceState.Undefined);

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Compute, "texture graph")) {
            for (var index = 0; index < plan.Ops.Length; index++) {
                var op = plan.Ops[index];
                var image = op.Output;
                var slot = schedule.SlotOf[image];
                var variant = VariantFor(op.Kernel, plan.Images[image].Format);

                sets.Add(Bind(plan, schedule, index, variant, slotViews, externals, constants));

                List<TextureBarrier> barriers = [];

                foreach (var input in op.Inputs) {
                    var from = schedule.SlotOf[input];

                    if (from >= 0 && state[from] != ResourceState.ShaderRead) {
                        barriers.Add(new(bake.TextureOf(input), state[from], ResourceState.ShaderRead));
                        state[from] = ResourceState.ShaderRead;
                    }
                }

                if (state[slot] != ResourceState.ShaderWrite) {
                    barriers.Add(new(bake.TextureOf(image), state[slot], ResourceState.ShaderWrite));
                    state[slot] = ResourceState.ShaderWrite;
                }

                if (barriers.Count > 0) {
                    commands.Barrier(new BarrierGroup([], [.. barriers]));
                }

                commands.BindPipeline(variant.Pipeline);
                commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, sets[^1]);

                var size = plan.SizeOf(image);

                commands.Dispatch(Groups(size.X), Groups(size.Y));

                Dispatches++;
                bake.Dispatches++;
            }

            // Everything the caller may look at ends readable, so a read-back does not have to know
            // which state the last op left each image in.
            List<TextureBarrier> settle = [];

            for (var image = 0; image < plan.Images.Length; image++) {
                var at = schedule.SlotOf[image];

                if (at >= 0 && state[at] == ResourceState.ShaderWrite) {
                    settle.Add(new(bake.TextureOf(image), ResourceState.ShaderWrite, ResourceState.ShaderRead));
                    state[at] = ResourceState.ShaderRead;
                }
            }

            if (settle.Count > 0) {
                commands.Barrier(new BarrierGroup([], [.. settle]));
            }

            commands.Finish();
            device.ComputeQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        foreach (var buffer in constants) {
            device.Destroy(buffer);
        }

        foreach (var set in sets) {
            device.Destroy(set);
        }
    }

    static int Groups(int extent) => (extent + GroupSize - 1) / GroupSize;

    /// <summary>Builds one op's descriptor set, and the uniform block behind it.</summary>
    DescriptorSetHandle Bind(
        TexturePlan plan,
        TexturePoolSchedule schedule,
        int index,
        Variant variant,
        TextureViewHandle[] slotViews,
        Dictionary<int, TextureViewHandle> externals,
        List<BufferHandle> constants
    ) {
        var op = plan.Ops[index];
        var set = device.CreateDescriptorSet(variant.SetLayout, $"texture graph {op.Kernel}");
        List<DescriptorWrite> writes = [];
        var textures = 0;

        foreach (var binding in variant.Effect.Bindings) {
            if (binding.Set != DescriptorSetSlot.PerMaterial) {
                continue;
            }

            switch (binding.Kind) {
                case DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer: {
                    var buffer = device.CreateBuffer(
                        new(binding.Size, BufferUsage.Uniform, MemoryAccess.HostUpload, $"{op.Kernel} constants")
                    );

                    device.Write(buffer, 0, Uniforms(plan, index, variant, binding.Size));
                    constants.Add(buffer);
                    writes.Add(DescriptorWrite.Uniform(binding.Binding, buffer));

                    break;
                }

                case DescriptorKind.SampledTexture: {
                    if (textures >= op.Inputs.Length) {
                        throw new ArgumentException(
                            $"Op {index} runs '{op.Kernel}', which reads more images than the op's "
                            + $"{op.Inputs.Length}. Inputs are bound positionally, in the order the kernel "
                            + "declares its textures."
                        );
                    }

                    var input = op.Inputs[textures];

                    writes.Add(
                        DescriptorWrite.Texture(
                            binding.Binding,
                            plan.Images[input].External ? externals[input] : slotViews[schedule.SlotOf[input]]
                        )
                    );

                    textures++;

                    break;
                }

                case DescriptorKind.StorageTexture:
                    writes.Add(DescriptorWrite.StorageImage(binding.Binding, slotViews[schedule.SlotOf[op.Output]]));

                    break;

                default:
                    throw new ArgumentException(
                        $"'{op.Kernel}' declares '{binding.Name}', which is a {binding.Kind}. A texture-graph "
                        + "kernel binds a uniform block, its input textures and one storage image, and nothing else."
                    );
            }
        }

        if (textures != op.Inputs.Length) {
            throw new ArgumentException(
                $"Op {index} names {op.Inputs.Length} input image(s) and '{op.Kernel}' reads {textures}."
            );
        }

        device.UpdateDescriptorSet(set, [.. writes]);

        return set;
    }

    /// <summary>The bytes of one op's uniform block, written member by member under the kernel's names.</summary>
    /// <remarks>
    ///     ⚠ <b>A member the op does not name is a refusal rather than a zero.</b> Zero is a valid
    ///     number for almost every parameter a kernel has — an opacity of zero is a no-op, an input
    ///     white of zero is a flat image — so a plan that forgot one would produce a picture that is
    ///     wrong in a way nothing points at. <c>seed</c> is the exception, because the evaluator is
    ///     what supplies it.
    /// </remarks>
    static byte[] Uniforms(TexturePlan plan, int index, Variant variant, int size) {
        var bytes = new byte[size];
        var op = plan.Ops[index];

        foreach (var member in variant.Data.Parameters) {
            if (member.Set != DescriptorSetSlot.PerMaterial) {
                continue;
            }

            // ⚠ Raven qualifies a block member with the shader that declared it — `Levels.gamma` —
            // because a composed pass holds several features' blocks at once and two of them may both
            // call something `amount`. A kernel is one shader, so an op names the parameter the way
            // the `.rvn` spells it and the qualifier comes off here. Matching on the qualified name
            // instead would mean every plan carried the kernel's name inside every parameter's.
            var name = Unqualified(member.Name, variant.Data.ShaderName);

            float value;

            if (op.Find(name) is { } parameter) {
                value = plan.Resolve(index, parameter);
            } else if (string.Equals(name, "seed", StringComparison.Ordinal)) {
                // ⚠ Twenty-four bits of the op's hashed seed, because the shader takes it as a float
                // and a float carries no more than that exactly. What a kernel needs of a seed is
                // that two ops disagree; the hashing itself is `TexturePlan.SeedFor`'s, on the CPU,
                // where it is the same on every machine and every run.
                value = plan.SeedFor(index) & 0xFFFFFF;
            } else {
                throw new ArgumentException(
                    $"Op {index} runs '{op.Kernel}', which declares '{name}', and the op does not carry it. "
                    + "A parameter left out would be written as zero, which is a valid-looking number for almost "
                    + "every one of them."
                );
            }

            Write(bytes.AsSpan(member.Offset, member.Size), member.Kind, value);
        }

        return bytes;
    }

    /// <summary>A block member's name without the shader that qualified it.</summary>
    static string Unqualified(string name, string shader) =>
        name.Length > shader.Length + 1
        && name.StartsWith(shader, StringComparison.Ordinal)
        && name[shader.Length] == '.'
            ? name[(shader.Length + 1)..]
            : name;

    static void Write(Span<byte> destination, ShaderValueKind kind, float value) {
        switch (kind) {
            case ShaderValueKind.Int or ShaderValueKind.Bool:
                BitConverter.TryWriteBytes(destination, (int)value);

                break;

            case ShaderValueKind.UInt:
                BitConverter.TryWriteBytes(destination, (uint)value);

                break;

            default:
                BitConverter.TryWriteBytes(destination, value);

                break;
        }
    }

    Variant VariantFor(string kernel, TextureFormat output) {
        if (variants.TryGetValue((kernel, output), out var existing)) {
            return existing;
        }

        var name = TextureKernels.VariantName(kernel, output);
        var source = TextureKernels.Variant(kernel, output);

        var data = RavenEffectCompiler.FromSources([(name, source)]).TryGet(EffectKey.Of(kernel))
            ?? throw new ArgumentException(
                $"'{name}' compiled and declares no shader called '{kernel}'. A kernel's file name is its shader "
                + "name, because an op names the shader.",
                nameof(kernel)
            );

        var effect = loader.Load(data);
        var stage = default(EffectStage);

        foreach (var compiled in effect.Stages) {
            if (compiled.Stage == ShaderStage.Compute) {
                stage = compiled;
            }
        }

        if (stage.Bytecode.IsDefaultOrEmpty) {
            throw new ArgumentException(
                $"'{kernel}' emitted no compute stage. A texture-graph kernel is one [ComputeShader] entry point.",
                nameof(kernel)
            );
        }

        var module = device.CreateShader(ShaderStage.Compute, stage.Bytecode.AsSpan(), name);

        var variant = new Variant {
            Data = data,
            Effect = effect,
            Module = module,
            Pipeline = device.CreateComputePipeline(new(module, effect.Layout, name)),
            SetLayout = effect.SetLayouts[(int)DescriptorSetSlot.PerMaterial]
        };

        variants[(kernel, output)] = variant;
        Compilations++;

        return variant;
    }

    sealed class Variant {
        public required EffectData Data { get; init; }
        public required Effect Effect { get; init; }
        public required ShaderHandle Module { get; init; }
        public required PipelineHandle Pipeline { get; init; }
        public required DescriptorSetLayoutHandle SetLayout { get; init; }
    }
}
