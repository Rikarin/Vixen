// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Matchmaking;

/// <summary>How good somebody is thought to be, and how sure anybody is of it.</summary>
/// <param name="Mean">The estimate.</param>
/// <param name="Deviation">How uncertain it is. Zero for a model that does not track uncertainty.</param>
/// <remarks>
///     ⚠ <b>Two numbers even for Elo, which only uses one.</b> A rating type per model would put the
///     choice of model into every signature that carries a rating — a ticket, a pool filter, a match
///     proposal — and doc 28 is explicit that <em>"the framework does not pick; the queue definition
///     does"</em>. Elo leaves <see cref="Deviation" /> at zero and nothing else has to care.
/// </remarks>
public readonly record struct Rating(double Mean, double Deviation = 0d) {
    /// <summary>What a rating is worth when somebody has to be matched on one number.</summary>
    /// <remarks>
    ///     Three deviations below the mean, which is TrueSkill's convention: a new player with a wide
    ///     deviation is treated conservatively rather than optimistically, so they are not matched
    ///     against experts on the strength of a guess.
    /// </remarks>
    public double Conservative => Mean - (3d * Deviation);

    /// <inheritdoc />
    public override string ToString() =>
        Deviation > 0d ? $"{Mean:0.###}±{Deviation:0.###}" : $"{Mean:0.###}";
}

/// <summary>How ratings change and how well two sides are matched.</summary>
/// <remarks>
///     <b>Doc 28 ships two and picks neither.</b> Elo is one number, transparent, and right for 1v1 and
///     for games whose players will ask how it works. A Bayesian model of the TrueSkill family carries
///     a variance, which is what handles teams, parties, uneven sizes and new players honestly. The
///     queue definition chooses.
/// </remarks>
public interface IRatingModel {
    /// <summary>What it is called, in a queue definition and in a report.</summary>
    string Name { get; }

    /// <summary>What somebody starts on.</summary>
    Rating Starting { get; }

    /// <summary>Updates every team's ratings after a result.</summary>
    /// <param name="teams">Each team's players' ratings.</param>
    /// <param name="ranks">Each team's finishing place, lowest first. Equal numbers are a draw.</param>
    /// <returns>The new ratings, in the same shape.</returns>
    IReadOnlyList<Rating[]> Update(IReadOnlyList<Rating[]> teams, IReadOnlyList<int> ranks);

    /// <summary>How even a match would be, from zero to one.</summary>
    /// <param name="teams">Each team's players' ratings.</param>
    /// <returns>One for a coin flip, towards zero for a foregone conclusion.</returns>
    double Quality(IReadOnlyList<Rating[]> teams);
}

/// <summary>The normal distribution, to the accuracy a rating needs.</summary>
static class Gaussian {
    const double Sqrt2Pi = 2.5066282746310002d;

    /// <summary>The density at a point.</summary>
    internal static double Density(double x) => Math.Exp(-0.5d * x * x) / Sqrt2Pi;

    /// <summary>The cumulative probability up to a point.</summary>
    /// <remarks>
    ///     Cody's rational approximation of the error function, which is good to about fifteen digits
    ///     — well past what a rating needs, and the reason the TrueSkill reference figures come out
    ///     right rather than nearly right.
    /// </remarks>
    internal static double Cumulative(double x) => 0.5d * Erfc(-x / Math.Sqrt(2d));

    /// <summary>The point below which a probability lies.</summary>
    /// <remarks>Acklam's rational approximation, refined by one Halley step.</remarks>
    internal static double Quantile(double p) {
        if (p is <= 0d or >= 1d) {
            return p <= 0d ? double.NegativeInfinity : double.PositiveInfinity;
        }

        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];

        const double Low = 0.02425d;
        double x;

