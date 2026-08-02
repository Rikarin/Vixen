// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>Where a draw's instance data is, for a feature that supplies it.</summary>
/// <remarks>
///     A separate interface from <see cref="IDrawSubFeature" /> because this does not <em>record</em>
///     anything — it changes two arguments of the draw call the mesh feature was going to make
///     anyway. A sub-feature that recorded its own draw would draw the geometry twice.
/// </remarks>
public interface IInstanceSource {
    /// <summary>How many instances of an object to draw, or zero to leave the draw alone.</summary>
    int InstanceCountOf(RenderSystem system, RenderObjectId id);

    /// <summary>The first instance index, which is where this object's transforms start.</summary>
    int FirstInstanceOf(RenderSystem system, RenderObjectId id);
}

/// <summary>
///     GPU instancing: many copies of one mesh from one draw call.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The instance offset is a draw-call argument, not a binding.</strong> One storage
///         buffer holds every instanced object's transforms back to back and is bound once for the
///         frame; a draw reaches its own run through <c>firstInstance</c>, which Vulkan adds into
///         <c>gl_InstanceIndex</c> before the shader ever sees it. So there is no descriptor per
///         object, no dynamic offset, no alignment to round up to, and no fixed maximum instance
///         count — three things the equivalent uniform-block design would all have needed.
///     </para>
///     <para>
///         <strong>The bounds must cover every instance.</strong> A forest drawn as one object with
///         ten thousand transforms is culled as one object, so its <see cref="RenderObject.Bounds" />
///         has to enclose the whole forest — which also means it is all-or-nothing. Batching by
///         locality rather than by mesh is what keeps that from being a regression, and it is the
///         caller's decision because only the caller knows the scene's shape.
///     </para>
///     <para>
///         The instanced variant is a permutation rather than a branch, because the two read a vertex
///         differently: the instanced one takes its world matrix from the buffer and the ordinary one
///         from the push constant. A runtime branch would make every non-instanced draw carry the
///         binding as well.
///     </para>
/// </remarks>
public sealed class InstancingRenderFeature
    : SubRenderFeature, IPermutationSubFeature, IInstanceSource, IDisposable {
    readonly UploadBuffer<Matrix4x4> transforms = new("Instancing.Transforms");
    readonly UploadBuffer<InstanceParameters> parameters = new("Instancing.Parameters");
    readonly List<PermutationKey<bool>> keys;
    bool disposed;

    /// <summary>Creates the feature, interning its permutation keys.</summary>
    public InstancingRenderFeature() =>
        keys = [
            ParameterKeys.NewPermutation(false, "Vixen.Instanced"),
            ParameterKeys.NewPermutation(false, "Vixen.InstanceParameters")
        ];

    /// <inheritdoc />
    public override string Name => "Instancing";

    /// <summary>Where each object's instance transforms start, and how many there are.</summary>
    public RenderDataKey<InstanceBatch> Batches { get; private set; }

    /// <summary>The device the buffers live on. Set before the first frame that prepares.</summary>
    public IGraphicsDevice? Device {
        get => transforms.Device;
        set {
            transforms.Device = value;
            parameters.Device = value;
        }
    }

    /// <summary>The buffer every batch lives in, for a host binding it once a frame.</summary>
    public BufferHandle Buffer => transforms.Buffer;

    /// <summary>The parallel buffer of <see cref="InstanceParameters" />, bound the same way.</summary>
    /// <remarks>
    ///     Empty for a frame in which no batch supplied any, in which case nothing samples it because
    ///     no object took the <c>Vixen.InstanceParameters</c> permutation.
    /// </remarks>
    public BufferHandle ParameterBuffer => parameters.Buffer;

    /// <summary>The byte offset this frame's parameters start at.</summary>
    public long ParameterBufferOffset => parameters.Offset;

    /// <summary>How many per-instance parameter records this frame holds.</summary>
    public int ParameterCount => parameters.Count;

    /// <summary>This frame's instance transforms, in the order they were added.</summary>
    /// <remarks>
    ///     The staged copy, before it reaches the device. What a CPU-side instance cull reads — see
    ///     [docs/plan/31 § B2] — and what a debug view lists. Always the same length as
    ///     <see cref="Parameters" />.
    /// </remarks>
    public ReadOnlySpan<Matrix4x4> Transforms => transforms.Items;

    /// <summary>This frame's per-instance parameters, at the same indices as
    /// <see cref="Transforms" />.</summary>
    public ReadOnlySpan<InstanceParameters> Parameters => parameters.Items;

    /// <summary>The byte offset this frame's transforms start at. Bind the buffer here, not at zero.</summary>
    /// <remarks>
    ///     The buffer holds one region per frame in flight, so a binding at zero reads whichever
    ///     region another frame is writing. See <see cref="UploadBuffer{T}.Offset" />.
    /// </remarks>
    public long BufferOffset => transforms.Offset;

    /// <summary>How many instance transforms this frame holds.</summary>
    public int TransformCount => transforms.Count;

    /// <inheritdoc />
    public IReadOnlyList<PermutationKey<bool>> PermutationKeys => keys;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         A batch of one is not instanced. It would draw identically either way, and giving it
    ///         the instanced variant would compile a second pipeline to draw one mesh — the cache
    ///         split that an automatic "is it instanced" check exists to avoid.
    ///     </para>
    ///     <para>
    ///         The parameter flag is narrower still: a batch that supplied none takes the variant
    ///         that never binds the second buffer, so an ordinary crate field pays no sampling for a
    ///         wind phase it does not have. It also implies the first flag, because parameters are
    ///         addressed by the instance index and a batch of one has no instance index worth the
    ///         name.
    ///     </para>
    /// </remarks>
    public bool ValueOf(RenderSystem system, RenderObjectId id, int index) {
        ArgumentNullException.ThrowIfNull(system);

        ref readonly var batch = ref system.Objects.Data.Data(Batches)[id.Index];

        return index switch {
            0 => batch.Count > 1,
            1 => batch is { Count: > 1, HasParameters: true },
            _ => false
        };
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        Batches = system.Objects.Data.Register<InstanceBatch>();
    }

    /// <summary>Starts a frame's batches. Call before the first <c>SetInstances</c>.</summary>
    public void Begin() {
        transforms.Begin();
        parameters.Begin();
    }

    /// <summary>Gives an object its instance transforms for this frame.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="instances">One object-to-world matrix per instance.</param>
    /// <remarks>
    ///     The parameter buffer still advances, by <see cref="InstanceParameters.Neutral" /> per
    ///     instance, because the two buffers are addressed by one index — see
    ///     <see cref="InstanceParameters" />. The object does not take the parameter permutation, so
    ///     nothing reads what was written.
    /// </remarks>
    public void SetInstances(RenderSystem system, RenderObjectId id, ReadOnlySpan<Matrix4x4> instances) {
        ArgumentNullException.ThrowIfNull(system);

        var first = transforms.Add(instances);
        AddNeutral(instances.Length);
        system.Objects.Data.Data(Batches)[id.Index] = new(first, instances.Length);
    }

    /// <summary>Gives an object its instance transforms and their per-instance parameters.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="instances">One object-to-world matrix per instance.</param>
    /// <param name="instanceParameters">
    ///     One <see cref="InstanceParameters" /> per instance, in the same order.
    /// </param>
    /// <exception cref="ArgumentException">The two spans are different lengths.</exception>
    /// <remarks>
    ///     The lengths are checked rather than zipped to the shorter, because a caller who built the
    ///     two from different loops has a bug whose symptom is one tree at the end of a forest with
    ///     another tree's wind — and the frame after that, a different tree.
    /// </remarks>
    public void SetInstances(
        RenderSystem system,
        RenderObjectId id,
        ReadOnlySpan<Matrix4x4> instances,
        ReadOnlySpan<InstanceParameters> instanceParameters
    ) {
        ArgumentNullException.ThrowIfNull(system);

        if (instances.Length != instanceParameters.Length) {
            throw new ArgumentException(
                $"An instance batch of {instances.Length} transforms needs {instances.Length} "
                + $"parameters, not {instanceParameters.Length}.",
                nameof(instanceParameters)
            );
        }

        var first = transforms.Add(instances);
        var parameterFirst = parameters.Add(instanceParameters);

        // The invariant the shader depends on, asserted where it is established rather than trusted
        // at the far end of a compute dispatch.
        if (first != parameterFirst) {
            throw new InvalidOperationException(
                $"The instance buffers drifted: transform {first} against parameter {parameterFirst}. "
                + "Every batch must write both, which is what SetInstances' other overload is for."
            );
        }

        system.Objects.Data.Data(Batches)[id.Index] = new(first, instances.Length, true);
    }

    void AddNeutral(int count) {
        if (count == 0) {
            return;
        }

        var neutral = InstanceParameters.Neutral;
        Span<InstanceParameters> run = stackalloc InstanceParameters[Math.Min(count, 256)];
        run.Fill(neutral);

        for (var written = 0; written < count; written += run.Length) {
            parameters.Add(run[..Math.Min(run.Length, count - written)]);
        }
    }

    /// <inheritdoc />
    protected internal override void Prepare(RenderSystem system) {
        transforms.Upload();
        parameters.Upload();
    }

    /// <inheritdoc />
    public int InstanceCountOf(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);
        return system.Objects.Data.Data(Batches)[id.Index].Count;
    }

    /// <inheritdoc />
    public int FirstInstanceOf(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);
        return system.Objects.Data.Data(Batches)[id.Index].First;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        transforms.Dispose();
        parameters.Dispose();
    }
}

