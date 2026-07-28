// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Music;
using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.Audio.Assets;

/// <summary>A named place in a segment, as a file declares it.</summary>
[DataContract("MusicMarker")]
public sealed record MusicMarkerAsset {
    /// <summary>What it is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How far into the segment, in beats.</summary>
    public float Beat { get; init; }
}

/// <summary>A change of tempo or metre part way through a segment, as a file declares it.</summary>
[DataContract("MusicTempoChange")]
public sealed record MusicTempoChangeAsset {
    /// <summary>Where it happens, in beats from the segment's start.</summary>
    public float Beat { get; init; }

    /// <summary>What it changes to.</summary>
    public float BeatsPerMinute { get; init; } = 120f;

    /// <summary>The top of the new time signature.</summary>
    public int BeatsPerBar { get; init; } = 4;
}

/// <summary>One piece of music, as a file declares it.</summary>
/// <remarks>
///     The tempo is written down rather than derived. It is what the composer wrote, and it is the
///     only thing that makes a bar line a real position — a segment whose declared tempo disagrees
///     with its recording transitions in the wrong place, and nothing can detect that but an ear.
/// </remarks>
[DataContract("MusicSegment")]
public sealed record MusicSegmentAsset {
    /// <summary>What it is called, and what a transition names it by.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The audio.</summary>
    public ContentReference<AudioClip>? Clip { get; init; }

    /// <summary>Its tempo.</summary>
    public float BeatsPerMinute { get; init; } = 120f;

    /// <summary>The top of its time signature.</summary>
    public int BeatsPerBar { get; init; } = 4;

    /// <summary>Where it changes tempo or metre, if it does.</summary>
    public MusicTempoChangeAsset[] TempoChanges { get; init; } = [];

    /// <summary>Whether it vamps rather than moving on, until gameplay releases it.</summary>
    public bool Sustains { get; init; }

    /// <summary>How many times it repeats before moving on. Zero plays it once; negative is forever.</summary>
    public int LoopCount { get; init; } = -1;

    /// <summary>Which segment follows when it runs out. Empty stops.</summary>
    public string Next { get; init; } = string.Empty;

    /// <summary>Named places in it, for gameplay to hang on.</summary>
    public MusicMarkerAsset[] Markers { get; init; } = [];

    /// <summary>The segment this describes.</summary>
    /// <returns>The segment. Its clip is null if the reference did not resolve, which plays nothing.</returns>
    public MusicSegment ToSegment() {
        var markers = new MusicMarker[Markers.Length];

        for (var i = 0; i < Markers.Length; i++) {
            markers[i] = new(Markers[i].Name, Markers[i].Beat);
        }

        var changes = new MusicTempoChange[TempoChanges.Length];

        for (var i = 0; i < TempoChanges.Length; i++) {
            var change = TempoChanges[i];

            changes[i] = new(change.Beat, new MusicTempo {
                BeatsPerMinute = change.BeatsPerMinute,
                BeatsPerBar = change.BeatsPerBar
            });
        }

        return new() {
            Name = Name,
            Clip = Clip?.Value,
            Tempo = new() { BeatsPerMinute = BeatsPerMinute, BeatsPerBar = BeatsPerBar },
            TempoChanges = changes,
            Sustains = Sustains,
            LoopCount = LoopCount,
            Next = Next,
            Markers = markers
        };
    }
}

/// <summary>A route from one segment to another, as a file declares it.</summary>
[DataContract("MusicTransition")]
public sealed record MusicTransitionAsset {
    /// <summary>Which segment it leads out of. Empty means any of them.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Which segment it leads to.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Where it is allowed to land.</summary>
    public MusicQuantize Quantize { get; init; } = MusicQuantize.Bar;

    /// <summary>Which engine-wide parameter decides.</summary>
    public string Parameter { get; init; } = string.Empty;

    /// <summary>The bottom of the range it applies over.</summary>
    public float Minimum { get; init; }

