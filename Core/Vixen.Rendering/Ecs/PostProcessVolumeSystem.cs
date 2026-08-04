// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Rendering.Ecs;

/// <summary>Folds the look profile and the volumes the camera is in into one overlay.</summary>
/// <remarks>
///     <para>
///         <b>The whole of what a post-process volume is at run time.</b> Lay down the project
///         <see cref="Look" /> as the base, gather every <see cref="PostProcessVolume" />, weigh
///         each by how far the camera is from it, sort by priority, and lay them over one another in
///         that order. What comes out is a <see cref="PostProcessOverlay" /> the frame's nodes read
///         — see <c>IPostProcessTarget</c>.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, ordered by its declared access</b>, the same
///         placement <see cref="LightExtractionSystem" /> explains: <c>TransformSystem</c> writes
///         <see cref="WorldTransform" /> in the same phase and this reads it, so a volume that moved
///         this frame is tested where it now is rather than where it was.
///     </para>
///     <para>
///         ⚠ <b>The camera's position comes from the view rather than from the world.</b> The view is
///         what the frame is actually drawn through — <c>CameraExtractionSystem</c> has already
///         decided which of a scene's cameras won — so asking the world again would be a second
///         answer to a question already settled, and would disagree the moment a scene had two
///         cameras.
///     </para>
///     <para>
///         ⚠ <b>Sorted ascending and folded in that order, because the fold is not commutative.</b>
///         Priority decides which volume is on top, and "on top" means applied last. A stable sort is
///         not required and is not promised: two volumes at one priority resolve arbitrarily, which
///         the component's own remarks call a level-design mistake rather than a case to define.
///     </para>
/// </remarks>
/// <param name="view">The view whose camera position decides which volumes apply.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class PostProcessVolumeSystem(RenderView view) : SystemBase, IDeclaredAccess {
    readonly QueryDescription volumes = new QueryDescription().WithAll<PostProcessVolume, WorldTransform>();
    readonly List<(int Priority, float Weight, bool Unbound, PostProcessSettings Settings)> gathered = [];

    /// <summary>The view this reads a camera position from.</summary>
    public RenderView View { get; } = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>The project look profile — the fold's base layer, under everything a scene says.</summary>
    /// <remarks>
    ///     <para>
    ///         Doc 39's fixed precedence, realised as an ordering fact: this is laid down first, at
    ///         full weight, and every contributing volume folds over it — so a scene's unbound volume
    ///         out-votes it per parameter, a local volume out-votes both, and a parameter nobody
    ///         above claims is the look's. <see cref="PostProcessSettings.None" /> — the default — is
    ///         no layer at all, and the fold is exactly what it was before looks existed.
    ///     </para>
    ///     <para>
    ///         <b>The host wires it, and this takes the payload rather than an address</b>, on
    ///         <c>PostEffectFactory.Preset</c>'s reasoning: the thing with an asset manager in its
    ///         hands loads the <c>.vxlook</c> and assigns <c>LookAsset.Settings</c> here on its own
    ///         schedule — <c>AppGraphics</c> does exactly that from <c>GraphicsOptions.Look</c> or
    ///         from the frame document's own <c>look:</c>. A struct property keeps the per-frame fold
    ///         allocation-free and makes "no look" a value rather than a null check.
    ///     </para>
    /// </remarks>
    public PostProcessSettings Look { get; set; } = PostProcessSettings.None;

    /// <summary>Where a <see cref="PostProcessShapeKind.Custom" /> volume's shape comes from.</summary>
    /// <remarks>
    ///     <para>
    ///         The one seam a non-box volume needs
    ///         ([35 § B2](../../../docs/plan/35-water.md#b2-doc-32s-volumes-are-boxes)). Underwater is a
    ///         volume whose shape is a water body below its surface, which this assembly cannot
    ///         evaluate and should not learn to — so the fold asks whoever can.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null, and a custom volume with no source reaches nothing.</b> The alternative —
    ///         falling back to the box — would make a water volume grade a rectangle around the lake
    ///         while looking correctly configured, which is the failure that takes an afternoon to
    ///         find.
    ///     </para>
    /// </remarks>
    public IPostProcessShapeSource? Shapes { get; set; }

    /// <summary>What the last pass folded to.</summary>
    /// <remarks>
    ///     <see cref="PostProcessOverlay.None" /> for a project with no look and a scene with no
    ///     volumes, or none the camera is inside — and every node then keeps exactly what its
    ///     document gave it, which is what makes the whole feature cost nothing when nobody uses it.
    /// </remarks>
    public PostProcessOverlay Overlay { get; private set; }

    /// <summary>How many volumes the last pass saw.</summary>
    public int VolumeCount { get; private set; }

    /// <summary>How many of them contributed anything.</summary>
    /// <remarks>
    ///     The number that says "the volume you placed is not reaching the camera", which is the
    ///     failure this feature has: a volume with a zero weight, zero extents or a camera outside its
    ///     blend radius is invisible and looks exactly like one that is not wired up.
    /// </remarks>
    public int ContributingCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <see cref="LightExtractionSystem" /> gives:
    ///     naming a component type in a generic call is what assigns it an id, and on the first frame
    ///     an attribute would have nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<PostProcessVolume>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Fold(context.World);
        return dependency;
    }

    /// <summary>Gathers, sorts and folds this frame's volumes.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test, a tool or an editor can fold without standing up a runner.</remarks>
    public void Fold(World world) {
        ArgumentNullException.ThrowIfNull(world);

        gathered.Clear();
        VolumeCount = 0;
        ContributingCount = 0;

        var camera = View.Position;

        foreach (var chunk in world.Chunks(volumes)) {
            var authored = chunk.ReadValues<PostProcessVolume>();
            var placements = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var i = 0; i < chunk.Count; i++) {
                VolumeCount++;

                var volume = authored[i];
                var weight = Math.Clamp(volume.Weight, 0f, 1f) * Reach(volume, placements[i], entities[i], camera);

                if (weight <= 0f || volume.Settings.IsEmpty) {
                    continue;
                }

                ContributingCount++;
                gathered.Add((volume.Priority, weight, volume.Unbound, volume.Settings));
            }
        }

        var overlay = PostProcessOverlay.None;

        // The look first and at full weight, which *is* the precedence: the fold's last word wins,
        // so the base layer is whoever speaks first. Below every volume whatever its priority —
        // a scene saying anything out-votes the project saying everything.
        if (!Look.IsEmpty) {
            overlay.Add(Look, 1f);
        }

        if (gathered.Count > 0) {
            gathered.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var (_, weight, _, settings) in gathered) {
                overlay.Add(settings, weight);
            }
        }

        Overlay = overlay;
    }

    /// <summary>Which layers said what, for the frame the last <see cref="Fold" /> folded.</summary>
    /// <param name="into">
    ///     Where the pairs go, bottom layer first — <c>("look", "fogDensity")</c>, then each
    ///     contributing volume in the order it was applied, <c>"scene"</c> for an unbound one and
    ///     <c>"volume(priority N)"</c> for a bounded one. Not cleared first.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     The resolved-stack view doc 39 promises the editor: "why does it look like this" answered
    ///     as a list of (layer, parameter) opinions, in application order, so the last claimant of a
    ///     parameter is the one on screen. Built on demand from what the fold already gathered —
    ///     nothing here runs per frame, and calling it costs only the caller's list.
    /// </remarks>
    public void Contributions(ICollection<(string Layer, string Parameter)> into) {
        ArgumentNullException.ThrowIfNull(into);

        var names = new List<string>();

        Describe("look", Look, names, into);

        foreach (var (priority, _, unbound, settings) in gathered) {
            Describe(unbound ? "scene" : $"volume(priority {priority})", settings, names, into);
        }

        static void Describe(
            string layer,
            in PostProcessSettings settings,
            List<string> names,
            ICollection<(string Layer, string Parameter)> into
        ) {
            names.Clear();
            settings.Opinions(names);

            foreach (var name in names) {
                into.Add((layer, name));
            }
        }
    }

    /// <summary>How much a volume reaches a world-space point.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Through <see cref="IPostProcessShape" />, which is what a box stopped being the only
    ///         one of</b> ([35 § B2](../../../docs/plan/35-water.md#b2-doc-32s-volumes-are-boxes)). The
    ///         two built-in shapes are constructed on the stack as concrete types, so the interface
    ///         costs nothing until somebody supplies a third — and the third is asked for by entity,
    ///         because a water body's shape belongs to the body rather than to the transform.
    ///     </para>
    ///     <para>
    ///         The built-ins take the point into the volume's own space first, so a rotated or scaled
    ///         entity is a rotated or scaled shape. That is the reason they invert a matrix rather than
    ///         comparing against world-space bounds: an axis-aligned test would make rotating a volume
    ///         change its shape, which is the kind of thing somebody notices only after building a
    ///         level around it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A singular transform reaches nothing rather than everything</b>, and so does a
    ///         custom volume nothing will resolve. A volume scaled to zero on an axis cannot be
    ///         inverted, and the two answers available are "it contains every point" and "it contains
    ///         none" — the second is the one that looks like the mistake it is, rather than blacking
    ///         out the level.
    ///     </para>
    /// </remarks>
    float Reach(in PostProcessVolume volume, in WorldTransform placement, Entity entity, Vector3 point) {
        if (volume.Unbound) {
            return 1f;
        }

        switch (volume.Shape) {
            case PostProcessShapeKind.Sphere: {
                var sphere = new SpherePostProcessShape(placement.Value, volume.Extents);
                sphere.Contains(point, out var outside);

                return PostProcessVolume.FadeAt(outside, volume.BlendRadius);
            }

            case PostProcessShapeKind.Custom: {
                if (Shapes?.ShapeFor(entity) is not { } custom) {
                    return 0f;
                }

                custom.Contains(point, out var outside);

                return PostProcessVolume.FadeAt(outside, volume.BlendRadius);
            }

            default: {
                var box = new BoxPostProcessShape(placement.Value, volume.Extents);
                box.Contains(point, out var outside);

                return PostProcessVolume.FadeAt(outside, volume.BlendRadius);
            }
        }
    }
}
