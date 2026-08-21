// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

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
///     <para>
///         <strong>And a cache, when the level's casters are put in a stage of their own.</strong>
///         <see cref="StaticCasterStage" /> plus <see cref="Slack" /> is the whole of it, and the two
///         are one switch: a cascade that re-fits every frame is a cache that is invalidated every
///         frame, which costs a whole-atlas copy and saves nothing. See <see cref="Slack" /> for what
///         stability is paid for in, and <see cref="StaticAtlas" /> for why the cache's texture is
///         this node's rather than the document's.
///     </para>
/// </remarks>
public sealed class ShadowMapRenderer : SceneRenderer, IDisposable {
    readonly List<RenderView> views = [];
    readonly ShadowCascade[] cascades = new ShadowCascade[ShadowCascades.MaxCascades];
    readonly float[] splits = new float[ShadowCascades.MaxCascades];
    readonly bool[] refitted = new bool[ShadowCascades.MaxCascades];
    int count;
    int fittedCount;
    int cachedStaticVersion = -1;

    // The cache's own atlas, when no host lends one under `StaticAtlas`. On
    // `PunctualShadowRenderer`'s terms and for its reason: depth that survives a frame cannot live in
    // the graph's pool, which exists precisely to recycle memory whose lifetime ends inside one.
    TextureHandle staticTexture;
    TextureViewHandle staticView;
    TextureDescription staticDescription;
    Int2 staticSize;
    bool staticBuilt;
    bool disposed;

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
    ///     Where to publish what a shading pass needs to read the atlas, or null to publish nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="SceneConstants" />' collection, normally — the shadow half of the frame's
    ///         set. The <em>texture</em> is not written here: it is a frame resource, so the pass that
    ///         declared it reads it is the one that may put it in front of a shader, and doing it from
    ///         here would hand over a handle whose barrier nobody declared. What is written here is
    ///         everything the atlas cannot supply about itself — the matrix, the texel size, the two
    ///         biases and the sampler.
    ///     </para>
    ///     <para>
    ///         <strong>Every cascade, with its own tile folded into its own matrix.</strong> The
    ///         shader picks between them per fragment, on the fragment's own view depth, which is
    ///         what a cascade <em>is</em>: distance deciding the resolution a surface is shadowed at.
    ///         This published cascade zero alone for a while, because the shader had one matrix and
    ///         one distance — so everything past the nearest slice projected outside its tile and
    ///         came back unshadowed, which reads as a shadow distance far shorter than the setting.
    ///     </para>
    ///     <para>
    ///         <see cref="CascadeCount" /> has to match the shader's <c>CascadeCount</c> permutation.
    ///         It sizes an array in the block, so the two disagreeing is a fragment reading a matrix
    ///         nobody wrote — the same agreement <c>MaxLights</c> needs, one array along.
    ///         <see cref="CascadeCountKey" /> is how the count reaches the compiler, and
    ///         <c>CompositorBuilder</c> is what calls it for a document's node.
    ///     </para>
    /// </remarks>
    public ParameterCollection? Scene { get; set; }

    /// <summary>Which pass's names to publish under.</summary>
    public string ShaderName { get; set; } = "ForwardPlus";

    /// <summary>Where the atlas's sampler comes from. Without one, no sampler is published.</summary>
    /// <remarks>
    ///     Shared rather than owned, for the reason every other node shares one: a sampler is pure
    ///     state and a device caps how many exist.
    /// </remarks>
    public SamplerCache? Samplers { get; set; }

    /// <summary>How far the depth comparison is nudged, <b>in metres</b>.</summary>
    /// <remarks>
    ///     ⚠ <b>Metres, and it used to be normalised depth</b> — 0.0005 of a range that is sixty
    ///     metres in the nearest cascade and several hundred in the furthest, so one number meant
    ///     centimetres in one and metres in the next. That is three artefacts at once: a hard line
    ///     along every cascade boundary where the effective bias jumps, acne inside a cascade where
    ///     it lands short, and light straight through a wall in the far ones where it lands so long
    ///     that nothing can be behind anything. <c>ShadowCascade.depthScale</c> is what converts it.
    /// </remarks>
    public float ConstantBias { get; set; } = 0.008f;

