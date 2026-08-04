// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay;

/// <summary>What a requirement is asking about.</summary>
public enum RequirementKind {
    /// <summary>A number the subject can be asked for — a level, a stat, a currency, a reputation.</summary>
    Value,

    /// <summary>The subject has a tag under a prefix.</summary>
    HasTag,

    /// <summary>The subject has nothing under a prefix.</summary>
    NotHasTag
}

/// <summary>How a <see cref="RequirementKind.Value" /> requirement compares.</summary>
public enum RequirementComparison {
    /// <summary>At least.</summary>
    AtLeast,

    /// <summary>At most.</summary>
    AtMost,

    /// <summary>Exactly. Compared with a tolerance, because the subject is a float.</summary>
    Exactly,

    /// <summary>Anything but.</summary>
    Not
}

/// <summary>One requirement, as a definition holds it: names rather than ids.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///     <see cref="ModifierDefinition" />.
/// </remarks>
[DataContract("Requirement")]
public sealed class RequirementDefinition {
    /// <summary>What it is asking about.</summary>
    public RequirementKind Kind { get; set; }

    /// <summary>
    ///     The name being asked about: a value's name for <see cref="RequirementKind.Value" />, a tag
    ///     prefix otherwise.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>How the number compares. Ignored by the tag kinds.</summary>
    public RequirementComparison Comparison { get; set; }

    /// <summary>What it compares against. Ignored by the tag kinds.</summary>
    public float Value { get; set; }
}

/// <summary>One requirement with its names resolved: the form both ends evaluate.</summary>
/// <remarks>
///     <b>The same struct on the client and on the realm, and that is the whole point of the type.</b>
///     Doc 28 § Requirements: evaluated on the client for the UI and on the realm for the truth, out
///     of one assembly, which is what makes a greyed-out button and a rejected request agree. Two
///     implementations of "can I do this" is how a player learns to spam a button that says no.
/// </remarks>
/// <param name="Kind">What it asks about.</param>
/// <param name="Subject">Which value, for <see cref="RequirementKind.Value" />.</param>
/// <param name="Range">Which tag prefix, for the tag kinds.</param>
/// <param name="Comparison">How the number compares.</param>
/// <param name="Value">What it compares against.</param>
public readonly record struct Requirement(
    RequirementKind Kind,
    AttributeId Subject,
    GameplayTagRange Range,
    RequirementComparison Comparison,
    float Value
) {
    /// <summary>How close two floats have to be to count as equal.</summary>
    /// <remarks>
    ///     A requirement's numbers are levels, stack counts and currency amounts — small integers held
    ///     in floats — so a thousandth is far below anything a designer means and far above anything
    ///     the arithmetic can lose.
    /// </remarks>
    public const float Tolerance = 1e-3f;

    /// <summary>Resolves an authored requirement.</summary>
    /// <param name="definition">What was authored.</param>
    /// <param name="tags">The table its tag names are numbered against.</param>
    /// <returns>The requirement.</returns>
    public static Requirement Compile(RequirementDefinition definition, GameplayTagTable tags) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(tags);

        return definition.Kind == RequirementKind.Value
            ? new(
                RequirementKind.Value,
                AttributeId.From(definition.Subject),
                GameplayTagRange.Empty,
                definition.Comparison,
                definition.Value
            )
            : new(
                definition.Kind,
                AttributeId.None,
                tags.RangeOf(definition.Subject),
                definition.Comparison,
                definition.Value
            );
    }

    /// <summary>Whether a subject satisfies it.</summary>
    /// <param name="context">What is being asked.</param>
    /// <returns>Whether it is met.</returns>
    /// <remarks>
    ///     ⚠ <b>A value the context does not have counts as zero, not as a pass.</b> A requirement
    ///     about a currency an archetype does not track has to fail, because the alternative is a
    ///     vendor that sells to anyone whose account is missing a column.
    /// </remarks>
    public bool IsMetBy(IRequirementContext context) {
        ArgumentNullException.ThrowIfNull(context);

        switch (Kind) {
            case RequirementKind.HasTag:
                return context.Tags?.ContainsAny(Range) == true;

            case RequirementKind.NotHasTag:
                return context.Tags?.ContainsAny(Range) != true;

            case RequirementKind.Value:
                var actual = context.TryGetValue(Subject, out var found) ? found : 0f;

                return Comparison switch {
                    RequirementComparison.AtLeast => actual >= Value,
                    RequirementComparison.AtMost => actual <= Value,
                    RequirementComparison.Exactly => MathF.Abs(actual - Value) <= Tolerance,
                    RequirementComparison.Not => MathF.Abs(actual - Value) > Tolerance,
                    _ => false
                };

            default:
                return false;
        }
    }
}

