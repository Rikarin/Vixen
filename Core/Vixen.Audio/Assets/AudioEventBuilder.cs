// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Events;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;

namespace Vixen.Audio.Assets;

/// <summary>Turns an event asset into an event an engine can play.</summary>
/// <remarks>
///     <para>
///         The two things a file cannot hold: a clip, which is a chunk id until something loads it,
///         and a bus, which is a name until there is a mixer to look it up in. Everything else copies
///         across.
///     </para>
///     <para>
///         <b>Problems are returned, not thrown</b>, exactly as <see cref="MixerBuilder" /> does and
///         for the same reason: an event is content. A variant whose clip failed to load should leave
///         an event that plays its other four, and a report saying which one is missing — not an
///         exception out of a level load.
///     </para>
/// </remarks>
public static class AudioEventBuilder {
    /// <summary>Resolves an asset against an engine.</summary>
    /// <param name="engine">The engine the event will play through, and whose mixer names the buses.</param>
    /// <param name="asset">What to build.</param>
    /// <param name="problems">Everything that did not resolve, in the order it was found.</param>
    /// <param name="library">
    ///     Where its layers are looked up. Without one an asset that declares layers builds without
    ///     them and says so, which is better than refusing to build at all — a gunshot that is only
    ///     its report is still a gunshot.
    /// </param>
    /// <returns>The event. Never null, even when nothing resolved — an event that plays nothing is quiet, not broken.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static AudioEvent Build(
        AudioEngine engine,
        AudioEventAsset asset,
        out IReadOnlyList<string> problems,
        IAudioEventLibrary? library = null
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(asset);

        var found = new List<string>();
        var name = string.IsNullOrEmpty(asset.Name) ? "<unnamed>" : asset.Name;
        var variants = new List<AudioEventVariant>(asset.Variants.Length);

        for (var i = 0; i < asset.Variants.Length; i++) {
            var variant = asset.Variants[i];
            var clip = variant.Clip?.Value;

            if (clip is null) {
                // The id is worth reporting even when there is one: "variant 2 is empty" and "variant
                // 2 points at a chunk nothing loaded" are different problems with different fixes.
                var target = variant.Clip is null ? "no clip" : $"unresolved clip {variant.Clip.Id}";
                found.Add($"Event '{name}' variant {i} has {target} and was dropped.");
                continue;
            }

            variants.Add(new(clip) {
                Weight = variant.Weight,
                GainDb = variant.GainDb,
                PitchSemitones = variant.PitchSemitones
            });
        }

        if (variants.Count == 0) {
            found.Add($"Event '{name}' has no playable variants and will be silent.");
        }

        problems = found;

        return new(engine, new AudioEventDescription {
            Name = asset.Name,
            Variants = [.. variants],
            Selection = asset.Selection,
            Seed = asset.Seed,
            Bus = ResolveBus(engine.Mixer, asset.Bus, name, found),
            GainDb = asset.GainDb,
            GainVarianceDb = asset.GainVarianceDb,
            PitchVarianceSemitones = asset.PitchVarianceSemitones,
            Loop = asset.Loop,
            Priority = asset.Priority,
            MaxInstances = asset.MaxInstances,
            Steal = asset.Steal,
            Parameters = BuildParameters(asset.Parameters),
            Layers = BuildLayers(asset.Layers, library, name, found),
            IsSpatial = asset.Spatial is not null,
            Spatial = asset.Spatial?.ToSettings() ?? new()
        });
    }

    static AudioEventLayer[] BuildLayers(
        AudioEventLayerAsset[] assets,
        IAudioEventLibrary? library,
        string name,
        List<string> problems
    ) {
        if (assets.Length == 0) {
            return [];
        }

        if (library is null) {
            problems.Add($"Event '{name}' has {assets.Length} layer(s), and no event library to resolve them in.");
            return [];
        }

        var layers = new List<AudioEventLayer>(assets.Length);

        foreach (var layer in assets) {
            if (library.Find(layer.Event) is not { } sound) {
                problems.Add($"Event '{name}' layers '{layer.Event}', which does not exist. It was dropped.");
                continue;
            }

            layers.Add(new(sound) {
                DelaySeconds = layer.DelaySeconds,
                GainDb = layer.GainDb,
                PitchSemitones = layer.PitchSemitones,
                Probability = layer.Probability
            });
        }

        return [.. layers];
    }

    static AudioParameterSheet? BuildParameters(AudioParameterAsset[] assets) {
        if (assets.Length == 0) {
            return null;
        }

        var definitions = new AudioParameterDefinition[assets.Length];

        for (var i = 0; i < assets.Length; i++) {
            definitions[i] = assets[i].ToDefinition();
        }

        return new(definitions);
    }

    static int ResolveBus(AudioMixer mixer, string bus, string name, List<string> problems) {
        if (string.IsNullOrEmpty(bus)) {
            return 0;
        }

        var target = mixer.FindBus(bus);

        if (target is not null) {
            return target.Index;
        }

        problems.Add($"Event '{name}' routes to bus '{bus}', which does not exist. It will play on the master.");
        return 0;
    }
}
