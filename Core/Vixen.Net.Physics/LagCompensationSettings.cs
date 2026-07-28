// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Physics;

/// <summary>How far back the server is willing to look, and how far it is willing to be told to.</summary>
/// <remarks>
///     <para>
///         <b>Every number here is a fairness decision, not a performance one.</b> Lag compensation
///         is the server agreeing to judge a shot by what the shooter saw rather than by what was
///         true when the packet arrived, and that agreement always costs somebody something: the
///         player who was shot has already moved, and is killed from behind cover they believe they
///         reached. Every value below is a point on that trade, which is why they are settings with
///         reasons rather than constants.
///     </para>
///     <para>
///         The defaults are the conventional ones for a 30 Hz shooter and are deliberately not
///         generous. A larger window buys fairness for high-latency shooters and sells it from
///         everybody they shoot at.
///     </para>
/// </remarks>
public sealed record LagCompensationSettings {
    /// <summary>The furthest back a rewind will ever go, whatever anybody claims or measures.</summary>
    /// <remarks>
    ///     <para>
    ///         A quarter of a second. Past this the "behind cover" complaint stops being an edge case
    ///         and becomes the normal experience of being shot, and no hit-registration improvement
    ///         is worth that. It is also the number a player with a genuinely terrible connection
    ///         runs into, and the honest answer for them is that some of their shots will not land.
    ///     </para>
    ///     <para>
    ///         Independent of <see cref="HistoryTicks" /> on purpose. The history may be longer, for
    ///         diagnostics or for a game that wants to raise this; a rewind never uses more of it
    ///         than this allows.
    ///     </para>
    /// </remarks>
    public TimeSpan MaxRewind { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How many ticks of pose history to keep per tracked body.</summary>
    /// <remarks>
    ///     Sized to cover <see cref="MaxRewind" /> at the tick rate with room to spare, because the
    ///     ring is also what a rewind interpolates <i>within</i> — a target at the very oldest entry
    ///     has nothing older to interpolate from and snaps instead.
    /// </remarks>
    public int HistoryTicks { get; init; } = 32;

    /// <summary>
    ///     Extra slack added to a player's measured round trip, covering the interpolation delay
    ///     they were rendering at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Without this the compensation is systematically one buffer too shallow.</b> A
    ///         client does not render the newest snapshot it holds — it renders behind it, by a
    ///         margin sized from jitter, so that the next one has arrived before it is needed
    ///         (<c>TickManager.InterpolationDelayTicks</c>). The moment the shooter saw is therefore
    ///         half a round trip <i>plus</i> that delay ago, and a server that allows only the round
    ///         trip rewinds to a world the shooter had not seen yet.
    ///     </para>
    ///     <para>
    ///         A constant rather than the client's own figure, because the client's figure is a
    ///         number the client chooses. Two ticks at 30 Hz is 66 ms, which covers the default
    ///         interpolation delay on any connection this is worth compensating for.
    ///     </para>
    /// </remarks>
    public int InterpolationSlackTicks { get; init; } = 2;

    /// <summary>Whether to interpolate between the two captures either side of the target.</summary>
    /// <remarks>
    ///     <para>
    ///         On, and worth the arithmetic. The moment a client saw falls <i>between</i> two server
    ///         ticks essentially always, and snapping to the nearer one puts every body up to half a
    ///         tick out of position — at 30 Hz and a 10 m/s strafe that is 17 cm, which is most of a
    ///         torso. It is the difference between compensation that mostly works and compensation
    ///         players describe as broken.
    ///     </para>
    ///     <para>
    ///         Off is for a game that wants rewind to land on exactly the poses it captured, which
    ///         is easier to reason about in a test and in a replay.
    ///     </para>
    /// </remarks>
    public bool Interpolate { get; init; } = true;
}
