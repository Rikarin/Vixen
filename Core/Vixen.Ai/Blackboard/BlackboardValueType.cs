// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>What a blackboard key holds. Six kinds, and the list is closed.</summary>
/// <remarks>
///     <para>
///         <b>Closing it is what makes the rest of the design possible.</b> A key is twelve bytes at
///         worst, a comparison is a switch rather than a virtual call, and an inspector can draw
///         every key there is with no extension point — which is what lets the editor's key list be
///         a table rather than a plugin surface.
///     </para>
///     <para>
///         Everything a game wants is one of these. A "class" or a state name is a
///         <see cref="Symbol" />, a rotation is three floats or an entity to look at, an object
///         reference is an <see cref="Entity" />, and a count is an <see cref="Int" />. The type that
///         is missing — an arbitrary object — is missing deliberately: a key holding a reference is
///         a key that cannot be compared, serialised, replicated or drawn, and it is the escape
///         hatch that turns a compiled table back into a dictionary.
///     </para>
/// </remarks>
public enum BlackboardValueType : byte {
    /// <summary>One bit of state, stored as a byte.</summary>
    Bool,

    /// <summary>A 32-bit signed integer: a count, an ammo total, a phase number.</summary>
    Int,

    /// <summary>A 32-bit float: a distance, a timer, a score.</summary>
    Float,

    /// <summary>A position or a direction in world space.</summary>
    Vector3,

    /// <summary>An entity: the target, the leader, the thing being carried.</summary>
    Entity,

    /// <summary>An interned name: a state, a stance, a tag the game means something by.</summary>
    Symbol
}
