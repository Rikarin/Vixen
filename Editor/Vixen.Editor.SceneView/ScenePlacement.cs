// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>What a ray hit in the scene.</summary>
/// <param name="Point">Where, in world space.</param>
/// <param name="Normal">Which way the surface faces there.</param>
/// <param name="Distance">How far along the ray.</param>
public readonly record struct SurfaceHit(Vector3 Point, Vector3 Normal, float Distance);

/// <summary>Something that can say what a ray hits.</summary>
/// <remarks>
///     An interface rather than a dependency on the physics world, so placement is testable with no
///     scene and so a project with no colliders can still answer with its render bounds. The viewport
///     supplies whichever it has.
/// </remarks>
public interface ISurfaceProbe {
    /// <summary>Casts a ray into the scene.</summary>
    /// <param name="ray">The ray.</param>
    /// <param name="hit">What it hit.</param>
    /// <returns>Whether it hit anything.</returns>
    bool Raycast(Ray ray, out SurfaceHit hit);
}

/// <summary>Where a thing dragged from the project browser would land.</summary>
/// <param name="Position">Where to put it.</param>
/// <param name="Rotation">How to turn it.</param>
/// <param name="OnSurface">Whether it landed on geometry rather than on the fallback plane.</param>
public readonly record struct Placement(Vector3 Position, Quaternion Rotation, bool OnSurface);

/// <summary>Turns a pointer in a viewport into somewhere to put a new object.</summary>
/// <remarks>
///     <para>
///         <b>Three answers, in order of how much the scene knows.</b> On geometry, it lands on the
///         surface. With nothing under the pointer, it lands on the ground plane, which is what makes
///         dropping something into an empty scene work at all. With the camera looking at the sky and
///         the ground plane behind it, it lands a fixed distance in front of the camera — because
///         the alternative is a position at infinity or a silent refusal to drop.
///     </para>
///     <para>
///         ⚠ <b>The fallback plane is at the grid's height, not at zero.</b> They are the same until
///         somebody moves the working plane, and a drop that ignored it would put things at a
///         different height from everything they had been placing.
///     </para>
///     <para>
///         <b>Aligning to the surface is opt-in.</b> A crate dropped on a slope usually should be
///         level and a decal usually should not, so which one is a setting rather than a guess — and
///         it is the same <see cref="SnapSettings.SnapToSurface" /> a gizmo drag uses, so the two
///         cannot disagree.
///     </para>
/// </remarks>
public sealed class ScenePlacement {
    /// <summary>Where the ground plane is, in world units up.</summary>
    public float GroundHeight { get; set; }

    /// <summary>How far in front of the camera to drop when nothing else answers.</summary>
    public float FallbackDistance { get; set; } = 10f;

    /// <summary>What a drop rounds to.</summary>
    public SnapSettings Snap { get; } = new();

    /// <summary>Where a pointer would put something.</summary>
    /// <param name="ray">The ray under the pointer.</param>
    /// <param name="probe">What can answer "what is under this ray", or <see langword="null" />.</param>
    /// <returns>The placement.</returns>
    public Placement Resolve(Ray ray, ISurfaceProbe? probe = null) {
        if (probe is not null && probe.Raycast(ray, out var hit)) {
            return new(
                Snap.Position(hit.Point),
                Snap.SnapToSurface ? Upright(hit.Normal) : Quaternion.Identity,
                true
            );
        }

        var ground = new Plane(Vector3.UnitY, -GroundHeight);

        if (ray.Intersects(ground, out var distance) && distance > 0f) {
            return new(Snap.Position(ray.GetPoint(distance)), Quaternion.Identity, false);
        }

        // Looking away from the ground. A drop still has to land somewhere the user can see, and a
        // fixed distance along the ray is the only answer that is always in front of the camera.
        return new(Snap.Position(ray.GetPoint(FallbackDistance)), Quaternion.Identity, false);
    }

    /// <summary>The rotation that stands an object up on a surface.</summary>
    /// <param name="normal">Which way the surface faces.</param>
    /// <returns>The rotation taking +Y onto the normal.</returns>
    /// <remarks>
    ///     ⚠ <b>+Y onto the normal, not −Z.</b> An entity <i>faces</i> its local −Z (Conventions.md §
    ///     Handedness) and <i>stands on</i> its local +Y, and surface alignment is the second of
    ///     those: a crate on a slope should keep facing the way it faced and tilt, not lie down.
    /// </remarks>
    public static Quaternion Upright(Vector3 normal) {
        var up = Vector3.Normalize(normal);

        return up.IsZero ? Quaternion.Identity : Quaternion.FromToRotation(Vector3.UnitY, up);
    }
}
