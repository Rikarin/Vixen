// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     A snapshot of one target in another, as a transfer pass.
/// </summary>
/// <remarks>
///     <para>
///         <b>What
///         [35 § B1](../../../docs/plan/35-water.md#b1-there-is-no-shading-model-that-can-read-the-scene-behind-it)
///         turned out to need, and it is not the pass.</b> The compositor could already express "run
///         after deferred lighting, read the scene colour, write the scene colour" —
///         <c>ReflectionTrace</c> binds <c>sceneColor</c> today. What it could not express is the part
///         that makes that legal: sampling a target a pass is also writing is undefined behaviour, so
///         the read has to come from somewhere else, and that somewhere has to be a resource the graph
///         knows the lifetime of.
///     </para>
///     <para>
///         <b>The graph is what makes this worth a node.</b> A copy recorded by hand needs two
///         barriers around it, needs to know whether its source is still a colour attachment, and
///         becomes wrong the moment a pass is inserted before it. Declared, it is two resource uses
///         and the graph derives the rest — including dropping the whole copy when nothing reads the
///         destination, which is what makes a document that has a water node cost nothing in a scene
///         with no water.
///     </para>
///     <para>
///         ⚠ <b>Refuses rather than rescales.</b> A copy needs matching formats and matching sizes;
///         where they differ the operation a caller wants is a blit, which is a draw and a different
///         node. Silently taking the smaller region would produce a scene-colour copy that is correct
///         in the top-left corner, which is exactly the kind of wrong that survives review.
///     </para>
/// </remarks>
public sealed class TextureCopyRenderer : SceneRenderer {
    /// <summary>The name of the target to copy, which must be declared as a copy source.</summary>
    public required string Source { get; init; }

    /// <summary>The name of the target it goes into, which must be declared as a copy destination.</summary>
    public required string Destination { get; init; }

    /// <summary>How many frames have been copied.</summary>
    /// <remarks>
    ///     The one diagnostic this node has, and it answers the question it is asked: a refraction
    ///     that reads last frame's colours is a copy that never ran, and a copy that never ran is
    ///     usually a copy the graph culled because nothing reads the destination.
    /// </remarks>
    public int CopyCount { get; private set; }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        var source = frame.Texture(ToString(), Source);
        var destination = frame.Texture(ToString(), Destination);

        var from = frame.Graph.DescribeTexture(source);
        var into = frame.Graph.DescribeTexture(destination);

        if ((from.Usage & TextureUsage.CopySource) == 0) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Source,
                "was not declared as a copy source, so nothing can be read out of it. Add CopySource "
                + "to its usage — without it the copy is a validation error on a debug driver and "
                + "silently nothing on a release one"
            );
        }

        if ((into.Usage & TextureUsage.CopyDestination) == 0) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Destination,
                "was not declared as a copy destination. Add CopyDestination to its usage"
            );
        }

        if (from.Format != into.Format) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Destination,
                $"is {into.Format} and '{Source}' is {from.Format}. A copy moves texels rather than "
                + "converting them, so the two have to agree — a mismatch is a reinterpretation on the "
                + "backends that allow it at all"
            );
        }

        if (from.Width != into.Width || from.Height != into.Height) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Destination,
                $"is {into.Width}×{into.Height} and '{Source}' is {from.Width}×{from.Height}. A copy "
                + "does not rescale; declare the destination with the same size or the same scale, or "
                + "use a full-screen node if the resample is what was wanted"
            );
        }

        var size = new Int3(from.Width, from.Height, Math.Max(from.Depth, 1));
        CopyCount++;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Transfer;

                // Both declared, and the read is what places the barrier after whatever produced the
                // source. A copy that only declared its write would be ordered against nothing and
                // would move whatever the target held before the pass that filled it — which is the
                // previous frame's picture, and therefore looks almost right.
                pass.Reads(source, ResourceState.CopySource);
                pass.Writes(destination, ResourceState.CopyDestination);

                pass.Execute(
                    context => context.CommandList.CopyTexture(
                        new(context.Texture(source)),
                        new(context.Texture(destination)),
                        size
                    )
                );
            }
        );
    }
}
