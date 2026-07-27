// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Net.Time;

/// <summary>How often the simulation ticks, and therefore what one <see cref="Tick" /> is worth.</summary>
/// <remarks>
///     <para>
///         The network tick is a divisor of the engine's fixed step rather than a clock of its own —
///         networking does not get its own loop. Thirty a second is the default, against the engine's
///         sixty: a snapshot every other simulation step is the usual shape, and doubling the send
///         rate for no gameplay reason is the most common way to double a bandwidth bill.
///     </para>
///     <para>
///         The duration is computed in <see cref="TimeSpan" /> ticks rather than from seconds, for
///         the reason the engine's accumulator gives: <c>TimeSpan.FromSeconds(1d / 30d)</c> rounds to
///         the nearest millisecond and hands back 33 ms, which is a 1 % error that shows up as a
///         missing tick in every frame whose length is a whole number of milliseconds.
///     </para>
/// </remarks>
/// <param name="TicksPerSecond">How many ticks a second. Between 1 and 1000.</param>
public readonly record struct TickRate(int TicksPerSecond) {
    /// <summary>Thirty a second — one tick per two engine steps at the default fixed step.</summary>
    public static TickRate Default => new(30);

    /// <summary>How many ticks a second. Between 1 and 1000.</summary>
    public int TicksPerSecond { get; } = Validate(TicksPerSecond);

    /// <summary>How long one tick lasts.</summary>
    /// <exception cref="InvalidOperationException">The rate was default-constructed and is zero.</exception>
    public TimeSpan Duration {
        get {
            if (TicksPerSecond == 0) {
                throw new InvalidOperationException(
                    "A default-constructed TickRate has no rate. Construct it with a number of ticks per second."
                );
            }

            return TimeSpan.FromTicks(TimeSpan.TicksPerSecond / TicksPerSecond);
        }
    }

    /// <summary>Whether this rate was constructed rather than defaulted.</summary>
    public bool IsValid => TicksPerSecond > 0;

    /// <summary>How many whole ticks fit in a span of time.</summary>
    /// <param name="time">The span.</param>
    /// <returns>The number of whole ticks, rounded to nearest.</returns>
    public int ToTicks(TimeSpan time) => (int)Math.Round(time.Ticks / (double)Duration.Ticks);

    /// <summary>How long a number of ticks lasts.</summary>
    /// <param name="ticks">The number of ticks.</param>
    /// <returns>The span they occupy.</returns>
    public TimeSpan ToTime(int ticks) => Duration * ticks;

    /// <inheritdoc />
    /// <remarks>
    ///     Written by hand rather than left to the record, and that is not cosmetic. The generated
    ///     <c>ToString</c> prints every property, <see cref="Duration" /> throws on a
    ///     default-constructed rate, and the two together mean that the exception complaining about
    ///     an invalid rate cannot render its own message — the failure reports as the wrong exception
    ///     from the wrong place. A property that can throw does not belong in a generated
    ///     <c>ToString</c>.
    /// </remarks>
    public override string ToString() =>
        IsValid ? string.Create(CultureInfo.InvariantCulture, $"{TicksPerSecond}/s") : "no rate";

    static int Validate(int ticksPerSecond) {
        // Zero is allowed through only because a struct can always be default-constructed and
        // pretending otherwise would be a lie; Duration is where that gets caught.
        if (ticksPerSecond is < 0 or > 1000) {
            throw new ArgumentOutOfRangeException(
                nameof(ticksPerSecond),
                ticksPerSecond,
                "A tick rate is between 1 and 1000 ticks per second."
            );
        }

        return ticksPerSecond;
    }
}
