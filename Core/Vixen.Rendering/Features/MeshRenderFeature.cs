// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>
///     Draws indexed geometry: the first concrete renderable, and the shape every other follows.
/// </summary>
/// <remarks>
///     <para>
///         What this owns is small on purpose — one <see cref="MeshDraw" /> per object and the draw
///         calls that follow from it. Where the object <em>is</em> belongs to
///         <see cref="TransformRenderFeature" />; what it is drawn <em>with</em> belongs to
///         <see cref="MaterialRenderFeature" />. Neither is referenced from here, and that is the
///         point of the arrangement rather than an accident of it: a mesh that gains skinning gains
///         a third sub-feature and this file does not change.
///     </para>
///     <para>
///         <strong>The binding order is what the sort was for.</strong> A run arrives already
///         grouped by pipeline, so the pipeline is bound once for the run and the descriptor set
///         only when the material changes within it. Re-binding per node would compile, draw the
///         same image, and throw away the reason the sort key puts grouping above depth.
///     </para>
/// </remarks>
public sealed class MeshRenderFeature : RootRenderFeature, Compositor.IDrawArgumentSource {
    /// <inheritdoc />
    public override string Name => "Mesh";

    /// <summary>One draw per object.</summary>
    public RenderDataKey<MeshDraw> Draws { get; private set; }

    /// <summary>Where pipelines come from. Set before the first frame that draws.</summary>
    public PipelineCache? Pipelines { get; set; }

    /// <summary>
    ///     How a pipeline is described for a given effect, stage and output.
    /// </summary>
    /// <remarks>
    ///     Supplied rather than decided here, because the blend and depth state belong to the stage
    ///     and the attachment formats to the pass — neither of which a mesh knows anything about.
    ///     <see cref="Compositor.EffectPipelineDescriber" /> is the one that assembles them, and a
    ///     project with an unusual pipeline supplies its own without touching this file.
    /// </remarks>
    public IPipelineDescriber? Describer { get; set; }

    /// <summary>
    ///     Where each draw's arguments come from, when the GPU decides whether it draws anything.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Null is the ordinary path: the work list is exactly what is visible, so the draw call
    ///         carries its own counts. Set — beside a
    ///         <see cref="GpuVisibilityGroup" /> with <see cref="GpuVisibilityGroup.ReadBack" /> off
    ///         — the work list is everything that <em>could</em> be visible and the instance count of
    ///         everything culled has been zeroed on the device, which is where the decision now
    ///         lives.
    ///     </para>
    ///     <para>
    ///         Indexed draws only. There is no non-indexed indirect entry point in the RHI, and
    ///         adding one for the case that has no reason to be GPU-driven would be adding it for a
    ///         path nothing takes: an object with no index buffer is drawn directly, whatever this
    ///         says.
    ///     </para>
    /// </remarks>
    public GpuDrawArguments? Arguments { get; set; }

    /// <summary>
    ///     How many draw commands this feature has recorded, and over how many indices.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The number a black frame is judged by, and the one nothing else in a host reports.</b>
    ///     Objects extracted, meshes loaded, variants resolved, descriptor sets bound — every one of
    ///     those can be nonzero in a frame that records no draw at all, because each is counted
    ///     somewhere upstream of the loop that skips an object with no drawable mesh or no resolved
    ///     effect. These two are counted at the command itself, so they are the only pair that
    ///     distinguishes "drew nothing" from "drew something invisible", and telling those two apart
    ///     is most of the work of finding out why a window is one colour.
    /// </remarks>
    public int DrawCount { get; private set; }

