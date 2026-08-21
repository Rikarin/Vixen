// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Draws every visible cluster into a visibility buffer: phase 4, placed in the frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>A graph pass, unlike its two siblings.</b>
///         <see cref="GpuCullingRenderer" /> and <see cref="ClusterCullingRenderer" /> declare no
///         resources and are side effects, because what they write are buffers that outlive the graph.
///         This one writes a texture the rest of the frame reads, so it declares it — and the graph then
///         aliases it, transitions it and hands it to whatever names it, exactly as it does for a depth
///         target or a bloom chain.
///     </para>
///     <para>
///         <b>The identity target clears to all ones, not to zero.</b> Zero is a real identity — the
///         first cluster of the frame, its first triangle — so a pixel nothing covered would read as
///         geometry. <see cref="GpuClusterRaster.Nothing" /> is what the resolve tests against, and this
///         is the only place it gets written.
///     </para>
///     <para>
///         <b>It needs a depth target and does not own one.</b> The whole reason phase 4 needs no
///         atomics is that the hardware's depth test resolves which cluster is nearest, so a pass without
///         depth would write whichever identity the rasterizer happened to reach last. Naming an existing
///         depth resource rather than creating one is deliberate: a frame that also draws classic
///         geometry wants both in <em>one</em> depth buffer, or the two occlude each other not at all.
///     </para>
/// </remarks>
public sealed class VisibilityBufferRenderer : SceneRenderer {
    /// <summary>The raster that records the draw. Null does nothing at all.</summary>
    public GpuClusterRaster? Raster { get; set; }

    /// <summary>
    ///     The software raster for the clusters the traversal thought too small. Null draws none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Recorded by this node and between the other two, for the reason the binning is recorded
    ///         here at all: the ordering is not something a document should be able to get wrong. It
    ///         reads the depth the draw above just wrote and overwrites the identities the draw above
    ///         just cleared, and what it leaves behind is what the binning has to bin.
    ///     </para>
    ///     <para>
    ///         Null on every device without <see cref="GraphicsDeviceFeatures.HasInt64Atomics" /> is not
    ///         necessary — the traversal routes nothing there whatever this holds, and
    ///         <see cref="GpuClusterSoftwareRaster.Record" /> answers false — which is what makes phase 6
    ///         an accelerator a document may name unconditionally.
    ///     </para>
    /// </remarks>
    public GpuClusterSoftwareRaster? Software { get; set; }

    /// <summary>
    ///     The binning pass that sorts this buffer's tiles by material. Null draws it and stops there.
    /// </summary>
    /// <remarks>
    ///     Recorded by this node rather than by one of its own, because the ordering is not something a
    ///     document should be able to get wrong: the binning reads the identity buffer, so it has to be
    ///     after the draw that writes it and before anything that resolves it. A separate node would be a
    ///     separate placement decision with no way to check it — and it would have to reopen the graph's
    ///     pass to name the same texture.
    /// </remarks>
    public GpuVisibilityTiles? Tiles { get; set; }

    /// <summary>
    ///     The per-material shading that consumes those bins. Null bins them and stops there.
    /// </summary>
    /// <remarks>
    ///     Recorded in the same pass as the binning and immediately after it, for the reason the binning
    ///     is recorded here at all: the dispatch reads the arguments the binning atomically filled, so the
    ///     two are one ordering decision rather than two placements a document could disagree about.
    /// </remarks>
    public GpuClusterResolve? Resolve { get; set; }

    /// <summary>The frame's per-frame constants, which the resolve's lighting reads.</summary>
    /// <remarks>
    ///     The same object the forward pass binds. Since the lighting moved into <c>ClusteredShading</c>
    ///     the resolve declares the same set 0 a forward draw does, so it is filled by the same writer —
    ///     which is what makes "the same lights with the same shadows" true by construction rather than
    ///     by two hosts agreeing.
    /// </remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>The frame's per-view constants, on the same terms.</summary>
    public ViewConstants? ViewConstants { get; set; }