        if (p < Low) {
            var q = Math.Sqrt(-2d * Math.Log(p));

            x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1d);
        } else if (p <= 1d - Low) {
            var q = p - 0.5d;
            var r = q * q;

            x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q
                / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1d);
        } else {
            var q = Math.Sqrt(-2d * Math.Log(1d - p));

            x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1d);
        }

        var error = Cumulative(x) - p;
        var step = error * Sqrt2Pi * Math.Exp(x * x / 2d);

        return x - (step / (1d + (x * step / 2d)));
    }

    static double Erfc(double x) {
        var z = Math.Abs(x);
        var t = 2d / (2d + z);
        var y = (4d * t) - 2d;

        double[] coefficients = [
            -1.3026537197817094e+00, 6.4196979235649026e-01, 1.9476473204185836e-02, -9.561514786808631e-03,
            -9.46595344482036e-04, 3.66839497852761e-04, 4.2523324806907e-05, -2.0278578112534e-05,
            -1.624290004647e-06, 1.303655835580e-06, 1.5626441722e-08, -8.5238095915e-08,
            6.529054439e-09, 5.059343495e-09, -9.91364156e-10, -2.27365122e-10,
            9.6467911e-11, 2.394038e-12, -6.886027e-12, 8.94487e-13,
            3.13092e-13, -1.12708e-13, 3.81e-16, 7.106e-15
        ];

        var d = 0d;
        var dd = 0d;

        for (var index = coefficients.Length - 1; index > 0; index--) {
            var tmp = d;

            d = (y * d) - dd + coefficients[index];
            dd = tmp;
        }

        var answer = t * Math.Exp((-z * z) + (0.5d * (coefficients[0] + (y * d))) - dd);

        return x >= 0d ? answer : 2d - answer;
    }
}

/// <summary>Elo: one number, and everybody knows how it works.</summary>
/// <remarks>
///     <para>
///         <b>Right for 1v1, and right for any game whose players will ask.</b> Doc 28 names
///         transparency as the reason it ships alongside a better model rather than being replaced by
///         one — a rating a player can compute themselves is a rating they will argue with rather than
///         about.
///     </para>
///     <para>
///         ⚠ <b>A team's rating is the mean of its players', not the sum.</b> A sum makes a five-player
///         team five times as strong as one player and matches it against nobody; the mean is what the
///         expectation formula's four-hundred-point scale is calibrated for.
///     </para>
/// </remarks>
public sealed class EloRatingModel : IRatingModel {
    /// <summary>Makes one.</summary>
    /// <param name="k">How far one result may move a rating. Thirty-two is the classical figure.</param>
    /// <param name="start">What somebody starts on.</param>
    public EloRatingModel(double k = 32d, double start = 1500d) {
        K = k;
        Starting = new(start);
    }

    /// <summary>How far one result may move a rating.</summary>
    public double K { get; }

    /// <inheritdoc />
    public string Name => "elo";

    /// <inheritdoc />
    public Rating Starting { get; }

    /// <summary>What one rating is expected to score against another, from zero to one.</summary>
    /// <param name="rating">Theirs.</param>
    /// <param name="opponent">The other.</param>
    /// <returns>The expectation.</returns>
    public static double Expected(double rating, double opponent) =>
        1d / (1d + Math.Pow(10d, (opponent - rating) / 400d));

