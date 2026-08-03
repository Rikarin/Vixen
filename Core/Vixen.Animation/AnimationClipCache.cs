// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Animation;

/// <summary>Pairs a loaded clip with a rig, once.</summary>
/// <remarks>
///     <para>
///         <b>The join a compiled clip needs and cannot do for itself.</b>
///         <see cref="AnimationClipContent" /> is rig-independent — it names joints and does not know
///         which skeleton will have them — and <see cref="AnimationClip" /> is the resolved form,
///         with every channel bound to a joint index and a bucket table built over the duration.
///         Somebody has to do that resolution, and doing it per instance is how a hundred copies of
///         one enemy come to hold a hundred identical clips.
///     </para>
///     <para>
///         <b>Keyed on both halves, because both decide the answer.</b> The same content baked
///         against two skeletons is two different clips — different joint indices, possibly a
///         different set of resolved channels — so a cache keyed on the address alone would hand a
///         character the clip belonging to a different rig, which poses the wrong joints and looks
///         like a corrupted animation rather than a cache bug.
///     </para>
///     <para>
///         ⚠ <b>Entries live as long as the content does and no longer.</b> The outer table holds its
///         key weakly, so unloading a clip's asset drops every rig's bake of it without anybody
///         calling a clear method. A static dictionary here would be a leak with a plausible excuse —
///         it is a cache — and the leak would be the whole animation set of every level ever loaded.
///     </para>
///     <para>
///         <b>Reference identity, not value equality.</b> Two <see cref="AnimationClipContent" />
///         instances with identical channels are two entries, which is right: the asset manager hands
///         out one instance per address, so equal-but-distinct contents mean somebody deserialised
///         twice and the duplicate bake is the smaller of their problems.
///     </para>
/// </remarks>
public static class AnimationClipCache {
    static readonly ConditionalWeakTable<AnimationClipContent, Entry> table = [];

    /// <summary>The clip for a content and a rig, baking it the first time it is asked for.</summary>
    /// <param name="content">The loaded clip.</param>
    /// <param name="skeleton">The skeleton to resolve its channels against.</param>
    /// <param name="rootJoint">
    ///     Which joint carries the character through the world, or <see langword="null" /> for the
    ///     skeleton's first root.
    /// </param>
    /// <returns>The runtime clip, shared with every other caller that asked for the same pair.</returns>
    /// <remarks>
    ///     <b>Safe to call every frame</b>, which is the point: a behaviour that holds an
    ///     <c>AssetHandle</c> and a skeleton can ask for its clip in <c>Update</c> without caring
    ///     whether the asset has finished loading or whether it is the first frame.
    /// </remarks>
    public static AnimationClip Get(AnimationClipContent content, Skeleton skeleton, string? rootJoint = null) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(skeleton);

        var entry = table.GetValue(content, static _ => new Entry());

        lock (entry.Gate) {
            foreach (var baked in entry.Baked) {
                if (ReferenceEquals(baked.Skeleton, skeleton)
                    && string.Equals(baked.RootJoint, rootJoint, StringComparison.Ordinal)) {
                    return baked.Clip;
                }
            }

            var clip = content.Bake(skeleton, rootJoint);
            entry.Baked.Add(new(skeleton, rootJoint, clip));

            return clip;
        }
    }

    /// <summary>Forgets every bake of a content, for a rig that has been rebuilt underneath it.</summary>
    /// <param name="content">The loaded clip.</param>
    /// <returns>Whether anything was cached.</returns>
    /// <remarks>
    ///     Exists for the editor, where a skeleton can be re-imported while the scene it poses is
    ///     still open. A game has no reason to call it: the weak table already does the right thing
    ///     when content is unloaded.
    /// </remarks>
    public static bool Forget(AnimationClipContent content) {
        ArgumentNullException.ThrowIfNull(content);
        return table.Remove(content);
    }

    /// <summary>
    ///     A list rather than a dictionary, because a clip is played on one rig in almost every case
    ///     and on a handful in the rest. Hashing a skeleton reference to find the only entry would
    ///     cost more than the comparison it replaces.
    /// </summary>
    sealed class Entry {
        public Lock Gate { get; } = new();

        public List<Baked> Baked { get; } = [];
    }

    readonly record struct Baked(Skeleton Skeleton, string? RootJoint, AnimationClip Clip);
}