    /// <summary>What the shaded colour is called in the graph.</summary>
    /// <remarks>
    ///     Named rather than created, because what a resolve writes is radiance into the scene colour the
    ///     forward pass and every post-effect already share — which is the whole of improvement 2. A
    ///     resolve with a target of its own would be a second colour buffer to composite, and compositing
    ///     it is the thing a GBuffer resolve does.
    /// </remarks>
    public string Colour { get; set; } = "SceneColour";

    /// <summary>What the identity target is called in the graph, for whatever resolves it.</summary>
    public string Output { get; set; } = "VisibilityBuffer";

    /// <summary>
    ///     The split's albedo plane — diffuse albedo, the material's occlusion in alpha. Named
    ///     with <see cref="Normals" /> or not at all; empty keeps the combined path.
    /// </summary>
    /// <remarks>
    ///     The ambient split, on the resolve's side of the frame: naming both planes sets
    ///     <see cref="GpuClusterResolve.SplitOutputs" />, so the resolve shades on
    ///     <c>ForwardPlus.SplitOutputs</c>'s terms — direct light in the colour, diffuse ambient
    ///     left to <c>!AmbientCombine</c>. Named rather than created, because the forward pass
    ///     writes the same planes as attachments and the two paths disagreeing per pixel is the
    ///     bug class the shared shading exists to remove.
    /// </remarks>
    public string Albedo { get; set; } = string.Empty;

    /// <summary>The split's normal plane — world normal, roughness in alpha. On <see cref="Albedo" />'s terms.</summary>
    public string Normals { get; set; } = string.Empty;

    /// <summary>The split's f0 plane — specular reflectance at normal incidence. On <see cref="Albedo" />'s terms.</summary>
    /// <remarks>
    ///     ⚠ Named with the other two or the node refuses. A resolve that wrote albedo and normals
    ///     but left this plane holding last frame's texels would hand <c>!AmbientCombine</c> an
    ///     <c>f0</c> for a surface that is no longer there — and unlike a missing plane, a stale one
    ///     draws a picture.
    /// </remarks>
    public string Specular { get; set; } = string.Empty;

    /// <summary>Which depth resource to test and write against.</summary>
    /// <remarks>
    ///     Required, for the reason in the class remarks: without a depth test the nearest cluster is
    ///     whichever one the rasterizer reached last. A name rather than a texture so that the frame's
    ///     one depth buffer serves this pass and the classic draws alike.
    /// </remarks>
    public string Depth { get; set; } = "SceneDepth";

    /// <summary>Which view's camera the clusters are projected with.</summary>
    /// <remarks>
    ///     One view, because a visibility buffer is a screen and a screen belongs to a camera. A shadow
    ///     view wants its own buffer at its own resolution, which is phase 7 rather than a second index
    ///     here.
    /// </remarks>
    public int ViewIndex { get; set; }

