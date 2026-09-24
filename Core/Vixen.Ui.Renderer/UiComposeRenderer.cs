// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.Compositor;

namespace Vixen.Ui.Renderer;

/// <summary>Where a document composes its interface: after the scene, over the target it names.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A HUD's top-level <c>mix-blend-mode</c> and <c>backdrop-filter</c> read the scene only
///         when the frame says where the scene is</b> (#1378). <c>WorldRenderer.Draw</c> composes an
///         interface's groups in its prologue, before any pass of the frame has run, so a group that
///         reads what lies beneath it reads the interface over transparent black: a multiplied panel
///         lands as its own flat colour on the world and a glass panel blurs nothing.
///         <c>UiRenderFeature.Sceneless</c> counts every such group. Placed in a document — at a
///         <c>!StandardFrame</c>'s <c>beforeUi</c> seam, ahead of the pass that draws the interface
///         stage — this node moves that work to after the scene and hands it <see cref="Source" /> as
///         what lies beneath, and the prologue then leaves the interface to it.
///     </para>
///     <para>
///         ⚠ <b>The same frame's scene, not the previous one's, and that is why it is a node.</b> The
///         other way to give a HUD its scene is last frame's colour target, which costs a frame of lag
///         in every blended panel and still needs a copy taken at this same seam — otherwise the HUD
///         blends with its own previous picture. A render-graph pass with no attachments runs its body
///         outside any render pass, which is exactly where <c>UiRenderer.Compose</c> has to be
///         recorded, and declaring a read of <see cref="Source" /> is what places it after whatever
///         wrote the scene.
///     </para>
///     <para>
///         ⚠ <b><see cref="Source" /> has to be sampleable, and a swapchain image is not.</b> Vixen's
///         swapchain is created for colour attachment and transfer destination only, so a frame that
///         wants a scene-aware HUD renders into a target of its own — declared <c>Sampled</c> — draws
///         the interface into it, and copies it out. The node refuses a target without the usage at
///         build time rather than binding one a driver may or may not sample.
///     </para>
/// </remarks>
[DataContract("UiCompose")]
public sealed record UiComposeAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The target the interface is drawn into, holding the scene when this node runs.</summary>
    /// <remarks>
    ///     The same name the interface pass's <c>colourTargets</c> gives, and it must be declared or
    ///     imported with <c>Sampled</c> — see the type's remarks.
    /// </remarks>
    public string Source { get; init; } = "SceneColour";
}

/// <summary>Composes a <see cref="UiRenderFeature" />'s interfaces over the frame's scene.</summary>
/// <remarks>
///     <para>
///         What a <see cref="UiComposeAsset" /> builds into. One pass with no attachments, reading
///         <see cref="Source" /> as a shader resource: the graph places it after the pass that last
///         wrote the scene, transitions the target for sampling, and — because the interface pass
///         declares the same target as a loaded colour attachment — transitions it back afterwards.
///         The body is <c>UiRenderFeature.Compose</c> with the target as
///         <see cref="UiBackdropSource.Image" />.
///     </para>
///     <para>
///         ⚠ <b>Marked as a side effect</b>, because the graph culls a pass whose writes nothing reads,
///         and this one writes only surfaces the graph has never heard of. Culled, the interface pass
///         would draw every group from a surface nothing had composed this frame.
///     </para>
///     <para>
///         ⚠ <b><c>WorldRenderer.Draw</c> skips its own compose for a frame that has one of these,
///         enabled, on an enabled path</b> — see <see cref="PathTo" />. Composing twice would cost a
///         pass per group, and whichever ran second would decide the picture.
///     </para>
/// </remarks>
public sealed class UiComposeRenderer : SceneRenderer {
    /// <summary>The feature whose mounted interfaces this composes.</summary>
    public required UiRenderFeature Feature { get; init; }

    /// <summary>The target holding the scene, which becomes every top-level group's backdrop.</summary>
    public required string Source { get; init; }

    /// <summary>How many frames this node has composed.</summary>
    /// <remarks>
    ///     Counted in the pass body rather than at build time, so a node the graph dropped — or a
    ///     frame that threw before execution — reads as not having composed.
    /// </remarks>
    public int ComposeCount { get; private set; }

    /// <summary>The nodes from <paramref name="root" /> down to the first node composing <paramref name="feature" />.</summary>
    /// <param name="root">The built frame's root node, or null.</param>
    /// <param name="feature">The feature the node has to compose.</param>
    /// <returns>The path, root first and the node last, or empty when the frame has no such node.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A path and not a yes, because every node on it has to be enabled for the node to
    ///         run.</b> A disabled sequence does not run its children, so a host that checked the node's
    ///         own flag alone would skip its own compose in a frame where nothing composed at all, and
    ///         every faded panel would be drawn from a surface nothing had filled this frame. The
    ///         flags can change between frames and the tree does not, so a host walks this once per
    ///         built frame and reads the flags every frame — <see cref="SceneRenderer.Nested" />
    ///         allocates.
    ///     </para>
    /// </remarks>
    public static SceneRenderer[] PathTo(SceneRenderer? root, UiRenderFeature feature) {
        ArgumentNullException.ThrowIfNull(feature);

        var path = new List<SceneRenderer>();

        return root is not null && Walk(root, feature, path) ? [.. path] : [];
    }

    static bool Walk(SceneRenderer node, UiRenderFeature feature, List<SceneRenderer> path) {
        path.Add(node);

        if (node is UiComposeRenderer composer && ReferenceEquals(composer.Feature, feature)) {
            return true;
        }

        foreach (var child in node.Nested) {
            if (Walk(child, feature, path)) {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);

        return false;
    }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        var source = frame.Texture(ToString(), Source);
        var described = frame.Graph.DescribeTexture(source);

        if ((described.Usage & TextureUsage.Sampled) == 0) {
            throw new CompositorBindingException(
                ToString(),
                "source",
                Source,
                "was not declared Sampled, so the interface cannot read the scene out of it. Add Sampled to "
                + "its usage — a swapchain image never has it, so render the frame into a target of its own "
                + "and copy it out after the interface"
            );
        }

        Degrade(Feature.Mismatched(new Int2(described.Width, described.Height)));

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Reads(source, ResourceState.ShaderRead);
                pass.SideEffect();

                pass.Execute(
                    context => {
                        Feature.Compose(
                            context.CommandList,
                            new UiBackdropSource(new Color4(0f, 0f, 0f, 0f)) { Image = context.View(source) }
                        );

                        ComposeCount++;
                    }
                );
            }
        );
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "UiCompose" : Name;
}

/// <summary>Builds <see cref="UiComposeRenderer" /> for a document that names <c>!UiCompose</c>.</summary>
/// <remarks>
///     Registered by <c>WorldRenderer</c>'s constructor on its own builder, with its own feature, so a
///     document names the node and never the feature — a host with two world renderers gets two
///     factories and each node composes the interfaces mounted in the renderer that built it.
/// </remarks>
/// <param name="feature">The feature every node this builds composes.</param>
public sealed class UiComposeFactory(UiRenderFeature feature) : ISceneRendererFactory {
    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) =>
        declared is UiComposeAsset asset
            ? new UiComposeRenderer {
                Name = asset.Name,
                Enabled = asset.Enabled,
                Feature = feature,
                Source = asset.Source
            }
            : null;
}
