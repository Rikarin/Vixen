// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     A pre-registered, interned name for something worth timing. An <see cref="int" /> at run
///     time; a readable name only when a report is written.
/// </summary>
/// <remarks>
///     <para>
///         The reason a profiler can be left on. A sample carries this and two timestamps, so
///         recording one is a few stores into a ring — no string, no dictionary, no allocation.
///         Resolving the name happens once, when a trace is exported, by which time nobody cares
///         what it costs.
///     </para>
///     <para>
///         Declare keys in a static class per subsystem, so registration happens once at type
///         initialisation rather than in the loop being measured:
///     </para>
///     <code>
///     static class RenderKeys {
///         public static readonly ProfilingKey Culling = ProfilingKey.Register("Render.Culling");
///     }
///     </code>
/// </remarks>
[DataContract]
public readonly record struct ProfilingKey(int Id) : IComparable<ProfilingKey> {
    static readonly ConcurrentDictionary<string, ProfilingKey> ByName = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<int, string> ByIdentifier = new();
    static int next;

    /// <summary>The key that names nothing, and the value of an uninitialised key.</summary>
    public static ProfilingKey None => default;

    /// <summary>Whether this names a registered scope.</summary>
    public bool IsValid => Id > 0;

    /// <summary>The registered name, or <c>"&lt;none&gt;"</c>.</summary>
    public string Name => ByIdentifier.TryGetValue(Id, out var name) ? name : "<none>";

    /// <summary>How many keys have been registered.</summary>
    public static int RegisteredCount => ByIdentifier.Count;

    /// <summary>
    ///     Interns a name, returning the same key every time. Safe to call from several threads and
    ///     safe to call repeatedly; the intended use is once, from a static field initialiser.
    /// </summary>
    /// <param name="name">The scope name. Dotted, subsystem first, so a trace groups readably.</param>
    /// <returns>The key for that name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public static ProfilingKey Register(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Ids start at 1 so that a zeroed key is detectably unregistered rather than an alias for
        // whichever scope happened to be declared first.
        return ByName.GetOrAdd(
            name,
            static key => {
                var registered = new ProfilingKey(Interlocked.Increment(ref next));
                ByIdentifier[registered.Id] = key;
                return registered;
            }
        );
    }

    /// <summary>Looks up an already-registered key.</summary>
    /// <param name="name">The scope name.</param>
    /// <param name="key">The key, or <see cref="None" />.</param>
    /// <returns><see langword="false" /> if the name was never registered.</returns>
    public static bool TryGet(string name, out ProfilingKey key) => ByName.TryGetValue(name, out key);

    /// <inheritdoc />
    public int CompareTo(ProfilingKey other) => Id.CompareTo(other.Id);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(ProfilingKey left, ProfilingKey right) => left.Id < right.Id;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(ProfilingKey left, ProfilingKey right) => left.Id <= right.Id;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(ProfilingKey left, ProfilingKey right) => left.Id > right.Id;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(ProfilingKey left, ProfilingKey right) => left.Id >= right.Id;

    /// <summary>The registered name.</summary>
    /// <returns>The name this key was registered under.</returns>
    public override string ToString() => Name;
}
