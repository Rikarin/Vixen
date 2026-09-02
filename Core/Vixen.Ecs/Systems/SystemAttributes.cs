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

/// <summary>
///     Asks the generator to read this system's access out of its own query bodies and emit the
///     declaration.
/// </summary>
/// <remarks>
///     <para>
///         The system has to be a <c>partial</c>, non-generic, top-level class implementing
///         <see cref="ISystem" />; the generated half implements <see cref="IDeclaredAccess" />. That
///         is the interface rather than the attributes deliberately —
///         <c>SystemAccess.Declare().Write&lt;Position&gt;()</c> closes a generic and so
///         <em>assigns</em> <c>Position</c> its component id, where an attribute can only look one up
///         and there is nothing to look up until something has stored one.
///     </para>
///     <para>
///         ⚠ <b>Opt-in, and it is not a formality.</b> Inference can only see what the class itself
///         says: a query the system builds by calling a helper in another assembly is invisible to
///         it, and an under-declared system is a data race rather than a slow one. Marking a class is
///         the author's statement that its access is visible in its own body.
///     </para>
///     <para>
///         ⚠ <b>Where the direction is not knowable it errs towards writing.</b> The delegate and
///         visitor forms take every component by <c>ref</c> whether or not the body assigns through
///         it, so their type arguments are inferred as writes. The chunk form distinguishes them,
///         because <c>Values&lt;T&gt;</c> and <c>ReadValues&lt;T&gt;</c> are different calls.
///         Over-declaring costs parallelism; under-declaring is the bug.
///     </para>
///     <para>
///         An explicit <see cref="ReadsAttribute" /> or <see cref="WritesAttribute" /> on the same
///         class overrides this: the generator emits nothing and says so, rather than leaving two
///         declarations where only one is read.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InferAccessAttribute : Attribute;

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
