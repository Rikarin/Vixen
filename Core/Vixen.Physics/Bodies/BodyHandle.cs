// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Physics.Bodies;

/// <summary>A body in a <see cref="PhysicsWorld" />.</summary>
/// <param name="Value">Jolt's body id: an index in the low bits and a sequence number in the high ones.</param>
/// <remarks>
///     <para>
///         The sequence number is what makes a stale handle detectable. Jolt reuses body indices as
///         soon as they are freed, and bumps the sequence each time, so a handle to a destroyed body
///         compares unequal to whatever took its slot instead of quietly addressing it — the same
///         property, for the same reason, that <c>Entity</c> gets from its version.
///     </para>
///     <para>
///         Handles belong to the world that issued them. Passing one to a different world is not
///         detectable — both are plain integers Jolt hands out from the same counter — so
///         <see cref="PhysicsWorld" /> keeps its own set and refuses handles it did not issue.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct BodyHandle(uint Value) {
    /// <summary>No body. Jolt's invalid id.</summary>
    public static BodyHandle None => new(uint.MaxValue);

    /// <summary>Whether this names a body at all.</summary>
    public bool IsNone => Value == uint.MaxValue;

    /// <summary>The index part, which is what indexes a side table of per-body data.</summary>
    /// <remarks>
    ///     Twenty-three bits, not twenty-four: bit 23 is Jolt's broad-phase flag and is set on every
    ///     body that has been added to the simulation. Masking it in would put each body two slots
    ///     apart in a side table and eight megabytes into the first allocation.
    /// </remarks>
    public uint Index => Value & 0x007FFFFFu;

    /// <summary>Renders the handle.</summary>
    /// <returns>The handle in text.</returns>
    public override string ToString() => IsNone ? "body none" : $"body #{Index}";
}