/// <summary>What a requirement is evaluated against.</summary>
/// <remarks>
///     Two questions and no more: <em>what is this number</em> and <em>what tags do you have</em>.
///     Everything doc 28 requires of a requirement — a level, a profession, a combat state, a
///     currency, a reputation, a lockout — is one of those, and an interface that grew a third would
///     be an interface a game has to implement before it can ask whether a door is openable.
/// </remarks>
public interface IRequirementContext {
    /// <summary>The subject's tags, or null when it has none.</summary>
    GameplayTagSet? Tags { get; }

    /// <summary>A number the subject can be asked for.</summary>
    /// <param name="subject">Which number.</param>
    /// <param name="value">It, or zero.</param>
    /// <returns>Whether the subject has such a number at all.</returns>
    bool TryGetValue(AttributeId subject, out float value);
}

/// <summary>A compiled list of requirements, evaluated as a conjunction.</summary>
/// <remarks>
///     <b>And, always.</b> A requirement list that could mean "or" would need a grouping syntax, and
///     doc 28's answer to "I need a disjunction" is the same as its answer everywhere else: that is a
///     <see cref="GameplayTagQuery" />, whose <c>any</c> list is exactly one.
/// </remarks>
public sealed class RequirementSet {
    readonly Requirement[] requirements;

    RequirementSet(Requirement[] requirements) => this.requirements = requirements;

    /// <summary>The set nothing fails.</summary>
    public static RequirementSet Always { get; } = new([]);

    /// <summary>How many requirements it holds.</summary>
    public int Count => requirements.Length;

    /// <summary>Them, in the order they were authored — which is the order they are reported in.</summary>
    public ReadOnlySpan<Requirement> Requirements => requirements;

    /// <summary>Resolves an authored list.</summary>
    /// <param name="definitions">What was authored, or null for none.</param>
    /// <param name="tags">The table tag names are numbered against.</param>
    /// <returns>The set.</returns>
    public static RequirementSet Compile(IReadOnlyList<RequirementDefinition>? definitions, GameplayTagTable tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (definitions is null || definitions.Count == 0) {
            return Always;
        }

        var compiled = new Requirement[definitions.Count];

        for (var index = 0; index < compiled.Length; index++) {
            compiled[index] = Requirement.Compile(definitions[index], tags);
        }

        return new(compiled);
    }

    /// <summary>Whether a subject satisfies every requirement.</summary>
    /// <param name="context">What is being asked.</param>
    /// <returns>Whether they are all met.</returns>
    public bool IsMetBy(IRequirementContext context) => !TryFindUnmet(context, out _);

    /// <summary>The first requirement a subject fails.</summary>
    /// <param name="context">What is being asked.</param>
    /// <param name="unmet">The failing requirement, or the default.</param>
    /// <returns>Whether anything failed.</returns>
    /// <remarks>
    ///     <b>What a tooltip is written from.</b> "Requires level 80" is a better answer than a
    ///     greyed-out button, and it is the same evaluation the realm runs, so the two cannot say
    ///     different things.
    /// </remarks>
    public bool TryFindUnmet(IRequirementContext context, out Requirement unmet) {
        foreach (ref readonly var requirement in requirements.AsSpan()) {
            if (!requirement.IsMetBy(context)) {
                unmet = requirement;

                return true;
            }
        }

        unmet = default;

        return false;
    }
}
