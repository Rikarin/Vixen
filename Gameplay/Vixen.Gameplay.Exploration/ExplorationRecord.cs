// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Exploration;

/// <summary>One character's map: what they have found and where they have been.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 says why this is its own library:</b> <em>"its state is a bitmap per character per
///         map and nothing else wants that shape"</em>. A quest counter is an integer, an inventory is
///         a list, a reputation is a number — this is thousands of bits that compress well and are
///         read as a whole, and putting it anywhere else would make that shape somebody else's problem.
///     </para>
///     <para>
///         ⚠ <b>Completion counts only the points a designer marked as counting, and it is computed
///         from the chart rather than stored.</b> Storing it would mean a patch that adds a point
///         leaves ten thousand characters claiming a hundred per cent of a map they have not finished;
///         computing it means the number moves, which is the honest behaviour and the reason
///         <c>counts</c> is opt-in in the first place.
///     </para>
///     <para>
///         ⚠ <b>Fog is revealed and never re-hidden.</b> There is deliberately no way to un-reveal a
///         cell: a map that could go back to fog is a map a bug can erase, and nobody has ever wanted
///         the feature.
///     </para>
/// </remarks>
public sealed class ExplorationRecord {
    readonly Dictionary<uint, ulong[]> fog = [];
    readonly Dictionary<uint, HashSet<int>> found = [];

    /// <summary>Makes an empty record.</summary>
    /// <param name="library">Where the maps come from.</param>
    /// <param name="tags">Where the tags a discovery grants go, or null to keep them to itself.</param>
    public ExplorationRecord(ExplorationLibrary library, GameplayTagSet? tags = null) {
        ArgumentNullException.ThrowIfNull(library);

        Library = library;
        Tags = tags ?? new GameplayTagSet();
    }

    /// <summary>Where the maps come from.</summary>
    public ExplorationLibrary Library { get; }

    /// <summary>The tags their discoveries have granted.</summary>
    public GameplayTagSet Tags { get; }

    /// <summary>How many maps they have set foot on.</summary>
    public int Visited => fog.Count;

    /// <summary>Raised whenever something is found for the first time.</summary>
    public event Action<MapChart, PointOfInterest>? Found;

    /// <summary>Raised when a map is completed.</summary>
    public event Action<MapChart>? Completed;

    /// <summary>Whether they have found something.</summary>
    /// <param name="map">Which map.</param>
    /// <param name="point">Which point.</param>
    /// <returns>Whether they have.</returns>
    public bool HasFound(MapChart map, PointOfInterest point) {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(point);

        return found.TryGetValue(map.Id.Value, out var set) && set.Contains(point.Index);
    }

    /// <summary>How many of a map's counting points they have found.</summary>
    /// <param name="map">Which map.</param>
    /// <returns>How many.</returns>
    public int FoundOn(MapChart map) {
        ArgumentNullException.ThrowIfNull(map);

        if (!found.TryGetValue(map.Id.Value, out var set)) {
            return 0;
        }

        var counted = 0;

        foreach (var point in map.Points) {
            if (point.Counts && set.Contains(point.Index)) {
                counted++;
            }
        }

        return counted;
    }

    /// <summary>How far through a map they are, from zero to one.</summary>
    /// <param name="map">Which map.</param>
    /// <returns>The fraction. One for a map with nothing on it that counts.</returns>
    public float CompletionOf(MapChart map) {
        ArgumentNullException.ThrowIfNull(map);

        return map.Counting == 0 ? 1f : (float)FoundOn(map) / map.Counting;
    }

    /// <summary>Whether they have finished a map.</summary>
    /// <param name="map">Which map.</param>
    /// <returns>Whether they have.</returns>
    public bool IsComplete(MapChart map) => FoundOn(map) >= (map?.Counting ?? 0);

    /// <summary>Finds something, if they may.</summary>
    /// <param name="map">Which map.</param>
    /// <param name="point">Which point.</param>
    /// <param name="context">What their requirements are evaluated against, or null to skip them.</param>
    /// <returns>Whether it was new to them and they were allowed it.</returns>
    public bool Discover(MapChart map, PointOfInterest point, IRequirementContext? context = null) {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(point);

        if (context is not null && !point.Requirements.IsMetBy(context)) {
            return false;
        }

        if (!found.TryGetValue(map.Id.Value, out var set)) {
            set = [];
            found.Add(map.Id.Value, set);
        }

        if (!set.Add(point.Index)) {
            return false;
        }

        if (point.Tag.IsSome) {
            Tags.Add(point.Tag);
        }

        Found?.Invoke(map, point);

        // Announced once, on the discovery that finished it — not every time somebody asks.
        if (point.Counts && IsComplete(map)) {
            if (map.Tag.IsSome) {
                Tags.Add(map.Tag);
            }

            Completed?.Invoke(map);
        }

        return true;
    }

