// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>
///     GPU skinning: a bone palette per object, and the index that finds it.
/// </summary>
/// <remarks>
///     <para>
///         The sub-feature docs/plan/06 names as the proof that the arrangement extends — "adding
///         skinning does not modify <c>MeshRenderFeature</c>; it registers its own parallel array".
///         It does not, and this file is the whole of it.
///     </para>
///     <para>
///         <strong>The palette is skinning matrices, not bone transforms.</strong> What goes in is
///         <c>inverseBindPose * boneWorld</c>, already multiplied — one multiply per bone per frame
///         on the CPU instead of one per vertex on the GPU. A character has a hundred bones and tens
///         of thousands of vertices, so doing it the other way round is four orders of magnitude more
///         work for the same answer.
///     </para>
///     <para>
///         <strong>A push constant carries the base index, not a descriptor offset.</strong> One
///         storage buffer holds every skeleton's palette back to back and is bound once for the
///         frame; a draw says where its own palette starts. A dynamic offset would work and would
///         force every palette to be padded up to
///         <c>minStorageBufferOffsetAlignment</c> and to a fixed maximum bone count — where an index
///         costs four bytes of a push block that already exists for the transform.
///     </para>
/// </remarks>
public sealed class SkinningRenderFeature : SubRenderFeature, IDrawSubFeature, IPermutationSubFeature, IDisposable {
    readonly UploadBuffer<Matrix4x4> palette = new("Skinning.Bones");
    readonly List<PermutationKey<bool>> keys;
    bool disposed;

    /// <summary>Creates the feature, interning its permutation key.</summary>
    public SkinningRenderFeature() => keys = [ParameterKeys.NewPermutation(false, "Vixen.Skinned")];

    /// <inheritdoc />
    public override string Name => "Skinning";

    /// <summary>Where each object's palette starts, and how many bones it has.</summary>
    public RenderDataKey<BonePalette> Palettes { get; private set; }

    /// <summary>The device the bone buffer lives on. Set before the first frame that prepares.</summary>
    public IGraphicsDevice? Device {
        get => palette.Device;
        set => palette.Device = value;
    }

    /// <summary>The buffer every palette lives in, for a host binding it once a frame.</summary>
    public BufferHandle Buffer => palette.Buffer;

    /// <summary>How many bone matrices this frame holds.</summary>
    public int BoneCount => palette.Count;

    /// <summary>Which stages read the base index.</summary>
    public ShaderStage Stages { get; set; } = ShaderStage.Vertex;

    /// <summary>
    ///     Where the base index goes in the push-constant block.
    /// </summary>
    /// <remarks>
    ///     Sixty-four by default, which is immediately after
    ///     <see cref="TransformRenderFeature" />'s <c>mat4</c>. The two share one block because
    ///     Vulkan guarantees only 128 bytes of it in total — a budget the two of them together use
    ///     68 of, and which Raven reports at <c>RVN3007</c> if a shader's block exceeds.
    /// </remarks>
    public int Offset { get; set; } = 64;

    /// <inheritdoc />
    public IReadOnlyList<PermutationKey<bool>> PermutationKeys => keys;

    /// <inheritdoc />
    /// <remarks>
    ///     An object is skinned when it has bones, not when a material says so. That is why this is
    ///     contributed rather than set on the material: a mesh with a skeleton and a mesh without one
    ///     can share every material property and still need different shaders.
    /// </remarks>
    public bool ValueOf(RenderSystem system, RenderObjectId id, int index) {
        ArgumentNullException.ThrowIfNull(system);
        return system.Objects.Data.Data(Palettes)[id.Index].BoneCount > 0;
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        Palettes = system.Objects.Data.Register<BonePalette>();
    }

    /// <summary>Starts a frame's palettes. Call before the first <see cref="SetBones" />.</summary>
    /// <remarks>
    ///     Explicit rather than folded into extraction, because whoever fills the palettes is the
    ///     animation system and it runs before the render system's phases — there is no callback of
    ///     ours between "animation finished" and "the first palette is written".
    /// </remarks>
    public void Begin() => palette.Begin();

    /// <summary>Gives an object its bone palette for this frame.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="bones">
    ///     The skinning matrices — <c>inverseBindPose * boneWorld</c>, one per bone, in the order the
    ///     mesh's vertex indices refer to them.
    /// </param>
    public void SetBones(RenderSystem system, RenderObjectId id, ReadOnlySpan<Matrix4x4> bones) {
        ArgumentNullException.ThrowIfNull(system);

        var first = palette.Add(bones);
        system.Objects.Data.Data(Palettes)[id.Index] = new(first, bones.Length);
    }

    /// <inheritdoc />
    protected internal override void Prepare(RenderSystem system) => palette.Upload();

    /// <inheritdoc />
    public void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(context);

        var assigned = system.Objects.Data.Data(Palettes)[node.Object.Index];

        if (assigned.BoneCount == 0) {
            // Nothing pushed for an unskinned object. Its variant has no skinning code in it, so the
            // constant would be read by nothing — and writing it would make an unskinned draw pay
            // for a feature it does not use.
            return;
        }

        var first = assigned.FirstBone;

        context.CommandList.PushConstants(
            Stages,
            Offset,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref first, 1))
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        palette.Dispose();
    }
}

/// <summary>Where one object's bones are in the shared palette buffer.</summary>
/// <param name="FirstBone">The index of its first matrix.</param>
/// <param name="BoneCount">How many it has. Zero for an object that is not skinned.</param>
public readonly record struct BonePalette(int FirstBone, int BoneCount);
