// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Ai;

/// <summary>One thing a spawn table can produce.</summary>
[DataContract("SpawnEntry")]
public sealed class SpawnEntryDefinition {
    /// <summary>The address of what.</summary>
    public string Creature { get; set; } = string.Empty;

    /// <summary>How likely it is against the other rows.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>The fewest that come at once.</summary>
    public int Minimum { get; set; } = 1;

    /// <summary>The most.</summary>
    public int Maximum { get; set; } = 1;
}

/// <summary>What lives somewhere, how many of it, and how fast it comes back.</summary>
[DataContract("SpawnTableDefinition")]
public sealed record SpawnTableDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What it can produce.</summary>
    public List<SpawnEntryDefinition> Entries { get; set; } = [];

    /// <summary>How many may be alive at once.</summary>
    public int Cap { get; set; } = 4;

    /// <summary>How long after something dies before it is replaced, in seconds.</summary>
    public float RespawnSeconds { get; set; } = 30f;

    /// <summary>How much that varies, in seconds.</summary>
    /// <remarks>
    ///     ⚠ <b>Not decoration.</b> A camp wiped in one pull comes back as one wave on a fixed timer,
    ///     for ever, and every pull after the first is the same pull. A little jitter is what breaks
    ///     the lockstep, and it is deterministic per spawner so a replay still matches.
    /// </remarks>
    public float RespawnJitter { get; set; } = 5f;

    /// <summary>How far what lives here may be pulled from it, or null for the default.</summary>
    /// <remarks>
    ///     ⚠ <b>On the table because a leash is about a place, and the table is the only thing in
    ///     this library that names one.</b> <see cref="LeashDefinition" /> is not a
    ///     <see cref="Definition" /> and has no address, so before this there was no authored path to
    ///     one at all — every camp in a game had to be leashed from code, which is the half of doc
    ///     28's AI section that had a type and no way to author it. <c>Samples/14-Mmo</c> is what
    ///     found that: it tried to write the file and there was nowhere for it to go.
    /// </remarks>
    public LeashDefinition? Leash { get; set; }
}

/// <summary>A spawn table with its addresses resolved.</summary>
public sealed class SpawnTable {
    readonly (DefId Creature, string Address, float Weight, int Minimum, int Maximum)[] entries;
    readonly float[] weights;

    internal SpawnTable(
        SpawnTableDefinition definition,
        (DefId, string, float, int, int)[] entries,
        float[] weights
    ) {
        Definition = definition;
        Leash = definition.Leash ?? new LeashDefinition();
        this.entries = entries;
        this.weights = weights;
    }

    /// <summary>What it was compiled from.</summary>
    public SpawnTableDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What it can produce.</summary>
    public ReadOnlySpan<(DefId Creature, string Address, float Weight, int Minimum, int Maximum)> Entries => entries;

    /// <summary>How many may be alive at once, never below one.</summary>
    public int Cap => Math.Max(1, Definition.Cap);

    /// <summary>How long after something dies before it is replaced.</summary>
    public float RespawnSeconds => MathF.Max(0f, Definition.RespawnSeconds);

    /// <summary>How much that varies.</summary>
    public float RespawnJitter => MathF.Max(0f, Definition.RespawnJitter);

    /// <summary>How far what lives here may be pulled from it.</summary>
    /// <remarks>
    ///     ⚠ <b>One definition, and a <see cref="Ai.Leash" /> per mob.</b> A leash holds how long
    ///     <em>this</em> mob has been stretched, so a camp of eight sharing one would have eight mobs
    ///     giving up the moment the first of them did. The caller makes one per spawn from this.
    /// </remarks>
    public LeashDefinition Leash { get; }

    /// <summary>Picks a row.</summary>
    /// <param name="random">The stream to draw from.</param>
    /// <returns>Its index, or −1 for a table with nothing in it.</returns>
    public int Pick(ref GameplayRandom random) => weights.Length == 0 ? -1 : random.Pick(weights);
}

/// <summary>One thing a spawner has been asked to put in the world.</summary>
/// <param name="Creature">What.</param>
/// <param name="Count">How many of it.</param>
/// <param name="Slot">Which of the spawner's slots it fills, so a caller can report its death.</param>
public readonly record struct SpawnOrder(DefId Creature, int Count, int Slot);

/// <summary>Every spawn table a build knows, compiled once.</summary>
public sealed class SpawnLibrary {
    readonly Dictionary<uint, SpawnTable> tables;
    readonly string[] problems;