    /// <summary>Whether a cell of the fog has been lifted.</summary>
    /// <param name="map">Which map.</param>
    /// <param name="column">Which column.</param>
    /// <param name="row">Which row.</param>
    /// <returns>Whether it has.</returns>
    public bool IsRevealed(MapChart map, int column, int row) {
        ArgumentNullException.ThrowIfNull(map);

        if (!Inside(map, column, row) || !fog.TryGetValue(map.Id.Value, out var bits)) {
            return false;
        }

        var cell = (row * map.Columns) + column;

        return (bits[cell >> 6] & (1ul << (cell & 63))) != 0ul;
    }

    /// <summary>Lifts the fog around somewhere.</summary>
    /// <param name="map">Which map.</param>
    /// <param name="column">Which column they are in.</param>
    /// <param name="row">Which row.</param>
    /// <param name="radius">How many cells they see, in every direction.</param>
    /// <returns>How many cells were newly revealed.</returns>
    /// <remarks>
    ///     A square rather than a circle, deliberately: the difference is invisible under a fog
    ///     texture, and a circle costs a multiply per cell on the one call in this library that
    ///     happens every time anybody moves.
    /// </remarks>
    public int Reveal(MapChart map, int column, int row, int radius = 1) {
        ArgumentNullException.ThrowIfNull(map);

        var bits = Fog(map);
        var revealed = 0;

        for (var y = row - radius; y <= row + radius; y++) {
            for (var x = column - radius; x <= column + radius; x++) {
                if (!Inside(map, x, y)) {
                    continue;
                }

                var cell = (y * map.Columns) + x;
                var mask = 1ul << (cell & 63);

                if ((bits[cell >> 6] & mask) != 0ul) {
                    continue;
                }

                bits[cell >> 6] |= mask;
                revealed++;
            }
        }

        return revealed;
    }

    /// <summary>How much of a map's fog has been lifted, from zero to one.</summary>
    /// <param name="map">Which map.</param>
    /// <returns>The fraction.</returns>
    public float RevealedOn(MapChart map) {
        ArgumentNullException.ThrowIfNull(map);

        if (!fog.TryGetValue(map.Id.Value, out var bits)) {
            return 0f;
        }

        var lit = 0;

        foreach (var word in bits) {
            lit += System.Numerics.BitOperations.PopCount(word);
        }

        return (float)lit / map.Cells;
    }

    /// <summary>The raw fog of a map, for a save or a wire.</summary>
    /// <param name="map">Which map.</param>
    /// <returns>The bits, or an empty span for a map never visited.</returns>
    public ReadOnlySpan<ulong> FogOf(MapChart map) {
        ArgumentNullException.ThrowIfNull(map);

        return fog.TryGetValue(map.Id.Value, out var bits) ? bits : [];
    }

    /// <summary>Puts a saved fog back.</summary>
    /// <param name="map">Which map.</param>
    /// <param name="bits">What <see cref="FogOf" /> returned.</param>
    /// <returns>Whether it was the right size.</returns>
    public bool RestoreFog(MapChart map, ReadOnlySpan<ulong> bits) {
        ArgumentNullException.ThrowIfNull(map);

        var words = Words(map);

        if (bits.Length != words) {
            return false;
        }

        var copy = new ulong[words];

        bits.CopyTo(copy);
        fog[map.Id.Value] = copy;

        return true;
    }

    ulong[] Fog(MapChart map) {
        if (fog.TryGetValue(map.Id.Value, out var bits)) {
            return bits;
        }

        bits = new ulong[Words(map)];
        fog.Add(map.Id.Value, bits);

        return bits;
    }

    static int Words(MapChart map) => ((map.Cells - 1) >> 6) + 1;

    static bool Inside(MapChart map, int column, int row) =>
        (uint)column < (uint)map.Columns && (uint)row < (uint)map.Rows;
}
