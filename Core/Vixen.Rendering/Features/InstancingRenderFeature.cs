// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
    readonly MatrixBuffer transforms = new("Instancing.Transforms");
    readonly List<PermutationKey<bool>> keys;
    bool disposed;

    /// <summary>Creates the feature, interning its permutation key.</summary>
    public InstancingRenderFeature() => keys = [ParameterKeys.NewPermutation(false, "Vixen.Instanced")];

    /// <inheritdoc />
    public override string Name => "Instancing";

    /// <summary>Where each object's instance transforms start, and how many there are.</summary>
    public RenderDataKey<InstanceBatch> Batches { get; private set; }

    /// <summary>The device the transform buffer lives on. Set before the first frame that prepares.</summary>
    public IGraphicsDevice? Device {
        get => transforms.Device;
        set => transforms.Device = value;
    }

    /// <summary>The buffer every batch lives in, for a host binding it once a frame.</summary>
    public BufferHandle Buffer => transforms.Buffer;

    /// <summary>How many instance transforms this frame holds.</summary>
    public int TransformCount => transforms.Count;

    /// <inheritdoc />
    public IReadOnlyList<PermutationKey<bool>> PermutationKeys => keys;

    /// <inheritdoc />
    /// <remarks>
    ///     A batch of one is not instanced. It would draw identically either way, and giving it the
    ///     instanced variant would compile a second pipeline to draw one mesh — the cache split that
    ///     an automatic "is it instanced" check exists to avoid.
    /// </remarks>
    public bool ValueOf(RenderSystem system, RenderObjectId id, int index) {
        ArgumentNullException.ThrowIfNull(system);
        return system.Objects.Data.Data(Batches)[id.Index].Count > 1;
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        Batches = system.Objects.Data.Register<InstanceBatch>();
    }

    /// <summary>Starts a frame's batches. Call before the first <see cref="SetInstances" />.</summary>
    public void Begin() => transforms.Begin();

    /// <summary>Gives an object its instance transforms for this frame.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="instances">One object-to-world matrix per instance.</param>
    public void SetInstances(RenderSystem system, RenderObjectId id, ReadOnlySpan<Matrix4x4> instances) {
        ArgumentNullException.ThrowIfNull(system);

        var first = transforms.Add(instances);
        system.Objects.Data.Data(Batches)[id.Index] = new(first, instances.Length);
    }

    /// <inheritdoc />
    protected internal override void Prepare(RenderSystem system) => transforms.Upload();

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
    }
}

/// <summary>Where one object's instance transforms are in the shared buffer.</summary>
/// <param name="First">The index of its first transform, which the draw passes as
/// <c>firstInstance</c>.</param>
/// <param name="Count">How many instances. Zero leaves the object's own draw alone.</param>
public readonly record struct InstanceBatch(int First, int Count);