    /// <inheritdoc cref="DrawCount" />
    public long IndexCount { get; private set; }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) =>
        Draws = system.Objects.Data.Register<MeshDraw>();

    /// <inheritdoc />
    /// <remarks>
    ///     The same five numbers <see cref="Draw" /> would have passed to
    ///     <see cref="ICommandList.DrawIndexed" />, including the instancing sub-feature's batch size
    ///     and first instance — which is why this runs after <c>Prepare</c> rather than inside it.
    ///     Anything not drawable, or not indexed, is left as the cleared record it arrived as: a draw
    ///     of no indices, which draws nothing.
    /// </remarks>
    public void FillArguments(RenderSystem system, Span<DrawCommand> commands) {
        ArgumentNullException.ThrowIfNull(system);

        var draws = system.Objects.Data.Data(Draws);
        var instances = SubFeatures.OfType<IInstanceSource>().FirstOrDefault();
        var transforms = SubFeatures.OfType<ITransformRecordSource>().FirstOrDefault();

        for (var index = 0; index < commands.Length && index < draws.Length; index++) {
            ref readonly var draw = ref draws[index];

            if (!draw.IsDrawable || !draw.IsIndexed) {
                continue;
            }

            var id = new RenderObjectId(index);
            var batch = instances?.InstanceCountOf(system, id) ?? 0;

            commands[index] = new() {
                IndexCount = (uint)draw.Count,
                InstanceCount = (uint)Math.Max(batch > 0 ? batch : draw.InstanceCount, 1),
                FirstIndex = (uint)draw.FirstIndex,
                VertexOffset = (uint)draw.VertexOffset,
                FirstInstance = (uint)FirstInstanceOf(system, id, batch, instances, transforms)
            };
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         The key is everything a merged draw would have to bind once: the resolved variant, the
    ///         two buffers, the index width and the vertex layout. Two objects sharing all of them
    ///         are two draws that differ only in their arguments, which is the definition compaction
    ///         needs.
    ///     </para>
    ///     <para>
    ///         ⚠ The variant rather than the effect, because an effect is resolved per stage and this
    ///         runs once for the frame. A stage that overrides the shader can therefore break a batch
    ///         into runs that do not match it — which is why the draw side checks that a run is the
    ///         whole batch rather than trusting the id.
    ///     </para>
    /// </remarks>
    public void FillBatches(RenderSystem system, Span<uint> batches) {
        ArgumentNullException.ThrowIfNull(system);

        var draws = system.Objects.Data.Data(Draws);
        var materials = MaterialsOf(system);
        var variants = materials is null ? default : system.Objects.Data.Data(materials.VariantIndex);
        var keys = new Dictionary<(int, ulong, ulong, IndexFormat, int), uint>();

        for (var index = 0; index < batches.Length && index < draws.Length; index++) {
            ref readonly var draw = ref draws[index];

            if (!draw.IsDrawable || !draw.IsIndexed) {
                continue;
            }

            var key = (
                variants.IsEmpty || index >= variants.Length ? 0 : variants[index],
                draw.VertexBuffer.Value.Packed,
                draw.IndexBuffer.Value.Packed,
                draw.IndexFormat,
                draw.VertexLayout
            );

            if (!keys.TryGetValue(key, out var batch)) {
                keys[key] = batch = (uint)keys.Count;
            }

            batches[index] = batch;
        }
    }

    /// <inheritdoc />
    protected internal override void Draw(
        RenderSystem system,
        RenderDrawContext context,
        ReadOnlySpan<RenderNode> nodes
    ) {
        if (Pipelines is null || Describer is null || context.Stage is null) {
            return;
        }

        var stage = context.Stage;
        var output = context.Output;

        var draws = system.Objects.Data.Data(Draws);
        var materials = MaterialsOf(system);
        var instances = SubFeatures.OfType<IInstanceSource>().FirstOrDefault();
        var transforms = SubFeatures.OfType<ITransformRecordSource>().FirstOrDefault();

        var boundPipeline = default(PipelineHandle);
        var boundDescriptors = default(DescriptorSetHandle);
        var boundVertices = default(BufferHandle);
        var boundIndices = default(BufferHandle);
        var boundIndexFormat = default(IndexFormat);
        var boundRecord = -1;
        var boundView = false;
        var boundScene = false;
        var boundTable = false;

        // Whether a run of nodes could become one command at all. Every per-node contributor is a
        // reason it cannot: a sub-feature that pushes this object's world matrix has to be given the
        // chance to push the next one's, and there is no next one inside a merged draw. A transform
        // feature whose records are on pushes nothing and therefore does not count — which is the
        // whole point of the record path. Asked once rather than per node, and it is the honest gate
        // — see MergeableRunAt.
        var merging = Arguments is { IsCompacted: true }
            && context.View is not null
            && !SubFeatures.Any(feature => feature is IDrawSubFeature { IsRecording: true });

        var remaining = 0;

        for (var position = 0; position < nodes.Length; position++) {
            var node = nodes[position];
            var draw = draws[node.Object.Index];

            if (!draw.IsDrawable) {
                continue;
            }

            // No material feature, or no effect resolved for this object: there is nothing to draw
            // it with. Skipped rather than drawn with whatever was bound last, which would put one
            // object's shader on another's geometry — an image that is wrong rather than absent.
            if (materials?.EffectOf(system, node.Object, stage) is not { } effect) {
                continue;
            }

            var key = new PipelineKey(effect, stage.Index, draw.VertexLayout, output);

            // Asked before GetOrCreate because that one takes a closure, and a closure allocates
            // whether or not it is invoked — which on the hit path is every draw in the frame.
            if (!Pipelines.TryGet(key, out var pipeline)) {
                var layout = draw.VertexLayout;
                pipeline = Pipelines.GetOrCreate(key, () => Describer.Describe(effect, stage, output, layout));
            }

            if (pipeline != boundPipeline) {
                context.CommandList.BindPipeline(pipeline);
                boundPipeline = pipeline;
            }

            // After the pipeline and only once. A descriptor set is bound against the layout of
            // whatever pipeline is current, so this cannot happen before the first one — and it does
            // not need to happen again, because every pipeline in a frame is layout-compatible up to
            // set 1 and a bound set survives a change that does not disturb it.
            if (!boundView && context.ViewConstants is { } view && context.View is { } from) {
                // The layout first, because it belongs to the shader and the shader is only in hand
                // here — see ViewConstants.AdoptLayout. A host that set one is left alone.
                view.AdoptLayout(effect);
                boundView = view.Bind(context.CommandList, from, effect);
            }

            // The frame's set, on the same terms and for the same reason. After the pipeline because
            // no set can precede one; once per run because every pipeline in a frame is layout-
            // compatible up to set 1, which covers set 0 as well.
            if (!boundScene && context.SceneConstants is { } scene) {
                boundScene = scene.Bind(context.CommandList, effect);
            }

            // The material table, on the same terms and once. It is not written here and not written
            // per frame at all — the table owns its set and updates descriptors in place as textures
            // enter it — so all a frame does is name it once, at the set the shader declared it in.
            //
            // ⚠ Only where the effect declares the set. A variant compiled without the table has a
            // four-set pipeline layout, and binding a fifth against it is a validation error rather
            // than a harmless extra call — which is exactly the mixed frame this is written for, since
            // the non-bindless variant of the same shader is the one every other device compiles.
            if (!boundTable
                && materials.Textures is { Set.IsValid: true } table
                && HasBindlessSet(effect)) {
                context.CommandList.BindDescriptorSet(DescriptorSetSlot.Bindless, table.Set);
                boundTable = true;
            }

            // Which record of the material buffer this draw reads, pushed rather than bound and
            // pushed once per run rather than per node.
            //
            // ⚠ It is per *draw* and never per object, which is the fact that makes it a push at all.
            // A variant is keyed (material, flags, shader), so one variant is one material; a batch
            // is keyed on the variant; so every object in a merged command has the same material and
            // the same record. Putting it in the per-object block — where it was — meant a clustered
            // frame never delivered it, because that path binds no per-draw set at all.
            PushRecordIndex(context, materials, system, node.Object, stage, effect, ref boundRecord);

            // Only when the resolved effect actually has a per-material set. A stage that overrode
            // the shader — a depth prepass, a shadow caster — is drawing something that reads no
            // material at all, and binding a set its pipeline layout does not declare is a validation
            // error rather than a harmless extra call.
            if (HasMaterialSet(effect)
                && materials.DescriptorsOf(system, node.Object, stage) is { IsValid: true } descriptors
                && descriptors != boundDescriptors) {
                context.CommandList.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors);
                boundDescriptors = descriptors;
            }

            // What the sub-features are contributing to, so one that pushes a constant can agree with
            // the range the shader declared rather than with the stages it guessed.
            context.Effect = effect;

            foreach (var subFeature in SubFeatures) {
                if (subFeature is IDrawSubFeature { IsRecording: true } contributor) {
                    contributor.Draw(system, context, node);
                }
            }

            // Only when it changed, which for geometry out of a shared GeometryBuffer is once for
            // the whole run. Every object used to rebind its own, and re-binding the same handle is
            // not free: it is a command the front end reads and, more to the point, the reason a run
            // of objects could not become one draw. A mesh with a buffer of its own still gets a
            // bind per object, which is the same behaviour it always had.
            if (draw.VertexBuffer != boundVertices) {
                context.CommandList.BindVertexBuffer(0, draw.VertexBuffer);
                boundVertices = draw.VertexBuffer;
            }

            // An instancing sub-feature overrides the draw's own count and supplies the offset its
            // transforms start at. `firstInstance` rather than a binding: Vulkan adds it into
            // `gl_InstanceIndex` before the shader runs, so a batch reaches its own run of one shared
            // buffer with no descriptor and no alignment of its own.
            var batch = instances?.InstanceCountOf(system, node.Object) ?? 0;
            var count = Math.Max(batch > 0 ? batch : draw.InstanceCount, 1);
            var first = FirstInstanceOf(system, node.Object, batch, instances, transforms);

            if (draw.IsIndexed) {
                // ⚠ The format as well as the handle. Two buffers may be the same object at
                // different index widths only if something rebound the format in between, and an
                // index buffer read at the wrong width is geometry through the world rather than a
                // missing mesh — so a comparison on the handle alone would be a correctness bug
                // wearing an optimisation's clothes.
                if (draw.IndexBuffer != boundIndices || draw.IndexFormat != boundIndexFormat) {
                    context.CommandList.BindIndexBuffer(draw.IndexBuffer, draw.IndexFormat);
                    boundIndices = draw.IndexBuffer;
                    boundIndexFormat = draw.IndexFormat;
                }

                // Already covered by the command this run opened with. The binds above still ran and
                // were all no-ops, which is what being in one run means — and letting them run is
                // cheaper than a second condition around each of them.
                if (remaining > 0) {
                    remaining--;
                    continue;
                }

                // One command for the whole batch, when the batch is exactly this run. The count
                // comes out of a buffer the host never reads, so a batch of a thousand candidates
                // with three survivors is one command and three draws.
                if (merging && MergeableRunAt(system, stage, nodes, position, draws) is { } run) {
                    var viewIndex = context.View!.Index;
                    var runBatch = Arguments!.BatchOf(node.Object);

                    context.CommandList.DrawIndexedIndirectCount(
                        Arguments.Commands,
                        Arguments.Counts,
                        Arguments.BatchOffsetOf(viewIndex, runBatch),
                        Arguments.CountOffsetOf(viewIndex, runBatch),
                        Arguments.BatchSizeOf(runBatch)
                    );

                    remaining = run - 1;
                    continue;
                }

                // Indirect when something has written this object's arguments for this view: the
                // counts are the same ones, except that culling may have zeroed the instance count —
                // which is how a draw the GPU rejected costs a command and no vertex work.
                if (Indirect(context, node.Object) is { } offset) {
                    context.CommandList.DrawIndexedIndirect(Arguments!.Commands, offset);
                } else {
                    context.CommandList.DrawIndexed(draw.Count, count, draw.FirstIndex, draw.VertexOffset, first);
                }
            } else {
                context.CommandList.Draw(draw.Count, count, draw.FirstIndex, first);
            }

            DrawCount++;
            IndexCount += draw.Count;
        }
    }

    /// <summary>
    ///     How long the run starting here is, when it is a whole batch that one command may cover.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Three conditions, and each rules out a picture rather than an error.</strong>
    ///         Every node in the run must be the same batch, or the command would draw arguments
    ///         belonging to geometry it is not bound for. Every node must resolve to the same effect
    ///         and the same buffers, because one command binds one of each. And the run must be the
    ///         <em>whole</em> batch — <see cref="GpuDrawArguments.BatchSizeOf" /> — because the
    ///         compacted list holds every survivor of the batch across the frame, so a stage holding
    ///         only some of them would draw the ones it does not have.
    ///     </para>
    ///     <para>
    ///         The last is the one that would otherwise be missed. A batch is a fact about objects
    ///         and a run is a fact about one stage's node list; a shadow cascade that sees half a
    ///         batch has a run of half the length, and drawing the batch there puts the other half
    ///         into the cascade.
    ///     </para>
    /// </remarks>
    int? MergeableRunAt(
        RenderSystem system,
        RenderStage stage,
        ReadOnlySpan<RenderNode> nodes,
        int start,
        ReadOnlySpan<MeshDraw> draws
    ) {
        var materials = MaterialsOf(system);
        var first = draws[nodes[start].Object.Index];
        var batch = Arguments!.BatchOf(nodes[start].Object);
        var effect = materials?.EffectOf(system, nodes[start].Object, stage);
        var length = 1;

        for (var position = start + 1; position < nodes.Length; position++) {
            var id = nodes[position].Object;
            var draw = draws[id.Index];

            if (Arguments.BatchOf(id) != batch) {
                break;
            }

            if (!draw.IsDrawable
                || !draw.IsIndexed
                || draw.VertexBuffer != first.VertexBuffer
                || draw.IndexBuffer != first.IndexBuffer
                || draw.IndexFormat != first.IndexFormat
                || draw.VertexLayout != first.VertexLayout
                || !ReferenceEquals(materials?.EffectOf(system, id, stage), effect)) {
                return null;
            }

            length++;
        }

        return length == Arguments.BatchSizeOf(batch) && length > 1 ? length : null;
    }

    /// <summary>
    ///     Pushes which record of the material buffer this draw reads, when it changed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Per run, which is what makes it compatible with a merged command.</strong> The
    ///         merge gate rules out anything that has to happen between two nodes; this happens
    ///         between two <em>runs</em>, at the same point the per-material set is bound on the path
    ///         that has one. A batch is one variant and a variant is one material, so a run and a
    ///         record change together by construction.
    ///     </para>
    ///     <para>
    ///         The offset comes from the effect and not from a constant here. A shader that adds a
    ///         member above this one renumbers it, and a host that had written 64 down would push the
    ///         index into the world matrix — which is a picture rather than an error, and a wild one.
    ///         An effect whose block has no such member is one compiled without records, and gets no
    ///         push at all.
    ///     </para>
    /// </remarks>
    static void PushRecordIndex(
        RenderDrawContext context,
        MaterialRenderFeature? materials,
        RenderSystem system,
        RenderObjectId id,
        RenderStage stage,
        Effect effect,
        ref int boundRecord
    ) {
        if (materials is not { UseRecords: true }) {
            return;
        }

        var record = materials.RecordOf(system, id, stage)?.Index ?? 0;

        if (record == boundRecord) {
            return;
        }

        foreach (var range in effect.PushConstants) {
            if (range.OffsetOf(MaterialRenderFeature.RecordIndexName) is not { } offset) {
                continue;
            }

            var value = (uint)record;

            context.CommandList.PushConstants(
                range.Stages,
                offset,
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1))
            );

            boundRecord = record;
            return;
        }
    }

    /// <summary>
    ///     What a draw carries in <c>firstInstance</c>: an instance batch's start, a transform
    ///     record's index, or nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>One field, two claimants, and instancing wins.</strong> An instanced draw reads
    ///         its own run of transforms out of the instancing feature's buffer, so the field has to
    ///         be where that run starts; a draw of one reads a single matrix out of the transform
    ///         feature's, so the field is which one. They cannot both be in it, and an object that is
    ///         instanced already has its transforms somewhere the record path is not.
    ///     </para>
    ///     <para>
    ///         Zero when neither applies, which is what every draw carried before either existed.
    ///     </para>
    /// </remarks>
    static int FirstInstanceOf(
        RenderSystem system,
        RenderObjectId id,
        int batch,
        IInstanceSource? instances,
        ITransformRecordSource? transforms
    ) {
        if (batch > 0) {
            return instances!.FirstInstanceOf(system, id);
        }

        return transforms?.RecordIndexOf(system, id) is { } record && record >= 0 ? record : 0;
    }

    /// <summary>Where this object's arguments are for this view, or null to draw directly.</summary>
    /// <remarks>
    ///     Every one of these conditions is a way the buffer can be present and not apply: a view the
    ///     argument pass did not cover, an object added after it ran, a frame in which it did not run
    ///     at all. Drawing indirectly from any of them reads arguments nobody wrote, which is a draw
    ///     of whatever the allocator left there — a hang on some drivers and geometry through the
    ///     world on others.
    /// </remarks>
    long? Indirect(RenderDrawContext context, RenderObjectId id) {
        if (Arguments is not { IsFilled: true } arguments || context.View is not { } view) {
            return null;
        }

        return view.Index >= 0 && view.Index < arguments.ViewCount && id.Index < arguments.ObjectCount
            ? arguments.OffsetOf(view.Index, id)
            : null;
    }

    /// <inheritdoc />
    protected internal override uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) =>
        MaterialsOf(system)?.SortGroupOf(system, id, stage) ?? system.Objects[id].SortGroup;

    MaterialRenderFeature? MaterialsOf(RenderSystem system) =>
        SubFeatures.OfType<MaterialRenderFeature>().FirstOrDefault();

    /// <summary>Whether an effect declares a per-material set for the material's to be bound to.</summary>
    /// <remarks>
    ///     An effect with no layouts at all — which is what a test fixture and an early bring-up
    ///     produce — is treated as having one, so the material path is not silently switched off by
    ///     reflection that has not been wired up yet. What this rejects is an effect that reported its
    ///     layouts and did not include this one.
    /// </remarks>
    static bool HasMaterialSet(Effect effect) {
        const int slot = (int)DescriptorSetSlot.PerMaterial;
        return effect.SetLayouts.Length == 0
            || (effect.SetLayouts.Length > slot && effect.SetLayouts[slot].IsValid);
    }

    /// <summary>Whether an effect declares the bindless table's set.</summary>
    /// <remarks>
    ///     ⚠ The opposite default to <see cref="HasMaterialSet" />, and deliberately. That one treats
    ///     an effect with no layouts as having a material set, so a fixture with no reflection is not
    ///     silently switched off. This one treats it as <em>not</em> having a table, because a fifth
    ///     set is the rare case: assuming it were present would make every four-set pipeline in a
    ///     bindless frame the target of a bind its layout does not declare.
    /// </remarks>
    static bool HasBindlessSet(Effect effect) {
        const int slot = (int)DescriptorSetSlot.Bindless;
        return effect.SetLayouts.Length > slot && effect.SetLayouts[slot].IsValid;
    }
}

