// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Foliage;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     A box a scene puts in the world that nothing grows inside.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T9]'s owed item: blocking volumes as <em>scene</em> objects.</b>
///         <see cref="FoliageBlocker" /> is the kernel's form and it is a centre and an extent —
///         device-free, testable, and unable to say where it is, because a kernel that knew about
///         entities would be a kernel that needs a world to run.
///     </para>
///     <para>
///         ⚠ <b>The extent is half-sizes and the transform is the entity's.</b> A blocker where a
///         building will be is a person dragging a box in the viewport, so its placement is a
///         <see cref="LocalTransform" /> like everything else's — and the component carries only the
///         shape. Giving it a position of its own would be two answers to where it is.
///     </para>
///     <para>
///         ⚠ <b>Axis-aligned in world space, and the rotation is deliberately dropped.</b> The
///         kernel's test is a box containment against a candidate's XZ, which is two comparisons; an
///         oriented box is a matrix inverse per candidate per step, and a growth simulation tests
///         every seed against every blocker at every one of its steps. What a rotated blocker buys is
///         a tighter fit around a diagonal wall, and the answer to that is two blockers.
///     </para>
///     <para>
///         ⚠ <b>It blocks <em>growth</em>, not painting.</b> A foliage stroke is a person aiming at
///         the ground and a blocker that refused it would be a tool that silently did nothing where
///         somebody clicked. The simulation is the thing that places without being watched, which is
///         what makes a volume worth having.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct FoliageBlockerComponent {
    /// <summary>How far the box reaches from its centre, in metres, before the entity's scale.</summary>
    /// <remarks>
    ///     Half-sizes rather than a full size, because that is what the kernel compares against and
    ///     converting in one place beats converting at every call site. It is also what a bounding box
    ///     is everywhere else in the engine.
    /// </remarks>
    public Vector3 Extent { get; set; }

    /// <summary>Whether the volume is blocking at all.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Here rather than expressed by deleting the entity</b>, because a blocker is
    ///         scenery an artist turns off to see what a hillside looks like without it — and an
    ///         entity deleted to answer that question has to be drawn again from memory.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Backed inverted, so the zero value is enabled.</b> A default-constructed
    ///         component, or a scene file that omits the member, is a blocker somebody placed — and
    ///         one that arrives switched off blocks nothing, silently, which reads as the volume not
    ///         working rather than as a field nobody set. The serialized member keeps its name and
    ///         its meaning; only the storage is inverted.
    ///     </para>
    /// </remarks>
    public bool IsEnabled {
        get => !disabled;
        set => disabled = !value;
    }

    bool disabled;

    /// <summary>A blocker of a given size, enabled.</summary>
    /// <param name="extent">How far it reaches from its centre.</param>
    /// <returns>The component.</returns>
    public static FoliageBlockerComponent Of(Vector3 extent) => new() { Extent = extent, IsEnabled = true };

    /// <summary>A cube blocker.</summary>
    /// <param name="size">How wide it is, in metres.</param>
    /// <returns>The component.</returns>
    public static FoliageBlockerComponent Cube(float size) => Of(new(size * 0.5f));
}

/// <summary>
///     The bounds query the growth kernel deliberately cannot name: scene entities as blockers.
/// </summary>
/// <remarks>
///     <para>
///         <b>The seam, and it is one method.</b> <c>FoliageGrowth.Simulate</c> takes a list of
///         <see cref="FoliageBlocker" /> and never asks where they came from; this is what turns a
///         world into that list, and it lives here because this assembly is already the one that
///         knows about both an ECS and foliage.
///     </para>
///     <para>
///         ⚠ <b>Gathered once per run rather than queried per candidate.</b> A simulation tests every
///         seed against every blocker at every step, so a query that walked the world each time would
///         be an archetype iteration inside three nested loops. A blocker that moves during a run is
///         not a case: a run is a button press.
///     </para>
/// </remarks>
public static class FoliageBlockers {
    /// <summary>Every enabled blocker in a world, as the kernel takes them.</summary>
    /// <param name="world">The world.</param>
    /// <param name="into">Where to put them. Cleared first.</param>
    /// <returns>How many were gathered.</returns>
    /// <exception cref="ArgumentNullException">There is no world or no destination.</exception>
    /// <remarks>
    ///     ⚠ <b>The entity's scale multiplies the extent and its rotation is dropped.</b> A scaled
    ///     blocker is what dragging a corner of the box in the viewport produces, so honouring the
    ///     scale is honouring the gesture; honouring the rotation would cost a matrix inverse per
    ///     candidate per step, and two blockers cover a diagonal wall.
    /// </remarks>
    public static int Gather(World world, List<FoliageBlocker> into) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var query = new QueryDescription().WithAll<FoliageBlockerComponent, LocalTransform>();

        foreach (var chunk in world.Query(query).Chunks()) {
            var blockers = chunk.Values<FoliageBlockerComponent>();
            var transforms = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!blockers[index].IsEnabled) {
                    continue;
                }

                var scale = transforms[index].Scale;
                var extent = blockers[index].Extent * scale;

                // ⚠ Absolute, because a negative scale is a mirrored box and not an inside-out one.
                // A blocker whose extent went negative would contain nothing, which reads as the
                // volume having stopped working rather than as a sign.
                into.Add(new(transforms[index].Position, Vector3.Abs(extent)));
            }
        }

        return into.Count;
    }

    /// <summary>Every enabled blocker in a world.</summary>
    /// <param name="world">The world.</param>
    /// <returns>The blockers.</returns>
    /// <exception cref="ArgumentNullException">There is no world.</exception>
    public static IReadOnlyList<FoliageBlocker> Gather(World world) {
        var into = new List<FoliageBlocker>();

        Gather(world, into);

        return into;
    }
}
