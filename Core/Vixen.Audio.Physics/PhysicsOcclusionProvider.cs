// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Vixen.Physics;
using Vixen.Physics.Queries;

namespace Vixen.Audio.Physics;

/// <summary>Answers the mixer's occlusion question by casting rays at the level.</summary>
/// <remarks>
///     <para>
///         <b>The layer mask is the whole feature.</b> A raycast against everything solid finds the
///         chain-link fence, the handrail and the crate the player is standing behind, and muffles a
///         conversation happening through a doorway because a lamp post is nominally in the way.
///         Occlusion belongs on the geometry a level designer decided blocks sound — usually the same
///         layer as the walls and nothing else — and <see cref="Layers" /> is where that is said.
///         Leaving it at everything is the setting most likely to make this sound wrong.
///     </para>
///     <para>
///         <b>It is also the answer to a sound occluding itself.</b> An engine emitter sits inside
///         the vehicle's collider, so a ray cast at it hits the vehicle and reports the sound as
///         blocked by the thing making it. Nothing here can fix that on its own — this is handed two
///         points and knows nothing about which body either belongs to — so the fix is to put
///         occluding geometry on its own layer and leave dynamic bodies off it, which is what a level
///         wants anyway.
///     </para>
///     <para>
///         <b>Several rays, because one is a coin toss.</b> A single centre-to-centre cast makes
///         occlusion binary and makes it flicker: a source a few centimetres to one side of a door
///         frame is either fully blocked or fully clear, and walking past the opening switches
///         between them. Casting to a few offsets around the source and counting how many got
///         through gives partial values — a doorway reads about half — which is what
///         <c>AudioOcclusion</c>'s smoothing then has something useful to smooth.
///     </para>
///     <para>
///         <b>The offsets are on a fixed cross and not random.</b> Random offsets would make the
///         answer differ between two frames that ought to agree, which is jitter dressed up as
///         detail; and between two machines, which is worse for anything recorded or replayed. A
///         fixed pattern is reproducible, and the smoothing takes care of the rest.
///     </para>
///     <para>
///         <b>Called on the game thread, a handful of times a frame.</b> <c>AudioOcclusion</c>
///         rations how many voices are asked about, so the real cost is
///         <see cref="Rays" /> × its budget — five by eight is forty casts a frame, which is nothing.
///     </para>
/// </remarks>
public sealed class PhysicsOcclusionProvider : IAudioOcclusionProvider {
    /// <summary>The most rays one query may cast.</summary>
    public const int MaxRays = 5;

    // A centre and the four arms of a cross, in the plane across the ray. Applied around the source,
    // because that is the end usually next to the doorway. X is the right axis and Y the up one;
    // there is no third, because an offset along the ray would only make it longer.
    static readonly Vector2[] Offsets = [
        new(0f, 0f),
        new(1f, 0f),
        new(-1f, 0f),
        new(0f, 1f),
        new(0f, -1f)
    ];

    readonly PhysicsWorld world;

    /// <summary>A provider over a physics world.</summary>
    /// <param name="world">It. Not owned — this does not dispose it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public PhysicsOcclusionProvider(PhysicsWorld world) {
        ArgumentNullException.ThrowIfNull(world);
        this.world = world;
    }

    /// <summary>Which layers block sound.</summary>
    /// <remarks>
    ///     Everything by default, which is the setting that works immediately and the one to change
    ///     first. See the note on the class about why.
    /// </remarks>
    public PhysicsLayerMask Layers { get; set; } = PhysicsLayerMask.All;

    /// <summary>How many rays are cast per query, from 1 to <see cref="MaxRays" />.</summary>
    /// <remarks>
    ///     One makes occlusion a switch. Three gives a usable partial. Five is the default and is
    ///     still cheap. More than five would want a different sampling pattern rather than more of
    ///     this one.
    /// </remarks>
    public int Rays {
        get => rays;
        set => rays = Math.Clamp(value, 1, MaxRays);
    }

    int rays = MaxRays;

    /// <summary>How wide the ray fan is around the source, in world units.</summary>
    /// <remarks>
    ///     About the size of the thing making the sound, and near enough the width of a doorway is a
    ///     good default: it is the scale at which "partly behind the frame" is a real answer.
    /// </remarks>
    public float Spread { get; set; } = 0.5f;

    /// <summary>How many rays have been cast, for a profiler.</summary>
    public long Casts { get; private set; }

    /// <inheritdoc />
    public float Occlusion(in Vector3 source, in Vector3 listener) {
        var toSource = source - listener;
        var distance = toSource.Length();

        // On top of the listener, or as near as makes no difference. Nothing can be between them.
        if (distance < 1e-4f) {
            return 0f;
        }

        var direction = toSource / distance;
        var filter = QueryFilter.On(Layers);
        var blocked = 0;
        var cast = rays;

        // A basis across the ray, so the fan opens sideways rather than along it — offsetting along
        // the direction would just shorten and lengthen the same ray.
        Basis(direction, out var right, out var up);

        for (var i = 0; i < cast; i++) {
            var offset = Offsets[i];
            var target = source + (right * offset.X * Spread) + (up * offset.Y * Spread);
            var ray = target - listener;
            var length = ray.Length();

            Casts++;

            if (length < 1e-4f) {
                continue;
            }

            // Stopping a hair short, so geometry the source is resting exactly on does not count as
            // being between the two. It does not solve the larger version of that problem — an
            // emitter inside its own collider — which is what Layers is for; see the note on the
            // class.
            if (world.Raycast(listener, ray / length, length - 1e-3f, out _, filter)) {
                blocked++;
            }
        }

        return (float)blocked / cast;
    }

    /// <summary>Two axes across a direction, without caring which way round they are.</summary>
    static void Basis(in Vector3 forward, out Vector3 right, out Vector3 up) {
        // Cross with whichever world axis the direction is least aligned to, so the cross product
        // never degenerates — straight up is the case that catches a naive implementation.
        var reference = MathF.Abs(forward.Y) < 0.9f ? Vector3.Up : Vector3.Right;

        right = Vector3.Normalize(Vector3.Cross(forward, reference));
        up = Vector3.Cross(forward, right);
    }
}
