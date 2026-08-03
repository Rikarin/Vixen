// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Animation.Constraints;

/// <summary>Pairs a loaded shape set with a rig, once.</summary>
/// <remarks>
///     <para>
///         <b>The same join <see cref="AnimationClipCache" /> does for a clip, and it was missing for
///         the same reason it was missing there.</b> <see cref="ProxyShapeSetContent" /> names joints
///         and does not know which skeleton will have them; <see cref="ProxyShapeSet" /> is the
///         resolved form with every shape bound to a joint index. Somebody has to do that resolution,
///         and doing it per character is how thirty copies of one enemy come to hold thirty identical
///         shape sets.
///     </para>
///     <para>
///         ⚠ <b>An empty set is an answer and not a failure.</b> A body with no shapes is legitimate,
///         and so is one whose shapes all name joints this rig does not have — the second is a
///         mistake, but it is a mistake the unresolved list reports rather than one a null return
///         would. Retrying a bad bake every frame would turn one authoring mistake
///         into a per-frame allocation, so the empty answer is cached like any other.
///     </para>
///     <para>
///         ⚠ <b>Unresolved shape names are collected on the first bake and kept.</b> A caller that
///         asks a second time is answered from the cache and would otherwise see an empty list —
///         which reads as "nothing was wrong" rather than "somebody already asked".
///     </para>
/// </remarks>
public static class ProxyShapeCache {
    static readonly ConditionalWeakTable<ProxyShapeSetContent, Entry> Table = [];

    /// <summary>The shape set for a content and a rig, baking it the first time it is asked for.</summary>
    /// <param name="content">The loaded set.</param>
    /// <param name="skeleton">The rig its shapes hang off.</param>
    /// <param name="unresolved">
    ///     Where the names of shapes whose joint the rig does not have go, or <see langword="null" />.
    ///     Filled from the first bake however many times this is called.
    /// </param>
    /// <returns>The set, shared with every other caller that asked for the same pair.</returns>
    /// <remarks>
    ///     <b>Safe to call every frame</b>, which is the point: a behaviour holding an asset handle
    ///     and a skeleton can ask in <c>Update</c> without caring whether this is the first frame.
    /// </remarks>
    public static ProxyShapeSet Get(
        ProxyShapeSetContent content,
        Skeleton skeleton,
        ICollection<string>? unresolved = null
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(skeleton);

        var entry = Table.GetValue(content, static _ => new Entry());

        lock (entry.Gate) {
            foreach (var baked in entry.Baked) {
                if (!ReferenceEquals(baked.Skeleton, skeleton)) {
                    continue;
                }

                Report(baked.Unresolved, unresolved);
                return baked.Set;
            }

            List<string> missing = [];

            var set = content.Bake(skeleton, missing);

            entry.Baked.Add(new(skeleton, set, missing));
            Report(missing, unresolved);

            return set;
        }
    }

    /// <summary>Forgets every bake of a content, for a rig that has been rebuilt underneath it.</summary>
    /// <param name="content">The loaded set.</param>
    /// <returns>Whether anything was cached.</returns>
    /// <remarks>
    ///     For the editor, where a skeleton can be re-imported while the body it dresses is still
    ///     open. A game has no reason to call it — the weak table already does the right thing when
    ///     content is unloaded.
    /// </remarks>
    public static bool Forget(ProxyShapeSetContent content) {
        ArgumentNullException.ThrowIfNull(content);
        return Table.Remove(content);
    }

    static void Report(List<string> from, ICollection<string>? into) {
        if (into is null) {
            return;
        }

        foreach (var name in from) {
            into.Add(name);
        }
    }

    /// <summary>A list, for <see cref="AnimationClipCache" />'s reason: a body has one rig.</summary>
    sealed class Entry {
        public Lock Gate { get; } = new();

        public List<Baked> Baked { get; } = [];
    }

    readonly record struct Baked(Skeleton Skeleton, ProxyShapeSet Set, List<string> Unresolved);
}
