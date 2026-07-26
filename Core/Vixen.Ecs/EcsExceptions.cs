// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>
///     An entity handle no longer names a live entity — it was destroyed, or it came from another
///     world.
/// </summary>
/// <remarks>
///     This is the payoff for versioning the handle. Without it the slot has been reused and the
///     call quietly reads or writes whatever entity occupies it now, which is a bug that reproduces
///     once a week and looks like corruption.
/// </remarks>
public sealed class EntityNotFoundException : InvalidOperationException {
    /// <summary>The handle that was used.</summary>
    public Entity Entity { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="entity">The handle that was used.</param>
    /// <param name="reason">What is wrong with it.</param>
    public EntityNotFoundException(Entity entity, string reason) : base($"Entity {entity} {reason}.") =>
        Entity = entity;

    /// <summary>Creates the exception with a plain message.</summary>
    /// <param name="message">The message.</param>
    public EntityNotFoundException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public EntityNotFoundException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception with no message.</summary>
    public EntityNotFoundException() {
    }
}

/// <summary>An entity is alive but does not have the component that was asked for.</summary>
public sealed class ComponentNotFoundException : InvalidOperationException {
    /// <summary>The entity.</summary>
    public Entity Entity { get; }

    /// <summary>The component type that is missing.</summary>
    public Type? ComponentType { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="componentType">The component type that is missing.</param>
    /// <param name="archetype">What the entity does have, which is what the reader wants to know.</param>
    public ComponentNotFoundException(Entity entity, Type componentType, ComponentSignature archetype)
        : base($"Entity {entity} has no {componentType.Name}. It has {archetype}.") {
        Entity = entity;
        ComponentType = componentType;
    }

    /// <summary>Creates the exception with a plain message.</summary>
    /// <param name="message">The message.</param>
    public ComponentNotFoundException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ComponentNotFoundException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception with no message.</summary>
    public ComponentNotFoundException() {
    }
}
