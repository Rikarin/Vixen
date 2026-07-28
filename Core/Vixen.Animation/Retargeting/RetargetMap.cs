// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Retargeting;

/// <summary>What a target joint takes from the source it is mapped to.</summary>
public enum RetargetMode {
    /// <summary>Nothing. The joint stays in the target's bind pose.</summary>
    Ignore,

    /// <summary>
    ///     Rotation only. The joint keeps the target rig's own bone length, which is the whole point
    ///     of retargeting rather than copying.
    /// </summary>
    Rotation,

    /// <summary>
    ///     Rotation, and the translation the source joint moved by, scaled to the target's
    ///     proportions. What exactly one joint per rig wants — the pelvis.
    /// </summary>
    RotationAndTranslation
}

/// <summary>
///     Which joint of one skeleton drives which joint of another, and how much of it it takes.
/// </summary>
/// <remarks>
///     <para>
///         <b>Indexed by the target, not the source.</b> A target joint has at most one source, a
///         source joint may drive several targets or none, and the pass that does the work walks
///         target joints in order. Storing it the other way round would mean a search per joint per
///         frame to answer the question the loop actually asks.
///     </para>
///     <para>
///         <b>Names are matched once, here.</b> Two rigs from different tools agree on almost
///         nothing — <c>mixamorig:LeftForeArm</c>, <c>lowerarm_l</c>, <c>arm.L</c> — so the mapping is
///         authored, and <see cref="Builder.ByName" /> is the shortcut for the case where somebody
///         has already made the names agree.
///     </para>
/// </remarks>
public sealed class RetargetMap {
    readonly int[] sourceOf;
    readonly RetargetMode[] modes;

    RetargetMap(Skeleton source, Skeleton target, int[] sourceOf, RetargetMode[] modes) {
        Source = source;
        Target = target;
        this.sourceOf = sourceOf;
        this.modes = modes;

        foreach (var mapped in sourceOf) {
            if (mapped >= 0) {
                MappedJointCount++;
            }
        }
    }

    /// <summary>The skeleton the animation was authored on.</summary>
    public Skeleton Source { get; }

    /// <summary>The skeleton it is being moved to.</summary>
    public Skeleton Target { get; }

    /// <summary>Which source joint drives each target joint, or −1. Indexed by target joint.</summary>
    public ReadOnlySpan<int> SourceOf => sourceOf;

    /// <summary>What each target joint takes. Indexed by target joint.</summary>
    public ReadOnlySpan<RetargetMode> Modes => modes;

    /// <summary>How many target joints have a source.</summary>
    /// <remarks>
    ///     Worth looking at after a <see cref="Builder.ByName" />: a mapping that covers three joints
    ///     of a sixty-joint rig is two rigs that do not share a naming convention, and it is the
    ///     number that says so before anybody watches the result.
    /// </remarks>
    public int MappedJointCount { get; }

    /// <summary>Which target joint carries the character through the world, or −1.</summary>
    /// <remarks>The one with <see cref="RetargetMode.RotationAndTranslation" />, if any.</remarks>
    public int TranslationJoint {
        get {
            for (var index = 0; index < modes.Length; index++) {
                if (modes[index] is RetargetMode.RotationAndTranslation) {
                    return index;
                }
            }

            return -1;
        }
    }

    /// <summary>Starts a mapping between two skeletons, with nothing mapped.</summary>
    /// <param name="source">The skeleton the animation was authored on.</param>
    /// <param name="target">The skeleton it is being moved to.</param>
    /// <returns>The builder.</returns>
    public static Builder Between(Skeleton source, Skeleton target) => new(source, target);

    /// <summary>Assembles a mapping.</summary>
    public struct Builder {
        readonly Skeleton source;
        readonly Skeleton target;
        readonly int[] sourceOf;
        readonly RetargetMode[] modes;

