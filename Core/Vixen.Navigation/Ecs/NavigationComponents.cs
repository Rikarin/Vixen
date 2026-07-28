// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Agents;

namespace Vixen.Navigation.Ecs;

/// <summary>An entity that walks on the navmesh.</summary>
/// <remarks>
///     <para>
///         The authored half is the size and the speed; <see cref="Handle" /> is the system's, and is
///         how the entity is joined to its agent in the <see cref="Crowd" />. It is written once, when
///         the entity is first seen, and read every frame after that.
///     </para>
///     <para>
///         Adding this component is what puts an entity in the crowd, and removing it or destroying
///         the entity is what takes it out — there is no register call. That is the ECS bargain: the
///         set of things being simulated is a query, not a list somebody has to keep in step.
///     </para>
/// </remarks>
public struct NavigationAgent {
    /// <summary>How wide the entity is. Should match what the mesh was baked for.</summary>
    public float Radius;

    /// <summary>How tall it is.</summary>
    public float Height;

    /// <summary>The fastest it walks.</summary>
    public float MaxSpeed;

    /// <summary>How quickly it can change velocity.</summary>
    public float MaxAcceleration;

    /// <summary>Whether it steers around other agents.</summary>
    public bool AvoidanceEnabled;

    /// <summary>Which agent of the crowd this is. Owned by <c>NavigationSystem</c>.</summary>
    public CrowdAgentHandle Handle;

    /// <summary>
    ///     The <see cref="NavigationDestination.Version" /> the crowd has already been told about.
    ///     Owned by <c>NavigationSystem</c>.
    /// </summary>
    public uint AppliedDestination;

    /// <summary>A human-sized walker, not yet joined to a crowd.</summary>
    /// <returns>The component.</returns>
    public static NavigationAgent Default() => new() {
        Radius = 0.6f,
        Height = 2f,
        MaxSpeed = 3.5f,
        MaxAcceleration = 8f,
        AvoidanceEnabled = true,
        Handle = CrowdAgentHandle.Null
    };
}

/// <summary>Where an entity has been told to go.</summary>
/// <remarks>
///     A component rather than a method call, so that "go here" is a piece of data a prefab can carry,
///     a command buffer can add, and a save file can hold. <see cref="Version" /> is what makes
///     re-issuing the same destination mean something: bump it and the path is planned again, which is
///     what a caller wants after the world has changed under a stale path.
/// </remarks>
public struct NavigationDestination {
    /// <summary>Where to go, in world space.</summary>
    public Vector3 Value;

    /// <summary>Increment to force a replan without moving the destination.</summary>
    public uint Version;
}

/// <summary>What the crowd did with an entity this frame.</summary>
/// <remarks>Written by the system, never read by it. Game code and animation read it.</remarks>
public struct NavigationState {
    /// <summary>How the entity is moving, in world space.</summary>
    public Vector3 Velocity;

    /// <summary>Where it is standing, snapped to the mesh.</summary>
    public Vector3 Position;

    /// <summary>How its journey is going.</summary>
    public CrowdTargetState Target;
}