    /// <summary>The top of the range it applies over.</summary>
    public float Maximum { get; init; } = 1f;

    /// <summary>The transition this describes.</summary>
    /// <returns>The transition.</returns>
    public MusicTransition ToTransition() => new() {
        From = From,
        To = To,
        Quantize = Quantize,
        Parameter = Parameter,
        Minimum = Minimum,
        Maximum = Maximum
    };
}

/// <summary>A whole piece of interactive music, as a file declares it.</summary>
/// <remarks>
///     <para>
///         Segments and the rules for getting between them, which together are what a composer
///         actually delivers: not a track, but a set of pieces and a description of how the game gets
///         from one to another. Everything gameplay has to know is the name of a parameter.
///     </para>
///     <para>
///         <b>No file format here</b>, as with every other asset in this assembly. The editor writes
///         YAML, the content build bakes a chunk, and a shipping runtime reads the chunk.
///     </para>
/// </remarks>
[DataContract("Music")]
public sealed record MusicAsset {
    /// <summary>What it is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Which bus it plays on, by name. Empty is the master.</summary>
    public string Bus { get; init; } = string.Empty;

    /// <summary>Which segment it starts on. Empty starts nothing until gameplay asks.</summary>
    public string Start { get; init; } = string.Empty;

    /// <summary>How long the outgoing segment takes to get out of the way.</summary>
    public float CrossfadeSeconds { get; init; } = 0.04f;

    /// <summary>The pieces.</summary>
    public MusicSegmentAsset[] Segments { get; init; } = [];

    /// <summary>The rules for getting between them.</summary>
    public MusicTransitionAsset[] Transitions { get; init; } = [];
}

/// <summary>Turns a music asset into a player.</summary>
/// <remarks>
///     The two things a file cannot hold, again: a clip, which is a chunk id until something loads it,
///     and a bus, which is a name until there is a mixer. Problems are returned rather than thrown,
///     because music is content — a level whose combat segment failed to load should still play its
///     exploration loop while somebody works out why.
/// </remarks>
public static class MusicBuilder {
    /// <summary>Builds a player from an asset.</summary>
    /// <param name="engine">The engine the music plays through.</param>
    /// <param name="asset">What to build.</param>
    /// <param name="problems">Everything that did not resolve. Empty is the good case.</param>
    /// <returns>The player, started on its opening segment if it named one.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static MusicPlayer Build(AudioEngine engine, MusicAsset asset, out IReadOnlyList<string> problems) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(asset);

        var found = new List<string>();
        var name = string.IsNullOrEmpty(asset.Name) ? "<unnamed>" : asset.Name;
        var bus = 0;

        if (!string.IsNullOrEmpty(asset.Bus)) {
            if (engine.FindBus(asset.Bus) is { } target) {
                bus = target.Index;
            } else {
                found.Add($"Music '{name}' routes to bus '{asset.Bus}', which does not exist. It will play on the master.");
            }
        }

        var player = new MusicPlayer(engine, bus) { CrossfadeSeconds = asset.CrossfadeSeconds };

        foreach (var segment in asset.Segments) {
            if (segment.Clip?.Value is null) {
                var reference = segment.Clip is null ? "no clip" : $"unresolved clip {segment.Clip.Id}";
                found.Add($"Music '{name}' segment '{segment.Name}' has {reference} and will be silent.");
            }

            player.Add(segment.ToSegment());
        }

        foreach (var transition in asset.Transitions) {
            if (player.Find(transition.To) is null) {
                found.Add($"Music '{name}' has a transition to '{transition.To}', which is not one of its segments.");
                continue;
            }

            player.AddTransition(transition.ToTransition());
        }

        if (!string.IsNullOrEmpty(asset.Start) && !player.Play(asset.Start)) {
            found.Add($"Music '{name}' starts on '{asset.Start}', which is not one of its segments.");
        }

        problems = found;
        return player;
    }
}
