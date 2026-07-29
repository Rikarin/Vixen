// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Audio.Assets;
using Vixen.Audio.Mixing;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Audio;

/// <summary>A mixer, open for editing: buses, sends, effects and snapshots.</summary>
/// <remarks>
///     <para>
///         <b>The format already existed and the panel did not, which is the shortest E5 row.</b>
///         <see cref="MixerAsset" /> is <c>Vixen.Audio</c>'s own authoring layer — its remarks say
///         why: "a sound designer who has to open a C# file to move a fader is a sound designer who
///         does not move the fader" — and everything under it is a serialisable record with no file
///         format attached. This document is the YAML end of that sentence and the undo stack over it.
///     </para>
///     <para>
///         ⚠ <b>Every number a person sees is in decibels and every number the mixer runs on is
///         linear.</b> That is <see cref="MixerBusAsset" />'s own decision and this editor does not
///         second-guess it: the fields edit <c>GainDb</c> directly, and the one conversion happens
///         where it always did, in <see cref="MixerBuilder" />.
///     </para>
///     <para>
///         ⚠ <b>Validation is the real builder against a real mixer, not a second set of rules.</b>
///         <see cref="Validate" /> constructs an <see cref="AudioMixer" /> and runs
///         <see cref="MixerBuilder.Build" /> over the document; what comes back is the list of
///         problems a game would hit at load. An editor-side checker would be a second implementation
///         of "is this mixer buildable" and the two would disagree the week either changed.
///     </para>
/// </remarks>
public sealed class AudioMixerDocument : EditorDocument {
    /// <summary>What a mixer is written as.</summary>
    public const string Extension = ".vxmixer";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The mixer.</summary>
    public MixerAsset Mixer { get; private set; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What the last <see cref="Validate" /> had to say.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>Raised after anything changes the mixer.</summary>
    public event Action<AudioMixerDocument>? Changed;

    /// <summary>Opens a mixer.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public AudioMixerDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var text = AssetFile.Read(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new mixer has the three buses every project grows anyway. Master is implicit —
            // `AudioMixer` makes it — so what a file has to declare is what hangs off it, and Music
            // and SFX under a Master is the shape of the first mixer in every game ever shipped.
            Mixer = new() {
                Buses = [
                    new() { Name = "Music", GainDb = -6f },
                    new() { Name = "SFX" },
                    new() { Name = "Voice" }
                ]
            };

            return;
        }