/// <summary>A sub-feature that contributes commands to each draw.</summary>
/// <remarks>
///     An interface rather than another virtual on <see cref="SubRenderFeature" />, because most
///     sub-features contribute <em>data</em> and never touch a command list — a transform written
///     into a constant buffer is read by the shader, not bound per draw. Only the ones that really
///     do record implement this, so the per-node loop asks a short list rather than every
///     sub-feature attached.
/// </remarks>
public interface IDrawSubFeature {
    /// <summary>Records this sub-feature's contribution for one node.</summary>
    void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node);

    /// <summary>Whether it has anything to record per node this frame.</summary>
    /// <remarks>
    ///     <para>
    ///         True unless a sub-feature says otherwise, because a sub-feature that implements this
    ///         interface at all normally does record. What it exists for is the one that stops:
    ///         <see cref="TransformRenderFeature" /> pushes a matrix per node until its records are on
    ///         and then pushes nothing, and the difference is not an optimisation but the whole
    ///         question of whether a run of nodes can become one command.
    ///     </para>
    ///     <para>
    ///         ⚠ Answered for the frame rather than per node. A sub-feature that recorded for some
    ///         objects and not others would have to be asked inside the run scan, and a run that was
    ///         mergeable except at one node is a run that is not mergeable — so there would be nothing
    ///         to gain from the finer answer.
    ///     </para>
    /// </remarks>
    bool IsRecording => true;
}
