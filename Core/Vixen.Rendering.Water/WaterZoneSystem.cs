// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>Where a body's curve comes from.</summary>
/// <remarks>
///     <para>
///         A seam rather than a reference to the asset system, for the reason
///         <see cref="IWaterGround" /> is one: a test's spline is a literal, an editor's is a document
///         being dragged, and a game's is a loaded <c>.vxspline</c> — and the fold has no business
///         knowing which.
///     </para>
///     <para>
///         ⚠ <b>Answering null is not an error, it is a body whose spline has not loaded.</b> A fold
///         that threw would fail a frame during streaming; a fold that silently skipped would leave an
///         author looking at dry ground with nothing to read. It skips and counts —
///         <see cref="WaterZoneSystem.UnresolvedBodies" /> — <b>and it asks again on the next fold</b>,
///         which is what makes "not loaded yet" different from "does not exist". A source that will
///         never have the curve is expected to remember that itself and answer cheaply, which is what
///         <c>AssetWaterSource</c>'s failed entries are for.
///     </para>
/// </remarks>
public interface IWaterSplineSource {
    /// <summary>The curve a body names, or <see langword="null" /> if it is not available.</summary>
    /// <param name="name">What the component named.</param>
    /// <param name="placement">Where the entity carrying the body is.</param>
    /// <returns>The curve in world space, or null.</returns>
    Spline? SplineFor(string name, in Matrix4x4 placement);
}

/// <summary>Where a zone's sea state comes from.</summary>
/// <remarks>
///     <para>
///         <see cref="IWaterSplineSource" />'s twin, one asset kind over, and a seam for the same
///         reason: a test's spectrum is a literal, an editor's is a document being dragged, and a
///         game's is a loaded <c>.vxwaves</c>. The fold has no business knowing which.
///     </para>
///     <para>
///         ⚠ <b>Answering null falls back to the zone's inline spectrum rather than to a flat sea.</b>
///         See <see cref="WaterZoneComponent.WaveAsset" /> for why this is the opposite of a body's
///         unresolved spline: a zone still has a perfectly good window, and rendering it dead flat
///         reads as the water stack being broken rather than as one asset still streaming.
///         <see cref="WaterZoneSystem.UnresolvedWaves" /> is what says it happened.
///     </para>
///     <para>
///         ⚠ <b>No placement argument, and the absence is deliberate.</b> A spline is geometry and is
///         asked for in world space; a sea state is a wind and a seed, and a spectrum that depended on
///         where the zone entity sat would be one a client and a server could disagree about — which
///         is the one thing
///         [§ D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)
///         makes determinism an exit criterion for.
///     </para>
/// </remarks>
public interface IWaterWaveSource {
    /// <summary>The sea state a zone names, or <see langword="null" /> if it is not available.</summary>
    /// <param name="name">What the component named.</param>
    /// <returns>The spectrum, or null.</returns>
    WaterWaveSpectrum? SpectrumFor(string name);
}

