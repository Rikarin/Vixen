// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;

namespace Vixen.Audio.Assets;

/// <summary>Turns a <see cref="MixerAsset" /> into buses, effects, sends and sidechains.</summary>
/// <remarks>
///     <para>
///         <b>Two passes, because a file has no order.</b> A bus may name a parent, a send target or
///         a sidechain declared after it — insisting otherwise would make the file's order load-
///         bearing, and the first person to sort it alphabetically would break the mix. So every bus
///         is created first and every reference resolved second.
///     </para>
///     <para>
///         <b>An unknown name is a diagnostic, not an exception.</b> A mixer asset is content: it
///         gets edited, buses get renamed, and a level whose ambience bus lost its reverb send should
///         still be playable while somebody works out why. The problems are collected and returned,
///         so a tool can show them and a game can log them.
///     </para>
/// </remarks>
public static class MixerBuilder {
    /// <summary>What went wrong while applying a mixer asset, if anything did.</summary>
    /// <param name="Snapshots">The snapshots, which work whether or not there were problems.</param>
    /// <param name="Problems">Everything that could not be resolved, in the order it was found.</param>
    public readonly record struct MixerBuildResult(MixerSnapshots Snapshots, IReadOnlyList<string> Problems);

    /// <summary>Applies an asset to a mixer.</summary>
    /// <param name="mixer">The mixer. Its existing buses are kept; the asset's are added.</param>
    /// <param name="asset">What to build.</param>
    /// <returns>The snapshots, and anything that did not resolve.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static MixerBuildResult Build(AudioMixer mixer, MixerAsset asset) {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(asset);

        var problems = new List<string>();
        var created = new Dictionary<string, AudioBus>(StringComparer.Ordinal);

        // Pass one: create the buses, parents first. A bus's parent is fixed when it is made —
        // Depth and the render order are derived from it — so the creation order has to follow the
        // hierarchy even though the file's order need not.
        var pending = new List<MixerBusAsset>();

        foreach (var declaration in asset.Buses) {
            if (string.IsNullOrEmpty(declaration.Name)) {
                problems.Add("A bus has no name and was skipped.");
                continue;
            }

            if (mixer.FindBus(declaration.Name) is not null) {
                problems.Add($"There is already a bus called '{declaration.Name}'; the asset's was skipped.");
                continue;
            }

            pending.Add(declaration);
        }

        while (pending.Count > 0) {
            var madeProgress = false;

            for (var i = pending.Count - 1; i >= 0; i--) {
                var declaration = pending[i];
                var wantsMaster = string.IsNullOrEmpty(declaration.Parent);
                var parent = wantsMaster ? mixer.Master : mixer.FindBus(declaration.Parent);

                if (parent is null) {
                    continue;
                }

                created[declaration.Name] = mixer.CreateBus(declaration.Name, parent);
                pending.RemoveAt(i);
                madeProgress = true;
            }

            if (madeProgress) {
                continue;
            }

            // What is left names a parent that does not exist, or a cycle. Both are content errors,
            // and both are more use as a mixer that plays with a flatter tree than as an exception.
            foreach (var declaration in pending) {
                problems.Add(
                    $"Bus '{declaration.Name}' names a parent '{declaration.Parent}' that does not "
                    + "exist or is part of a loop; it was put on the master."
                );

                created[declaration.Name] = mixer.CreateBus(declaration.Name);
            }

            pending.Clear();
        }

        // Pass two: gains, effects, sends and sidechains, all of which can point anywhere.
        foreach (var declaration in asset.Buses) {
            if (!created.TryGetValue(declaration.Name, out var bus)) {
                continue;
            }

            bus.Gain = Decibels.ToLinear(declaration.GainDb);
            bus.Muted = declaration.Muted;

            foreach (var effect in declaration.Effects) {
                bus.AddEffect(effect.Create());
            }

            foreach (var send in declaration.Sends) {
                if (mixer.FindBus(send.Target) is not { } target) {
                    problems.Add($"Bus '{declaration.Name}' sends to '{send.Target}', which does not exist.");
                    continue;
                }

                try {
                    bus.AddSend(target, Decibels.ToLinear(send.LevelDb), send.PreFader);
                } catch (ArgumentException exception) {
                    problems.Add($"Bus '{declaration.Name}' cannot send to '{send.Target}': {exception.Message}");
                }
            }

            if (string.IsNullOrEmpty(declaration.Sidechain)) {
                continue;
            }

            if (mixer.FindBus(declaration.Sidechain) is not { } key) {
                problems.Add(
                    $"Bus '{declaration.Name}' is keyed by '{declaration.Sidechain}', which does not exist."
                );

                continue;
            }

            try {
                bus.SetSidechain(key);
            } catch (ArgumentException exception) {
                problems.Add($"Bus '{declaration.Name}' cannot be keyed by '{declaration.Sidechain}': {exception.Message}");
            }
        }

        var snapshots = new MixerSnapshots(mixer, asset.Snapshots);

        if (!string.IsNullOrEmpty(asset.DefaultSnapshot) && !snapshots.TransitionTo(asset.DefaultSnapshot, TimeSpan.Zero)) {
            problems.Add($"The default snapshot '{asset.DefaultSnapshot}' is not one of the ones declared.");
        }

        return new MixerBuildResult(snapshots, problems);
    }
}
