// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Animation.Motions;

namespace Vixen.Animation.Moves;

/// <summary>Turns a loaded move set into one a selector can be pointed at.</summary>
/// <remarks>
///     <para>
///         <b>The third of the joins <see cref="AnimationClipCache" /> made for a clip.</b> A
///         <see cref="MoveSetContent" /> names its clips by address and its overlays by address, and
///         neither is anything a selector can read: <see cref="MoveSet" /> holds
///         <see cref="Motion" />s, and a motion holds a clip already baked against a rig.
///     </para>
///     <para>
///         ⚠ <b>Keyed on the rig, because the clips are.</b> Every row goes through
///         <see cref="AnimationClipCache" />, so a set baked against two skeletons is two sets holding
///         two different clips — and a cache keyed on the address alone would hand a character
///         somebody else's joint indices.
///     </para>
///     <para>
///         ⚠ <b>A row whose clip will not load is dropped and named.</b> An entry with no motion would
///         be selected like any other and then play silence, which reads in game as a character
///         freezing — much harder to trace than a set with one fewer move in it.
///         <see cref="MoveSetContent.Preview" /> is the editor's opposite choice and says why.
///     </para>
///     <para>
///         ⚠ <b>An overlay cycle is broken rather than followed.</b> A set naming itself, directly or
///         round a chain, is a mistake somebody will make in a text file — and following it is a stack
///         overflow rather than a diagnostic.
///     </para>
/// </remarks>
public static class MoveSetCache {
    static readonly ConditionalWeakTable<MoveSetContent, Entry> table = [];

    /// <summary>The set for a content and a rig, baking it the first time it is asked for.</summary>
    /// <param name="content">The loaded set.</param>
    /// <param name="skeleton">The rig its clips are played on.</param>
    /// <param name="clips">How a clip address is loaded, or <see langword="null" /> for none.</param>
    /// <param name="overlays">How a base set's address is loaded, or <see langword="null" /> for none.</param>
    /// <param name="unresolved">Where the names of dropped rows go. Filled from the first bake.</param>
    /// <returns>The set, shared with every other caller that asked for the same pair.</returns>
    /// <remarks>
    ///     <b>Safe to call every frame.</b> A behaviour that holds an asset handle and a skeleton asks
    ///     in <c>Update</c> without caring whether this is the first frame or whether the clips have
    ///     finished loading — a row whose clip is not there yet is simply not in the set, and the set
    ///     is rebuilt when <see cref="Forget" /> is called or the content is reloaded.
    /// </remarks>
    public static MoveSet Get(
        MoveSetContent content,
        Skeleton skeleton,
        Func<string, AnimationClipContent?>? clips = null,
        Func<string, MoveSetContent?>? overlays = null,
        ICollection<string>? unresolved = null
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(skeleton);

        var entry = table.GetValue(content, static _ => new Entry());

        lock (entry.Gate) {
            foreach (var baked in entry.Baked) {
                if (!ReferenceEquals(baked.Skeleton, skeleton)) {
                    continue;
                }

                Report(baked.Unresolved, unresolved);
                return baked.Set;
            }

            List<string> missing = [];
            HashSet<MoveSetContent> visiting = [content];

            var set = Compose(content, skeleton, clips, overlays, missing, visiting);

            entry.Baked.Add(new(skeleton, set, missing));
            Report(missing, unresolved);

            return set;
        }
    }

    /// <summary>Forgets every bake of a content, for a rig or a clip that has been re-imported.</summary>
    /// <param name="content">The loaded set.</param>
    /// <returns>Whether anything was cached.</returns>
    public static bool Forget(MoveSetContent content) {
        ArgumentNullException.ThrowIfNull(content);
        return table.Remove(content);
    }

    /// <summary>
    ///     ⚠ <b>Bases are composed without going through the cache</b>, because a base baked here
    ///     would be keyed on the rig and reachable by anybody, and a caller asking for the base
    ///     directly would then get the copy this composition made rather than one of its own. The
    ///     duplicate work is one bake per base per rig, which happens once.
    /// </summary>
    static MoveSet Compose(
        MoveSetContent content,
        Skeleton skeleton,
        Func<string, AnimationClipContent?>? clips,
        Func<string, MoveSetContent?>? overlays,
        List<string> missing,
        HashSet<MoveSetContent> visiting
    ) {
        List<MoveSet> bases = [];

        if (overlays is not null) {
            foreach (var address in content.Bases) {
                if (overlays(address) is not { } under) {
                    missing.Add(address);
                    continue;
                }

                if (!visiting.Add(under)) {
                    // Named twice, or round a chain. Either way this set is already being composed
                    // further up, and following it again is a stack overflow rather than an answer.
                    missing.Add(address);
                    continue;
                }

                bases.Add(Compose(under, skeleton, clips, overlays, missing, visiting));
                visiting.Remove(under);
            }
        }

        return content.Bake(address => Motion(address, skeleton, clips), bases, missing);
    }

    static ClipMotion? Motion(string address, Skeleton skeleton, Func<string, AnimationClipContent?>? clips) =>
        clips?.Invoke(address) is { } clip ? new ClipMotion(AnimationClipCache.Get(clip, skeleton)) : null;

    static void Report(List<string> from, ICollection<string>? into) {
        if (into is null) {
            return;
        }

        foreach (var name in from) {
            into.Add(name);
        }
    }

    sealed class Entry {
        public Lock Gate { get; } = new();

        public List<Baked> Baked { get; } = [];
    }

    readonly record struct Baked(Skeleton Skeleton, MoveSet Set, List<string> Unresolved);
}