        try {
            Mixer = YamlSerializer.Parse<MixerAsset>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            Mixer = new();
            LoadError = exception.Message;
        }
    }

    /// <summary>Replaces the mixer, undoably.</summary>
    /// <param name="name">What the undo history calls the edit.</param>
    /// <param name="mixer">The new mixer.</param>
    /// <remarks>
    ///     ⚠ <b>Whole-document replacement, for <c>InputActionsDocument</c>'s reason.</b>
    ///     <see cref="MixerAsset" /> and everything under it are immutable records — there is no "set
    ///     this bus's gain", only a new record — so an edit <i>is</i> a replacement and undo is an
    ///     assignment. A mixer is tens of records; the alternative is an editor-side mutable mirror
    ///     of a model that is deliberately not one.
    /// </remarks>
    public void Replace(string name, MixerAsset mixer) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(mixer);

        var previous = Mixer;

        Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    Mixer = mixer;
                    Changed?.Invoke(this);
                },
                _ => {
                    Mixer = previous;
                    Changed?.Invoke(this);
                }
            )
        );
    }

    /// <summary>Adds a bus, undoably.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="parent">What it sums into, or empty for the master.</param>
    public void AddBus(string name, string parent = "") {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Replace(
            "Add Bus",
            Mixer with {
                Buses = [
                    .. Mixer.Buses,
                    new MixerBusAsset {
                        Name = InputUnique(name, Mixer.Buses.Select(bus => bus.Name)),
                        Parent = parent
                    }
                ]
            }
        );
    }

    /// <summary>Removes a bus and everything pointing at it, undoably.</summary>
    /// <param name="name">Which bus, by name.</param>
    /// <remarks>
    ///     ⚠ <b>The sends into it go too, and so do the snapshot lines about it.</b> A send to a bus
    ///     that is not there is a problem <see cref="MixerBuilder" /> reports at load, and leaving the
    ///     project in that state because somebody deleted a bus is the editor making a broken file
    ///     rather than an edit. A child bus is <i>reparented</i> rather than deleted, because deleting
    ///     a group would silently take everything under it.
    /// </remarks>
    public void RemoveBus(string name) {
        var removed = Mixer.Buses.FirstOrDefault(bus => string.Equals(bus.Name, name, StringComparison.Ordinal));

        if (removed is null) {
            return;
        }

        Replace(
            "Remove Bus",
            Mixer with {
                Buses = [
                    .. Mixer.Buses
                        .Where(bus => !string.Equals(bus.Name, name, StringComparison.Ordinal))
                        .Select(bus => bus with {
                            Parent = string.Equals(bus.Parent, name, StringComparison.Ordinal) ? removed.Parent : bus.Parent,
                            Sidechain = string.Equals(bus.Sidechain, name, StringComparison.Ordinal) ? string.Empty : bus.Sidechain,
                            Sends = [.. bus.Sends.Where(send => !string.Equals(send.Target, name, StringComparison.Ordinal))]
                        })
                ],
                Snapshots = [
                    .. Mixer.Snapshots.Select(snapshot => snapshot with {
                        Buses = [.. snapshot.Buses.Where(entry => !string.Equals(entry.Bus, name, StringComparison.Ordinal))],
                        Sends = [.. snapshot.Sends.Where(entry =>
                            !string.Equals(entry.Bus, name, StringComparison.Ordinal)
                            && !string.Equals(entry.Target, name, StringComparison.Ordinal))]
                    })
                ]
            }
        );
    }

    /// <summary>Replaces one bus, undoably.</summary>
    /// <param name="name">What the undo history calls the edit.</param>
    /// <param name="bus">Which bus, by name.</param>
    /// <param name="change">What to replace it with.</param>
    public void EditBus(string name, string bus, Func<MixerBusAsset, MixerBusAsset> change) {
        ArgumentNullException.ThrowIfNull(change);

        Replace(
            name,
            Mixer with {
                Buses = [.. Mixer.Buses.Select(entry =>
                    string.Equals(entry.Name, bus, StringComparison.Ordinal) ? change(entry) : entry)]
            }
        );
    }

    /// <summary>Adds a send from one bus to another, undoably.</summary>
    /// <param name="from">The bus the copy leaves.</param>
    /// <param name="to">The bus it arrives at.</param>
    public void AddSend(string from, string to) =>
        EditBus("Add Send", from, bus => bus with { Sends = [.. bus.Sends, new MixerSendAsset { Target = to }] });

    /// <summary>Adds an effect to a bus's insert chain, undoably.</summary>
    /// <param name="bus">Which bus, by name.</param>
    /// <param name="effect">The effect.</param>
    public void AddEffect(string bus, IAudioEffectAsset effect) {
        ArgumentNullException.ThrowIfNull(effect);

        EditBus("Add Effect", bus, found => found with { Effects = [.. found.Effects, effect] });
    }

    /// <summary>Removes an effect by its position in the chain, undoably.</summary>
    /// <param name="bus">Which bus, by name.</param>
    /// <param name="index">Which insert.</param>
    public void RemoveEffect(string bus, int index) =>
        EditBus(
            "Remove Effect",
            bus,
            found => found with { Effects = [.. found.Effects.Where((_, position) => position != index)] }
        );

    /// <summary>Adds a snapshot, undoably.</summary>
    /// <param name="name">What it is called.</param>
    public void AddSnapshot(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Replace(
            "Add Snapshot",
            Mixer with {
                Snapshots = [
                    .. Mixer.Snapshots,
                    new MixerSnapshotAsset { Name = InputUnique(name, Mixer.Snapshots.Select(entry => entry.Name)) }
                ]
            }
        );
    }

    /// <summary>Removes a snapshot, undoably.</summary>
    /// <param name="name">Which one, by name.</param>
    public void RemoveSnapshot(string name) =>
        Replace(
            "Remove Snapshot",
            Mixer with {
                Snapshots = [.. Mixer.Snapshots.Where(entry => !string.Equals(entry.Name, name, StringComparison.Ordinal))],
                DefaultSnapshot = string.Equals(Mixer.DefaultSnapshot, name, StringComparison.Ordinal)
                    ? string.Empty
                    : Mixer.DefaultSnapshot
            }
        );

    /// <summary>Records what a snapshot does to a bus, undoably.</summary>
    /// <param name="snapshot">Which snapshot, by name.</param>
    /// <param name="bus">Which bus, by name.</param>
    /// <param name="gainDb">What its gain becomes.</param>
    /// <param name="muted">What its mute becomes.</param>
    /// <remarks>
    ///     ⚠ <b>A snapshot names only the buses it changes</b> — that is <see cref="MixerSnapshotAsset" />'s
    ///     own rule and the whole reason a snapshot for "the player is underwater" is two lines rather
    ///     than a copy of the mixer that goes stale when a bus is added. So this replaces a line if
    ///     there is one and appends if there is not; removing a bus from a snapshot is a separate
    ///     verb, not a gain set back to zero.
    /// </remarks>
    public void SetSnapshotBus(string snapshot, string bus, float gainDb, bool muted) =>
        Replace(
            "Edit Snapshot",
            Mixer with {
                Snapshots = [.. Mixer.Snapshots.Select(entry =>
                    string.Equals(entry.Name, snapshot, StringComparison.Ordinal)
                        ? entry with {
                            Buses = entry.Buses.Any(line => string.Equals(line.Bus, bus, StringComparison.Ordinal))
                                ? [.. entry.Buses.Select(line => string.Equals(line.Bus, bus, StringComparison.Ordinal)
                                    ? line with { GainDb = gainDb, Muted = muted }
                                    : line)]
                                : [.. entry.Buses, new SnapshotBusAsset { Bus = bus, GainDb = gainDb, Muted = muted }]
                        }
                        : entry)]
            }
        );

    /// <summary>Removes a bus's line from a snapshot, undoably.</summary>
    /// <param name="snapshot">Which snapshot, by name.</param>
    /// <param name="bus">Which bus, by name.</param>
    public void ClearSnapshotBus(string snapshot, string bus) =>
        Replace(
            "Edit Snapshot",
            Mixer with {
                Snapshots = [.. Mixer.Snapshots.Select(entry =>
                    string.Equals(entry.Name, snapshot, StringComparison.Ordinal)
                        ? entry with {
                            Buses = [.. entry.Buses.Where(line => !string.Equals(line.Bus, bus, StringComparison.Ordinal))]
                        }
                        : entry)]
            }
        );

    /// <summary>Builds the mixer for real and keeps what the builder complained about.</summary>
    /// <returns>The problems, empty when there are none.</returns>
    public IReadOnlyList<string> Validate() {
        var mixer = new AudioMixer();

        Problems = MixerBuilder.Build(mixer, Mixer).Problems;
        return Problems;
    }

    /// <summary>The mixer as it would be written, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(Mixer);

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    /// <summary>A name nothing in a set already has, for the reason the input editor gives.</summary>
    static string InputUnique(string wanted, IEnumerable<string> taken) =>
        Input.InputActionsDocument.Unique(wanted, taken);
}
