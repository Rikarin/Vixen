// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Core;

namespace Vixen.Animation.Moves;

/// <summary>The one facet key the engine reserves, and the values it may take.</summary>
/// <remarks>
///     <para>
///         <b>Everything else in a project's vocabulary is the project's business; this is not.</b>
///         What can follow a run, whether two moves are the same kind of thing, which of them a phase
///         can be carried across — those are questions the transition rules and the phase sync have
///         to answer, and a set that spells the axis differently gets no answer from either. Without
///         a reserved key, no rule, no editor check and no imported set is portable between two
///         projects, which is the cross-product problem one level up.
///     </para>
///     <para>
///         ⚠ <b>The vocabulary is closed on purpose.</b> A project that needs a distinction this list
///         does not draw adds a facet of its own on another key — <c>style=vault</c> beside
///         <c>role=transition</c> — rather than inventing a role, because a role nothing recognises
///         is worse than no role at all.
///     </para>
/// </remarks>
public static class MoveRole {
    /// <summary>The facet key. <c>role</c>.</summary>
    public static Symbol Key { get; } = Symbol.Intern("role");

    /// <summary>Standing.</summary>
    public static Symbol Idle { get; } = Symbol.Intern("idle");

    /// <summary>Turning while standing.</summary>
    public static Symbol IdleTurn { get; } = Symbol.Intern("idle-turn");

    /// <summary>Entering movement from rest.</summary>
    public static Symbol Start { get; } = Symbol.Intern("start");

    /// <summary>Leaving movement to rest.</summary>
    public static Symbol Stop { get; } = Symbol.Intern("stop");

    /// <summary>The sustained cycle — what a gait mostly is.</summary>
    public static Symbol Loop { get; } = Symbol.Intern("loop");

    /// <summary>Changing heading while moving.</summary>
    public static Symbol Turn { get; } = Symbol.Intern("turn");

    /// <summary>One sustained gait to another.</summary>
    public static Symbol Transition { get; } = Symbol.Intern("transition");

    /// <summary>A single deliberate placement, not a cycle.</summary>
    public static Symbol Step { get; } = Symbol.Intern("step");

    /// <summary>The facet for a role.</summary>
    /// <param name="role">One of the values on this class.</param>
    /// <returns>The facet.</returns>
    public static Facet Facet(Symbol role) => new(Key, role);

    /// <summary>Whether a symbol is one of the reserved values.</summary>
    /// <param name="role">The candidate.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsKnown(Symbol role) =>
        role == Idle || role == IdleTurn || role == Start || role == Stop
        || role == Loop || role == Turn || role == Transition || role == Step;

    /// <summary>Every reserved value, for an editor's list and for validation.</summary>
    /// <returns>The values.</returns>
    public static IReadOnlyList<Symbol> All { get; } =
        [Idle, IdleTurn, Start, Stop, Loop, Turn, Transition, Step];
}

/// <summary>A move's identity: stable, hashable, and the same on every machine.</summary>
/// <param name="Hash">A hash of the move's name.</param>
/// <remarks>
///     <para>
///         <b>What an overlay keys on and what a tie breaks on.</b> Two sets are composed by matching
///         these, and a scoring tie is settled by comparing them — so it has to mean the same thing
///         in the editor that composed the set and on every machine that later plays it.
///     </para>
///     <para>
///         ⚠ <b>64 bits here where a <see cref="Symbol" /> is 32.</b> A symbol lives in a vocabulary
///         of a few dozen words that a bake can check for collisions; a key lives in a set of
///         hundreds of move names composed from several sources, and a collision there would silently
///         make one move override another. The extra four bytes are on a type that appears once per
///         entry rather than several times.
///     </para>
/// </remarks>
public readonly record struct MoveKey(ulong Hash) : IComparable<MoveKey> {
    /// <summary>Hashes a name.</summary>
    /// <param name="name">What the move is called.</param>
    /// <returns>The key.</returns>
    public static MoveKey Of(string name) {
        ArgumentNullException.ThrowIfNull(name);

        // FNV-1a, 64-bit. Same reasoning as Symbol: reimplementable anywhere, and not the runtime's
        // own randomised string hash.
        var hash = 14695981039346656037ul;

        foreach (var character in name) {
            hash ^= character;
            hash *= 1099511628211ul;
        }

        return new(hash);
    }

    /// <inheritdoc />
    public int CompareTo(MoveKey other) => Hash.CompareTo(other.Hash);

    /// <summary>Orders two keys.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts first.</returns>
    public static bool operator <(MoveKey left, MoveKey right) => left.Hash < right.Hash;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator <=(MoveKey left, MoveKey right) => left.Hash <= right.Hash;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >(MoveKey left, MoveKey right) => left.Hash > right.Hash;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >=(MoveKey left, MoveKey right) => left.Hash >= right.Hash;
}