    /// <summary>How much more of that a surface gets as it turns away from the light, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Multiplied by up to ten, so read this as its worst case and size it against the
    ///     thinnest thing in the level.</b> A caster leaks when the bias approaches the depth of
    ///     material a light ray crosses — for a slab of thickness <c>t</c> under a sun at elevation
    ///     <c>e</c> that is <c>t / sin(e)</c>, so a 0.3 m roof at thirty degrees is 0.6 m of depth and
    ///     a bias of half a metre very nearly cancels it. Sunlight indoors, sweeping as the sun
    ///     moves, with the roof and walls demonstrably in the caster stage.
    ///
    ///     At a hundredth of a metre the worst case is 0.1 m against that 0.6, which is the margin a
    ///     bias wants. Back-face culling and <c>Lighting.NormalOffset</c> are what make so little
    ///     enough: the first records the near surface rather than the far one, and the second moves
    ///     the sample instead of the comparison.
    /// </remarks>
    public float SlopeBias { get; set; } = 0.01f;

    /// <summary>
    ///     Over what distance shadows fade out at the last cascade's end, in metres.
    /// </summary>
    /// <remarks>
    ///     Without a ramp the shadow term stops at a line across the ground, which reads as a
    ///     rendering error rather than as the end of <see cref="ShadowDistance" />. Ten metres against
    ///     a hundred and fifty is about a frame of walking, which is where it stops being visible.
    /// </remarks>
    public float FadeRange { get; set; } = 10f;

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
    ///     The name of a texture a host lends the cache, or empty for the node to own one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Whichever it is, it has to be an <see cref="ImportedTexture" /> rather than a declared
    ///         resource, and the reason is the definition of both: a cache outlives its frame, and the
    ///         graph's pool exists precisely to recycle memory whose lifetime ends inside one. A
    ///         document therefore <em>cannot</em> declare one — every <c>!Resource</c> is transient —
    ///         which is why empty means "the node makes its own" rather than "no cache". A frame that
    ///         kept every cascade would otherwise be a frame in which nothing writes the name the copy
    ///         reads, and a read with no producer is refused at compile.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A name that is given is resolved rather than probed.</b> Falling back to a texture
    ///         of this node's own when a host's name does not resolve would silently ignore the
    ///         binding the host asked for, which is the one thing a document can be wrong about.
    ///         Naming nothing needs a <see cref="Device" />; naming nothing with no device is refused
    ///         for the reason a static stage with no cache at all is.
    ///     </para>
    /// </remarks>
    public string StaticAtlas { get; set; } = string.Empty;

    /// <summary>Where the node's own cache texture comes from, when it owns one.</summary>
    /// <remarks>
    ///     Read only while <see cref="StaticCasterStage" /> is set and <see cref="StaticAtlas" /> names
    ///     nothing. <c>CompositorBuilder</c> passes the frame's device, so a document that turns
    ///     caching on gets a cache without having to describe a resource the graph has no way to keep.
    /// </remarks>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>
    ///     How much larger than each slice to cut its cascade, as a fraction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Zero by default, which is the tightest fit and the one that re-fits whenever the camera
    ///         moves a texel. Caching wants slack — see <see cref="ShadowCascades.Fit" /> for what it
    ///         costs — and twenty per cent is the usual starting point.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero with a static stage set is a cache that costs and never pays.</b> A tight fit
    ///         re-fits the frame the camera moves, a re-fit invalidates the cache, and the frame is
    ///         then the uncached one plus a whole-atlas copy. The node says so through
    ///         <see cref="SceneRenderer.Degraded" /> rather than quietly.
    ///     </para>
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

    /// <summary>How many of those rebuilds were a cascade re-fitting.</summary>
    /// <remarks>
    ///     <see cref="PunctualShadowRenderer.TilesMoved" />'s counterpart, and beside
    ///     <see cref="StaticInvalidations" /> for its reason: a cache that saves nothing saves nothing
    ///     for one of two reasons and they have opposite fixes. Re-fits dominating means
    ///     <see cref="Slack" /> is too small for how fast the camera moves — the projection itself will
    ///     not stand still — and no amount of care about what the scene claims is static will help.
    /// </remarks>
    public int StaticRefits { get; private set; }

    /// <summary>How many were the host saying the static content changed.</summary>
    /// <remarks>
    ///     The other half. These dominating means <see cref="StaticVersion" /> is being bumped by
    ///     something that is not actually static, which is a scene problem rather than a fitting one.
    /// </remarks>
    public int StaticInvalidations { get; private set; }

    /// <summary>How many cascade tiles were rasterised this frame.</summary>
    /// <remarks>
    ///     <see cref="PunctualShadowRenderer.TilesDrawn" />'s counterpart and the number this node is
    ///     judged by frame to frame: <see cref="CascadeCount" /> on a kept frame, twice that on a
    ///     rebuilt one, and exactly <see cref="CascadeCount" /> on every frame of the uncached path —
    ///     which is the honest way to read the trade, because a cache that rebuilds every frame costs
    ///     double and a copy.
    /// </remarks>
    public int TilesDrawn { get; private set; }

