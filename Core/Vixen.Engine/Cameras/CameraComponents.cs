// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>What an entity sees. Its position and orientation come from its transform.</summary>
/// <remarks>
///     <para>
///         A camera is a component and not an object, so an entity can be one, and so a scene can
///         have any number of them without the engine holding a list. Which one renders is the
///         renderer's decision, made from <see cref="Order" /> and the entity being enabled.
///     </para>
///     <para>
///         <b><c>[DataContract]</c>, because a level places its cameras</b> — that is what gives it a
///         name a <c>.vxscene</c> can write and a serializer a compiled one can be made of — and
///         <b><c>[Component]</c>, because the pair of them is what declares it to
///         <c>SceneComponentRegistry</c>.</b> A game's own components say the same two things the
///         same way and need nothing else; this used to be registered by hand in the registry's static
///         constructor, which was the only reason the engine's components and a game's were different.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct Camera {
    /// <summary>Vertical field of view, in radians. Ignored when <see cref="Orthographic" />.</summary>
    public float FieldOfView;

    /// <summary>Distance to the near plane.</summary>
    public float NearPlane;

    /// <summary>Distance to the far plane.</summary>
    public float FarPlane;

    /// <summary>Whether the projection is orthographic.</summary>
    public bool Orthographic;

    /// <summary>The height the orthographic view covers, in world units.</summary>
    public float OrthographicHeight;

    /// <summary>Width over height. Zero means "ask the target", which the renderer fills in.</summary>
    public float AspectRatio;

    /// <summary>Which camera renders first. Lower is earlier.</summary>
    public int Order;

    /// <summary>
    ///     A sensible perspective camera: 60° vertical, from 0.1 to 1000, aspect from the target.
    /// </summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, because a zeroed <see cref="Camera" /> has a zero
    ///     field of view and a zero far plane, and every matrix built from it is degenerate. See what
    ///     the same mistake cost in <c>Vixen.Graphics</c>' pipeline defaults
    ///     ([14](../../../docs/plan/14-roadmap.md) § Phase 1).
    /// </remarks>
    public static Camera Perspective => new() {
        FieldOfView = MathUtil.DegreesToRadians(60f),
        NearPlane = 0.1f,
        FarPlane = 1000f,
        Orthographic = false,
        OrthographicHeight = 10f,
        AspectRatio = 0f,
        Order = 0
    };

    /// <summary>A sensible orthographic camera covering ten world units of height.</summary>
    public static Camera Orthographic2D => Perspective with { Orthographic = true };
}
