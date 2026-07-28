// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

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

    /// <summary>The per-view block to bind before each cascade's casters.</summary>
    /// <remarks>
    ///     A cascade is a view, and this is how it tells a caster which projection it is being drawn
    ///     for. Bound per cascade inside the pass rather than once for the node, because that is the
    ///     whole distinction between the tiles: they differ by nothing except their view.
    /// </remarks>
    public ViewConstants? Constants { get; set; }

    /// <summary>The name of the atlas to render into.</summary>
    public string Atlas { get; set; } = string.Empty;

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

    /// <summary>
    ///     The name of the texture the cached static content lives in, or empty for no cache.
    /// </summary>
    /// <remarks>
    ///     This one has to be an <see cref="ImportedTexture" /> rather than a declared resource, and
    ///     the reason is the definition of both: a cache outlives its frame, and the graph's pool
    ///     exists precisely to recycle memory whose lifetime ends inside one.
    /// </remarks>
    public string StaticAtlas { get; set; } = string.Empty;

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

    /// <summary>
    ///     The view whose frustum the cascades are fitted to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The camera taken from the frame rather than copied into this node. A view carrying a
    ///         <see cref="RenderView.Camera" /> supplies every one of the seven scalars below, and
    ///         they are then ignored — which is the point, because a host setting both is a host that
    ///         can set them differently. A cascade fitted to a field of view the camera no longer has
    ///         puts the shadow distance somewhere the setting does not say, and nothing reports it.
    ///     </para>
    ///     <para>
    ///         Null, or a view with no camera, leaves the scalars in charge. A test fitting cascades
    ///         to a hypothetical camera has no view to point at, and neither has a tool.
    ///     </para>
    /// </remarks>
    public RenderView? Camera { get; set; }

    /// <summary>
    ///     Where the sun comes from, when it comes from the scene.
    /// </summary>
    /// <remarks>
    ///     The other half of the same argument. A scene's brightest directional light is what casts
    ///     its cascaded shadows, and a host copying that direction onto this node every frame is a host
    ///     that will one day forget — leaving a level lit from one direction and shadowed from
    ///     another. Null leaves <see cref="LightDirection" /> in charge.
    /// </remarks>
    public ISunSource? Sun { get; set; }

    /// <summary>Where the camera is, when no <see cref="Camera" /> says.</summary>
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
        // The frame's camera and the frame's sun, when there are any. Read once here rather than
        // per cascade, so that every cascade in a frame is fitted to the same description even if
        // something changed the scene between them.
        var camera = Camera?.Camera ?? new RenderCamera(Eye, Forward, Up, FieldOfView, AspectRatio, NearPlane, 0f);
        var light = Sun?.Sun is { } sun ? sun.Direction : LightDirection;

        ShadowCascades.Split(camera.NearPlane, ShadowDistance, SplitLambda, splits.AsSpan(0, count));

        while (views.Count < count) {
            views.Add(new($"{Name}[{views.Count}]"));
        }

        var near = camera.NearPlane;

        for (var i = 0; i < count; i++) {
            var (centre, radius) = ShadowCascades.Sphere(
                camera.Position,
                camera.Forward,
                camera.FieldOfView,
                camera.AspectRatio,
                near,
                splits[i]
            );

            // Kept when it still covers the slice. With no slack the cut is tight and this is almost
            // never true, which is correct — a cascade that no longer covers its slice would leave
            // the edge of the world unshadowed, and that is worse than redrawing it.
            refitted[i] = i >= fittedCount || !ShadowCascades.Covers(cascades[i], centre, radius);

            if (refitted[i]) {
                cascades[i] = ShadowCascades.Fit(
                    camera.Position,
                    camera.Forward,
                    camera.Up,
                    light,
                    camera.FieldOfView,
                    camera.AspectRatio,
                    near,
                    splits[i],
                    Resolution,
                    Extrusion,
                    Slack
                );
            }

            var view = views[i];
            // The matrix, which derives the frustum — not the frustum alone. A view whose planes
            // came from a matrix it does not carry is one a shader cannot be told how to project
            // into, which is what kept a caster from knowing its own cascade.
            view.ViewProjection = cascades[i].ViewProjection;

            // The light's position, not the camera's. Sorting a shadow cascade front-to-back is
            // front-to-back *from the light*, which is what early-Z in a depth-only pass rewards —
            // measuring from the camera would order the casters by something the pass never tests.
            view.Position = cascades[i].Centre - (Vector3.Normalize(light) * cascades[i].Radius);

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
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (count == 0 || Atlas.Length == 0) {
            return;
        }

        var atlas = frame.Texture(ToString(), Atlas);
        var format = frame.FormatOf(ToString(), Atlas);

        if (StaticCasterStage is null) {
            Pass(compositor, frame, ToString(), atlas, format, LoadAction.Clear, CasterStage);
            return;
        }

        if (StaticAtlas.Length == 0) {
            // Half a cache is worse than none: the static casters would be in a stage nothing draws,
            // so a level's shadows would simply not appear, and nothing would say why. The name is
            // missing rather than the texture, which is a thing a document can be wrong about.
            throw new CompositorBindingException(
                $"'{ToString()}' has a static caster stage and no static atlas to cache it in, so "
                + "every static caster would be dropped rather than drawn."
            );
        }

        // Resolved rather than probed. A cache whose name is bound to nothing is a configuration
        // mistake, and falling back to the uncached path would draw the moving casters and silently
        // lose every static one.
        var statics = frame.Texture(ToString(), StaticAtlas);
        var stale = cachedStaticVersion != StaticVersion;

        for (var i = 0; i < count && !stale; i++) {
            stale = refitted[i];
        }

        if (stale) {
            Pass(compositor, frame, $"{Name}.Static", statics, format, LoadAction.Clear, StaticCasterStage!);
            cachedStaticVersion = StaticVersion;
            StaticRebuilds++;
        }

        // Declared as a copy, so the graph is the one that knows the cache has to be a copy source and
        // the working atlas a copy destination before this runs — and, when the static pass did run
        // this frame, that it has to finish first. Hand-written, those are the two barriers a shadow
        // cache gets wrong.
        var size = AtlasSize;

        frame.Graph.AddPass(
            $"{Name}.Copy",
            pass => {
                pass.Reads(statics, ResourceState.CopySource);
                pass.Writes(atlas, ResourceState.CopyDestination);

                pass.Execute(
                    context => context.CommandList.CopyTexture(
                        new(context.Texture(statics)),
                        new(context.Texture(atlas)),
                        new(size.X, size.Y, 1)
                    )
                );
            }
        );

        // Loaded, not cleared: what the copy just put there is the point.
        Pass(compositor, frame, ToString(), atlas, format, LoadAction.Load, CasterStage);
    }

    /// <summary>Declares one pass that draws a stage into every cascade's tile.</summary>
    void Pass(
        GraphicsCompositor compositor,
        CompositorFrame frame,
        string name,
        GraphTexture target,
        PixelFormat format,
        LoadAction load,
        RenderStage stage
    ) {
        frame.Graph.AddPass(
            name,
            pass => {
                pass.DepthAttachment(target, load);

                pass.Execute(
                    graphContext => {
                        var context = frame.Context(graphContext.CommandList);
                        var previous = context.Output;

                        // No colour attachment at all. A shadow pass writes depth and nothing else, and
                        // a bound colour target would be bandwidth spent on a value no one ever reads —
                        // on a mobile tiler, the most expensive mistake available in a shadow pass.
                        context.Output = new([], format);

                        for (var i = 0; i < count; i++) {
                            var viewport = ShadowCascades.TileViewport(i, count, Resolution);

                            graphContext.CommandList.SetViewport(viewport);

                            graphContext.CommandList.SetScissor(
                                new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height)
                            );

                            // The scissor is what makes one atlas safe. A caster whose triangle crosses
                            // a tile edge would otherwise write into the neighbouring cascade, and a
                            // shadow in a cascade it was never fitted for is the artefact nobody can
                            // attribute.
                            context.ViewConstants = Constants;
                            compositor.System.Record(views[i], stage, context);
                        }

                        context.Output = previous;
                    }
                );
            }
        );
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "ShadowMap" : Name;
}
