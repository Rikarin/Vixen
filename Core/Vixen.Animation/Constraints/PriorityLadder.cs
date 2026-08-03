// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Vixen.Core;

namespace Vixen.Animation.Constraints;

/// <summary>One named rung of a project's priority ladder.</summary>
/// <param name="Name">What an author picks it by.</param>
/// <param name="Value">The integer arbitration actually uses.</param>
/// <param name="Meaning">What choosing it says, in a sentence somebody can read.</param>
[DataContract("PriorityRungRecord")]
public sealed record PriorityRungRecord(string Name, int Value, string Meaning) {
    /// <summary>A record with nothing filled in.</summary>
    public PriorityRungRecord() : this("", 0, "") {
    }
}

/// <summary>The names a project's authors pick priorities from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two reasons this is data rather than sugar, and neither is presentation.</b> A raw
///         integer has no meaning across a project, so two authors pick 70 and 700 for the same intent
///         and the arbitration between their clips is an accident. And the right ladder is
///         domain-specific — the ordering that makes sense for characters swimming is not the one for
///         characters driving — so it is a file with aliases rather than an enum in the engine, and one
///         project may name several and say which applies where.
///     </para>
///     <para>
///         <b>A sub-step within a rung</b> is what lets two contacts at the same level be ordered
///         without inventing a rung: <c>contact</c> and <c>contact+1</c> resolve to adjacent integers
///         and read as the same intent, which is what an author means when they say one grip matters
///         slightly more than another.
///     </para>
/// </remarks>
[DataContract("PriorityLadderContent")]
public sealed class PriorityLadderContent {
    /// <summary>The version this build writes.</summary>
    public const int Current = 1;

    /// <summary>The file extension.</summary>
    public const string Extension = ".vxpriorities";

    /// <summary>Which version of the format wrote it.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the ladder is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How far apart two adjacent rungs are, so a sub-step has room between them.</summary>
    public int Step { get; set; } = 100;

    /// <summary>The rungs, lowest first.</summary>
    public PriorityRungRecord[] Rungs { get; set; } = [];

    /// <summary>Markup this build did not interpret.</summary>
    public Dictionary<string, string> Extensions { get; set; } = [];

    /// <summary>Turns it into the lookup a bake uses.</summary>
    /// <returns>The ladder.</returns>
    public PriorityLadder Bake() => new(Name, Rungs, Step);

    /// <summary>The ladder every project gets before it declares one of its own.</summary>
    /// <remarks>
    ///     Runs from a flourish nobody would miss up to a contact that must not be violated at any
    ///     cost. Deliberately short: a ladder with fifteen rungs is one where nobody can say what the
    ///     difference between two adjacent ones is.
    /// </remarks>
    public static PriorityLadderContent Default => new() {
        Name = "default",
        Step = 100,
        Rungs = [
            new("flourish", 0, "A secondary motion. First thing to lose."),
            new("look", 100, "Where the character is looking."),
            new("aim", 200, "Where a weapon or a tool is pointing."),
            new("balance", 300, "Keeping the body over its feet."),
            new("interaction", 400, "A hand on the thing the character is using."),
            new("contact", 500, "A contact the eye reads as touching. Must not be violated.")
        ]
    };
}

/// <summary>A priority ladder, as a bake reads it.</summary>
public sealed class PriorityLadder {
    readonly FrozenDictionary<string, int> byName;

    /// <summary>Builds a ladder.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="rungs">Its rungs.</param>
    /// <param name="step">How far apart two adjacent rungs are.</param>
    public PriorityLadder(string name, IEnumerable<PriorityRungRecord> rungs, int step = 100) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rungs);

        Name = name;
        Step = Math.Max(step, 1);

        Dictionary<string, int> built = new(StringComparer.Ordinal);

        foreach (var rung in rungs) {
            built.TryAdd(rung.Name, rung.Value);
        }

        byName = built.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The one a project gets before it declares its own.</summary>
    public static PriorityLadder Default { get; } = PriorityLadderContent.Default.Bake();

    /// <summary>What the ladder is called.</summary>
    public string Name { get; }

    /// <summary>How far apart two adjacent rungs are.</summary>
    public int Step { get; }

    /// <summary>How many rungs it has.</summary>
    public int Count => byName.Count;

    /// <summary>The names, so an editor can offer them.</summary>
    /// <returns>The names.</returns>
    public IReadOnlyCollection<string> Names => byName.Keys;

    /// <summary>What a name is worth.</summary>
    /// <param name="name">
    ///     A rung, optionally with a sub-step — <c>contact+1</c>, <c>look-2</c>.
    /// </param>
    /// <returns>The integer, or zero when the name is not on this ladder.</returns>
    /// <remarks>
    ///     ⚠ <b>An unknown name is zero rather than an exception.</b> A clip marked up against one
    ///     project's ladder and opened in another is an ordinary thing to happen, and it should behave
    ///     like the lowest priority rather than refusing to load — the validation that says so belongs
    ///     at import, where somebody can read it.
    /// </remarks>
    public int Value(string? name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return 0;
        }

        if (byName.TryGetValue(name, out var exact)) {
            return exact;
        }

        var (rung, step) = Split(name);

        return rung is not null && byName.TryGetValue(rung, out var found) ? found + step : 0;
    }

    /// <summary>Whether the ladder declares a name, sub-step and all.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it does.</returns>
    public bool Declares(string? name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return true;
        }

        return byName.ContainsKey(name) || (Split(name).Rung is { } rung && byName.ContainsKey(rung));
    }

    /// <summary>Splits <c>contact+1</c> into its rung and its sub-step.</summary>
    /// <remarks>
    ///     ⚠ <b>The sub-step is clamped to less than a whole rung.</b> A <c>look+200</c> that quietly
    ///     outranked <c>aim</c> would make the ladder's order a lie, and the ladder's order is the only
    ///     thing it is for.
    /// </remarks>
    static (string? Rung, int Step) Split(string name) {
        var at = name.LastIndexOfAny(['+', '-']);

        if (at <= 0 || !int.TryParse(name.AsSpan(at + 1), out var magnitude)) {
            return (null, 0);
        }

        var step = name[at] == '-' ? -magnitude : magnitude;
        return (name[..at], Math.Clamp(step, -99, 99));
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({byName.Count} rungs)";
}