/// <summary>Where one object's instance transforms are in the shared buffer.</summary>
/// <param name="First">The index of its first transform, which the draw passes as
/// <c>firstInstance</c>.</param>
/// <param name="Count">How many instances. Zero leaves the object's own draw alone.</param>
/// <param name="HasParameters">
///     Whether the batch also wrote <see cref="InstanceParameters" />. They live at the same indices
///     in a parallel buffer, so there is no second offset to carry.
/// </param>
public readonly record struct InstanceBatch(int First, int Count, bool HasParameters = false);

/// <summary>The four per-instance numbers that are not the transform.</summary>
/// <remarks>
///     <para>
///         <b>One vector, and the four things in it were chosen because a forest needs all four and
///         nothing else.</b> A field of instances that share a mesh, a material and a transform layout
///         still has to not look stamped, and the ways it stops looking stamped are: tint each copy a
///         little differently, start each copy's wind at a different phase, grow them to different
///         sizes, and fade the ones crossing a level boundary. See [docs/plan/31 § B3].
///     </para>
///     <para>
///         <b>A parallel buffer rather than an interleaved field — and parallel is meant
///         literally.</b> An instance's parameters sit at the same index as its transform, so a
///         shader reaches them with the <c>gl_InstanceIndex</c> it already has and no second offset
///         travels in the draw. Keeping that true is why
///         <see cref="InstancingRenderFeature.SetInstances(RenderSystem, RenderObjectId, ReadOnlySpan{Matrix4x4})" />
///         writes <see cref="Neutral" /> for a batch that supplied none rather than writing nothing:
///         two buffers that could drift apart would need a per-draw delta to reconcile them, and the
///         one frame they drifted would draw every forest with another tree's wind.
///     </para>
///     <para>
///         That costs sixteen bytes per instance whether the instance uses them or not — 25 % on top
///         of the transform's sixty-four. Against this feature's actual callers, which are trees and
///         props in the tens of thousands rather than the grass in [docs/plan/31 § T6] that scatters
///         into a buffer of its own, it is under a megabyte.
///     </para>
///     <para>
///         The half of the separation that does pay is binding: a pass wanting only positions binds
///         one buffer, so a shadow cascade and a depth prepass never read this at all.
///     </para>
///     <para>
///         ⚠ <b>A default-constructed value is not neutral, and that is deliberate.</b> Neutral is
///         <see cref="Neutral" /> — fade 1, scale 1 — because a zero fade is invisible and a zero
///         scale is a point. A batch that forgot to fill this draws nothing rather than drawing
///         something subtly wrong, which is the failure that gets noticed. This is
///         [docs/memory zero-value traps] as a design rule rather than a lesson.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct InstanceParameters {
    /// <summary>A per-instance variation the material maps how it likes. Conventionally 0…1.</summary>
    public float Tint;

    /// <summary>
    ///     A phase offset in radians, added to whatever drives the instance's motion.
    /// </summary>
    /// <remarks>
    ///     What stops a field of grass moving as one object. <c>Displacement.Wind</c> takes its phase
    ///     from world position, which decorrelates instances that are far apart and does nothing for
    ///     two that are ten centimetres apart — which is every pair of blades in a clump.
    /// </remarks>
    public float WindPhase;

    /// <summary>A per-instance scale, multiplying the transform's own. 1 is unchanged.</summary>
    /// <remarks>
    ///     Here rather than baked into the matrix so that a growth simulation, a wind gust or a
    ///     density scalar can change it without rewriting a transform the placement owns — and so
    ///     that the CPU-side instance record stays the thing an artist placed.
    /// </remarks>
    public float Scale;

    /// <summary>The LOD cross-fade weight, 0…1. 1 is fully present.</summary>
    /// <remarks>
    ///     Read by the same dithered discard <c>LodRenderFeature</c>'s cross-fade already uses, which
    ///     is why it is a weight rather than an alpha: two dithered levels whose weights sum to one
    ///     tile the silhouette exactly once, and two translucent ones would sort against each other.
    /// </remarks>
    public float Fade;

    /// <summary>The value that changes nothing: no tint, no phase, full size, fully present.</summary>
    public static InstanceParameters Neutral => new() { Tint = 0f, WindPhase = 0f, Scale = 1f, Fade = 1f };

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<InstanceParameters>();
}