    /// <summary>What a rating becomes after a set of games treated as one period.</summary>
    /// <param name="rating">Theirs before.</param>
    /// <param name="opponents">Who they played.</param>
    /// <param name="score">What they actually scored — a win is one, a draw a half.</param>
    /// <returns>Their rating after.</returns>
    /// <remarks>
    ///     ⚠ <b>A period, not a sequence.</b> Applying each game in turn moves the rating between them
    ///     and gives a different answer, which is why every published Elo worked example — including
    ///     the one the tests pin — is stated as a period.
    /// </remarks>
    public double AfterPeriod(double rating, IReadOnlyList<double> opponents, double score) {
        ArgumentNullException.ThrowIfNull(opponents);

        var expected = 0d;

        foreach (var opponent in opponents) {
            expected += Expected(rating, opponent);
        }

        return rating + (K * (score - expected));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">There are not exactly two teams.</exception>
    public IReadOnlyList<Rating[]> Update(IReadOnlyList<Rating[]> teams, IReadOnlyList<int> ranks) {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(ranks);

        if (teams.Count != 2 || ranks.Count != 2) {
            throw new ArgumentException(
                "Elo is a two-sided formula. A free-for-all wants the Bayesian model, which is why "
                + "both ship.",
                nameof(teams)
            );
        }

        var left = Mean(teams[0]);
        var right = Mean(teams[1]);
        var score = ranks[0] == ranks[1] ? 0.5d : ranks[0] < ranks[1] ? 1d : 0d;
        var expected = Expected(left, right);
        var change = K * (score - expected);

        return [Shift(teams[0], change), Shift(teams[1], -change)];
    }

    /// <inheritdoc />
    public double Quality(IReadOnlyList<Rating[]> teams) {
        ArgumentNullException.ThrowIfNull(teams);

        if (teams.Count != 2) {
            return 0d;
        }

        // One at an even match, falling away as the expectation leaves a half.
        return 1d - (2d * Math.Abs(Expected(Mean(teams[0]), Mean(teams[1])) - 0.5d));
    }

    static double Mean(Rating[] team) {
        if (team.Length == 0) {
            return 0d;
        }

        var total = 0d;

        foreach (var rating in team) {
            total += rating.Mean;
        }

        return total / team.Length;
    }

    static Rating[] Shift(Rating[] team, double change) {
        var after = new Rating[team.Length];

        for (var index = 0; index < team.Length; index++) {
            after[index] = team[index] with { Mean = team[index].Mean + change };
        }

        return after;
    }
}

/// <summary>A Bayesian skill model of the TrueSkill family: a mean and a variance per player.</summary>
/// <remarks>
///     <para>
///         <b>What handles teams, parties, uneven sizes and new players honestly.</b> A team's
///         performance is the sum of its players' skills with a per-player performance variance, and
///         a result moves each player in proportion to how uncertain they were — so a newcomer's
///         rating converges in a handful of games while a veteran's barely moves.
///     </para>
///     <para>
///         ⚠ <b>Two teams, and a free-for-all is not supported.</b> The full model handles any number
///         of teams with a factor graph over the whole ranking; two teams is the closed form, it is
///         what every queue in doc 28 needs, and shipping the closed form honestly is better than
///         shipping an approximation of the general one that nobody can check. A queue with three
///         sides is a real gap and the README says so.
///     </para>
///     <para>
///         ⚠ <b>A little uncertainty is added back before every update.</b> Without it a rating
///         converges to a variance of nearly nothing and then cannot move again, which means somebody
///         who improves is stuck at what they used to be for ever.
///     </para>
/// </remarks>
public sealed class BayesianRatingModel : IRatingModel {
    /// <summary>Makes one with the classical parameters.</summary>
    /// <param name="mean">What somebody starts on.</param>
    /// <param name="deviation">How unsure that is. A third of the mean is the convention.</param>
    /// <param name="beta">How much a single performance varies from a player's skill.</param>
    /// <param name="tau">How much uncertainty is added back each game.</param>
    /// <param name="drawProbability">How often two even sides draw.</param>
    public BayesianRatingModel(
        double mean = 25d,
        double deviation = 25d / 3d,
        double beta = 25d / 6d,
        double tau = 25d / 300d,
        double drawProbability = 0.1d
    ) {
        Starting = new(mean, deviation);
        Beta = beta;
        Tau = tau;
        DrawProbability = Math.Clamp(drawProbability, 0d, 0.99d);
    }

    /// <summary>How much a single performance varies from a player's skill.</summary>
    public double Beta { get; }

    /// <summary>How much uncertainty is added back each game.</summary>
    public double Tau { get; }

    /// <summary>How often two even sides draw.</summary>
    public double DrawProbability { get; }

    /// <inheritdoc />
    public string Name => "bayesian";