    /// <summary>How many cascades to fit, up to <see cref="ShadowCascades.MaxCascades" />.</summary>
    /// <remarks>
    ///     ⚠ <b>A shading pass has to be compiled for the same number</b>, which is not something this
    ///     node can do to itself — see <see cref="CascadeCountKey" />.
    /// </remarks>
    public int CascadeCount { get; set; } = 4;

    /// <summary>
    ///     The shader permutation that has to carry <see cref="CascadeCount" />, for the pass named.
    /// </summary>
    /// <param name="shaderName">The shading pass, as <see cref="ShaderName" /> names it.</param>
    /// <returns>The key, interned under <c>&lt;pass&gt;.CascadeCount</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong>The agreement this node's remarks have always described and nothing enforced.</strong>
    ///         <c>ClusteredShading.rvn</c> declares <c>[Permutation] val CascadeCount: int = 4</c> and
    ///         sizes <c>cascades[CascadeCount]</c> from it, so the block's <em>size</em> is per variant.
    ///         <see cref="Publish" /> fills as many slots as this node fitted; a variant compiled for
    ///         more folds its atlas into a grid of a different shape, so the matrices it does have
    ///         address the wrong tiles and the ones it does not are read out of memory nobody wrote.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The failure is a frame with no sun shadow in it, not a wrong one.</b> Two cascades
    ///         published into a four-cascade variant fold 2 × 1 against a lookup expecting 2 × 2, so
    ///         <c>CascadeContaining</c> answers −1 for most of the screen and the shadow term is one
    ///         everywhere — which is exactly what every project on the Low tier drew, that tier being
    ///         the one that ships <c>CascadeCount = 2</c>.
    ///     </para>
    ///     <para>
    ///         Given to <c>MaterialRenderFeature.SetPermutation</c>, which is the layer that decides
    ///         which variant a draw resolves to. A node cannot do it itself: the count belongs to the
    ///         frame rather than to an object or a material, and the feature that owns that third
    ///         layer is not something a compositor node holds.
    ///     </para>
    /// </remarks>
    public static PermutationKey<int> CascadeCountKey(string shaderName) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        return ParameterKeys.NewPermutation(ShaderDefaultCascades, $"{shaderName}.CascadeCount");
    }

    /// <summary>What <c>ClusteredShading.rvn</c> declares <c>CascadeCount</c> as.</summary>
    /// <remarks>
    ///     The shader's own number and not <see cref="ShadowCascades.MaxCascades" />, which happens to
    ///     equal it: the default a key is interned with is what a host that never sets one selects on,
    ///     so it has to be the value the compiler would have used. Raising the ceiling does not change
    ///     what the <c>.rvn</c> says.
    /// </remarks>
    const int ShaderDefaultCascades = 4;

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
    /// <remarks>
    ///     Each holds its <em>unfolded</em> matrix — what a foreign caster rasterises with under
    ///     this node's own tile viewports, the terrain caster being the one that does. The folded
    ///     form is <see cref="Publish" />'s and belongs to lookups alone: folding is
    ///     <c>NdcToUv</c>'s business, and rasterisation is the viewport's.
    /// </remarks>
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

        // ⚠ Both halves are fallbacks that draw. Every cascade is fitted to `camera`, so a node with
        // no Camera fits them to this type's authored Eye/Forward defaults — a frustum at the origin,
        // unrelated to the view being shaded — and the atlas is then filled with casters from
        // somewhere the player is not. The frame has four cascades in it and a shadow term that
        // samples them, and the shadows land in the wrong place; nothing else in the frame says why.
        // The sun is the same shape one input along: no Sun node leaves every cascade fitted to this
        // node's authored LightDirection rather than to the scene's.
        Degrade(
            (Camera, Sun?.Sun) switch {
                (null, null) => "no Camera and no Sun, so the cascades are fitted to a fabricated "
                    + "frustum at the node's authored Eye and lit from its authored LightDirection",
                (null, _) => "no Camera, so the cascades are fitted to a fabricated frustum at the "
                    + "node's authored Eye rather than to the view being shaded",
                (_, null) => "no Sun, so the cascades are fitted to the node's authored "
                    + "LightDirection rather than to the scene's sun",

                // Last, because the three above are wrong pictures and this one is only a wasted
                // frame — but it is a whole frame, and it is invisible: the cache rebuilds, the copy
                // runs, every counter says the feature is on and the total work is strictly greater
                // than leaving it off. See Slack.
                _ when StaticCasterStage is not null && Slack <= 0f =>
                    "a static caster stage with no Slack, so every cascade re-fits the moment the "
                    + "camera moves and the cache is rebuilt and copied every frame",
                _ => null
            }
        );

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
        Publish();
    }

    /// <summary>Hands a shading pass everything about the atlas that the atlas does not carry.</summary>
    void Publish() {
        if (Scene is not { } parameters || count == 0) {
            return;
        }

        var atlas = AtlasSize;

        for (var i = 0; i < count; i++) {
            var slot = $"{ShaderName}.cascades[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";

            // The tile folded in, not the cascade's own matrix. One atlas holds all of them, so a
            // raw matrix would land every lookup a quarter of the way into somebody else's tile —
            // and read a plausible depth from it.
            parameters.Set(
                ParameterKeys.New<Matrix4x4>($"{slot}.viewProjection"),
                ShadowCascades.AtlasProjection(cascades[i], i, count)
            );

            // The distance it is valid to, which is what the fragment selects on. Beside the matrix
            // in one record rather than in a second array, because a matrix used past the distance
            // it was fitted for is a shadow that looks like a shadow and is in the wrong place.
            parameters.Set(ParameterKeys.New<float>($"{slot}.split"), splits[i]);

            // One metre along the light, in this cascade's normalised depth — which is what lets the
            // two biases above be distances. The range is the fit's own: twice the extent it was cut
            // to, plus the extrusion that keeps casters behind it casting.
            var extent = cascades[i].Radius + (4f * cascades[i].Radius / Resolution);

            parameters.Set(
                ParameterKeys.New<float>($"{slot}.depthScale"),
                1f / MathF.Max((2f * extent) + Extrusion, 1e-3f)
            );
        }

        // The atlas's texel, not a tile's: the PCF taps step in the coordinates the lookup produced,
        // which the tile transform above just made atlas-wide.
        parameters.Set(
            ParameterKeys.New<Vector2>($"{ShaderName}.shadowTexelSize"),
            new(1f / atlas.X, 1f / atlas.Y)
        );

        parameters.Set(ParameterKeys.New<float>($"{ShaderName}.shadowConstantBias"), ConstantBias);
        parameters.Set(ParameterKeys.New<float>($"{ShaderName}.shadowSlopeBias"), SlopeBias);
        parameters.Set(ParameterKeys.New<float>($"{ShaderName}.shadowFadeRange"), FadeRange);

        if (Samplers is { } samplers) {
            // Clamped and nearest. Clamped because a fragment outside the cascade projects outside
            // the atlas, and a wrapped lookup would shadow it with whatever is on the opposite side.
            // Nearest because the tap compares *after* sampling: a linear sampler blends four depths
            // into a value that is no surface's, and the compare against it draws a gradient of
            // wrong answers along every silhouette — and linear filtering of Depth32Float is
            // optional in Vulkan besides. The PCF kernel supplies the softness itself.
            parameters.Set(
                ParameterKeys.New<SamplerHandle>($"{ShaderName}.shadowSampler"),
                samplers.PointClamp
            );
        }
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

        // The declared extent has to be this node's own arithmetic, checked here because the
        // failure is otherwise silent by construction: a tile viewport outside the texture is an
        // empty scissor, so the outer cascades simply never rasterize while the lookup's folded
        // matrices read squashed halves of the tiles that did. A document that says 8192 × 2048
        // over a 2 × 2 fold is exactly how the sun's shadows came to slide with the camera.
        var declared = frame.Graph.DescribeTexture(atlas);
        var required = AtlasSize;

        if (declared.Width != required.X || declared.Height != required.Y) {
            throw new CompositorBindingException(
                $"'{ToString()}' fits {count} cascade(s) at {Resolution} texels into a "
                + $"{required.X}x{required.Y} atlas, but '{Atlas}' is declared "
                + $"{declared.Width}x{declared.Height}. The resource's extent must be "
                + "ShadowCascades.AtlasSize(cascadeCount, resolution) or the outer tiles fall "
                + "outside the texture."
            );
        }

        if (StaticCasterStage is null) {
            TilesDrawn = count;
            Pass(compositor, frame, ToString(), atlas, format, LoadAction.Clear, CasterStage);

            return;
        }

        var statics = Cache(frame, format, required);

        // ⚠ **The invalidation rule, in one place and stated whole.** The cached half is kept when it
        // has been drawn at least once, when no cascade was re-fitted this frame, and when the host
        // has not bumped `StaticVersion` since it was drawn. Anything else redraws all of it.
        //
        // A re-fit is the load-bearing half and it is not a caster fact at all: the depth in the cache
        // was rasterised through one projection, and a cascade that moved is a cascade whose tile now
        // means something else. That is also why this cache does *not* hash its caster list the way
        // `PunctualShadowRenderer` has to — a cascade's content is separated by **stage** rather than
        // detected, so the conservative work list a GPU-culled frame hands the host cannot mislead it.
        // The price of that is the bargain the word "static" already implied: a host that moves an
        // object in the static stage without saying so gets a shadow where the object used to be.
        var refit = false;

        for (var i = 0; i < count; i++) {
            refit |= refitted[i];
        }

        var bumped = cachedStaticVersion != StaticVersion;
        var stale = !staticBuilt || refit || bumped;

        TilesDrawn = stale ? 2 * count : count;

        if (stale) {
            Pass(compositor, frame, $"{Name}.Static", statics, format, LoadAction.Clear, StaticCasterStage!);
            cachedStaticVersion = StaticVersion;
            staticBuilt = true;
            StaticRebuilds++;

            // Attributed rather than split in two, because the first frame is neither: nothing has
            // been drawn yet, and counting that as a re-fit or as the host's doing would put a one in
            // a number a profiler reads as a rate.
            if (refit) {
                StaticRefits++;
            } else if (bumped) {
                StaticInvalidations++;
            }
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

    /// <summary>The texture the cached half lives in — the host's when it lends one, else this node's.</summary>
    /// <remarks>
    ///     Both are imports. A declared resource is one the graph's pool may hand to something else the
    ///     moment the last pass reading it ends, which is the definition of the wrong memory for a
    ///     cache; and on a frame where every cascade was kept, nothing writes it at all, so a declared
    ///     one would additionally be a read with no producer and refused at compile.
    /// </remarks>
    GraphTexture Cache(CompositorFrame frame, PixelFormat format, Int2 required) {
        if (StaticAtlas.Length > 0) {
            // Resolved rather than probed. A cache whose name is bound to nothing is a configuration
            // mistake, and quietly substituting a texture of this node's own would ignore the binding
            // the document asked for.
            return frame.Texture(ToString(), StaticAtlas);
        }

        if (Device is not { } device) {
            // Half a cache is worse than none: the static casters would be in a stage nothing draws,
            // so a level's shadows would simply not appear, and nothing would say why.
            throw new CompositorBindingException(
                $"'{ToString()}' has a static caster stage, names no static atlas and has no Device "
                + "to make one with, so every static caster would be dropped rather than drawn."
            );
        }

        Allocate(device, required, format);

        // The entry state is the whole difference between the first frame and the rest: nothing has
        // been written yet, so the contents are `Undefined` and a backend may discard them. Claiming
        // `CopySource` on frame one is a transition from a layout the texture is not in, which is a
        // validation error on Vulkan and undefined behaviour without the layer.
        return frame.Graph.ImportTexture(
            staticTexture,
            staticView,
            staticDescription,
            staticBuilt ? ResourceState.CopySource : ResourceState.Undefined,
            ResourceState.CopySource
        );
    }

    /// <summary>Creates the node's own cache texture, or recreates it when its shape changed.</summary>
    /// <remarks>
    ///     A resolution or a cascade count that moved makes every cached texel meaningless — a tile is
    ///     a different rectangle of a different texture — so the content is forgotten with the memory.
    ///     It costs one frame of full redraw on a knob nothing turns per frame.
    /// </remarks>
    void Allocate(IGraphicsDevice device, Int2 size, PixelFormat format) {
        if (staticSize == size && staticTexture.IsValid && staticDescription.Format == format) {
            return;
        }

        Release(device);

        staticDescription = new(
            format,
            Math.Max(size.X, 1),
            Math.Max(size.Y, 1),

            // No `Sampled`: nothing reads this texture through a shader. What reads it is the copy
            // into the working atlas, and that is the only thing a cascade cache's texture is for —
            // unlike `PunctualShadowRenderer`'s, which *is* the atlas a shading pass resolves.
            TextureUsage.DepthStencilTarget | TextureUsage.CopySource,
            Name: $"{this}.StaticCache"
        );

        staticTexture = device.CreateTexture(staticDescription);
        staticView = device.CreateTextureView(staticTexture);
        staticSize = size;
        staticBuilt = false;
    }

    void Release(IGraphicsDevice device) {
        if (staticView.IsValid) {
            device.Destroy(staticView);
        }

        if (staticTexture.IsValid) {
            device.Destroy(staticTexture);
        }

        staticView = default;
        staticTexture = default;
        staticSize = default;
        staticBuilt = false;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (Device is { } device) {
            Release(device);
        }
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
