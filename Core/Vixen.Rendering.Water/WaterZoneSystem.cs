// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
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
///         <see cref="WaterZoneSystem.UnresolvedBodies" />.
///     </para>
/// </remarks>
public interface IWaterSplineSource {
    /// <summary>The curve a body names, or <see langword="null" /> if it is not available.</summary>
    /// <param name="name">What the component named.</param>
    /// <param name="placement">Where the entity carrying the body is.</param>
    /// <returns>The curve in world space, or null.</returns>
    Spline? SplineFor(string name, in Matrix4x4 placement);
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
public sealed class WaterZoneSystem(RenderView view) : SystemBase, IDeclaredAccess {
    // ⚠ Zones carry no transform requirement because nothing about a zone reads one: the window and
    // its claim both follow the view — see Reaches — and the entity's transform only places it in
    // the hierarchy. Bodies do require one, because a body is rasterised where its spline is.
    readonly QueryDescription zoneQuery = new QueryDescription().WithAll<WaterZoneComponent>();
    readonly QueryDescription bodyQuery = new QueryDescription().WithAll<WaterBodyComponent, WorldTransform>();

    readonly Dictionary<Entity, WaterZoneState> states = [];
    readonly Dictionary<Entity, Built> built = [];
    readonly List<(Entity Entity, WaterZoneComponent Component)> zones = [];
    readonly List<WaterBody> resolved = [];
    readonly List<WaterBody> claimed = [];
    readonly List<Entity> stale = [];

    /// <summary>The view this centres its windows on.</summary>
    public RenderView View { get; } = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>Where a body's curve comes from.</summary>
    public IWaterSplineSource? Splines { get; set; }

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
    /// </remarks>
    public int UnresolvedBodies { get; private set; }

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

    /// <inheritdoc />
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
        }
    }

    void GatherZones(World world) {
        zones.Clear();
        ZoneCount = 0;

        foreach (var chunk in world.Chunks(zoneQuery)) {
            var authored = chunk.ReadValues<WaterZoneComponent>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                // A zone that cannot be rasterised is skipped rather than thrown over: an author
                // dragging a resolution through an invalid value should see the last good frame, not
                // an exception from a system.
                if (authored[i].Zone.Validate() is not null) {
                    continue;
                }

                zones.Add((entities[i], authored[i]));
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
                if (built.TryGetValue(entity, out var cached)
                    && cached.Component.Equals(component)
                    && cached.Placement.Equals(placements[i].Value)) {
                    if (cached.Body is { } kept) {
                        resolved.Add(kept);
                        BodyCount++;
                    } else {
                        UnresolvedBodies++;
                    }

                    continue;
                }

                var body = Build(component, placements[i]);

                built[entity] = new(component, placements[i].Value, body);
                RebuiltBodies++;

                if (body is null) {
                    UnresolvedBodies++;
                    continue;
                }

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

        // ⚠ A closed kind over an open curve is an authoring mistake, and the kernel refuses it by
        // throwing — which is right at the point of construction and wrong in a per-frame fold. Here
        // it counts as unresolved, which is a number an author can see.
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

    readonly record struct Built(WaterBodyComponent Component, Matrix4x4 Placement, WaterBody? Body);
}
