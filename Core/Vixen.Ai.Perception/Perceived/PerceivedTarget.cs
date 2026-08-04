// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception;

/// <summary>Something a listener knows about, and how it found out.</summary>
/// <param name="Source">What was perceived.</param>
/// <param name="Sense">Which sense last reported it. <see cref="AiSense.Sight" /> wins any tie.</param>
/// <param name="LastKnownLocation">Where it was when the sense last had it.</param>
/// <param name="Strength">What the source's <c>Strength</c> was then — how loud, how bright, how much damage.</param>
/// <param name="Stamp">The clock reading when the sense last had it.</param>
/// <param name="Current">Whether the most recent pass perceived it, as opposed to remembering it.</param>
/// <remarks>
///     ⚠ <b><see cref="LastKnownLocation" /> is frozen when <see cref="Current" /> goes false, and that
///     is the whole reason the list survives losing sight of something.</b> "Search where he was" is
///     otherwise a thing every game writes by hand — a component holding a position, a timer, and the
///     code that decides when to throw both away — and every hand-written copy of it disagrees with
///     the sense about when the target was actually lost.
/// </remarks>
public readonly record struct PerceivedTarget(
    Entity Source,
    AiSense Sense,
    Vector3 LastKnownLocation,
    float Strength,
    float Stamp,
    bool Current
) {
    /// <summary>How long ago the sense last had it, in seconds.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>The age, which is zero while it is being perceived.</returns>
    public float AgeAt(float now) => MathF.Max(0f, now - Stamp);
}

/// <summary>What one listener currently knows, and what it has not forgotten yet.</summary>
/// <remarks>
///     <para>
///         One of these lives beside each listener's slot in <see cref="Ecs.PerceptionSystem" />, for the
///         reason a blackboard does: it is a managed object with a list in it, which is not a thing
///         that goes in a chunk column.
///     </para>
///     <para>
///         <b>Bounded, and it drops the oldest.</b> <see cref="PerceptionConfig.MaxPerceived" /> is
///         what keeps an agent standing in a crowd from growing a list the size of the crowd — and
///         dropping the <i>oldest</i> rather than refusing the newest is what stops a stale memory
///         from locking out the thing that just shot at it.
///     </para>
/// </remarks>
public sealed class PerceivedTargets {
    readonly List<PerceivedTarget> targets = [];

    // What was current when the pass began, because Current is cleared before the pass reports and
    // the lose-sight radius is a question about the *previous* answer. Kept as a small list rather
    // than a set: it is bounded by MaxPerceived, and a linear scan of sixteen entries beats hashing.
    readonly List<(Entity Source, AiSense Sense)> before = [];

    /// <summary>How many it holds, remembered ones included.</summary>
    public int Count => targets.Count;

    /// <summary>Everything it knows about, in no particular order.</summary>
    public ReadOnlySpan<PerceivedTarget> Targets => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(targets);

    /// <summary>What it knows about one entity.</summary>
    /// <param name="source">The entity.</param>
    /// <param name="target">Where to put it.</param>
    /// <returns>Whether it knows about it at all.</returns>
    public bool TryGet(Entity source, out PerceivedTarget target) {
        foreach (var candidate in targets) {
            if (candidate.Source == source) {
                target = candidate;

                return true;
            }
        }

        target = default;

        return false;
    }

