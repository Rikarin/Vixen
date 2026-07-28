// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Physics;

/// <summary>Jolt could not be brought up — almost always a native library that would not load.</summary>
public sealed class PhysicsInitializationException : InvalidOperationException {
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public PhysicsInitializationException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public PhysicsInitializationException(string message, Exception innerException)
        : base(message, innerException) {
    }

    /// <summary>Creates the exception with no message.</summary>
    public PhysicsInitializationException() {
    }
}

/// <summary>
///     A handle does not name anything the world still has: a body that was destroyed, a shape from
///     a different registry, a constraint that went away with one of its bodies.
/// </summary>
/// <remarks>
///     Thrown rather than ignored, for the reason <c>EntityNotFoundException</c> gives: Jolt reuses
///     body indices, so a stale handle that is merely passed through addresses whichever body took
///     the slot, and the resulting bug looks like corruption rather than like a mistake.
/// </remarks>
public sealed class PhysicsHandleException : InvalidOperationException {
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong with the handle.</param>
    public PhysicsHandleException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What is wrong with the handle.</param>
    /// <param name="innerException">The cause.</param>
    public PhysicsHandleException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception with no message.</summary>
    public PhysicsHandleException() {
    }
}

/// <summary>A shape could not be built from the description it was given.</summary>
/// <remarks>
///     Jolt validates shape parameters at construction — a capsule with a negative radius, a convex
///     hull whose points are coplanar, a mesh with no triangles — and a shape that failed to build is
///     a null native pointer that crashes at the first body that uses it. Catching it here names the
///     description instead.
/// </remarks>
public sealed class PhysicsShapeException : ArgumentException {
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong with the description.</param>
    public PhysicsShapeException(string message) : base(message) {
    }

    /// <summary>Creates the exception naming the offending argument.</summary>
    /// <param name="message">What is wrong with the description.</param>
    /// <param name="paramName">The parameter that carried it.</param>
    public PhysicsShapeException(string message, string? paramName) : base(message, paramName) {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What is wrong with the description.</param>
    /// <param name="innerException">The cause.</param>
    public PhysicsShapeException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception with no message.</summary>
    public PhysicsShapeException() {
    }
}
