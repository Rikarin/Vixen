// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ecs.Systems;

/// <summary>Declares the component types a system reads.</summary>
/// <remarks>
///     Read against read is not a conflict, so two systems that only read the same components run at
///     the same time. Declaring more than the system touches costs parallelism; declaring less is a
///     data race, so <b>the safe direction is over-declaring</b> and that is the direction the
///     generator will err in when it infers these from query bodies.
/// </remarks>
/// <param name="componentTypes">The component types.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class ReadsAttribute(params Type[] componentTypes) : Attribute {
    /// <summary>The component types.</summary>
    public IReadOnlyList<Type> ComponentTypes { get; } = componentTypes;
}

/// <summary>Declares the component types a system writes.</summary>
/// <param name="componentTypes">The component types.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WritesAttribute(params Type[] componentTypes) : Attribute {
    /// <summary>The component types.</summary>
    public IReadOnlyList<Type> ComponentTypes { get; } = componentTypes;
}

/// <summary>Puts a system in a phase. Without one, a system lands in <see cref="SystemPhase.Update" />.</summary>
/// <param name="phase">Which phase.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class UpdateInGroupAttribute(SystemPhase phase) : Attribute {
    /// <summary>Which phase.</summary>
    public SystemPhase Phase { get; } = phase;
}

/// <summary>Orders this system before another in the same phase.</summary>
/// <param name="systemType">The system that must run after this one.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class UpdateBeforeAttribute(Type systemType) : Attribute {
    /// <summary>The system that must run after this one.</summary>
    public Type SystemType { get; } = systemType;
}

/// <summary>Orders this system after another in the same phase.</summary>
/// <param name="systemType">The system that must run before this one.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class UpdateAfterAttribute(Type systemType) : Attribute {
    /// <summary>The system that must run before this one.</summary>
    public Type SystemType { get; } = systemType;
}