        internal Builder(Skeleton source, Skeleton target) {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(target);

            this.source = source;
            this.target = target;
            sourceOf = new int[target.JointCount];
            modes = new RetargetMode[target.JointCount];

            Array.Fill(sourceOf, -1);
        }

        /// <summary>Maps every target joint whose name a source joint also has.</summary>
        /// <param name="sourcePrefix">
        ///     A prefix to strip from the source's names before comparing — <c>"mixamorig:"</c>, and
        ///     the reason this parameter exists at all.
        /// </param>
        /// <param name="comparison">How names are compared. Ordinal by default.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <remarks>
        ///     Does not clear what was mapped by hand: an explicit <see cref="Map" /> before this
        ///     call survives it, so the pattern of "match the ones that agree, then fix the three
        ///     that do not" works in either order.
        /// </remarks>
        public Builder ByName(
            string sourcePrefix = "",
            StringComparison comparison = StringComparison.Ordinal
        ) {
            var names = source.Names;

            for (var index = 0; index < sourceOf.Length; index++) {
                if (sourceOf[index] >= 0) {
                    continue;
                }

                var wanted = target.NameOf(index);

                for (var candidate = 0; candidate < names.Length; candidate++) {
                    var name = names[candidate];

                    if (sourcePrefix.Length > 0 && name.StartsWith(sourcePrefix, comparison)) {
                        name = name[sourcePrefix.Length..];
                    }

                    if (string.Equals(name, wanted, comparison)) {
                        sourceOf[index] = candidate;
                        modes[index] = RetargetMode.Rotation;

                        break;
                    }
                }
            }

            return this;
        }

        /// <summary>Maps one joint by name.</summary>
        /// <param name="sourceJoint">The source joint's name.</param>
        /// <param name="targetJoint">The target joint's name.</param>
        /// <param name="mode">What the target takes.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <remarks>
        ///     A name neither skeleton has is ignored. A mapping outlives the rigs it was authored
        ///     against, and a joint that no longer exists means one joint that does not animate — not
        ///     a load that fails.
        /// </remarks>
        public Builder Map(string sourceJoint, string targetJoint, RetargetMode mode = RetargetMode.Rotation) {
            var from = source.IndexOf(sourceJoint);
            var to = target.IndexOf(targetJoint);

            if (from < 0 || to < 0) {
                return this;
            }

            sourceOf[to] = from;
            modes[to] = mode;

            return this;
        }

        /// <summary>Changes what an already-mapped target joint takes.</summary>
        /// <param name="targetJoint">The target joint's name.</param>
        /// <param name="mode">What it takes.</param>
        /// <returns>The builder, for chaining.</returns>
        public Builder SetMode(string targetJoint, RetargetMode mode) {
            var index = target.IndexOf(targetJoint);

            if (index >= 0) {
                modes[index] = mode;
            }

            return this;
        }

        /// <summary>Finishes the mapping.</summary>
        /// <returns>The mapping.</returns>
        /// <remarks>
        ///     If nothing was given <see cref="RetargetMode.RotationAndTranslation" />, the topmost
        ///     mapped joint gets it. Without a joint that carries translation the character animates
        ///     perfectly and never leaves the origin, which is a mistake that looks like a bug in the
        ///     clip rather than a hole in the mapping — so the default is the thing somebody would
        ///     have picked anyway, and <see cref="SetMode" /> overrides it.
        /// </remarks>
        public readonly RetargetMap Build() {
            var translation = -1;

            for (var index = 0; index < modes.Length; index++) {
                if (modes[index] is RetargetMode.RotationAndTranslation) {
                    translation = index;
                    break;
                }

                if (translation < 0 && sourceOf[index] >= 0 && modes[index] is not RetargetMode.Ignore) {
                    translation = index;
                }
            }

            if (translation >= 0) {
                modes[translation] = RetargetMode.RotationAndTranslation;
            }

            return new(source, target, sourceOf, modes);
        }
    }
}
