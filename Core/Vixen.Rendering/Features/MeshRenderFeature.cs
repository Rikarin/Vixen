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
            if (materials?.EffectOf(system, node.Object) is not { } effect) {
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

            if (materials.DescriptorsOf(system, node.Object) is { IsValid: true } descriptors
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

            if (draw.IsIndexed) {
                context.CommandList.BindIndexBuffer(draw.IndexBuffer, draw.IndexFormat);
                context.CommandList.DrawIndexed(
                    draw.Count,
                    Math.Max(draw.InstanceCount, 1),
                    draw.FirstIndex,
                    draw.VertexOffset
                );
            } else {
                context.CommandList.Draw(draw.Count, Math.Max(draw.InstanceCount, 1), draw.FirstIndex);
            }
        }
    }

    /// <inheritdoc />
    protected internal override uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) =>
        MaterialsOf(system)?.SortGroupOf(system, id) ?? system.Objects[id].SortGroup;

    MaterialRenderFeature? MaterialsOf(RenderSystem system) =>
        SubFeatures.OfType<MaterialRenderFeature>().FirstOrDefault();
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
