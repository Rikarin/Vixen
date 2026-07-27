// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Renders a directional light's cascaded shadow map.
/// </summary>
/// <remarks>
///     <para>
///         The clearest thing a compositor buys, and the reason the collect phase exists at all: a
///         cascade is <em>a view</em>. Four cascades are four <see cref="RenderView" />s over one
///         stage, culled independently, sorted independently, and drawn into four tiles of one
///         texture. Nothing about the mesh feature, the material feature or the sort key changes to
///         support them.
///     </para>
///     <para>
///         <strong>One pass, four viewports.</strong> The atlas is cleared once and each cascade
///         draws into its tile, rather than four passes into four textures. On a tile-based GPU four
///         passes mean four loads and four stores of a depth buffer that is never read outside the
///         frame; on a desktop one they are four pipeline barriers for no reason.
///     </para>
///     <para>
///         <strong>The camera's parameters are here, not on the view.</strong> A
///         <see cref="RenderView" /> deliberately holds only what culling needs — a frustum and a
///         position — because a view is not a camera. Fitting a cascade needs the field of view and
///         the aspect ratio, which are the camera's, so the node that fits them is where they belong.
///     </para>
/// </remarks>
public sealed class ShadowMapRenderer : SceneRenderer {
    readonly List<RenderView> views = [];
    readonly ShadowCascade[] cascades = new ShadowCascade[ShadowCascades.MaxCascades];
    readonly float[] splits = new float[ShadowCascades.MaxCascades];
    int count;

    /// <summary>The stage that draws depth-only casters.</summary>
    public required RenderStage CasterStage { get; init; }

    /// <summary>The atlas to render into, and its format.</summary>
    public DepthTargetBinding? Atlas { get; set; }

    /// <summary>How many cascades to fit, up to <see cref="ShadowCascades.MaxCascades" />.</summary>
    public int CascadeCount { get; set; } = 4;

    /// <summary>One cascade's side in texels.</summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>Where the camera is.</summary>
    public Vector3 Eye { get; set; }

    /// <summary>Where it looks.</summary>
    public Vector3 Forward { get; set; } = new(0f, 0f, -1f);

    /// <summary>Its approximate up direction.</summary>
    public Vector3 Up { get; set; } = new(0f, 1f, 0f);

    /// <summary>Its vertical field of view, in radians.</summary>
    public float FieldOfView { get; set; } = MathF.PI / 3f;

    /// <summary>Its width divided by its height.</summary>
    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Where the first cascade starts.</summary>
    public float NearPlane { get; set; } = 0.1f;

    /// <summary>
    ///     How far shadows are drawn — the shadow distance, not the camera's far plane.
    /// </summary>
    /// <remarks>
    ///     Its own setting because the two are not the same number and never were. Cascades sized to
    ///     a two-kilometre view distance spend their whole budget on terrain nobody can see a shadow
    ///     on; this is the knob a quality preset moves.
    /// </remarks>
    public float ShadowDistance { get; set; } = 150f;

    /// <summary>How far to blend the splits from uniform toward logarithmic.</summary>
    public float SplitLambda { get; set; } = 0.75f;

    /// <summary>How far behind a cascade the light's near plane sits, so outside casters still cast.</summary>
    public float Extrusion { get; set; } = 50f;

    /// <summary>The direction light travels — away from the sun, toward the scene.</summary>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new(-0.4f, -1f, -0.3f));

    /// <summary>How far each cascade is culled to, which is its own end distance.</summary>
    public ReadOnlySpan<float> Splits => splits.AsSpan(0, count);

    /// <summary>The cascades this frame fitted.</summary>
    public ReadOnlySpan<ShadowCascade> Cascades => cascades.AsSpan(0, count);

    /// <summary>The views the cascades are drawn from, for a host that wants to inspect them.</summary>
    public IReadOnlyList<RenderView> Views => views;

    /// <summary>The atlas's size in texels, for whoever creates the texture.</summary>
    public Int2 AtlasSize => ShadowCascades.AtlasSize(Math.Clamp(CascadeCount, 1, ShadowCascades.MaxCascades), Resolution);

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        count = Math.Clamp(CascadeCount, 1, ShadowCascades.MaxCascades);
        ShadowCascades.Split(NearPlane, ShadowDistance, SplitLambda, splits.AsSpan(0, count));

        while (views.Count < count) {
            views.Add(new($"{Name}[{views.Count}]"));
        }

        var near = NearPlane;

        for (var i = 0; i < count; i++) {
            cascades[i] = ShadowCascades.Fit(
                Eye,
                Forward,
                Up,
                LightDirection,
                FieldOfView,
                AspectRatio,
                near,
                splits[i],
                Resolution,
                Extrusion
            );

            var view = views[i];
            view.Frustum = new(cascades[i].ViewProjection);

            // The light's position, not the camera's. Sorting a shadow cascade front-to-back is
            // front-to-back *from the light*, which is what early-Z in a depth-only pass rewards —
            // measuring from the camera would order the casters by something the pass never tests.
            view.Position = cascades[i].Centre - (Vector3.Normalize(LightDirection) * cascades[i].Radius);

            // No distance cutoff: the cascade's own frustum is already exactly as far as it reaches,
            // and a second limit measured from a synthetic light position would cut casters out of
            // the middle of it.
            view.MaximumDistance = 0f;

            compositor.Use(view, CasterStage);
            near = splits[i];
        }
    }

    /// <inheritdoc />
    protected internal override void Draw(GraphicsCompositor compositor, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(context);

        if (Atlas is not { } atlas || count == 0) {
            return;
        }

        var list = context.CommandList;
        var previous = context.Output;

        // No colour attachment at all. A shadow pass writes depth and nothing else, and a bound
        // colour target would be bandwidth spent on a value no one ever reads — on a mobile tiler,
        // the single most expensive mistake available in a shadow pass.
        context.Output = new([], atlas.Format);

        list.PushDebugGroup(ToString());
        list.BeginRenderPass(new([], atlas.ToAttachment(), Name));

        for (var i = 0; i < count; i++) {
            var viewport = ShadowCascades.TileViewport(i, count, Resolution);

            list.SetViewport(viewport);
            list.SetScissor(new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height));

            // The scissor is what makes one atlas safe. A caster whose triangle crosses the tile
            // edge would otherwise write into the neighbouring cascade, and a shadow that appears in
            // a cascade it was never fitted for is the artefact nobody can attribute.
            compositor.System.Record(views[i], CasterStage, context);
        }

        list.EndRenderPass();
        list.PopDebugGroup();

        context.Output = previous;
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "ShadowMap" : Name;
}
