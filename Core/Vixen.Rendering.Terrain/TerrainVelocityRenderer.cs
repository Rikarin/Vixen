// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.Terrain;

/// <summary>The terrain velocity node the transformer splices after the frame's velocity pass.</summary>
/// <remarks>
///     <para>
///         <b>Internal, because the transformer is its only author</b> —
///         <see cref="TerrainCasterNodeAsset" />'s argument, at the other end of the frame:
///         <see cref="TerrainFactory" /> inserts one wherever a document holds both a <c>!Terrain</c>
///         node and a render pass drawing a <c>Motion</c> stage, so a standard frame under TAA gets
///         ground velocity from the same <c>extensions:</c> line that got it ground.
///     </para>
///     <para>
///         The names are copied from the velocity pass it follows rather than from the terrain node,
///         because the contract is the frame's: whatever target the frame's own motion vectors land
///         in is the one the ground's must join, cleared by that pass and loaded by this one.
///     </para>
/// </remarks>
sealed record TerrainVelocityNodeAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = "TerrainVelocity";

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The motion target to draw into — the frame's velocity pass's own colour target.</summary>
    public string Motion { get; init; } = "SceneMotion";

    /// <summary>The scene depth to test against, read-only — the velocity pass's own.</summary>
    public string Depth { get; init; } = "SceneDepth";
}

/// <summary>
///     Draws the ground stack's motion vectors, directly after the frame's velocity pass.
/// </summary>
/// <remarks>
///     <para>
///         <b>Its own node, because position in the frame is the whole point</b> —
///         <see cref="TerrainCasterRenderer" />'s argument, mirrored: the graph runs passes in
///         declaration order, and the frame's velocity pass <em>clears</em> the motion target before
///         the extracted Motion-stage objects draw into it. A velocity pass declared by
///         <see cref="TerrainSceneRenderer" /> — which builds at the afterOpaque seam, before the
///         velocity pass — would be wiped by that clear before TAA ever read it. This node sits
///         directly after, so the ground's vectors join the meshes' in the same plane.
///     </para>
///     <para>
///         <b>Why the ground must write velocity at all.</b> <c>Taa.rvn</c> reprojects with the
///         motion texture and nothing else — its <c>depthBuffer</c> binding is declared and unread,
///         so there is no camera-only fallback reconstructed from depth. An unwritten texel reads
///         the cleared zero, which means "this pixel did not move on screen": under any camera
///         motion the history lands on the wrong surface and only the variance clip contains it,
///         which is the smear. Static geometry therefore owes the resolve its camera term, and the
///         wind-blown grass owes its sway on top — which is why all three of the stack's draws
///         reproject here rather than the grass alone.
///     </para>
///     <para>
///         ⚠ <b>The work is the surface node's, resolved at execute</b> — the caster's arrangement,
///         with the opposite build order: this node builds <em>after</em> the surface node, so by
///         the time its pass records, the surface's upload pass has staged every velocity block and
///         set this frame. The recording itself lives on <see cref="TerrainSceneRenderer" />, which
///         owns the draw sets and their lifetimes; this node contributes the pass at the right
///         position and the attachments under the frame's names.
///     </para>
/// </remarks>
sealed class TerrainVelocityRenderer : SceneRenderer {
    /// <summary>The motion target this draws into.</summary>
    public string Motion { get; init; } = "SceneMotion";

    /// <summary>The scene depth it tests against, read-only.</summary>
    public string Depth { get; init; } = "SceneDepth";

    /// <summary>The device, or null for a node that declines to draw.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The surface node whose draw sets the reprojection borrows — the factory pairs the two.</summary>
    /// <remarks>Null draws nothing quietly: a velocity node no terrain node answered is a document
    ///     holding half the contract, and half a contract moves nothing.</remarks>
    internal TerrainSceneRenderer? Surfaces { get; set; }

    /// <summary>How many draws the last frame reprojected — terrains, grass fields and foliage batches.</summary>
    public int VelocityDraws { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        VelocityDraws = 0;

        if (Device is null || Surfaces is not { } surfaces) {
            return;
        }

        // The surface node built earlier this frame and decided whether the velocity path is live —
        // the target exists, the three shaders resolved, something is drawn. Nothing staged means
        // nothing to record, and a pass with no draws is still a clear-order hazard worth skipping.
        if (!surfaces.MotionVectors) {
            return;
        }

        var motion = frame.Texture(ToString(), Motion);
        var depth = frame.Texture(ToString(), Depth);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // Loaded, never cleared: the frame's own velocity pass cleared this plane and the
                // extracted meshes are already in it — the ground joins, exactly as its colour
                // joins the Main pass's picture. The depth is read-only on the frame pass's terms:
                // a fragment that lost the test is behind something and has no business writing
                // its velocity.
                pass.ColourAttachment(motion, LoadAction.Load, default);
                pass.DepthAttachment(depth, LoadAction.Load, 0f, readOnly: true);

                pass.Execute(context => VelocityDraws = surfaces.RecordVelocity(context.CommandList));
            }
        );
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "TerrainVelocity" : Name;
}
