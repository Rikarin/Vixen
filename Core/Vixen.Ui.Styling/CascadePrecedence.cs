// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling;

/// <summary>Everything about a declaration that decides whether it wins, in comparable form.</summary>
/// <param name="OriginRank">Origin and importance, folded into one rank.</param>
/// <param name="LayerRank">Where its layer sorts, in the direction its importance implies.</param>
/// <param name="Specificity">How specific the selector that brought it here is.</param>
/// <param name="Order">Its position in source order.</param>
/// <remarks>
///     <para>
///         The cascade is four nested tie-breaks, and writing them as four nested <c>if</c>s is how
///         a subtle one gets left out. As one comparable key it is a <c>&gt;</c>, and every rule
///         about who beats whom lives in the one place that builds it.
///     </para>
///     <para>
///         Read in order: <b>origin and importance</b>, then <b>layer</b>, then <b>specificity</b>,
///         then <b>source order</b>. Specificity is third, not first — which is the thing most people
///         who write CSS have backwards, and the reason a utility layer works.
///     </para>
/// </remarks>
public readonly record struct CascadePrecedence(int OriginRank, int LayerRank, Specificity Specificity, int Order)
    : IComparable<CascadePrecedence> {
    /// <summary>The precedence nothing loses to.</summary>
    public static readonly CascadePrecedence None = new(int.MinValue, int.MinValue, default, int.MinValue);

    /// <inheritdoc />
    public int CompareTo(CascadePrecedence other) {
        var origin = OriginRank.CompareTo(other.OriginRank);
        if (origin != 0) {
            return origin;
        }

        var layer = LayerRank.CompareTo(other.LayerRank);
        if (layer != 0) {
            return layer;
        }

        var specificity = Specificity.CompareTo(other.Specificity);
        return specificity != 0 ? specificity : Order.CompareTo(other.Order);
    }

    /// <summary>Compares two precedences.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left loses.</returns>
    public static bool operator <(CascadePrecedence left, CascadePrecedence right) => left.CompareTo(right) < 0;

    /// <summary>Compares two precedences.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left wins.</returns>
    public static bool operator >(CascadePrecedence left, CascadePrecedence right) => left.CompareTo(right) > 0;

    /// <summary>Compares two precedences.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left does not win.</returns>
    public static bool operator <=(CascadePrecedence left, CascadePrecedence right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two precedences.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the left does not lose.</returns>
    public static bool operator >=(CascadePrecedence left, CascadePrecedence right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"origin {OriginRank}, layer {LayerRank}, {Specificity}, #{Order}");
}

/// <summary>Builds the precedence of a declaration.</summary>
/// <remarks>
///     <para>
///         The origin ranks below are CSS Cascading 5 §6.2 read straight down, and the shape worth
///         noticing is the <b>mirror</b>. Normal declarations run user-agent → user → author →
///         inline; important ones run inline → author → user → user-agent. It is not decoration:
///         a user stylesheet that marks a font size important is a player who needs it, and it has to
///         be able to beat a game that also marked one important. Importance is not "wins", it is
///         "reverse the order of who is trusted".
///     </para>
///     <para>
///         Layers mirror the same way, and unlayered styles sit at the strong end of whichever
///         direction is in force. Both reversals are covered by tests that would pass just as
///         happily if either were left out, which is why they are separate tests naming the
///         behaviour rather than one test asserting a winner.
///     </para>
///     <para>
///         Animations and transitions have their own tiers in the same table — above all normal
///         declarations and above all important ones respectively — and are not written yet. The
///         gaps between the numbers are where they go.
///     </para>
/// </remarks>
public static class CascadeRanks {
    /// <summary>Normal declarations from Vixen's own control styles.</summary>
    public const int NormalUserAgent = 10;

    /// <summary>Normal declarations from a player's overrides.</summary>
    public const int NormalUser = 20;

    /// <summary>Normal declarations from the game's stylesheets.</summary>
    public const int NormalAuthor = 30;

    /// <summary>Normal declarations written on the element itself.</summary>
    public const int NormalInline = 40;

    /// <summary>Where a running animation's declarations will sit.</summary>
    public const int Animation = 50;

    /// <summary><c>!important</c> declarations written on the element itself.</summary>
    public const int ImportantInline = 60;

    /// <summary><c>!important</c> declarations from the game's stylesheets.</summary>
    public const int ImportantAuthor = 70;

    /// <summary><c>!important</c> declarations from a player's overrides.</summary>
    public const int ImportantUser = 80;

    /// <summary><c>!important</c> declarations from Vixen's own control styles.</summary>
    public const int ImportantUserAgent = 90;

    /// <summary>Where a running transition's declarations will sit.</summary>
    public const int Transition = 100;

    /// <summary>The rank of a declaration from a stylesheet rule.</summary>
    /// <param name="origin">Who the rule came from.</param>
    /// <param name="important">Whether the declaration carried <c>!important</c>.</param>
    /// <returns>Its rank.</returns>
    public static int Of(StyleOrigin origin, bool important) => (origin, important) switch {
        (StyleOrigin.UserAgent, false) => NormalUserAgent,
        (StyleOrigin.User, false) => NormalUser,
        (StyleOrigin.Author, false) => NormalAuthor,
        (StyleOrigin.UserAgent, true) => ImportantUserAgent,
        (StyleOrigin.User, true) => ImportantUser,
        _ => ImportantAuthor
    };

    /// <summary>The rank of a declaration written on the element itself.</summary>
    /// <param name="important">Whether it carried <c>!important</c>.</param>
    /// <returns>Its rank.</returns>
    public static int OfInline(bool important) => important ? ImportantInline : NormalInline;
}
