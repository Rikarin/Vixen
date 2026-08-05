// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;

namespace Vixen.Water.Physics;

/// <summary>`water.showBuoyancy`: pontoons, how far under they are, and the forces as arrows.</summary>
/// <remarks>
///     <para>
///         <b>[35 § Part 2 § Debugging](../../docs/plan/35-water.md#debugging)'s sixth verb, and the
///         only one of the six that is not in <c>Vixen.Rendering.Water</c>.</b> The pontoons and the
///         forces are this assembly's, and the renderer must not reference it — § D1 puts the physics
///         join in its own assembly precisely so that nothing linking Jolt is linked by a renderer.
///         So the flag lives with the console verb and the drawing lives with the data.
///     </para>
///     <para>
///         ⚠ <b><see cref="Enabled" /> rather than reading <c>WaterDebug.ShowBuoyancy</c>, and the
///         copy is the price of the split.</b> A host that wants the verb wired sets this from that
///         flag once a frame; one line, and it is what keeps this assembly linkable by a dedicated
///         server that has no renderer at all.
///     </para>
///     <para>
///         ⚠ <b>It draws what the <em>last step</em> did, which is why it is worth having at all.</b>
///         A boat that sits too low, launches out of the lake or drifts sideways is a bug with no
///         visible cause; four spheres, their submerged fractions and their force arrows are the
///         difference between "buoyancy is broken" and "two of four pontoons are dry".
///     </para>
/// </remarks>
public sealed class BuoyancyDebugDraw {
    /// <summary>Whether to draw anything. A host copies <c>WaterDebug.ShowBuoyancy</c> into it.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many metres an arrow one body-weight of force draws.</summary>
    /// <remarks>
    ///     ⚠ <b>Scaled by the body's own weight rather than by newtons.</b> A crate's lift is a few
    ///     kilonewtons and a barge's is a few meganewtons, so a fixed metres-per-newton scale draws
    ///     one of them as a dot and the other off the map. At rest every arrow is one unit long, which
    ///     is also what makes "this one is doing more than its share" readable.
    /// </remarks>
    public float ForceScale { get; set; } = 2f;

    /// <summary>Draws every floating body's pontoons and the forces on them.</summary>
    /// <param name="into">Where the geometry goes.</param>
    /// <param name="world">The world the bodies are in.</param>
    /// <param name="buoyancy">The system, whose <c>Forces</c> hold the last body's detail.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Every body gets its spheres and its state; only the last gets its per-pontoon
    ///     forces.</b> <see cref="BuoyancySystem.Forces" /> is one scratch span reused per body — see
    ///     there for why keeping every body's would be an array per body per step for a picture nobody
    ///     is usually looking at. Which body it is does not matter to a person watching a scene with
    ///     one boat in it, and a person watching a scene with fifty is reading the spheres.
    /// </remarks>
    public void Draw(DebugDraw into, World world, BuoyancySystem buoyancy) {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(buoyancy);

        if (!Enabled) {
            return;
        }

        var query = new QueryDescription().WithAll<BuoyancyBody, BuoyancyState, WorldTransform>();

        foreach (var chunk in world.Chunks(query)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                Body(into, world, entities[index]);
            }
        }

        Forces(into, buoyancy);
    }

    static void Body(DebugDraw into, World world, Entity entity) {
        var authored = world.Read<BuoyancyBody>(entity);

        if (authored.Pontoons is not { Length: > 0 } pontoons) {
            return;
        }

        var state = world.Read<BuoyancyState>(entity);
        var placement = world.Read<WorldTransform>(entity).Value;

        foreach (var pontoon in pontoons) {
            var centre = Matrix4x4.TransformPosition(pontoon.Offset, in placement);

            // ⚠ The sphere in the *dry* colour and the submerged cap in the wet one, rather than one
            // colour lerped between them. A lerp says "this pontoon is 40% under" as a shade nobody
            // can read off a screen; two circles say it as a position, which is what a waterline is.
            into.Sphere(new(centre, pontoon.Radius), state.IsFloating ? Wet : Dry);

            if (state.Wet <= 0) {
                continue;
            }

            var depth = Buoyancy.SubmergedFraction(pontoon.Radius, centre.Y, state.SurfaceHeight);

            if (depth > 0f) {
                into.Circle(
                    new(centre.X, MathF.Min(state.SurfaceHeight, centre.Y + pontoon.Radius), centre.Z),
                    Vector3.UnitY,
                    pontoon.Radius * MathF.Sqrt(MathF.Max(1f - ((depth * 2f) - 1f) * ((depth * 2f) - 1f), 0f)),
                    Waterline
                );
            }
        }

        // And the numbers, at the body's own origin, because "2/4 wet" is the whole diagnosis.
        into.Text(
            placement.Translation + new Vector3(0f, 0.5f, 0f),
            $"{state.Wet}/{state.Total}  {state.Submerged:0.00}",
            state.IsFloating ? Wet : Dry,
            0.2f
        );
    }

    void Forces(DebugDraw into, BuoyancySystem buoyancy) {
        var forces = buoyancy.Forces;

        if (forces.Length == 0) {
            return;
        }

        // The scale is the *total* lift over the pontoon count, so an arrow of one unit is a pontoon
        // carrying its share — see ForceScale.
        var total = 0f;

        foreach (var force in forces) {
            total += MathF.Abs(force.Force.Y);
        }

        var share = total / forces.Length;

        if (!(share > 0f)) {
            return;
        }

        foreach (var force in forces) {
            if (force.Submerged <= 0f) {
                continue;
            }

            into.Arrow(force.Position, force.Position + (force.Force / share * ForceScale), Force);
        }
    }

    static readonly Color4 Wet = new(0.3f, 0.8f, 1f, 1f);
    static readonly Color4 Dry = new(0.6f, 0.6f, 0.6f, 1f);
    static readonly Color4 Waterline = new(1f, 1f, 1f, 1f);
    static readonly Color4 Force = new(1f, 0.85f, 0.2f, 1f);
}
