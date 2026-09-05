// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
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
        ICommandSubmitter queue,
        TexturePlan plan,
        TexturePoolSchedule schedule,
        TextureHandle[] textures,
        List<TextureViewHandle> views
    ) {
        this.device = device;
        this.textures = textures;
        this.views = views;
        Queue = queue;
        Plan = plan;
        Schedule = schedule;
    }

    /// <summary>The queue the evaluation ran on, and the only one this bake's textures are touched from.</summary>
    /// <remarks>
    ///     ⚠ <b>The submitter itself rather than a <see cref="QueueKind" />, so that the queue a
    ///     read-back records on and the queue it submits to cannot drift apart.</b> That is exactly
    ///     how <a href="https://github.com/Rikarin/Vixen/issues/617">#617</a> happened: the dispatches
    ///     named <see cref="QueueKind.Compute" /> in one method and the read-back named
    ///     <see cref="QueueKind.Graphics" /> in another, and on every adapter this engine has been
    ///     developed on the two are one family, so nothing anywhere said so. There is one object here
    ///     now, and <see cref="Read" /> takes both its list kind and its submission from it.
    /// </remarks>
    public ICommandSubmitter Queue { get; }

    /// <summary>What was evaluated.</summary>
    public TexturePlan Plan { get; }

    /// <summary>What this bake drew differently from the graph, and drew anyway.</summary>
    /// <remarks>
    ///     ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/692">#692</a>: the middle state
    ///     between "fine" and "refused", and the case that needed it is a clipped radius.</b> A blur
    ///     authored at 20 texels on a 1K graph resolves to 80 at a 4× bake and the kernel loops to
    ///     64 — so the bake succeeds and is a different material, which before this was silent
    ///     everywhere. A caller showing a bake to an artist shows these beside the resolution they
    ///     chose, which is the decision that caused it.
    /// </remarks>
    public ImmutableArray<string> Warnings { get; internal set; } = [];

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
    ///     <para>
    ///         ⚠ <b>The copy is recorded on <see cref="Queue" /> — the queue the bake wrote on — and
    ///         that is a correctness requirement rather than a tidiness one.</b> See
    ///         <see cref="TexturePlanEvaluator" />: every texture here is
    ///         <c>ResourceSharing.Exclusive</c>, and reading one from a second queue family without an
    ///         ownership transfer is undefined. The list kind comes from the same object as the
    ///         submission, so a copy recorded for one queue can never be submitted to another.
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

        using (var commands = device.BeginCommandList(Queue.Kind, "texture graph readback")) {
            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(texture), new(size.X, size.Y, 1), readback, 0);

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.CopySource, ResourceState.ShaderRead)])
            );

            commands.Finish();
            Queue.Submit([commands]);
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
///         ⚠ <b>Every command list a bake records — the dispatches, and every later read-back — goes
///         to <see cref="IGraphicsDevice.ComputeQueue" />, recorded for that submitter's own
///         <see cref="ICommandSubmitter.Kind" />, and that is a correctness requirement rather than a
///         preference.</b> (On a unified adapter that kind <em>is</em>
///         <see cref="QueueKind.Graphics" />, because the backend collapses a queue sharing the
///         graphics family — which is precisely why the defect below was invisible here.) The pool's
///         textures are created with
///         <c>TextureDescription.Sharing</c> at its default, <c>ResourceSharing.Exclusive</c>, so on
///         an adapter whose <c>QueueFamilySelection</c> found a compute family of its own — a discrete
///         AMD or NVIDIA card — touching one of them from a second family without a queue-family
///         ownership transfer leaves its contents <b>undefined by specification</b>. The validation
///         layers say nothing, because it is undefined behaviour and not invalid usage, and
///         <c>VulkanBarriers.cs</c> records in as many words that a separate compute family is no
///         device this engine has been developed on — so this would have been a corrupt bake on
///         somebody else's machine and a clean run on every machine here.
///     </para>
///     <para>
///         <b>One queue rather than the ownership-transfer pair, and a future reader should not
///         "optimise" it back.</b> A transfer is two barriers with identical parameters plus a
///         semaphore edge between the submissions (<c>TextureBarrier.TransfersOwnership</c>) — and the
///         release half would have to be recorded at the end of the bake's own list, for every image,
///         before anybody knows which ones will ever be read, how often, or whether at all. An image
///         released to a queue that never acquires it is exactly the corruption that pair exists to
///         prevent. There is also nothing to buy: a bake is modal, <see cref="Evaluate" /> waits for
///         the device before it returns, and the read-back has no frame to overlap with. The
///         precedent is <c>Platform/Vixen.Raven.Gpu.Tests/ShaderRun.cs</c>, which dispatches and
///         copies on one compute list for this same reason.
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

    /// <summary>Evaluates a plan whose external images are only ever sampled.</summary>
    /// <param name="plan">What to run.</param>
    /// <param name="externals">
    ///     The textures behind the plan's external images, by image index. Every external image an op
    ///     reads has to be in here, created with <see cref="TextureUsage.Sampled" /> and already in
    ///     <see cref="ResourceState.ShaderRead" />.
    /// </param>
    /// <returns>The bake, which owns the textures until it is disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <exception cref="ArgumentException">The plan is unsound, or an external image was not supplied.</exception>
    /// <exception cref="ObjectDisposedException">The evaluator has been disposed.</exception>
    /// <remarks>
    ///     ⚠ <b>A bare handle declares <see cref="TextureUsage.Sampled" /> and nothing else</b>, which
    ///     is the whole of what a dispatch needs and is <em>not</em> enough for a
    ///     <see cref="TextureOp.Cpu" /> op, which copies out of the image it reads. A plan with one of
    ///     those over an external image is refused here rather than run — pass
    ///     <see cref="Evaluate(TexturePlan, IReadOnlyDictionary{int, TextureExternal})" /> and say what
    ///     the texture was created with.
    /// </remarks>
    public TextureBake Evaluate(TexturePlan plan, IReadOnlyDictionary<int, TextureHandle>? externals = null) =>
        Evaluate(
            plan,
            externals?.ToDictionary(
                supplied => supplied.Key,
                supplied => new TextureExternal(supplied.Value, TextureExternal.Sampled)
            ) ?? []
        );

    /// <summary>Evaluates a plan, with the caller declaring what its own textures can be used for.</summary>
    /// <param name="plan">What to run.</param>
    /// <param name="externals">
    ///     The textures behind the plan's external images, by image index, each with the
    ///     <see cref="TextureUsage" /> it was created with. Every external image an op reads has to be
    ///     in here, already in <see cref="ResourceState.ShaderRead" />.
    /// </param>
    /// <returns>The bake, which owns the textures until it is disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The plan is unsound, an external image was not supplied, or one was supplied without the
    ///     usage the plan needs from it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The evaluator has been disposed.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What the plan needs from a caller's texture, and where it comes from.</b> Every
    ///         external image a dispatch reads is bound as a sampled image, so it needs
    ///         <see cref="TextureUsage.Sampled" />; an external image a <see cref="TextureOp.Cpu" /> op
    ///         reads is <em>copied out of</em>, so it needs <see cref="TextureUsage.CopySource" /> as
    ///         well. The second is the one that gets forgotten, because the pooled textures this class
    ///         creates for itself have always had it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The declaration is checked against the plan, not against the texture</b> — see
    ///         <see cref="TextureExternal" />. Nothing in <see cref="IGraphicsDevice" /> can describe a
    ///         handle back, so a caller who declares a usage the image does not have gets the
    ///         undefined behaviour they would have got anyway. What this stops is the caller who
    ///         forgot, which is <a href="https://github.com/Rikarin/Vixen/issues/722">#722</a> and the
    ///         two before it.
    ///     </para>
    /// </remarks>
    public TextureBake Evaluate(TexturePlan plan, IReadOnlyDictionary<int, TextureExternal> externals) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(externals);

        var problems = plan.Check();
        var refusals = problems
            .Where(problem => problem.Severity == TextureProblemSeverity.Error)
            .Select(problem => problem.Message)
            .ToArray();

        if (refusals.Length > 0) {
            throw new ArgumentException(
                "This plan cannot be evaluated:" + Environment.NewLine + string.Join(Environment.NewLine, refusals),
                nameof(plan)
            );
        }

        CheckExternalUsage(plan, externals);

        var handles = externals.ToDictionary(supplied => supplied.Key, supplied => supplied.Value.Texture);
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
                    // Storage to be written by a kernel, Sampled to be read by the next one,
                    // CopySource so a bake can become a file, and CopyDestination because a
                    // TextureOp.Cpu op is written by a buffer copy rather than by a dispatch. All
                    // four at creation, because a backend wants the full set then and an image's
                    // role changes between ops.
                    TextureUsage.Storage
                    | TextureUsage.Sampled
                    | TextureUsage.CopySource
                    | TextureUsage.CopyDestination,
                    Name: string.Create(CultureInfo.InvariantCulture, $"texture graph slot {slot}")
                )
            );

            slotViews[slot] = device.CreateTextureView(textures[slot]);
            owned.Add(slotViews[slot]);
        }

        var bake = new TextureBake(device, device.ComputeQueue, plan, schedule, textures, owned) {
            Warnings = [
                .. problems
                    .Where(problem => problem.Severity == TextureProblemSeverity.Warning)
                    .Select(problem => problem.Message)
            ]
        };

        try {
            Run(plan, schedule, bake, slotViews, ExternalViews(plan, handles, owned), handles);
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

    /// <summary>Refuses a caller's texture that was not created for what the plan does to it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The usage bits a Vulkan image was created with are part of what its commands are
    ///         allowed to be, and MoltenVK enforces none of them</b> — so this refusal is the whole of
    ///         the enforcement on the machine this is developed on.
    ///         <a href="https://github.com/Rikarin/Vixen/issues/722">#722</a>: the CPU-op seam copied
    ///         out of a caller's image for a whole batch with no <c>TRANSFER_SRC</c> on it, past a
    ///         device test, because a unified adapter reads it anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="TextureUsage.Sampled" /> is required of <em>every</em> external image
    ///         and not only of one a dispatch reads</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/745">#745</a>, which was this method's
    ///         own defect reproduced inside its fix. The first version computed the requirement from
    ///         what the plan does to each image, so an image only a <see cref="TextureOp.Cpu" /> op
    ///         reads was asked for <c>CopySource</c> alone — while <see cref="ExternalViews" /> makes
    ///         a view over every external unconditionally and <see cref="OnCpu" /> names
    ///         <c>SHADER_READ_ONLY_OPTIMAL</c> on both sides of its copy. The test that asserted the
    ///         permissive rule was legal ran on the Null device, which validates nothing.
    ///     </para>
    ///     <para>
    ///         <b>Measured on an Apple M1 Max with the validation layers on</b>, a CPU-op-only plan
    ///         over an image created <c>CopySource | CopyDestination</c> produced three errors and a
    ///         correct picture: <c>VUID-VkImageViewCreateInfo-image-04441</c> for the view, and
    ///         <c>VUID-VkImageMemoryBarrier-oldLayout-01211</c> twice for the barriers either side of
    ///         the copy. So the honest requirement is the one the evaluator's own behaviour states —
    ///         every external is viewed and held readable for the whole bake — and a
    ///         <see cref="TextureOp.Cpu" /> op adds <see cref="TextureUsage.CopySource" /> on top of
    ///         it. <c>TextureValidationDeviceTests</c> holds the layers' own word for the view half.
    ///     </para>
    /// </remarks>
    static void CheckExternalUsage(TexturePlan plan, IReadOnlyDictionary<int, TextureExternal> externals) {
        var required = new TextureUsage[plan.Images.Length];

        // The base requirement is a property of being external, because ExternalViews below loops
        // over exactly this predicate. Deriving both from `plan.Images[i].External` is what stops the
        // check and the thing it is checking from drifting apart, which is the whole of #745.
        for (var image = 0; image < plan.Images.Length; image++) {
            if (plan.Images[image].External) {
                required[image] = TextureExternal.Sampled;
            }
        }

        foreach (var op in plan.Ops) {
            if (op.Cpu is null) {
                continue;
            }

            foreach (var input in op.Inputs) {
                if (input < 0 || input >= plan.Images.Length || !plan.Images[input].External) {
                    continue;
                }

                required[input] |= TextureExternal.ReadBack;
            }
        }

        for (var image = 0; image < required.Length; image++) {
            if (required[image] == TextureUsage.None || !externals.TryGetValue(image, out var supplied)) {
                continue;
            }

            var missing = required[image] & ~supplied.Usage;

            if (missing == TextureUsage.None) {
                continue;
            }

            List<string> why = [];

            if ((missing & TextureExternal.Sampled) != TextureUsage.None) {
                why.Add(
                    "every external image is viewed and held in ShaderRead for the whole bake, and a "
                    + "Vulkan image may be neither without Sampled — VUID-VkImageViewCreateInfo-image-04441 "
                    + "for the view and VUID-VkImageMemoryBarrier-oldLayout-01211 for the layout"
                );
            }

            if ((missing & TextureExternal.ReadBack) != TextureUsage.None) {
                why.Add(
                    "it is read by a CPU op, which copies out of it and transitions it through "
                    + "CopySource either side of that copy — and a Vulkan image may only be either if it "
                    + "was created with TransferSrc"
                );
            }

            throw new ArgumentException(
                $"Image {image} is external and the texture supplied for it was declared "
                + $"{supplied.Usage}, which is missing {missing}: {string.Join("; and ", why)}. "
                + "⚠ A unified adapter does all of it anyway, so the wrong answer here belongs to a "
                + "discrete card and not to this machine.",
                nameof(externals)
            );
        }
    }

    /// <summary>A view onto each external image, made once and destroyed with the bake.</summary>
    /// <remarks>
    ///     ⚠ <b>Unconditional, which is why <see cref="CheckExternalUsage" /> asks every external for
    ///     <see cref="TextureUsage.Sampled" />.</b> A view over an image created for transfers alone
    ///     is invalid — VUID-VkImageViewCreateInfo-image-04441 — and this loop runs before any op
    ///     does, so "no dispatch reads it" is not a reason it does not happen.
    ///     <a href="https://github.com/Rikarin/Vixen/issues/745">#745</a>: the two were written from
    ///     different pictures of what the evaluator does, and only the strict one is true.
    /// </remarks>
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
        Dictionary<int, TextureViewHandle> externals,
        IReadOnlyDictionary<int, TextureHandle>? externalTextures
    ) {
        var state = new ResourceState[schedule.Allocations];
        List<BufferHandle> constants = [];
        List<BufferHandle> staging = [];
        List<DescriptorSetHandle> sets = [];

        Array.Fill(state, ResourceState.Undefined);

        device.BeginFrame();

        var commands = device.BeginCommandList(bake.Queue.Kind, "texture graph");

        try {
            for (var index = 0; index < plan.Ops.Length; index++) {
                var op = plan.Ops[index];
                var image = op.Output;
                var slot = schedule.SlotOf[image];

                if (op.Cpu is not null) {
                    commands = OnCpu(plan, schedule, bake, index, state, externalTextures, staging, commands);

                    continue;
                }

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
            // which state the last op left each image in. ⚠ Anything that is not already readable,
            // rather than only what a dispatch wrote: a CPU op leaves its inputs in CopySource and
            // its output in CopyDestination, and an image left in either would be transitioned from
            // ShaderRead by TextureBake.Read — a barrier whose source state is a lie.
            List<TextureBarrier> settle = [];

            for (var image = 0; image < plan.Images.Length; image++) {
                var at = schedule.SlotOf[image];

                if (at >= 0 && state[at] is not (ResourceState.ShaderRead or ResourceState.Undefined)) {
                    settle.Add(new(bake.TextureOf(image), state[at], ResourceState.ShaderRead));
                    state[at] = ResourceState.ShaderRead;
                }
            }

            if (settle.Count > 0) {
                commands.Barrier(new BarrierGroup([], [.. settle]));
            }

            commands.Finish();
            bake.Queue.Submit([commands]);
        } finally {
            commands.Dispose();
        }

        device.EndFrame();
        device.WaitIdle();

        foreach (var buffer in constants) {
            device.Destroy(buffer);
        }

        foreach (var buffer in staging) {
            device.Destroy(buffer);
        }

        foreach (var set in sets) {
            device.Destroy(set);
        }
    }

    /// <summary>Runs one <see cref="TextureOp.Cpu" /> op: read back, compute, upload.</summary>
    /// <returns>The command list the rest of the plan carries on recording into.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 48 § 4.6's exception to § D3, and the whole of what makes it an exception is
    ///         here.</b> A dispatch is appended to the list already in flight and costs nothing but
    ///         its own time; this closes that list, ends the frame, waits for the device, maps every
    ///         input into host memory, runs, writes the answer back and opens a new frame. Two full
    ///         drains and the bandwidth of every image involved — so a chain of these serialises the
    ///         bake, which is exactly the property that stops <see cref="ITextureCpuOperation" />
    ///         becoming the easy way to add a node.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A frame of its own rather than more lists inside one frame.</b> The read-back has
    ///         to complete before the bytes are mapped, and the pattern that is proved on this
    ///         repository's devices — <see cref="TextureBake.Read" /> — is begin, record, submit,
    ///         end, wait, map. Doing it inside the bake's frame would be a second arrangement of the
    ///         same four calls whose failure mode is reading whatever the allocator left.
    ///     </para>
    ///     <para>
    ///         <b>Both barriers are recorded and both directions matter.</b> The inputs go to
    ///         <see cref="ResourceState.CopySource" /> and the output to
    ///         <see cref="ResourceState.CopyDestination" />; the pooled ones are left in those states
    ///         for the ordinary per-op barrier loop to pick up, and an <em>external</em> image is put
    ///         back to <see cref="ResourceState.ShaderRead" /> here, because the plan's contract is
    ///         that the caller's textures arrive and stay readable and nothing tracks their state.
    ///     </para>
    /// </remarks>
    ICommandList OnCpu(
        TexturePlan plan,
        TexturePoolSchedule schedule,
        TextureBake bake,
        int index,
        ResourceState[] state,
        IReadOnlyDictionary<int, TextureHandle>? externalTextures,
        List<BufferHandle> staging,
        ICommandList commands
    ) {
        var op = plan.Ops[index];
        var cpu = op.Cpu!;
        var outputSlot = schedule.SlotOf[op.Output];
        var outputSize = plan.SizeOf(op.Output);
        var outputFormat = plan.Images[op.Output].Format;
        List<TextureBarrier> barriers = [];
        List<(int Image, BufferHandle Buffer)> reads = [];

        foreach (var input in op.Inputs) {
            var at = schedule.SlotOf[input];
            var from = at >= 0 ? state[at] : ResourceState.ShaderRead;

            if (from == ResourceState.CopySource) {
                continue;
            }

            barriers.Add(new(TextureFor(schedule, bake, externalTextures, input), from, ResourceState.CopySource));

            if (at >= 0) {
                state[at] = ResourceState.CopySource;
            }
        }

        if (state[outputSlot] != ResourceState.CopyDestination) {
            barriers.Add(new(bake.TextureOf(op.Output), state[outputSlot], ResourceState.CopyDestination));
            state[outputSlot] = ResourceState.CopyDestination;
        }

        if (barriers.Count > 0) {
            commands.Barrier(new BarrierGroup([], [.. barriers]));
        }

        foreach (var input in op.Inputs) {
            var size = plan.SizeOf(input);
            var bytes = size.X * size.Y * TextureFormats.BytesPerTexel(plan.Images[input].Format);
            var buffer = device.CreateBuffer(
                new(bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, $"{op.Kernel} read-back")
            );

            staging.Add(buffer);
            reads.Add((input, buffer));

            commands.CopyTextureToBuffer(
                new TextureRegion(TextureFor(schedule, bake, externalTextures, input)),
                new(size.X, size.Y, 1),
                buffer,
                0
            );
        }

        commands.Finish();
        bake.Queue.Submit([commands]);
        commands.Dispose();

        device.EndFrame();
        device.WaitIdle();

        var inputs = ImmutableArray.CreateBuilder<TextureCpuImage>(reads.Count);

        foreach (var (image, buffer) in reads) {
            var size = plan.SizeOf(image);
            var format = plan.Images[image].Format;
            var raw = new byte[size.X * size.Y * TextureFormats.BytesPerTexel(format)];

            device.Read(buffer, 0, raw);
            inputs.Add(new(format, size.X, size.Y, raw));
        }

        var produced = new byte[outputSize.X * outputSize.Y * TextureFormats.BytesPerTexel(outputFormat)];
        var output = new TextureCpuImage(outputFormat, outputSize.X, outputSize.Y, produced);

        cpu.Run(new(plan, index, inputs.ToImmutable(), output));

        var upload = device.CreateBuffer(
            new(produced.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{op.Kernel} upload")
        );

        staging.Add(upload);
        device.Write(upload, 0, produced);
        device.BeginFrame();

        var next = device.BeginCommandList(bake.Queue.Kind, "texture graph");

        next.CopyBufferToTexture(upload, 0, new(bake.TextureOf(op.Output)), new(outputSize.X, outputSize.Y, 1));

        // The caller's own textures are handed back the way they arrived: nothing tracks an external
        // image's state, so leaving one in CopySource would make the next op's read a lie.
        List<TextureBarrier> restore = [];

        foreach (var input in op.Inputs) {
            if (plan.Images[input].External) {
                restore.Add(
                    new(
                        TextureFor(schedule, bake, externalTextures, input),
                        ResourceState.CopySource,
                        ResourceState.ShaderRead
                    )
                );
            }
        }

        if (restore.Count > 0) {
            next.Barrier(new BarrierGroup([], [.. restore]));
        }

        return next;
    }

    /// <summary>The texture behind one image, whether the pool made it or the caller supplied it.</summary>
    static TextureHandle TextureFor(
        TexturePoolSchedule schedule,
        TextureBake bake,
        IReadOnlyDictionary<int, TextureHandle>? externals,
        int image
    ) =>
        schedule.SlotOf[image] >= 0
            ? bake.TextureOf(image)
            : externals?[image]
            ?? throw new ArgumentException(
                $"Image {image} is external and no texture was supplied for it.",
                nameof(externals)
            );

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