/// <summary>
///     Folds a world's zones and bodies into the fields everything else reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)'s
///         zone, wired to a scene.</b> Every <see cref="WaterBodyComponent" /> is claimed by whichever
///         zones' windows reach it, and each zone re-rasterises only when it has to.
///     </para>
///     <para>
///         ⚠ <b>A body with no zone renders nothing, and the count is what says so.</b> That is
///         Unreal's rule — <c>AWaterZone</c> must exist or nothing renders — kept because the field is
///         the interchange every consumer reads. What is not kept is discovering it from a blank
///         frame: <see cref="ZonelessBodies" /> is a number an author can look at, and it is the
///         answer to "I placed a lake and there is no water".
///     </para>
///     <para>
///         ⚠ <b>Bodies are cached by identity, and that is what makes the whole amortisation real.</b>
///         A fold that built a fresh <see cref="WaterBody" /> every frame would hand the zone a
///         different list every frame, mark the field dirty every frame, and re-rasterise every frame
///         — which is the cost § D3's threshold exists to avoid, paid in full and invisibly. So a body
///         is rebuilt only when its component or its transform actually changed, and the zone compares
///         by reference.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" /></b>, after <c>TransformSystem</c> has written
///         <see cref="WorldTransform" />, so a body that moved this frame is rasterised where it now
///         is rather than where it was.
///     </para>
/// </remarks>
/// <param name="view">The view whose position the windows are centred on.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class WaterZoneSystem(RenderView view)
    : SystemBase, IDeclaredAccess, IPostProcessShapeSource, IWaterSurface {
    // ⚠ Zones carry no transform requirement because nothing about a zone reads one: the window and
    // its claim both follow the view — see Reaches — and the entity's transform only places it in
    // the hierarchy. Bodies do require one, because a body is rasterised where its spline is.
    readonly QueryDescription zoneQuery = new QueryDescription().WithAll<WaterZoneComponent>();
    readonly QueryDescription bodyQuery = new QueryDescription().WithAll<WaterBodyComponent, WorldTransform>();

    readonly Dictionary<Entity, WaterZoneState> states = [];
    readonly Dictionary<Entity, Built> built = [];

    // ⚠ One query per zone, rebuilt only when its sea state changed. A WaterQuery sums a spectrum in
    // its constructor and QueryAt is asked once per pontoon per fixed step, which for a river of
    // crates is thousands of times a second — see QueryAt.
    readonly Dictionary<Entity, (WaterWaveSpectrum Spectrum, float Attenuation, WaterQuery Query)> queries = [];
    readonly List<(Entity Entity, WaterZoneComponent Component)> zones = [];
    readonly List<WaterBody> resolved = [];
    readonly List<WaterBody> claimed = [];
    readonly List<Entity> stale = [];

    /// <summary>The view this centres its windows on.</summary>
    public RenderView View { get; } = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>Where a body's curve comes from.</summary>
    public IWaterSplineSource? Splines { get; set; }

    /// <summary>Where a zone's sea state comes from, for the zones that name a <c>.vxwaves</c>.</summary>
    /// <remarks>
    ///     Left unset, every zone uses its own inline <see cref="WaterZoneComponent.Waves" /> and any
    ///     that named an asset counts into <see cref="UnresolvedWaves" /> — which is a running frame
    ///     with plausible water and a number that says the sharing is not wired, rather than a flat sea.
    /// </remarks>
    public IWaterWaveSource? Waves { get; set; }

    /// <summary>Where the ground under the water is.</summary>
    /// <remarks>
    ///     ⚠ <b>The terrain is a first-class producer and not a component somebody remembers to
    ///     attach</b> — § D3. Left as it is, every zone sits on flat ground at zero, which is what an
    ///     ocean with no terrain under it wants and is visibly wrong for a lake in a valley.
    /// </remarks>
    public IWaterGround Ground { get; set; } = new FlatWaterGround(0f);

    /// <summary>How many zones the last fold saw.</summary>
    public int ZoneCount { get; private set; }

    /// <summary>How many bodies resolved to a curve.</summary>
    public int BodyCount { get; private set; }

    /// <summary>How many bodies no zone's window reached.</summary>
    /// <remarks>
    ///     The answer to "I placed a lake and there is no water". A body outside every zone is
    ///     rasterised by nothing, which is Unreal's rule kept and its diagnostic added.
    /// </remarks>
    public int ZonelessBodies { get; private set; }

    /// <summary>How many bodies named a spline nothing could supply.</summary>
    /// <remarks>
    ///     Distinct from <see cref="ZonelessBodies" /> because the fix is different: one is a zone
    ///     that does not reach, the other is an asset that has not loaded or a name that is wrong.
    ///     ⚠ <b>Every body counted here is asked for again by the next fold</b> — see
    ///     <see cref="IWaterSplineSource" /> — so a number that falls to zero a few frames into a level
    ///     is streaming working, and one that stays is content.
    /// </remarks>
    public int UnresolvedBodies { get; private set; }

    /// <summary>How many zones named a <c>.vxwaves</c> that could not be used.</summary>
    /// <remarks>
    ///     ⚠ <b>A zone counted here is still drawing water</b> — its inline
    ///     <see cref="WaterZoneComponent.Waves" /> is what it falls back to — so this is the only
    ///     evidence that the sea somebody is looking at is not the one they authored. Without it, a
    ///     misspelt asset name is a sea that looks fine and is a different sea on the server.
    ///     Both failures count into it: an asset nothing could supply, and one that arrived carrying a
    ///     spectrum <see cref="WaterWaveSpectrum.Validate" /> refuses.
    /// </remarks>
    public int UnresolvedWaves { get; private set; }

    /// <summary>How many bodies were rebuilt by the last fold.</summary>
    /// <remarks>
    ///     ⚠ <b>The reading that says the cache is working.</b> A number that equals
    ///     <see cref="BodyCount" /> every frame is a fold rebuilding everything, which marks every
    ///     zone dirty and re-rasterises every field — the cost the whole threshold exists to avoid,
    ///     paid in full and invisible in a picture.
    /// </remarks>
    public int RebuiltBodies { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for <c>LightExtractionSystem</c>'s reason: naming a
    ///     component type in a generic call is what assigns it an id, and on the first frame an
    ///     attribute would have nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<WaterZoneComponent>()
        .Read<WaterBodyComponent>()
        .Build();

    /// <summary>The state of a zone, by the entity carrying it.</summary>
    public IReadOnlyDictionary<Entity, WaterZoneState> States => states;

    /// <summary>What each zone entity said, as the last fold read it — with its names resolved.</summary>
    /// <remarks>
    ///     <para>
    ///         Beside <see cref="States" /> rather than folded into it, because the two are different
    ///         things: a <see cref="WaterZoneState" /> is the kernel's — a window, a field and when it
    ///         was last right — and the component is the document's, carrying the sea state and the
    ///         attenuation that the <em>renderer</em> needs and the kernel has no use for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not quite verbatim: a zone naming a <c>.vxwaves</c> that resolved has that
    ///         spectrum in its <see cref="WaterZoneComponent.Waves" /> rather than the one on disk.</b>
    ///         The name becomes a value in one place — see <c>Resolve</c> — so that every consumer
    ///         reads one field and none of them has to know the asset exists. What a consumer wanting
    ///         the authored document must do is read the world, not this.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<(Entity Entity, WaterZoneComponent Component)> Zones => zones;

    /// <summary>The simulation's water time, in seconds — the one clock the whole stack reads.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>[§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)'s
    ///         first consequence, and there is exactly one of it in a running game.</b> The vertex
    ///         stage reads it through <see cref="WaterMeshRenderer.WaterTime" />, the underwater
    ///         volume reads it through <see cref="ShapeFor" />, and a buoyancy solver reads it
    ///         directly. A second place to write it is a vertex stage a frame ahead of a solver, which
    ///         is a boat that hovers — invisible until the frame rate changes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Advanced from <c>GameTime.Total</c> and not from a delta this accumulates.</b> The
    ///         fixed-step simulation and the render both read the same accumulated clock, which is
    ///         what the doc means by "a value derived from the same clock"; a system summing its own
    ///         deltas would drift from the physics step by exactly the rounding, and would keep
    ///         running while the game was paused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written by <see cref="WaterClockSystem" /> and by nothing else, which is the rule
    ///         made structural rather than asked for in a comment.</b> This system folds in
    ///         <see cref="SystemPhase.PreRender" /> because a body has to be rasterised where
    ///         <c>TransformSystem</c> has just put it — and <see cref="SystemPhase.FixedUpdate" />,
    ///         where a buoyancy solver runs, is <em>earlier in the same frame</em>. Advancing the clock
    ///         here would hand the solver last frame's value while the vertex stage drew this one's,
    ///         which is a boat exactly one frame of swell behind the water under it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Left at zero if no clock system runs, which is a still sea and is meant to be
    ///         obvious.</b> The alternative — a fallback advance in this system's own
    ///         <see cref="Update" /> — is a second writer, and a second writer is what the whole rule
    ///         is against.
    ///     </para>
    ///     <para>
    ///         Settable so a test, a tool or a cinematic can pin it. <see cref="Fold" /> does not
    ///         touch it.
    ///     </para>
    /// </remarks>
    public float WaterTime { get; set; }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>What lets <c>Vixen.Water.Physics</c> exist without linking a device</b> — see
    ///         <see cref="IWaterSurface" />. A buoyancy solver holds this interface and never learns
    ///         that the thing answering it also uploads textures.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first zone whose window contains the place wins, and overlapping zones are
    ///         not blended.</b> Two zones over one lake is an authoring mistake rather than a case to
    ///         resolve — they would each rasterise the same bodies at different resolutions, and a
    ///         solver averaging two surfaces would float a boat between them. Bodies are what
    ///         overlap and are resolved by priority; zones are windows, and a window is one or none.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The query is cached per zone rather than built per ask.</b>
    ///         <see cref="WaterZoneState.Query" /> allocates and sums the spectrum, and this is asked
    ///         once per pontoon per fixed step — which for a river of crates is thousands of times a
    ///         second. The cache is refreshed in <see cref="Fold" />, so a sea state an author is
    ///         dragging reaches the boats on the next fold rather than the next ask.
    ///     </para>
    /// </remarks>
    public WaterQuery? QueryAt(Vector2 position) {
        foreach (var (entity, _) in zones) {
            if (!states.TryGetValue(entity, out var state) || state.Field is null) {
                continue;
            }

            // ⚠ The window's own corner and extent, not the component's. A window slides and snaps —
            // see WaterZoneState.Update — so a containment test against the authored extent about
            // some other centre answers for a rectangle the field is not in.
            var window = state.Window;
            var low = window.Origin;

            if (position.X < low.X || position.X > low.X + window.Extent) {
                continue;
            }

            if (position.Y < low.Y || position.Y > low.Y + window.Extent) {
                continue;
            }

            if (queries.TryGetValue(entity, out var cached)) {
                return cached.Query;
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>[§ D9](../../docs/plan/35-water.md#d9-underwater-is-a-post-process-volume-and-the-waterline-is-named-as-the-hard-part),
    ///         and it is why [32](../../docs/plan/32-post-process-volumes.md)'s shape interface exists.</b>
    ///         Put a <see cref="PostProcessVolume" /> with
    ///         <see cref="PostProcessShapeKind.Custom" /> on the same entity as a
    ///         <see cref="WaterZoneComponent" />, wire this into
    ///         <c>PostProcessVolumeSystem.Shapes</c>, and the underwater grade applies exactly where
    ///         the water is — waves included.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A zone with no field answers <see langword="null" />, which reaches nothing.</b>
    ///         That is <c>PostProcessVolumeSystem.Reach</c>'s stated behaviour and the right one: the
    ///         alternative — falling back to the volume's box — would grade a rectangle around the
    ///         lake while the inspector looked correct.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Allocates one shape per asking entity per frame, which is a decision rather than
    ///         an oversight.</b> The surface moves, so a cached shape is one testing against last
    ///         frame's waves; the fold runs once a frame over the handful of entities that carry an
    ///         underwater volume, and an object per zone per frame is cheaper than the wave sum it
    ///         wraps.
    ///     </para>
    /// </remarks>
    public IPostProcessShape? ShapeFor(Entity entity) {
        if (!states.TryGetValue(entity, out var state) || state.Field is null) {
            return null;
        }

        foreach (var (candidate, component) in zones) {
            if (candidate == entity) {
                return new UnderwaterShape(
                    state,
                    component.Waves,
                    WaterTime,
                    new(component.AttenuationDepth)
                );
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>It does not touch <see cref="WaterTime" />, and that is the fix rather than an
    ///     omission.</b> It used to, and the consequence was one frame of drift the wrong way: this
    ///     runs in <see cref="SystemPhase.PreRender" /> and a buoyancy solver runs in
    ///     <see cref="SystemPhase.FixedUpdate" />, which is <em>earlier in the same frame</em> — so a
    ///     solver reading a clock advanced here read last frame's value while the vertex stage drew
    ///     this one's. <see cref="WaterClockSystem" /> advances it before anything reads it.
    /// </remarks>
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Fold(context.World);

        return dependency;
    }

    /// <summary>Gathers this frame's zones and bodies and brings every field up to date.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test, a tool or an editor can fold without standing up a runner.</remarks>
    public void Fold(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var eye = new Vector2(View.Position.X, View.Position.Z);

        GatherZones(world);
        GatherBodies(world);

        ZonelessBodies = 0;

        // ⚠ The same question the claiming loop asks, of the same centre. A diagnostic tested
        // against any other point lies in both directions the moment the view leaves the origin —
        // a claimed body counted as zoneless, and a zoneless one counted as covered.
        foreach (var body in resolved) {
            var reached = false;

            foreach (var (_, component) in zones) {
                if (Reaches(eye, component, body)) {
                    reached = true;
                    break;
                }
            }

            if (!reached) {
                ZonelessBodies++;
            }
        }

        foreach (var (entity, component) in zones) {
            var state = StateOf(entity, component);

            claimed.Clear();

            foreach (var body in resolved) {
                if (Reaches(eye, component, body)) {
                    claimed.Add(body);
                }
            }

            // ⚠ Compared before it is set, because SetBodies invalidates unconditionally — a fold that
            // set the same list every frame would re-rasterise every frame, which is what the
            // threshold exists to avoid.
            if (!Same(state.Bodies, claimed)) {
                state.SetBodies(claimed);
            }

            state.Update(eye, Ground);

            Requery(entity, state, component);
        }

        // ⚠ Zones whose entity has gone take their query with them, on GatherZones' terms: a
        // dictionary that only ever grew would hold a summed spectrum per zone for as long as the
        // world lived, and a level streaming regions in and out would do that once per region.
        stale.Clear();

        foreach (var (entity, _) in queries) {
            if (!states.ContainsKey(entity)) {
                stale.Add(entity);
            }
        }

        foreach (var entity in stale) {
            queries.Remove(entity);
        }
    }

    /// <summary>Brings a zone's cached query into line with its sea state.</summary>
    /// <remarks>
    ///     ⚠ <b>Rebuilt only when the spectrum or the attenuation actually changed.</b> The
    ///     constructor sums the spectrum into up to thirty-two waves, and a fold that rebuilt every
    ///     frame would do that per zone per frame for a number that changes when somebody drags a
    ///     slider. The field is not part of the key: the query holds the <em>state</em> rather than
    ///     the field, so a window that scrolled or a reshape that built a new field is one the boats
    ///     floating on it see — see <see cref="WaterZoneState.Query" />.
    /// </remarks>
    void Requery(Entity entity, WaterZoneState state, in WaterZoneComponent component) {
        var attenuation = component.AttenuationDepth > 0f
            ? component.AttenuationDepth
            : WaterAttenuation.Default.Depth;

        if (queries.TryGetValue(entity, out var cached)
            && cached.Spectrum.Equals(component.Waves)
            && cached.Attenuation.Equals(attenuation)) {
            return;
        }

        // An unsummable spectrum would throw from the constructor, which in a per-frame fold is a
        // frame that does not happen. A zone in that state keeps whatever query it had; the renderer
        // already draws it flat, and UnresolvedWaves is what says so.
        if (component.Waves.Validate() is not null) {
            return;
        }

        queries[entity] = (component.Waves, attenuation, state.Query(component.Waves, new(attenuation)));
    }

    void GatherZones(World world) {
        zones.Clear();
        ZoneCount = 0;
        UnresolvedWaves = 0;

        // ⚠ One entity at a time, and not a span — GatherBodies' reason, one component over.
        // WaterZoneComponent names its .vxwaves by *string*, which makes it a managed component: its
        // values live in the world's store and the chunk holds handles, so ReadValues would throw.
        // There are a handful of zones in a scene where there are hundreds of bodies, so the cost of
        // the slower path is a rounding error against the fold it sits in.
        foreach (var chunk in world.Chunks(zoneQuery)) {
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                var authored = world.Read<WaterZoneComponent>(entities[i]);

                // A zone that cannot be rasterised is skipped rather than thrown over: an author
                // dragging a resolution through an invalid value should see the last good frame, not
                // an exception from a system.
                if (authored.Zone.Validate() is not null) {
                    continue;
                }

                zones.Add((entities[i], Resolve(authored)));
                ZoneCount++;
            }
        }

        // ⚠ Zones whose entity has gone take their fields with them. A dictionary that only ever grew
        // would hold a field per zone for as long as the world lived, and a level streaming regions in
        // and out would do that once per region.
        stale.Clear();

        foreach (var (entity, _) in states) {
            if (!zones.Exists(candidate => candidate.Entity == entity)) {
                stale.Add(entity);
            }
        }

        foreach (var entity in stale) {
            states.Remove(entity);
        }
    }

    void GatherBodies(World world) {
        resolved.Clear();
        stale.Clear();

        BodyCount = 0;
        UnresolvedBodies = 0;
        RebuiltBodies = 0;

        var seen = new HashSet<Entity>();

        // ⚠ One entity at a time, and not a span. WaterBodyComponent names its spline by *string*,
        // which makes it a managed component — its values live in the world's store and the chunk
        // holds handles, so there is no contiguous span of them to walk. The transforms beside it are
        // unmanaged and are read as a span, which is why only one of the two loops looks unusual.
        foreach (var chunk in world.Chunks(bodyQuery)) {
            var placements = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                var entity = entities[i];
                var component = world.Read<WaterBodyComponent>(entity);

                seen.Add(entity);

                // Rebuilt only when something about it changed — see RebuiltBodies for what happens
                // when this is skipped.
                //
                // ⚠ The *success* is what is cached, and the failure deliberately is not. A body whose
                // spline had not loaded used to be recorded as unresolved against its component and
                // its placement, and neither of those then changes — so it was never asked again for
                // the life of the world. Every source a game has answers null for its first few frames
                // by construction (AssetWaterSource starts a read and polls it), which made a lake
                // named in a scene one that could never appear: a permanent failure out of a transient
                // one. GatherZones has always re-resolved with no cache at all, so the .vxwaves beside
                // the .vxspline arrived late and worked while the curve did not.
                if (built.TryGetValue(entity, out var cached)
                    && cached.Component.Equals(component)
                    && cached.Placement.Equals(placements[i].Value)) {
                    resolved.Add(cached.Body);
                    BodyCount++;

                    continue;
                }

                var body = Build(component, placements[i]);

                if (body is null) {
                    // ⚠ And the stale entry goes with it, so a body whose spline was swapped for one
                    // that has not loaded is asked for the new curve rather than drawn with the old.
                    built.Remove(entity);
                    UnresolvedBodies++;

                    continue;
                }

                built[entity] = new(component, placements[i].Value, body);
                RebuiltBodies++;

                resolved.Add(body);
                BodyCount++;
            }
        }

        foreach (var (entity, _) in built) {
            if (!seen.Contains(entity)) {
                stale.Add(entity);
            }
        }

        foreach (var entity in stale) {
            built.Remove(entity);
        }
    }

    /// <summary>Substitutes a named sea state into the component, where one resolves.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The name becomes a value here and nowhere else, which is what makes the sharing
    ///         real.</b> Every consumer — the vertex stage through <see cref="Zones" />, the underwater
    ///         shape through <see cref="ShapeFor" /> — reads <see cref="WaterZoneComponent.Waves" />
    ///         and is unchanged by this asset existing. Resolving in two places instead would be two
    ///         answers to "what sea is this", and the frame they disagree on is a boat riding a
    ///         different swell from the one drawn under it.
    ///     </para>
    ///     <para>
    ///         It is the same seam <see cref="Build" /> is for a body's spline, and deliberately the
    ///         same shape: a name, a source that may not have it, and a count for when it does not.
    ///     </para>
    /// </remarks>
    WaterZoneComponent Resolve(WaterZoneComponent component) {
        if (component.WaveAsset is not { Length: > 0 } name) {
            return component;
        }

        if (Waves?.SpectrumFor(name) is not { } spectrum) {
            UnresolvedWaves++;

            return component;
        }

        // ⚠ Validated here rather than trusted, because an asset is a file somebody can edit outside
        // the editor and the importer that would refuse it. An unsummable spectrum substituted in is a
        // zone that draws nothing where the inline one would have drawn a sea — see
        // WaterMeshRenderer.Stage, which generates zero waves from a spectrum that does not validate.
        // It counts as unresolved for the same reason a missing file does: the sea on screen is not
        // the one the zone named.
        if (spectrum.Validate() is not null) {
            UnresolvedWaves++;

            return component;
        }

        return component with { Waves = spectrum };
    }

    WaterZoneState StateOf(Entity entity, in WaterZoneComponent component) {
        if (states.TryGetValue(entity, out var existing)) {
            if (!existing.Zone.Equals(component.Zone)) {
                existing.Reshape(component.Zone);
            }

            return existing;
        }

        var created = new WaterZoneState(component.Zone);
        states[entity] = created;

        return created;
    }

    /// <summary>The body a scene component and its resolved curve describe.</summary>
    /// <param name="component">What the scene says about the body.</param>
    /// <param name="spline">The curve it names, already in world space.</param>
    /// <returns>The body, or <see langword="null" /> if the two cannot make one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spline" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Public because the fold is not the only thing that has to turn a component into a
    ///         body.</b> The editor's carve reads the same components and needs the same objects, and
    ///         a second copy of these six lines is a second opinion about what a zeroed
    ///         <see cref="WaterBodyComponent.ShoreFalloff" /> means — which is the seam
    ///         [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)
    ///         exists to keep single.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null rather than an exception for a closed kind over an open curve.</b> That is an
    ///         authoring mistake and <see cref="WaterBody" />'s constructor refuses it by throwing,
    ///         which is right at the point of construction and wrong in a per-frame fold. Here it is a
    ///         body that does not resolve, which is a number an author can be shown.
    ///     </para>
    /// </remarks>
    public static WaterBody? BodyOf(in WaterBodyComponent component, Spline spline) {
        ArgumentNullException.ThrowIfNull(spline);

        if (component.Kind != WaterBodyKind.River && !spline.IsClosed) {
            return null;
        }

        return new(component.Kind, spline, defaults: component.Profile) {
            SurfaceHeight = component.SurfaceHeight,
            Priority = component.Priority,
            // Zero is what a zeroed component holds, not an authored hard edge — see the field's
            // remarks. The kernel keeps zero meaningful; the seam is where unset becomes the default.
            ShoreFalloff = component.ShoreFalloff == 0f ? WaterBodyComponent.Default.ShoreFalloff : component.ShoreFalloff,
            BedRamp = component.BedRamp
        };
    }

    WaterBody? Build(in WaterBodyComponent component, in WorldTransform placement) {
        // ⚠ A zeroed component's spline is null — a chunk's column is zeroed memory, not constructed
        // values — and null is not a name a source can be asked for. It counts as unresolved, the
        // same number a spline that has not loaded counts into.
        if (component.Spline is not { Length: > 0 } name) {
            return null;
        }

        if (Splines?.SplineFor(name, placement.Value) is not { } spline) {
            return null;
        }

        return BodyOf(component, spline);
    }

    /// <summary>Whether a zone's window at a centre reaches any part of a body.</summary>
    /// <remarks>
    ///     <para>
    ///         Against the window rather than against the zone's own transform: the window slides
    ///         with the view, so what a zone claims is what its <em>current</em> window overlaps. A
    ///         body just outside is picked up on the frame the window scrolls far enough — which is
    ///         the same frame the field is re-rasterised anyway.
    ///     </para>
    ///     <para>
    ///         The overlap itself is <see cref="WaterBody.Reaches" /> — the flattened polyline the
    ///         body built once, plus containment, so a body larger than the whole window is claimed
    ///         from inside it rather than left as dry ground mid-ocean.
    ///     </para>
    /// </remarks>
    static bool Reaches(Vector2 centre, in WaterZoneComponent component, WaterBody body) =>
        body.Reaches(centre, component.Extent * 0.5f);

    static bool Same(IReadOnlyList<WaterBody> a, List<WaterBody> b) {
        if (a.Count != b.Count) {
            return false;
        }

        for (var index = 0; index < a.Count; index++) {
            if (!ReferenceEquals(a[index], b[index])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A body that resolved, and what it resolved from.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Body" /> is not nullable, and that is the guard rather than a tidiness.</b>
    ///     An entry in this dictionary means "this component at this placement produced this curve";
    ///     a body that did not resolve has no entry, which is what makes the next fold ask again. It
    ///     held a nullable body once, and the null it held was a lake that could never appear.
    /// </remarks>
    readonly record struct Built(WaterBodyComponent Component, Matrix4x4 Placement, WaterBody Body);
}