    /// <summary>Whether it is perceiving anything at all through a sense right now.</summary>
    /// <param name="senses">Which senses count.</param>
    /// <returns>Whether any current target came in through one of them.</returns>
    public bool IsPerceiving(SenseMask senses) {
        foreach (var target in targets) {
            if (target.Current && senses.Has(target.Sense)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest thing it knows about.</summary>
    /// <param name="senses">Which senses count.</param>
    /// <param name="from">Where the listener is.</param>
    /// <param name="target">Where to put it.</param>
    /// <param name="currentOnly">Whether a remembered target is allowed to win.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     ⚠ Nearest by <see cref="PerceivedTarget.LastKnownLocation" />, which for a remembered
    ///     target is where it <i>was</i>. That is the right answer — it is the only position anybody
    ///     has — but it means "nearest" can name something that has since walked away, which is
    ///     exactly the situation <see cref="PerceivedTarget.AgeAt" /> exists to let a tree notice.
    /// </remarks>
    public bool TryNearest(SenseMask senses, Vector3 from, out PerceivedTarget target, bool currentOnly = true) {
        var best = float.MaxValue;
        var found = false;

        target = default;

        foreach (var candidate in targets) {
            if ((currentOnly && !candidate.Current) || !senses.Has(candidate.Sense)) {
                continue;
            }

            var distance = (candidate.LastKnownLocation - from).LengthSquared();

            if (distance >= best) {
                continue;
            }

            best = distance;
            target = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>The freshest thing it knows about, current or remembered.</summary>
    /// <param name="senses">Which senses count.</param>
    /// <param name="target">Where to put it.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>What the default binding writes: the most recent news, which is what a tree acts on.</remarks>
    public bool TryFreshest(SenseMask senses, out PerceivedTarget target) {
        var best = float.MinValue;
        var found = false;

        target = default;

        foreach (var candidate in targets) {
            if (!senses.Has(candidate.Sense)) {
                continue;
            }

            // Current beats remembered outright, and only then does the stamp break the tie — so a
            // target being looked at now cannot lose to one that was heard a moment later and lost.
            var rank = candidate.Stamp + (candidate.Current ? 1e6f : 0f);

            if (rank <= best) {
                continue;
            }

            best = rank;
            target = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>The one thing this listener would shout to an ally.</summary>
    /// <param name="target">Where to put it.</param>
    /// <returns>Whether there was anything worth shouting.</returns>
    /// <remarks>
    ///     The freshest target it perceived <i>itself</i> — a relayed one is not passed on, because a
    ///     chain of relays wakes a whole level several seconds after one guard saw something with no
    ///     guard in the chain having seen it. One target rather than the list is what keeps the team
    ///     sense from costing <c>listeners × allies × targets</c>; see <c>PerceptionSystem.Relayed</c>.
    /// </remarks>
    public bool TryShout(out PerceivedTarget target) {
        var best = float.MinValue;
        var found = false;

        target = default;

        foreach (var candidate in targets) {
            if (!candidate.Current || candidate.Sense == AiSense.Team || candidate.Stamp <= best) {
                continue;
            }

            best = candidate.Stamp;
            target = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>Whether a sense had this source when the current pass began.</summary>
    /// <param name="source">The entity.</param>
    /// <param name="sense">The sense.</param>
    /// <returns>Whether it did.</returns>
    /// <remarks>
    ///     ⚠ What <see cref="SightSettings.LoseSightRadius" /> is asked about. It has to be the state
    ///     <i>before</i> this pass reported anything, or the larger radius would apply to a target the
    ///     same pass had already found — which makes the radius that finds something and the radius
    ///     that keeps it the same number again, and the flicker comes back.
    /// </remarks>
    public bool WasPerceived(Entity source, AiSense sense) {
        foreach (var entry in before) {
            if (entry.Source == source && entry.Sense == sense) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() {
        targets.Clear();
        before.Clear();
    }

    /// <summary>Marks everything as remembered rather than perceived, before a pass reports.</summary>
    internal void BeginPass() {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(targets);

        before.Clear();

        for (var index = 0; index < span.Length; index++) {
            if (span[index].Current) {
                before.Add((span[index].Source, span[index].Sense));
            }

            span[index] = span[index] with { Current = false };
        }
    }

    /// <summary>Records that a sense has it.</summary>
    /// <remarks>
    ///     ⚠ The tie-break is <see cref="AiSense" />'s own order, so a target that is both seen and
    ///     heard in one pass is recorded as <i>seen</i>. Sight knows where something is; hearing knows
    ///     where a noise was, and letting the second overwrite the first would move a visible enemy to
    ///     wherever it last made a sound.
    /// </remarks>
    internal void Report(Entity source, AiSense sense, Vector3 where, float strength, float now, int maximum) {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(targets);

        for (var index = 0; index < span.Length; index++) {
            if (span[index].Source != source) {
                continue;
            }

            if (span[index].Current && span[index].Sense <= sense) {
                return;
            }

            span[index] = new(source, sense, where, strength, now, true);

            return;
        }

        if (targets.Count >= maximum && !Evict(now)) {
            return;
        }

        targets.Add(new(source, sense, where, strength, now, true));
    }

    /// <summary>Drops what has been remembered for too long.</summary>
    internal void Expire(float now, float memory) {
        for (var index = targets.Count - 1; index >= 0; index--) {
            if (!targets[index].Current && targets[index].AgeAt(now) > memory) {
                targets.RemoveAt(index);
            }
        }
    }

    /// <summary>Makes room by dropping the stalest remembered target.</summary>
    /// <returns>Whether it found one to drop. A list of things all being perceived right now is full.</returns>
    bool Evict(float now) {
        var oldest = -1;
        var age = float.MinValue;

        for (var index = 0; index < targets.Count; index++) {
            if (targets[index].Current) {
                continue;
            }

            var candidate = targets[index].AgeAt(now);

            if (candidate <= age) {
                continue;
            }

            age = candidate;
            oldest = index;
        }

        if (oldest < 0) {
            return false;
        }

        targets.RemoveAt(oldest);

        return true;
    }
}
