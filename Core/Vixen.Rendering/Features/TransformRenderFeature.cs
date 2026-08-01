// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>Which record of a shared transform buffer an object's world matrix is.</summary>
/// <remarks>
///     <para>
///         A separate interface from <see cref="IInstanceSource" /> because the two answer different
///         questions about the same field. Instancing says <em>how many</em> copies a draw has and
///         where their run starts; this says <em>which one</em> matrix a single draw reads. Both land
///         in <c>firstInstance</c>, and instancing wins where it applies — its transforms are the ones
///         the instanced variant of a shader reads.
///     </para>
///     <para>
///         An interface rather than a reference to <see cref="TransformRenderFeature" />, because
///         <see cref="MeshRenderFeature" /> naming a transform feature is exactly the coupling the
///         sub-feature arrangement exists to avoid — see that class's remarks.
///     </para>
/// </remarks>
public interface ITransformRecordSource {
    /// <summary>The object's record index, or <c>-1</c> when its matrix is not in a buffer at all.</summary>
    int RecordIndexOf(RenderSystem system, RenderObjectId id);
}

/// <summary>
///     Where each object is, and getting that to the shader.
/// </summary>
/// <remarks>
///     <para>
///         A sub-feature rather than a field on <see cref="RenderObject" />, and it is the clearest
///         case for why: a UI quad in screen space and a particle billboard built in the vertex
///         shader both have bounds and both need culling, and neither has a world matrix. Putting
///         one on every object would make them carry sixty-four bytes to say nothing.
///     </para>
///     <para>
///         <strong>Two ways to the shader, and which one is a decision about the frame.</strong>
///         By default the matrix is a push constant: the smallest and most per-draw thing a frame has,
///         travelling in the command buffer with no descriptor, no allocation from an upload ring and
///         no offset to track. A <c>mat4</c> is 64 bytes against Vulkan's guaranteed 128, so it fits
///         everywhere with room to spare — and Raven warns at <c>RVN3007</c> if a shader's block
///         exceeds that, so the two sides agree about the budget.
///     </para>
///     <para>
///         <strong>But a pushed constant is per draw, and that is what stops draws merging.</strong>
///         A merged command covers several objects, and there is no point inside one at which a host
///         could push the second object's matrix. <see cref="EnableRecords" /> is the other way: every
///         object's matrix goes into one buffer at its own slot, and the draw carries the slot in its
///         own <c>firstInstance</c> — which the API adds into <c>gl_InstanceIndex</c> before the
///         vertex stage runs, and which the compaction shader copies along with the rest of the
///         command. Not a new mechanism: it is the one <see cref="InstancingRenderFeature" /> already
///         uses, with a run of one.
///     </para>
///     <para>
///         The matrix is sent as-is either way: the engine stores row-major with the translation in
///         <c>M41..M43</c>, the shader reads the same bytes as <c>ColMajor</c>, and that makes its
///         matrix the transpose <c>mul(v, M)</c> wants. Transposing here would compute the wrong
///         transform more expensively — see docs/plan/07 § E.
///     </para>
/// </remarks>
public sealed class TransformRenderFeature
    : SubRenderFeature, IDrawSubFeature, IPermutationSubFeature, ITransformRecordSource, IDisposable {
    readonly UploadBuffer<Matrix4x4> records = new("Transform.Records");
    readonly List<PermutationKey<bool>> keys = [];
    bool disposed;

    /// <inheritdoc />
    public override string Name => "Transform";

    /// <summary>Each object's object-to-world matrix.</summary>
    public RenderDataKey<Matrix4x4> World { get; private set; }

    /// <summary>Which stages the transform is pushed to. Vertex only, by default.</summary>
    public ShaderStage Stages { get; set; } = ShaderStage.Vertex;

    /// <summary>Where in the push-constant block the transform goes.</summary>
    public int Offset { get; set; }

    /// <summary>The device the record buffer lives on. Set before <see cref="EnableRecords" />.</summary>
    public IGraphicsDevice? Device {
        get => records.Device;
        set => records.Device = value;
    }

    /// <summary>
    ///     Whether this frame's matrices go into a buffer rather than into each draw's push constants.
    /// </summary>
    /// <remarks>
    ///     Set through <see cref="EnableRecords" /> rather than directly, because the host's choice and
    ///     the shader's have to be the same one — see there.
    /// </remarks>
    public bool UseRecords { get; private set; }

    /// <summary>The buffer every record lives in, for a host binding it once a frame.</summary>
    public BufferHandle Buffer => records.Buffer;

    /// <summary>
    ///     Which record this frame's first object is, so a shader can add it to the instance index.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>An index rather than a bound offset, and that is not a preference.</strong> The
    ///         buffer holds one region per frame in flight — a descriptor bound at zero reads the
    ///         region another frame is writing — and the route a resource takes to a set is
    ///         <see cref="EffectSetWriter" />, which writes a handle a host named and has nowhere to
    ///         put an offset. So the whole ring is bound and the shader is told where this frame
    ///         starts, which is the same trick <see cref="UploadBuffer{T}" /> already recommends for
    ///         everything else it holds.
    ///     </para>
    ///     <para>
    ///         Exact rather than rounded: a region's stride is a multiple of
    ///         <see cref="UploadBuffer{T}.Alignment" />, which is 256, and a matrix is 64 — so a
    ///         region always begins on a whole record.
    ///     </para>
    /// </remarks>
    public int BaseIndex => (int)(records.Offset / Unsafe.SizeOf<Matrix4x4>());

    /// <summary>How many records this frame holds.</summary>
    public int RecordCount => records.Count;

    /// <summary>What was written this frame, for a test or an inspector.</summary>
    public ReadOnlySpan<Matrix4x4> Records => records.Items;

    /// <summary>
    ///     Where to publish the record buffer and its base, or null to publish them nowhere.
    /// </summary>
    /// <remarks>
    ///     <see cref="SceneConstants" />' collection, normally — the transform buffer is a binding of
    ///     the frame's set, and it is recreated whenever the scene outgrows it, so a host that read
    ///     the handle once and wrote it down would be binding a buffer the device has since retired.
    ///     Publishing from <see cref="Prepare" /> means the name always resolves to this frame's, and
    ///     so does the base, which moves every frame by construction.
    /// </remarks>
    public ParameterCollection? Scene { get; set; }

    /// <summary>Which pass's names to publish under.</summary>
    public string ShaderName { get; set; } = "ForwardPlus";

    /// <inheritdoc />
    public IReadOnlyList<PermutationKey<bool>> PermutationKeys => keys;

    /// <inheritdoc />
    /// <remarks>
    ///     The same answer for every object, which is what makes it cheap rather than what makes it
    ///     wrong: it is a fact about the frame, so every object in the frame shares one variant bit and
    ///     no pair of objects can differ on it.
    /// </remarks>
    public bool ValueOf(RenderSystem system, RenderObjectId id, int index) => UseRecords;

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing at all once the matrices are in a buffer, and that is the entire point: a run of
    ///     nodes with no per-node contributor is a run one command may cover.
    /// </remarks>
    public bool IsRecording => !UseRecords;

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        World = system.Objects.Data.Register<Matrix4x4>();
    }

    /// <summary>
    ///     Turns the record path on where a merged draw is possible, and leaves it off where it is not.
    /// </summary>
    /// <param name="shaderKey">
    ///     The shader's own permutation — <c>ForwardPlusKeys.UseTransformRecords</c> for the shipped
    ///     forward pass.
    /// </param>
    /// <returns>Whether records were turned on.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong>Both halves together, for the same reason
    ///         <see cref="MaterialRenderFeature.EnableRecords" /> does it.</strong>
    ///         <see cref="UseRecords" /> decides whether a host pushes the matrix, and the permutation
    ///         decides whether the shader reads a push constant or a buffer. Either alone draws a
    ///         picture and the picture is wrong: a pushed matrix nothing reads leaves every object at
    ///         whatever record zero holds, and a buffer nobody filled puts every object at the origin.
    ///         Neither fails.
    ///     </para>
    ///     <para>
    ///         ⚠ <strong>The gate is <see cref="GraphicsDeviceFeatures.HasDrawIndirectCount" />, which
    ///         is not what reads the buffer.</strong> Any device can read a matrix out of a storage
    ///         buffer in the vertex stage. What the capability decides is whether it is <em>worth</em>
    ///         it: without a draw whose count comes from the device there is no compaction, without
    ///         compaction there is no merged command, and a dependent buffer read per vertex against a
    ///         constant already in the command stream is a straight loss. Off is the right answer and
    ///         not the degraded one.
    ///     </para>
    ///     <para>
    ///         ⚠ Call this before the first <c>Prepare</c>. Variants are cached by their effect key,
    ///         so a permutation that changed after one was resolved would leave already-resolved
    ///         variants compiled for the old value.
    ///     </para>
    /// </remarks>
    public bool EnableRecords(PermutationKey<bool> shaderKey) {
        UseRecords = Device?.Features is { HasDrawIndirectCount: true };

        // Contributed whichever way it went. A key registered only when it is true would change how
        // many bits `MaterialRenderFeature.FlagsOf` packs, so the same object would hash to a
        // different variant on two devices for a reason that has nothing to do with this feature.
        keys.Clear();
        keys.Add(shaderKey);

        return UseRecords;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Refilled whole rather than by the objects that moved. The array is already dense and
    ///         indexed by object slot — which is exactly the layout the shader wants, because a slot
    ///         is what <c>firstInstance</c> can carry — so writing all of it is one memcpy against
    ///         tracking which matrices changed.
    ///     </para>
    ///     <para>
    ///         ⚠ <strong>And there is a buffer even with the records off.</strong> A binding is in a
    ///         shader's plan because it was declared and not because a variant reads it — which is
    ///         true of every permutation-gated binding in <c>ForwardPlus.rvn</c> and is written down
    ///         at the top of that file. A set short one entry is not bound <em>at all</em>, so a frame
    ///         that pushed its matrices and left this empty would lose set 0 and go dark rather than
    ///         draw untransformed. One identity record, uploaded once, is what that costs.
    ///     </para>
    /// </remarks>
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        if (UseRecords) {
            records.Begin();

            // As far as the ids go and no further. The backing array is grown in chunks, so it is
            // longer than the id space — and uploading its tail would be uploading matrices no id can
            // name. `RenderObjectStore.Count` is the same bound every other per-object buffer uses.
            var world = system.Objects.Data.Data(World)[..system.Objects.Count];
            records.Add(world);

            // A scene with no objects yet still has to leave the binding something to resolve to. An
            // empty upload creates no buffer, and a buffer that does not exist is the dark frame the
            // remarks above are about.
            if (world.Length == 0) {
                records.Add([Matrix4x4.Identity]);
            }

            records.Upload();
        } else if (!records.Buffer.IsValid) {
            records.Add([Matrix4x4.Identity]);
            records.Upload();
        }

        Publish();
    }

    /// <inheritdoc />
    public void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(context);

        // The matrix is already in the buffer, and the draw carries the index. Pushing it as well
        // would be correct and pointless — but it would also be a command per node, which is the one
        // thing a mergeable run cannot have.
        if (UseRecords) {
            return;
        }

        // ⚠ Only where the shader has somewhere to put it. A stage that overrode the shader is
        // drawing something with its own pipeline layout — a depth prepass reads a combined matrix
        // and declares no push range at all — and pushing into a layout that has none is a
        // validation error every draw rather than a value quietly dropped.
        if (StagesFor(context) is not { } stages) {
            return;
        }

        var world = system.Objects.Data.Data(World)[node.Object.Index];

        // The struct's bytes directly. Sequential layout with sixteen floats in M11..M44 order is
        // what the shader reads, so there is nothing to serialise — see the class remarks.
        context.CommandList.PushConstants(
            stages,
            Offset,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref world, 1))
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The object's own slot, because that is what <see cref="Prepare" /> wrote: the record array
    ///     is the object array. No allocation, no per-frame remap, and the same number the argument
    ///     buffer's template can carry for a draw the host never records.
    /// </remarks>
    public int RecordIndexOf(RenderSystem system, RenderObjectId id) => UseRecords ? id.Index : -1;

    /// <summary>
    ///     The stages to push to, or null when this shader has no range to push into.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A push has to name every stage of every range it overlaps, so pushing to the vertex
    ///         stage alone against a range the shader declared for vertex and fragment is refused —
    ///         and a host cannot know which without asking the shader. <c>ForwardPlus</c> declares
    ///         both.
    ///     </para>
    ///     <para>
    ///         A <em>reflected</em> effect that declares no range covering this offset is a different
    ///         answer from one that declares a different set of stages, and it is null rather than
    ///         <see cref="Stages" />: the matrix has nowhere to go, and pushing anyway is a
    ///         validation error on every draw of the stage. That is a shadow caster or a depth
    ///         prepass whose shader the stage imposed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An effect with no set layouts is one with no reflection at all</b> — a host
    ///         feeding this hand-written modules — and it gets <see cref="Stages" />, exactly as it
    ///         did before there was anything to ask. <c>MeshRenderFeature.HasMaterialSet</c> reads
    ///         the same tell for the same reason; there is no other way from here to tell "this
    ///         shader wants no push" from "nobody described this shader".
    ///     </para>
    /// </remarks>
    ShaderStage? StagesFor(RenderDrawContext context) {
        if (context.Effect is not { } effect || effect.SetLayouts.Length == 0) {
            return Stages;
        }

        foreach (var range in effect.PushConstants) {
            if (Offset >= range.Offset && Offset < range.Offset + range.Size) {
                return range.Stages;
            }
        }

        return null;
    }

    void Publish() {
        if (Scene is not { } parameters || !records.Buffer.IsValid) {
            return;
        }

        parameters.Set(ParameterKeys.New<BufferHandle>($"{ShaderName}.transforms"), records.Buffer);
        parameters.Set(ParameterKeys.New<int>($"{ShaderName}.transformBase"), BaseIndex);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        records.Dispose();
    }
}