    /// <summary>The view it draws from, and the one the traversal chooses a cut for.</summary>
    /// <remarks>
    ///     Null collects nothing, on the same terms as every other resource here.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Which stages' objects this buffer draws — the mask the traversal tests instances against.</summary>
    /// <remarks>
    ///     <para>
    ///         Every stage by default, because a cluster draw is not a stage: one indirect draw covers
    ///         every cluster of every instance, so there is no per-stage command a narrower default
    ///         would correspond to. What the mask is for is the traversal's <em>filter</em> — an
    ///         instance still carries its object's stage mask, and the two intersect exactly as the
    ///         object cull's do — so a document that stages its virtualized objects can narrow this to
    ///         draw only some of them here.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="RenderStageMask.None" /> here is a buffer that is always empty, and it is
    ///         the value this node effectively had before the property existed:
    ///         <see cref="GraphicsCompositor.Use(RenderView)" /> clears a view's mask on first use each
    ///         frame — so a stage removed from the tree stops being collected for — and only stage
    ///         nodes put bits back. A virtualized frame has no stage nodes, so its view reached the
    ///         device with an empty mask and the traversal rejected every instance before the frustum.
    ///         The golden fixture is what caught it, as a visibility buffer that stayed uniformly
    ///         empty while the host mirror — fed a hand-packed view — accepted clusters.
    ///     </para>
    /// </remarks>
    public RenderStageMask Stages { get; set; } = RenderStageMask.All;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>Collecting the view is what puts it in the frame at all.</b> A view reaches
    ///         <c>RenderSystem.SetViews</c> because a node collected it — see
    ///         <see cref="SingleStageRenderer" /> — and a virtualized document has no <c>SingleStage</c> in
    ///         it, because a cluster draw is not a stage. Without this the frame collects no views, the
    ///         traversal has nothing to choose a cut for, and every pass runs and draws nothing. That is
    ///         what this node shipped with, and it took running the frame on a device to see: every test
    ///         placed the nodes and none of them asked what they drew.
    ///     </para>
    ///     <para>
    ///         The stage mask goes on after <see cref="GraphicsCompositor.Use(RenderView)" /> and not
    ///         before, because first use <em>clears</em> it — see <see cref="Stages" />. Or-ed rather
    ///         than assigned, so a view this node shares with stage nodes keeps what they declared.
    ///     </para>
    /// </remarks>
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        if (View is { } view) {
            compositor.Use(view);
            view.Stages |= Stages;
        }
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Raster is not { Visibility.MeshCount: > 0 }) {
            return;
        }

        var system = compositor.System;

        if (ViewIndex < 0 || ViewIndex >= system.Views.Count) {
            return;
        }

        var raster = Raster;
        var software = Software;
        var tiles = Tiles;
        var resolve = Resolve;
        var view = system.Views[ViewIndex];
        var viewProjection = system.Views[ViewIndex].ViewProjection;
        var size = frame.Size;

        // A declaration wins over a creation, and that is the whole of what makes the buffer
        // inspectable. A transient this node creates is stored only if something inside the frame reads
        // it — nothing outside one can — so a document that wants to look at the visibility buffer, or a
        // harness that wants to compare it against another renderer, declares or imports it by name and
        // gets a resource that outlives the frame. Creating one unconditionally made that impossible and
        // said nothing about it.
        var identity = frame.Has(Output)
            ? frame.Texture(ToString(), Output)
            : frame.Graph.CreateTexture(
                new(
                    GpuClusterRaster.Format,
                    size.X,
                    size.Y,
                    TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage,
                    Name: Output
                )
            );

        if (!frame.Has(Output)) {
            // Registered under its name so whatever resolves it can ask for it the way every other node
            // asks for a target — which is what makes phase 5 a node placed in a document rather than a
            // reference to this class.
            frame.Add(Output, identity, GpuClusterRaster.Format);
        }

        var depth = frame.Texture(ToString(), Depth);
        var format = frame.FormatOf(ToString(), Depth);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // Cleared, and to zero — which GpuClusterRaster.Nothing is, and which is why the packed
                // slot is biased by one. A pixel nothing drew reads as nothing rather than as the frame's
                // first cluster.
                pass.ColourAttachment(identity, LoadAction.Clear);
                pass.DepthAttachment(depth, LoadAction.Load);

                pass.Execute(
                    context => {
                        // Outside the render pass and before it: the argument copy that turns the
                        // traversal's count into an instance count is a buffer copy, and a copy between
                        // BeginRenderPass and EndRenderPass is not legal. The graph opens the pass around
                        // Execute, so this has to be the pass's first act rather than the node's.
                        raster.Record(context.CommandList, viewProjection, GpuClusterRaster.Format, format);
                    }
                );
            }
        );

        // A pass of its own, between the draw and the binning. It cannot be inside the render pass above
        // — a compute dispatch is not a draw, and the identity buffer is an attachment there rather than
        // a storage image — and it must not be somewhere a document could place it wrongly, because it
        // reads that pass's depth and rewrites that pass's output.
        // ⚠ Not merely "records nothing" on a device with no 64-bit atomics — no *pass* at all. The pass
        // declares a read of the depth target and a write of the identity buffer, and a graph that
        // declared those would demand a sampled depth image and a storage identity image of every
        // document, on hardware that can never run the dispatch that wanted them.
        if (software is { Supported: true }) {
            frame.Graph.AddPass(
                $"{this}.Software",
                pass => {
                    // ⚠ **A dispatch declared Graphics, because two of its three inputs are
                    // invisible here.** The depth and the identity buffer are declared and would
                    // carry their own edges, but the dispatch's extents come from the argument
                    // buffer `ClusterCullingRenderer` filled with `software.Prepare` — a buffer the
                    // graph never sees, in a pass that is not this one. Hoisting this onto a compute
                    // queue would put a wait edge on the depth and none at all on the extents.
                    pass.Kind = PassKind.Graphics;
                    pass.Reads(depth);
                    pass.Writes(identity);
                    pass.SideEffect();

                    pass.Execute(
                        context => software.Record(
                            context.CommandList,
                            context.View(identity),
                            context.View(depth),
                            viewProjection,
                            size
                        )
                    );
                }
            );
        }

        if (tiles is null) {
            return;
        }

        // The scene colour, named rather than created — see Colour. Resolved outside the pass because a
        // node's Build is where a name becomes a resource, and a missing one should refuse the whole node
        // rather than fail inside a command list.
        var colour = resolve is null ? default : frame.Texture(ToString(), Colour);

        // All three split planes or none: one without the others is a half-split frame no combine can
        // read, and a loud refusal here beats an albedo plane quietly holding last frame's texels.
        var named = (Albedo.Length > 0 ? 1 : 0) + (Normals.Length > 0 ? 1 : 0) + (Specular.Length > 0 ? 1 : 0);

        if (named is not (0 or 3)) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Albedo.Length > 0 ? Normals.Length > 0 ? Specular : Normals : Albedo,
                "is empty while its split partners are named. The ambient split writes albedo, "
                + "normals and specular together or not at all — name all three planes, or none"
            );
        }

        var split = resolve is not null && Albedo.Length > 0;
        var albedo = split ? frame.Texture(ToString(), Albedo) : default;
        var normal = split ? frame.Texture(ToString(), Normals) : default;
        var specular = split ? frame.Texture(ToString(), Specular) : default;

        // A second pass, and not a second node. The binning samples what the draw wrote, so it cannot be
        // inside the render pass that writes it — an attachment being written is not a texture being read
        // — and it must not be somewhere a document could place it wrongly. Two passes from one node is
        // the arrangement that makes both true.
        frame.Graph.AddPass(
            $"{this}.Tiles",
            pass => {
                // ⚠ **Graphics, for the reason the software pass above is.** The binning's own
                // inputs are declared, but `GpuClusterResolve` binds `pages.Pages` and
                // `visibility.Instances` — the two buffers `ClusterCullingRenderer` fills, which are
                // the residency service's and the traversal's rather than the graph's. A pass that
                // reads them without being able to say so is a pass the scheduler must not move.
                pass.Kind = PassKind.Graphics;
                pass.Reads(identity);

                if (resolve is not null) {
                    pass.Writes(colour);
                }

                if (split) {
                    pass.Writes(albedo);
                    pass.Writes(normal);
                    pass.Writes(specular);
                }

                pass.SideEffect();

                pass.Execute(
                    context => {
                        // The counters the previous frame left, before this frame overwrites them. Read
                        // here rather than by the caller because this is the one place that knows the
                        // dispatch has been submitted and the next one has not started.
                        tiles.ReadCounts();

                        if (!tiles.Record(context.CommandList, context.View(identity), size) || resolve is null) {
                            return;
                        }

                        // Immediately after, in the same pass: the dispatch reads the arguments the binning
                        // just filled, and the barrier between them is the one `Record` already left.
                        resolve.Scene = SceneConstants;
                        resolve.ViewConstants = ViewConstants;
                        resolve.SplitOutputs = split;

                        var albedoView = split ? context.View(albedo) : default;
                        var normalView = split ? context.View(normal) : default;
                        var specularView = split ? context.View(specular) : default;

                        if (resolve.Prepare(
                                view,
                                context.View(colour),
                                context.View(identity),
                                size,
                                albedoView,
                                normalView,
                                specularView
                            )) {
                            resolve.Record(context.CommandList);
                        }
                    }
                );
            }
        );
    }
}
