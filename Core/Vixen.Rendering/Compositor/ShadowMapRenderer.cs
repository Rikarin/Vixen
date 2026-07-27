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
    readonly bool[] refitted = new bool[ShadowCascades.MaxCascades];
    int count;
    int fittedCount;
    int cachedStaticVersion = -1;

    /// <summary>The stage that draws depth-only casters.</summary>
    public required RenderStage CasterStage { get; init; }

    /// <summary>The atlas to render into, and its format.</summary>
    public DepthTargetBinding? Atlas { get; set; }

    /// <summary>
    ///     The stage that draws casters which do not move, or null to redraw everything every frame.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>A stage is the filter, and no new machinery is needed for it.</strong> "Which
    ///         objects, in what order" is exactly what a stage is, so a host puts level geometry in
    ///         this one and everything that moves in <see cref="CasterStage" />, and the cache falls
    ///         out: the static stage is drawn into <see cref="StaticAtlas" /> only when something
    ///         invalidates it, and every frame copies that into the working atlas and draws the
    ///         moving casters on top.
    ///     </para>
    ///     <para>
    ///         The copy is not free — it is a full depth atlas per frame — so this is a trade rather
    ///         than a win: it pays when a level's worth of static geometry would otherwise be
    ///         rasterised into four cascades every frame, and it does not when the scene is small.
    ///         Leaving it null keeps the uncached path exactly as it was.
    ///     </para>
    /// </remarks>
    public RenderStage? StaticCasterStage { get; set; }

    /// <summary>Where the cached static content lives. Needs a texture, because a copy names one.</summary>
    public DepthTargetBinding? StaticAtlas { get; set; }

    /// <summary>
    ///     How much larger than each slice to cut its cascade, as a fraction.
    /// </summary>
    /// <remarks>
    ///     Zero by default, which is the tightest fit and the one that re-fits whenever the camera
    ///     moves a texel. Caching wants slack — see <see cref="ShadowCascades.Fit" /> for what it
    ///     costs — and twenty per cent is the usual starting point.
    /// </remarks>
    public float Slack { get; set; }

    /// <summary>
    ///     Bumped by the host whenever a static caster is added, removed or moved.
    /// </summary>
    /// <remarks>
    ///     Supplied rather than detected, because "static" is a claim the scene makes and this cannot
    ///     check it. A host that moves a static caster and does not say so gets a shadow that stays
    ///     where the object was, which is the bargain the word already implied.
    /// </remarks>
    public int StaticVersion { get; set; }

    /// <summary>How many times the static atlas has been redrawn.</summary>
    /// <remarks>
    ///     For a test and for a profiler. The number a cached shadow map is judged by is how rarely
    ///     this moves — "it caches" is otherwise a claim nothing can check.
    /// </remarks>
    public int StaticRebuilds { get; private set; }

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
            var (centre, radius) = ShadowCascades.Sphere(Eye, Forward, FieldOfView, AspectRatio, near, splits[i]);

            // Kept when it still covers the slice. With no slack the cut is tight and this is almost
            // never true, which is correct — a cascade that no longer covers its slice would leave
            // the edge of the world unshadowed, and that is worse than redrawing it.
            refitted[i] = i >= fittedCount || !ShadowCascades.Covers(cascades[i], centre, radius);

            if (refitted[i]) {
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
                    Extrusion,
                    Slack
                );
            }

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

            if (StaticCasterStage is { } statics) {
                compositor.Use(view, statics);
            }

            near = splits[i];
        }

        fittedCount = count;
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

        var cached = StaticCasterStage is not null && StaticAtlas is { Texture.IsValid: true };

        if (cached) {
            DrawCached(compositor, context, atlas, StaticAtlas!.Value, StaticCasterStage!);
        } else {
            Pass(compositor, context, atlas.ToAttachment(), CasterStage, Name);
        }

        list.PopDebugGroup();
        context.Output = previous;
    }

    /// <summary>
    ///     Redraws the static atlas only when something invalidated it, then copies and adds the
    ///     movers.
    /// </summary>
    /// <remarks>
    ///     The invalidation is exactly two things, and both are things the atlas's <em>content</em>
    ///     depends on: a cascade that re-fitted, so what it covers changed; and a bump of
    ///     <see cref="StaticVersion" />, so what is in it changed. A camera that moved without either
    ///     happening changes nothing about the map, which is the whole point of cutting the cascade
    ///     with slack.
    /// </remarks>
    void DrawCached(
        GraphicsCompositor compositor,
        RenderDrawContext context,
        in DepthTargetBinding atlas,
        in DepthTargetBinding statics,
        RenderStage staticStage
    ) {
        if (!atlas.Texture.IsValid) {
            // No texture to copy *into*, so nothing cached could ever reach the working atlas.
            // Checked before the rebuild rather than after it, because filling a cache that cannot
            // be read is the one outcome worse than not caching. Drawing both stages here is exactly
            // what an uncached frame costs, and a slow shadow beats a missing one.
            Pass(compositor, context, atlas.ToAttachment(), staticStage, $"{Name}.Static");
            Pass(compositor, context, atlas.ToAttachment(LoadAction.Load), CasterStage, Name);
            return;
        }

        var stale = cachedStaticVersion != StaticVersion;

        for (var i = 0; i < count && !stale; i++) {
            stale = refitted[i];
        }

        if (stale) {
            Pass(compositor, context, statics.ToAttachment(), staticStage, $"{Name}.Static");
            cachedStaticVersion = StaticVersion;
            StaticRebuilds++;
        }

        var size = AtlasSize;
        context.CommandList.CopyTexture(new(statics.Texture), new(atlas.Texture), new(size.X, size.Y, 1));

        // Loaded, not cleared: what the copy just put there is the point.
        Pass(compositor, context, atlas.ToAttachment(LoadAction.Load), CasterStage, Name);
    }

    void Pass(
        GraphicsCompositor compositor,
        RenderDrawContext context,
        in DepthStencilAttachment attachment,
        RenderStage stage,
        string name
    ) {
        var list = context.CommandList;
        list.BeginRenderPass(new([], attachment, name));

        for (var i = 0; i < count; i++) {
            var viewport = ShadowCascades.TileViewport(i, count, Resolution);

            list.SetViewport(viewport);
            list.SetScissor(new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height));

            // The scissor is what makes one atlas safe. A caster whose triangle crosses the tile
            // edge would otherwise write into the neighbouring cascade, and a shadow that appears in
            // a cascade it was never fitted for is the artefact nobody can attribute.
            compositor.System.Record(views[i], stage, context);
        }

        list.EndRenderPass();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "ShadowMap" : Name;
}
