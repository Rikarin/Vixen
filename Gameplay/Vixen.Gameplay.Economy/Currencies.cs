// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Economy;

/// <summary>Whether a currency is one character's or the whole account's.</summary>
public enum CurrencyScope {
    /// <summary>Each character has their own.</summary>
    Character,

    /// <summary>One pile for everybody on the account.</summary>
    Account
}

/// <summary>What one currency turns into, and at what rate.</summary>
[DataContract("CurrencyConversion")]
public sealed class CurrencyConversionDefinition {
    /// <summary>The address of what it becomes.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>How many of this make one of that.</summary>
    public int Rate { get; set; } = 100;

    /// <summary>Whether it can be turned back.</summary>
    public bool OneWay { get; set; } = true;
}

/// <summary>A currency: gold, tokens, marks, karma — all one type.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///     <see cref="ModifierDefinition" />.
/// </remarks>
[DataContract("CurrencyDefinition")]
public sealed record CurrencyDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What holding any of it is — <c>Currency.Gold</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The most anybody may hold. Zero for no cap.</summary>
    public long Cap { get; set; }

    /// <summary>Whose pile it is.</summary>
    public CurrencyScope Scope { get; set; }

    /// <summary>How much of it evaporates per day, as a fraction. Zero for none.</summary>
    public float DecayPerDay { get; set; }

    /// <summary>What it turns into.</summary>
    public List<CurrencyConversionDefinition> Conversions { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }
    }
}

/// <summary>A conversion with its address resolved.</summary>
/// <param name="To">What it becomes.</param>
/// <param name="Address">Its address, kept so a report can name it.</param>
/// <param name="Rate">How many of the source make one of it.</param>
/// <param name="OneWay">Whether it can be turned back.</param>
public readonly record struct CurrencyConversion(DefId To, string Address, int Rate, bool OneWay);

/// <summary>What a conversion did.</summary>
/// <param name="Converted">How much of the source was spent.</param>
/// <param name="Produced">How much of the target came out.</param>
/// <param name="Remainder">What was left behind because it did not divide.</param>
public readonly record struct CurrencyExchange(long Converted, long Produced, long Remainder);

/// <summary>A currency with its names resolved.</summary>
public sealed class Currency {
    readonly CurrencyConversion[] conversions;

    internal Currency(CurrencyDefinition definition, GameplayTag tag, CurrencyConversion[] conversions) {
        Definition = definition;
        Tag = tag;
        this.conversions = conversions;
    }

    /// <summary>What it was compiled from.</summary>
    public CurrencyDefinition Definition { get; }

    /// <summary>Its id, which is also its ledger asset.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What holding any of it is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>The most anybody may hold, or zero.</summary>
    public long Cap => Math.Max(0, Definition.Cap);

    /// <summary>Whose pile it is.</summary>
    public CurrencyScope Scope => Definition.Scope;

    /// <summary>How much evaporates per day.</summary>
    public float DecayPerDay => Math.Clamp(Definition.DecayPerDay, 0f, 1f);

    /// <summary>What it turns into.</summary>
    public ReadOnlySpan<CurrencyConversion> Conversions => conversions;

    /// <summary>How much of a gain actually fits.</summary>
    /// <param name="held">What they have.</param>
    /// <param name="gain">What they are being given.</param>
    /// <returns>What fits, and what does not.</returns>
    /// <remarks>
    ///     ⚠ <b>The overflow is reported rather than dropped.</b> Only the caller knows whether the
    ///     right answer is to mail it, refuse the whole reward or convert it — the same decision
    ///     <c>Container.Add</c> refuses to make, and for the same reason.
    /// </remarks>
    public (long Fits, long Overflow) Fit(long held, long gain) {
        if (Cap <= 0 || gain <= 0) {
            return (gain, 0);
        }

        var room = Math.Max(0, Cap - held);

        return (Math.Min(room, gain), Math.Max(0, gain - room));
    }

    /// <summary>What converting some of it would do.</summary>
    /// <param name="amount">How much is being offered.</param>
    /// <param name="to">What it is being turned into.</param>
    /// <returns>What would happen, all zeroes when there is no such conversion.</returns>
    /// <remarks>
    ///     ⚠ <b>Integer, and the remainder stays put.</b> A hundred silver to a gold converting two
    ///     hundred and fifty silver yields two gold and leaves fifty — it does not round, and it does
    ///     not quietly keep the change.
    /// </remarks>
    public CurrencyExchange Convert(long amount, DefId to) {
        foreach (ref readonly var conversion in conversions.AsSpan()) {
            if (conversion.To != to || conversion.Rate <= 0 || amount <= 0) {
                continue;
            }

            var produced = amount / conversion.Rate;

            return new(produced * conversion.Rate, produced, amount - (produced * conversion.Rate));
        }

        return default;
    }

    /// <summary>How much is left after some days of decay.</summary>
    /// <param name="held">What they have.</param>
    /// <param name="days">How long since it was last applied.</param>
    /// <returns>What is left.</returns>
    /// <remarks>
    ///     ⚠ <b>Rounded down, so decay can reach zero.</b> Rounding to nearest leaves everybody with a
    ///     single coin for ever, and a currency that never quite disappears is not a sink.
    /// </remarks>
    public long Decay(long held, float days) {
        if (DecayPerDay <= 0f || days <= 0f || held <= 0) {
            return held;
        }

        var kept = held * MathF.Pow(1f - DecayPerDay, days);

        return (long)MathF.Floor(kept);
    }
}