    SpawnLibrary(Dictionary<uint, SpawnTable> tables, string[] problems) {
        this.tables = tables;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static SpawnLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every table, in address order.</summary>
    public IEnumerable<SpawnTable> Tables =>
        tables.Values.OrderBy(table => table.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static SpawnLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var problems = new List<string>();
        var tables = new Dictionary<uint, SpawnTable>();

        foreach (var definition in catalog.OfType<SpawnTableDefinition>()) {
            if (definition.Entries.Count == 0) {
                problems.Add($"'{definition.Address}' has no entries, so nothing ever spawns from it.");
            }

            var rows = new List<(DefId, string, float, int, int)>();
            var weights = new List<float>();

            foreach (var entry in definition.Entries) {
                if (entry.Weight <= 0f) {
                    problems.Add(
                        $"'{definition.Address}' has '{entry.Creature}' at weight {entry.Weight}, which is a "
                        + "row that can never be picked."
                    );

                    continue;
                }

                if (entry.Maximum < entry.Minimum) {
                    problems.Add(
                        $"'{definition.Address}' wants between {entry.Minimum} and {entry.Maximum} of "
                        + $"'{entry.Creature}', which is no number at all."
                    );
                }

                rows.Add((DefId.From(entry.Creature), entry.Creature, entry.Weight, Math.Max(0, entry.Minimum), Math.Max(entry.Minimum, entry.Maximum)));
                weights.Add(entry.Weight);
            }

            if (definition.RespawnJitter > definition.RespawnSeconds && definition.RespawnSeconds > 0f) {
                problems.Add(
                    $"'{definition.Address}' jitters by more than its respawn time, so something can come "
                    + "back before it died."
                );
            }

            // ⚠ Checked here rather than in Leash, because a leash whose tether is not inside its
            // break is the single-radius flicker the two radii exist to prevent — and it is a
            // content mistake, so it belongs in a list somebody reads at build time.
            if (definition.Leash is { } leash && leash.Tether >= leash.Break) {
                problems.Add(
                    $"'{definition.Address}' leashes at a tether of {leash.Tether} and a break of "
                    + $"{leash.Break}, which is one radius wearing two names — a mob on the boundary "
                    + "will flicker between chasing and resetting once a frame."
                );
            }

            tables.Add(definition.Id.Value, new(definition, [.. rows], [.. weights]));
        }

        return new(tables, [.. problems]);
    }

    /// <summary>Finds a table.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public SpawnTable? Find(DefId id) => tables.GetValueOrDefault(id.Value);
}

/// <summary>One camp: what is alive, and what is due back.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It says <em>what</em> to spawn and never <em>where</em>.</b> Placing something needs
///         the scene and a navigation mesh, and a spawner that owned those would be a second one —
///         the boundary every library in this framework sits on.
///     </para>
///     <para>
///         ⚠ <b>A respawn timer starts when something dies, not on the tick that notices.</b> A
///         server that fell behind would otherwise repopulate faster than one that did not, which is
///         a difference players feel and nobody can explain.
///     </para>
///     <para>
///         ⚠ <b>The cap counts what is alive, not what has been spawned.</b> Counting spawns makes a
///         camp that has been cleared twice permanently empty.
///     </para>
/// </remarks>
public sealed class Spawner {
    readonly SpawnTable table;
    readonly bool[] alive;
    readonly float[] dueAt;

    GameplayRandom random;

    /// <summary>Makes one, empty and due immediately.</summary>
    /// <param name="table">What lives here.</param>
    /// <param name="seed">What its stream is seeded from, so a replay matches.</param>
    public Spawner(SpawnTable table, ulong seed) {
        ArgumentNullException.ThrowIfNull(table);

        this.table = table;
        alive = new bool[table.Cap];
        dueAt = new float[table.Cap];
        random = GameplayRandom.For(seed, table.Id.Value);
    }

    /// <summary>What lives here.</summary>
    public SpawnTable Table => table;

    /// <summary>How many are alive.</summary>
    public int Alive => alive.Count(entry => entry);

    /// <summary>How many could still be.</summary>
    public int Free => table.Cap - Alive;

    /// <summary>Whether it is full.</summary>
    public bool IsFull => Free == 0;

    /// <summary>Puts back whatever is due, and says what to make.</summary>
    /// <param name="now">The clock.</param>
    /// <param name="into">Where the orders go.</param>
    /// <returns>How many orders were made.</returns>
    public int Tick(float now, ICollection<SpawnOrder> into) {
        ArgumentNullException.ThrowIfNull(into);

        var made = 0;

        for (var slot = 0; slot < alive.Length; slot++) {
            if (alive[slot] || now < dueAt[slot]) {
                continue;
            }

            var row = table.Pick(ref random);

            if (row < 0) {
                break;
            }

            var entry = table.Entries[row];
            var count = entry.Minimum == entry.Maximum
                ? entry.Minimum
                : random.NextInt(entry.Minimum, entry.Maximum + 1);

            alive[slot] = true;
            into.Add(new(entry.Creature, count, slot));
            made++;
        }

        return made;
    }

    /// <summary>Says something died.</summary>
    /// <param name="slot">Which slot it filled.</param>
    /// <param name="now">When, on the caller's clock.</param>
    /// <returns>Whether that slot was occupied.</returns>
    public bool Died(int slot, float now) {
        if ((uint)slot >= (uint)alive.Length || !alive[slot]) {
            return false;
        }

        alive[slot] = false;

        // From the death, not from the tick that noticed it, and jittered deterministically.
        var jitter = table.RespawnJitter > 0f ? (random.NextFloat() * 2f - 1f) * table.RespawnJitter : 0f;

        dueAt[slot] = now + MathF.Max(0f, table.RespawnSeconds + jitter);

        return true;
    }

    /// <summary>Clears everything and makes it all due at once. What a reset does.</summary>
    /// <param name="now">The clock.</param>
    public void Reset(float now) {
        for (var slot = 0; slot < alive.Length; slot++) {
            alive[slot] = false;
            dueAt[slot] = now;
        }
    }

    /// <summary>When a slot is due back.</summary>
    /// <param name="slot">Which one.</param>
    /// <returns>The time, or zero when it is alive or due now.</returns>
    public float DueAt(int slot) => (uint)slot < (uint)dueAt.Length ? dueAt[slot] : 0f;
}
