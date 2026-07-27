// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
public sealed class MeshRenderFeature : RootRenderFeature {
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

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) =>
        Draws = system.Objects.Data.Register<MeshDraw>();

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

        var boundPipeline = default(PipelineHandle);
        var boundDescriptors = default(DescriptorSetHandle);

        foreach (var node in nodes) {
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

            // Only when the resolved effect actually has a per-material set. A stage that overrode
            // the shader — a depth prepass, a shadow caster — is drawing something that reads no
            // material at all, and binding a set its pipeline layout does not declare is a validation
            // error rather than a harmless extra call.
            if (HasMaterialSet(effect)
                && materials.DescriptorsOf(system, node.Object) is { IsValid: true } descriptors
                && descriptors != boundDescriptors) {
                context.CommandList.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors);
                boundDescriptors = descriptors;
            }

            foreach (var subFeature in SubFeatures) {
                if (subFeature is IDrawSubFeature contributor) {
                    contributor.Draw(system, context, node);
                }
            }

            context.CommandList.BindVertexBuffer(0, draw.VertexBuffer);

            // An instancing sub-feature overrides the draw's own count and supplies the offset its
            // transforms start at. `firstInstance` rather than a binding: Vulkan adds it into
            // `gl_InstanceIndex` before the shader runs, so a batch reaches its own run of one shared
            // buffer with no descriptor and no alignment of its own.
            var batch = instances?.InstanceCountOf(system, node.Object) ?? 0;
            var count = Math.Max(batch > 0 ? batch : draw.InstanceCount, 1);
            var first = batch > 0 ? instances!.FirstInstanceOf(system, node.Object) : 0;

            if (draw.IsIndexed) {
                context.CommandList.BindIndexBuffer(draw.IndexBuffer, draw.IndexFormat);
                context.CommandList.DrawIndexed(draw.Count, count, draw.FirstIndex, draw.VertexOffset, first);
            } else {
                context.CommandList.Draw(draw.Count, count, draw.FirstIndex, first);
            }
        }
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
}
