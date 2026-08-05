// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Exploration;

/// <summary>What kind of thing is worth finding.</summary>
public enum PointKind {
    /// <summary>A named place.</summary>
    Landmark,

    /// <summary>A view somebody has to climb to.</summary>
    Vista,

    /// <summary>Somewhere that can be travelled to once it is found.</summary>
    Waypoint,

    /// <summary>Something that pays out once.</summary>
    Cache
}

/// <summary>One thing on a map worth finding.</summary>
[DataContract("PointOfInterest")]
public sealed class PointOfInterestDefinition {
    /// <summary>What it is called within its map.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What kind it is.</summary>
    public PointKind Kind { get; set; }

    /// <summary>What finding it grants — <c>Discovered.Queensdale.Ascalon</c>. Empty for one nothing asks about.</summary>
    /// <remarks>
    ///     ⚠ <b>How a waypoint gets unlocked without <c>Vixen.Gameplay.Travel</c> referencing this
    ///     library.</b> Doc 28's spine forbids the edge; a tag granted here and asked about there is
    ///     the same requirement algebra everything else uses, and it means a game can gate a waypoint
    ///     on anything else too.
    /// </remarks>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Whether it counts towards the map's completion.</summary>
    /// <remarks>
    ///     ⚠ <b>Opt-in, and that is deliberate.</b> A patch that adds a point to a finished map would
    ///     otherwise un-complete it for everybody who had it at a hundred per cent, which is the one
    ///     thing a completion number must never do.
    /// </remarks>
    public bool Counts { get; set; } = true;

    /// <summary>What has to be true to find it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];
}

/// <summary>A map: what is on it worth finding, and how finely its fog is remembered.</summary>
[DataContract("MapDefinition")]
public sealed record MapDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How many cells across the revealed-area grid is.</summary>
    public int Columns { get; set; } = 64;

    /// <summary>How many down.</summary>
    public int Rows { get; set; } = 64;

    /// <summary>What is on it.</summary>
    public List<PointOfInterestDefinition> Points { get; set; } = [];

    /// <summary>What completing it grants — <c>Completion.Queensdale</c>.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var point in Points) {
            if (point.Tag.Length > 0) {
                tags.Add(point.Tag);
            }

            foreach (var requirement in point.Requires) {
                if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                    tags.Add(requirement.Subject);
                }
            }
        }
    }
}

/// <summary>A point of interest with its names resolved.</summary>
public sealed class PointOfInterest {
    internal PointOfInterest(PointOfInterestDefinition definition, int index, GameplayTag tag, RequirementSet requirements) {
        Definition = definition;
        Index = index;
        Tag = tag;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public PointOfInterestDefinition Definition { get; }

    /// <summary>Which of its map's points it is. What a bit in the discovery set means.</summary>
    public int Index { get; }

    /// <summary>What it is called within its map.</summary>
    public string Id => Definition.Id;

    /// <summary>What kind it is.</summary>
    public PointKind Kind => Definition.Kind;

    /// <summary>What finding it grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>Whether it counts towards completion.</summary>
    public bool Counts => Definition.Counts;

    /// <summary>What has to be true to find it.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>A map with its points compiled.</summary>
public sealed class MapChart {
    readonly PointOfInterest[] points;

    internal MapChart(MapDefinition definition, GameplayTag tag, PointOfInterest[] points) {
        Definition = definition;
        Tag = tag;
        this.points = points;
        Counting = points.Count(point => point.Counts);
    }

    /// <summary>What it was compiled from.</summary>
    public MapDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What completing it grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What is on it.</summary>
    public ReadOnlySpan<PointOfInterest> Points => points;

    /// <summary>How many of them count towards completion.</summary>
    public int Counting { get; }

    /// <summary>How many cells across, never below one.</summary>
    public int Columns => Math.Max(1, Definition.Columns);

    /// <summary>How many down, never below one.</summary>
    public int Rows => Math.Max(1, Definition.Rows);

    /// <summary>How many cells there are.</summary>
    public int Cells => Columns * Rows;

    /// <summary>Finds a point.</summary>
    /// <param name="id">Its id within the map.</param>
    /// <returns>It, or null.</returns>
    public PointOfInterest? Find(string? id) {
        foreach (var point in points) {
            if (string.Equals(point.Id, id, StringComparison.Ordinal)) {
                return point;
            }
        }

        return null;
    }
}

/// <summary>Every map a build knows, compiled once.</summary>
public sealed class ExplorationLibrary {
    readonly Dictionary<uint, MapChart> maps;
    readonly string[] problems;

    ExplorationLibrary(Dictionary<uint, MapChart> maps, string[] problems) {
        this.maps = maps;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static ExplorationLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every map, in address order.</summary>
    public IEnumerable<MapChart> Maps => maps.Values.OrderBy(map => map.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static ExplorationLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var maps = new Dictionary<uint, MapChart>();

        foreach (var definition in catalog.OfType<MapDefinition>()) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var points = new PointOfInterest[definition.Points.Count];

            for (var index = 0; index < points.Length; index++) {
                var point = definition.Points[index];

                if (point.Id.Length == 0) {
                    problems.Add($"'{definition.Address}' has a point with no id, so nothing can name it.");
                } else if (!seen.Add(point.Id)) {
                    problems.Add($"'{definition.Address}' has two points called '{point.Id}'.");
                }

                if (point.Kind == PointKind.Waypoint && point.Tag.Length == 0) {
                    problems.Add(
                        $"'{definition.Address}' point '{point.Id}' is a waypoint with no tag, so nothing "
                        + "can be unlocked by finding it."
                    );
                }

                points[index] = new(point, index, tags.Resolve(point.Tag), RequirementSet.Compile(point.Requires, tags));
            }

            if (definition.Columns * definition.Rows > 1 << 20) {
                problems.Add(
                    $"'{definition.Address}' has a {definition.Columns}×{definition.Rows} fog grid, which is "
                    + "a bitmap per character per map and will not fit in a save."
                );
            }

            maps.Add(definition.Id.Value, new(definition, tags.Resolve(definition.Tag), points));
        }

        return new(maps, [.. problems]);
    }

    /// <summary>Finds a map.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public MapChart? Find(DefId id) => maps.GetValueOrDefault(id.Value);
}