    /// <inheritdoc />
    public Rating Starting { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">There are not exactly two teams.</exception>
    public IReadOnlyList<Rating[]> Update(IReadOnlyList<Rating[]> teams, IReadOnlyList<int> ranks) {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(ranks);

        if (teams.Count != 2 || ranks.Count != 2) {
            throw new ArgumentException("This model is the two-team closed form. See the README.", nameof(teams));
        }

        var drawn = ranks[0] == ranks[1];
        var winner = ranks[0] <= ranks[1] ? 0 : 1;
        var loser = 1 - winner;

        var (winnerMean, winnerVariance) = Totals(teams[winner]);
        var (loserMean, loserVariance) = Totals(teams[loser]);

        var players = teams[0].Length + teams[1].Length;
        var c = Math.Sqrt(winnerVariance + loserVariance + (players * Beta * Beta));
        var epsilon = Margin(players);
        var t = (winnerMean - loserMean) / c;
        var alpha = epsilon / c;

        // One rule for both outcomes: the first-ranked side moves up by v and the other down by it.
        // A draw between uneven sides still moves them — DrawV is signed by t, so the stronger side
        // loses a little and the weaker gains, which is what a draw means.
        var v = drawn ? DrawV(t, alpha) : WinV(t, alpha);
        var w = drawn ? DrawW(t, alpha) : WinW(t, alpha);

        var after = new Rating[2][];

        after[winner] = Shift(teams[winner], c, v, w, 1d);
        after[loser] = Shift(teams[loser], c, v, w, -1d);

        return after;
    }

    /// <inheritdoc />
    public double Quality(IReadOnlyList<Rating[]> teams) {
        ArgumentNullException.ThrowIfNull(teams);

        if (teams.Count != 2) {
            return 0d;
        }

        var (leftMean, leftVariance) = Totals(teams[0]);
        var (rightMean, rightVariance) = Totals(teams[1]);
        var players = teams[0].Length + teams[1].Length;
        var beta = players * Beta * Beta;
        var total = beta + leftVariance + rightVariance;

        if (total <= 0d) {
            return 0d;
        }

        var difference = leftMean - rightMean;

        return Math.Sqrt(beta / total) * Math.Exp(-difference * difference / (2d * total));
    }

    double Margin(int players) =>
        Gaussian.Quantile((DrawProbability + 1d) / 2d) * Math.Sqrt(players) * Beta;

    (double Mean, double Variance) Totals(Rating[] team) {
        var mean = 0d;
        var variance = 0d;

        foreach (var rating in team) {
            mean += rating.Mean;

            // The uncertainty a game adds back, applied before it is used rather than after — a
            // rating that had converged to nothing could otherwise never move again.
            variance += (rating.Deviation * rating.Deviation) + (Tau * Tau);
        }

        return (mean, variance);
    }

    Rating[] Shift(Rating[] team, double c, double v, double w, double direction) {
        var after = new Rating[team.Length];

        for (var index = 0; index < team.Length; index++) {
            var variance = (team[index].Deviation * team[index].Deviation) + (Tau * Tau);

            after[index] = new(
                team[index].Mean + (direction * (variance / c) * v),
                Math.Sqrt(Math.Max(1e-9d, variance * (1d - (variance / (c * c) * w))))
            );
        }

        return after;
    }

    static double WinV(double t, double alpha) {
        var denominator = Gaussian.Cumulative(t - alpha);

        return denominator < 1e-12d ? alpha - t : Gaussian.Density(t - alpha) / denominator;
    }

    static double WinW(double t, double alpha) {
        var v = WinV(t, alpha);

        return v * (v + t - alpha);
    }

    static double DrawV(double t, double alpha) {
        var absolute = Math.Abs(t);
        var denominator = Gaussian.Cumulative(alpha - absolute) - Gaussian.Cumulative(-alpha - absolute);

        if (denominator < 1e-12d) {
            return t < 0d ? -alpha - t : alpha - t;
        }

        var numerator = Gaussian.Density(-alpha - absolute) - Gaussian.Density(alpha - absolute);

        return (t < 0d ? -numerator : numerator) / denominator;
    }

    static double DrawW(double t, double alpha) {
        var absolute = Math.Abs(t);
        var denominator = Gaussian.Cumulative(alpha - absolute) - Gaussian.Cumulative(-alpha - absolute);

        if (denominator < 1e-12d) {
            return 1d;
        }

        var v = DrawV(t, alpha);

        return (v * v)
            + (((alpha - absolute) * Gaussian.Density(alpha - absolute)
                    + ((alpha + absolute) * Gaussian.Density(alpha + absolute)))
                / denominator);
    }
}
