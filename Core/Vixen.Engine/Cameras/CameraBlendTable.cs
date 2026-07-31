// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Engine.Cameras;

/// <summary>Which blend to use between two named shots, when the default will not do.</summary>
/// <remarks>
///     <para>
///         Cinemachine's custom blend asset, as a runtime table. A game usually wants one default and
///         three exceptions — "cut into the death camera, never blend", "take four seconds coming out
///         of the map" — and writing those as a rule per pair is both smaller than a state machine
///         and easier to reason about than one.
///     </para>
///     <para>
///         <b>The most specific rule wins</b>, and specificity is counted rather than ordered:
///         a rule naming both shots beats one naming either, which beats the director's default. An
///         order-dependent table is a table whose behaviour changes when somebody sorts it.
///     </para>
/// </remarks>
public sealed class CameraBlendTable {
    readonly Dictionary<(Entity From, Entity To), CameraBlend> rules = [];

    /// <summary>How many rules there are.</summary>
    public int Count => rules.Count;

    /// <summary>Adds or replaces a rule.</summary>
    /// <param name="from">The outgoing shot, or <see cref="Entity.Null" /> for any.</param>
    /// <param name="to">The incoming shot, or <see cref="Entity.Null" /> for any.</param>
    /// <param name="blend">The blend to use.</param>
    /// <returns>This table, for chaining.</returns>
    public CameraBlendTable Add(Entity from, Entity to, CameraBlend blend) {
        rules[(from, to)] = blend;
        return this;
    }

    /// <summary>Removes every rule.</summary>
    public void Clear() => rules.Clear();

    /// <summary>The blend for a transition.</summary>
    /// <param name="from">The outgoing shot.</param>
    /// <param name="to">The incoming shot.</param>
    /// <param name="fallback">What to use when no rule matches.</param>
    /// <returns>The blend.</returns>
    public CameraBlend Resolve(Entity from, Entity to, CameraBlend fallback) {
        if (rules.TryGetValue((from, to), out var exact)) {
            return exact;
        }

        if (rules.TryGetValue((from, Entity.Null), out var leaving)) {
            return leaving;
        }

        return rules.TryGetValue((Entity.Null, to), out var arriving) ? arriving : fallback;
    }
}