/// <summary>The numbers a query is scored against, and the limits a move admits to.</summary>
/// <remarks>
///     ⚠ <b><see cref="MinRate" /> and <see cref="MaxRate" /> are what let a set have one clip per
///     gait rather than one every 0.4 m/s.</b> A walk cycle usually reads correctly ±15 %; a stop
///     whose weight lands on a specific frame does not survive any retiming at all, and a move that
///     says so is a move the selector will not stretch.
/// </remarks>
public readonly record struct MoveTraits {
    /// <summary>What it plays at as authored, in metres a second. Zero for a move that goes nowhere.</summary>
    public float Speed { get; init; }

    /// <summary>How fast it turns as authored, in radians a second. Signed; positive is left.</summary>
    public float TurnRate { get; init; }

    /// <summary>The slowest playback rate it still reads correctly at.</summary>
    public float MinRate { get; init; } = 1f;

    /// <summary>The fastest.</summary>
    public float MaxRate { get; init; } = 1f;

    /// <summary>Where in normalised time the first foot plants. What a phase sync aligns on.</summary>
    public float FootPhase { get; init; }

    /// <summary>Creates traits that admit no retiming.</summary>
    public MoveTraits() {
    }

    /// <summary>The slowest speed it can be retimed to.</summary>
    public float SlowestSpeed => Speed * MinRate;

    /// <summary>The fastest.</summary>
    public float FastestSpeed => Speed * MaxRate;

    /// <summary>The playback rate that would hit a speed, clamped to what the move admits.</summary>
    /// <param name="speed">The wanted speed, in metres a second.</param>
    /// <returns>The rate. One when the move goes nowhere, since nothing can be inferred.</returns>
    public float RateFor(float speed) =>
        Speed <= 1e-4f ? 1f : Math.Clamp(speed / Speed, MinRate, MaxRate);
}

/// <summary>One move: a clip or a tree, what it is for, and what it does.</summary>
/// <remarks>
///     <b>Flat, with style as an ordinary facet.</b> There is no container per style and no
///     containment between entries — a move's "style" has no more structural standing than "this one
///     turns left", which is what stops the set encoding a cross-product nobody wants to author.
/// </remarks>
public sealed class MoveEntry {
    /// <summary>Creates an entry.</summary>
    /// <param name="name">What the move is called. Its key is hashed from this.</param>
    /// <param name="motion">What plays.</param>
    /// <param name="facets">What it is for.</param>
    /// <param name="traits">What it does.</param>
    public MoveEntry(string name, Motion motion, FacetSet? facets = null, MoveTraits traits = default) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(motion);

        Name = name;
        Key = MoveKey.Of(name);
        Motion = motion;
        Facets = facets ?? FacetSet.Empty;
        Traits = traits;
    }

    /// <summary>What the move is called.</summary>
    public string Name { get; }

    /// <summary>Its identity. What an overlay matches on.</summary>
    public MoveKey Key { get; }

    /// <summary>What plays. A clip, or a tree of them.</summary>
    public Motion Motion { get; }

    /// <summary>What it is for.</summary>
    public FacetSet Facets { get; }

    /// <summary>What it does.</summary>
    public MoveTraits Traits { get; }

    /// <summary>Its role, or <see cref="Symbol.None" /> if it declares none.</summary>
    public Symbol Role => Facets.TryGet(MoveRole.Key, out var role) ? role : Symbol.None;

    /// <inheritdoc />
    public override string ToString() => $"{Name} {Facets}";
}
