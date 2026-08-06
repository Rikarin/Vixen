// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Audio.Assets;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Audio;

/// <summary>A mixer, open for editing: a strip per bus, its inserts, and the snapshots.</summary>
/// <remarks>
///     <para>
///         Doc 20's B5 row is "buses, sends, effects, snapshots", and the layout is the one every
///         mixer has had since the hardware ones: a horizontal row of vertical strips, because a
///         fader is a thing you compare against its neighbours and a list of numbers is not.
///     </para>
///     <para>
///         ⚠ <b>The fader is a slider in decibels with a fixed scale, not a normalised one.</b> A
///         0–1 fader is linear in amplitude, which puts every useful mix position in the top fifth of
///         its travel; −60 to +12 dB is what a person can actually aim at, and it is the number the
///         file stores, so what the strip shows and what the file says are the same number.
///     </para>
///     <para>
///         ⚠ <b>Solo is an editor state and is not written.</b> It is a thing you do while listening
///         and never a thing you ship, so it lives here and mutes the others in the preview rather
///         than being a member of <see cref="MixerBusAsset" /> that a build would honour.
///     </para>
/// </remarks>
public sealed class AudioMixerView : Control {
    readonly HashSet<string> soloed = new(StringComparer.Ordinal);

    AudioMixerDocument? document;
    string selected = string.Empty;
    string snapshot = string.Empty;

    /// <inheritdoc />
    protected override string TagName => "mixer-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strips.</summary>
    public UiElement Strips { get; private set; } = null!;

    /// <summary>The column beside them.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The selected bus's inserts and sends.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>The snapshots.</summary>
    public UiElement Snapshots { get; private set; } = null!;

    /// <summary>What building the mixer said.</summary>
    public UiElement Problems { get; private set; } = null!;

    /// <summary>Adds a bus under the selected one.</summary>
    public Button AddBus { get; private set; } = null!;

    /// <summary>Removes the selected bus.</summary>
    public Button RemoveBus { get; private set; } = null!;

    /// <summary>Adds a snapshot.</summary>
    public Button AddSnapshot { get; private set; } = null!;

    /// <summary>Which bus is selected, or empty.</summary>
    public string Selected => selected;

    /// <summary>Which snapshot the strips are showing, or empty for the authored mix.</summary>
    public string Snapshot => snapshot;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("mixer-bar");

        AddBus = Push(bar, "Add Bus");
        RemoveBus = Push(bar, "Remove Bus");
        AddSnapshot = Push(bar, "Add Snapshot");

        var body = Part("mixer-body");

        Strips = body.Add("mixer-strips");
        Side = body.Add("mixer-side");
        Fields = Side.Add("mixer-fields");
        Snapshots = Side.Add("mixer-snapshots");
        Problems = Side.Add("analysis-list");

        AddHandler<ClickEvent>(static (element, args) => ((AudioMixerView) element).Chosen(args));
    }

    static Button Push(UiElement bar, string label) {
        var button = bar.Add<Button>();

        button.Label = label;
        button.Size = ControlSize.Small;
        button.Variant = ControlVariant.Subtle;

        return button;
    }

    /// <summary>Shows a mixer.</summary>
    /// <param name="mixer">The document.</param>
    public void Show(AudioMixerDocument mixer) {
        ArgumentNullException.ThrowIfNull(mixer);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = mixer;
        mixer.Changed += Reload;

        Reload(mixer);
    }

    /// <summary>Rebuilds the strips from the document.</summary>
    /// <param name="mixer">The document.</param>
    public void Reload(AudioMixerDocument mixer) {
        ArgumentNullException.ThrowIfNull(mixer);

        while (Strips.Children.Count > 0) {
            Strips.Children[^1].Remove();
        }

        // ⚠ The master strip is drawn and is not in the file. `AudioMixer` makes one; a document
        // that declared it would be a second declaration of a bus that always exists, and deleting
        // it from a file would produce a mixer that still has one.
        Strip(mixer, name: "Master", parent: string.Empty, master: true);

        foreach (var bus in Ordered(mixer.Mixer)) {
            Strip(mixer, bus.Name, bus.Parent, master: false, bus);
        }

        Restate();
        Report(mixer);
    }

    /// <summary>The buses, parents before children, so the strips read left to right as the tree.</summary>
    /// <remarks>
    ///     ⚠ <b>The file is deliberately unordered</b> — <see cref="MixerAsset.Buses" /> says a bus may
    ///     name a parent declared after it — so the order the strips are drawn in is this view's and
    ///     has to be derived rather than taken. A cycle, which the builder reports, is drawn in file
    ///     order rather than dropped: a strip nobody can see is a bus nobody can fix.
    /// </remarks>
    static List<MixerBusAsset> Ordered(MixerAsset mixer) {
        List<MixerBusAsset> ordered = [];
        HashSet<string> placed = new(StringComparer.Ordinal);

        var remaining = mixer.Buses.ToList();

        while (remaining.Count > 0) {
            var before = remaining.Count;

            for (var index = remaining.Count - 1; index >= 0; index--) {
                var bus = remaining[index];

                if (bus.Parent.Length != 0 && !placed.Contains(bus.Parent)) {
                    continue;
                }

                ordered.Add(bus);
                placed.Add(bus.Name);
                remaining.RemoveAt(index);
            }

            if (remaining.Count == before) {
                ordered.AddRange(remaining);

                break;
            }
        }

        return ordered;
    }

    void Strip(
        AudioMixerDocument mixer,
        string name,
        string parent,
        bool master,
        MixerBusAsset? bus = null
    ) {
        var strip = Strips.Add("mixer-strip");
        strip.SetClass("selected", string.Equals(selected, name, StringComparison.Ordinal));

        strip.Add("mixer-strip-name").Text = name;

        if (parent.Length > 0) {
            strip.Add("mixer-strip-parent").Text = "→ " + parent;
        }

        var gain = Effective(mixer, name, bus?.GainDb ?? 0f);

        var fader = strip.Add<Slider>();

        // ⚠ Vertical, which is the whole reason a strip is a strip. The layout has been a column of
        // stacked strips since the first frame — `mixer-strip` is `flex-direction: column` and gives
        // the fader a `min-height` — but the slider had one axis, so what a designer got was a row of
        // narrow boxes each containing a *horizontal* fader with about sixty pixels of travel. A
        // fader is a thing you compare against its neighbours, and two faders you compare by their
        // height are two you can read at a glance.
        fader.Orientation = Orientation.Vertical;

        fader.Minimum = MinimumDb;
        fader.Maximum = MaximumDb;
        fader.Value = gain;
        fader.Disabled = master;

        strip.Add("mixer-strip-gain").Text = string.Create(CultureInfo.InvariantCulture, $"{gain:0.0} dB");

        var buttons = strip.Add("mixer-strip-buttons");

        var mute = buttons.Add<ToggleButton>();
        mute.Label = "M";
        mute.IsChecked = bus?.Muted ?? false;
        mute.Disabled = master;

        var solo = buttons.Add<ToggleButton>();
        solo.Label = "S";
        solo.IsChecked = soloed.Contains(name);
        solo.Disabled = master;

        if (bus is null) {
            return;
        }

        var busName = bus.Name;

        fader.ValueChanged += (_, value) => Write(mixer, busName, (float) value, mute.IsChecked);
        mute.CheckedChanged += (_, on) => Write(mixer, busName, (float) fader.Value, on);

        solo.CheckedChanged += (_, on) => {
            if (on) {
                soloed.Add(busName);
            } else {
                soloed.Remove(busName);
            }

            Reload(mixer);
        };

        strip.AddHandler<PointerEvent>(
            (element, args) => {
                if (args.Action != PointerAction.Pressed) {
                    return;
                }

                selected = busName;
                Reload(mixer);
            }
        );
    }

    /// <summary>The quietest a fader goes, in decibels.</summary>
    public const float MinimumDb = -60f;

    /// <summary>And the loudest.</summary>
    public const float MaximumDb = 12f;

    /// <summary>What a bus's fader reads, allowing for the snapshot being edited.</summary>
    /// <remarks>
    ///     ⚠ <b>A bus a snapshot does not mention shows the authored gain, not zero.</b> That is what
    ///     the snapshot means — it changes what it names and leaves the rest — so a strip that read
    ///     zero would be telling the designer their mix had been flattened by choosing a snapshot.
    /// </remarks>
    float Effective(AudioMixerDocument mixer, string bus, float authored) {
        if (snapshot.Length == 0) {
            return authored;
        }

        var line = mixer.Mixer.Snapshots
            .FirstOrDefault(entry => string.Equals(entry.Name, snapshot, StringComparison.Ordinal))
            ?.Buses.FirstOrDefault(entry => string.Equals(entry.Bus, bus, StringComparison.Ordinal));

        return line?.GainDb ?? authored;
    }

    /// <summary>Writes a fader and a mute, into the snapshot when one is being edited.</summary>
    /// <remarks>
    ///     ⚠ <b>Which of the two a drag writes to is the whole of what "editing a snapshot"
    ///     means.</b> With no snapshot chosen a fader changes the authored mix; with one chosen it
    ///     records a line in that snapshot and leaves the authored mix alone — which is what stops a
    ///     sound designer flattening their base mix while auditioning the underwater one.
    /// </remarks>
    void Write(AudioMixerDocument mixer, string bus, float gainDb, bool muted) {
        if (snapshot.Length > 0) {
            mixer.SetSnapshotBus(snapshot, bus, gainDb, muted);

            return;
        }

        mixer.EditBus("Set Bus Gain", bus, found => found with { GainDb = gainDb, Muted = muted });
    }

    /// <summary>Rebuilds the side panel for the selected bus and the snapshot list.</summary>
    public void Restate() {
        while (Fields.Children.Count > 0) {
            Fields.Children[^1].Remove();
        }

        while (Snapshots.Children.Count > 0) {
            Snapshots.Children[^1].Remove();
        }

        if (document is not { } mixer) {
            return;
        }

        Snapshots.Add("mixer-title").Text = "Snapshots";

        var authored = Snapshots.Add("mixer-snapshot");
        authored.Text = "Authored mix";
        authored.SetClass("selected", snapshot.Length == 0);
        authored.AddHandler<PointerEvent>((_, args) => Choose(mixer, string.Empty, args));

        foreach (var entry in mixer.Mixer.Snapshots) {
            var name = entry.Name;
            var row = Snapshots.Add("mixer-snapshot");

            row.Text = $"{name}  ·  {entry.Buses.Length} bus(es)";
            row.SetClass("selected", string.Equals(snapshot, name, StringComparison.Ordinal));
            row.AddHandler<PointerEvent>((_, args) => Choose(mixer, name, args));
        }

        if (mixer.Mixer.Buses.FirstOrDefault(bus => string.Equals(bus.Name, selected, StringComparison.Ordinal))
            is not { } found) {
            Fields.Add("text").Text = "Select a bus.";

            return;
        }

        Fields.Add("mixer-title").Text = found.Name;

        Line("Name", found.Name, value => mixer.EditBus("Rename Bus", found.Name, bus => bus with { Name = value }));
        Line("Parent", found.Parent, value => mixer.EditBus("Reparent Bus", found.Name, bus => bus with { Parent = value }));

        Line(
            "Sidechain",
            found.Sidechain,
            value => mixer.EditBus("Set Sidechain", found.Name, bus => bus with { Sidechain = value })
        );

        Fields.Add("mixer-title").Text = "Sends";

        for (var index = 0; index < found.Sends.Length; index++) {
            var position = index;
            var send = found.Sends[index];

            var row = Fields.Add("fact-row");
            row.Add("fact-name").Text = send.Target.Length > 0 ? send.Target : "(nowhere)";

            var level = row.Add("fact-value").Add<NumericInput>();

            level.Decimals = 1;
            level.Number = send.LevelDb;

            level.NumberChanged += (_, value) => mixer.EditBus(
                "Set Send Level",
                found.Name,
                bus => bus with {
                    Sends = [.. bus.Sends.Select((entry, slot) =>
                        slot == position ? entry with { LevelDb = (float) value } : entry)]
                }
            );
        }

        var add = Fields.Add("fact-row").Add("fact-value").Add<Select>();
        add.Placeholder = "Send to…";

        foreach (var bus in mixer.Mixer.Buses) {
            if (!string.Equals(bus.Name, found.Name, StringComparison.Ordinal)) {
                add.AddOption(bus.Name);
            }
        }

        add.SelectionChanged += (_, value) => {
            if (value is { Length: > 0 } target) {
                mixer.AddSend(found.Name, target);
            }
        };

        Fields.Add("mixer-title").Text = "Inserts";

        for (var index = 0; index < found.Effects.Length; index++) {
            var position = index;
            var row = Fields.Add("fact-row");

            row.Add("fact-name").Text = found.Effects[index].GetType().Name.Replace("Asset", string.Empty, StringComparison.Ordinal);

            var remove = row.Add("fact-value").Add<Button>();

            remove.Label = "Remove";
            remove.Size = ControlSize.Small;
            remove.Variant = ControlVariant.Subtle;
            remove.Clicked += _ => mixer.RemoveEffect(found.Name, position);
        }

        var insert = Fields.Add("fact-row").Add("fact-value").Add<Select>();
        insert.Placeholder = "Add insert…";

        foreach (var effect in Inserts.Keys) {
            insert.AddOption(effect);
        }

        insert.SelectionChanged += (_, value) => {
            if (value is { Length: > 0 } chosen && Inserts.TryGetValue(chosen, out var make)) {
                mixer.AddEffect(found.Name, make());
            }
        };
    }

    /// <summary>The inserts this editor offers, by the name a menu shows.</summary>
    /// <remarks>
    ///     ⚠ <b>A table rather than a reflection scan over <see cref="IAudioEffectAsset" />.</b> The
    ///     same argument <c>AssetEditorRegistry</c> makes about being told rather than discovering:
    ///     a scan makes "which effects does this build have" a question with a different answer in a
    ///     trimmed publish, and a plugin's effect would appear in the menu with no way to configure it.
    /// </remarks>
    static Dictionary<string, Func<IAudioEffectAsset>> Inserts { get; } = new(StringComparer.Ordinal) {
        ["Filter"] = static () => new FilterEffectAsset(),
        ["Equalizer"] = static () => new EqualizerEffectAsset(),
        ["Reverb"] = static () => new ReverbEffectAsset(),
        ["Delay"] = static () => new DelayEffectAsset(),
        ["Compressor"] = static () => new CompressorEffectAsset(),
        ["Gate"] = static () => new GateEffectAsset(),
        ["Limiter"] = static () => new LimiterEffectAsset()
    };

    void Choose(AudioMixerDocument mixer, string name, PointerEvent args) {
        if (args.Action != PointerAction.Pressed) {
            return;
        }

        snapshot = name;
        Reload(mixer);
    }

    void Line(string label, string value, Action<string> write) {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    void Report(AudioMixerDocument mixer) {
        while (Problems.Children.Count > 0) {
            Problems.Children[^1].Remove();
        }

        if (mixer.LoadError is { } error) {
            Line("read", error);
        }

        foreach (var problem in mixer.Validate()) {
            Line("mixer", problem);
        }

        // Said on success too, for the compositor's reason: an empty list cannot be told apart from
        // one that never ran.
        if (mixer.Problems.Count == 0 && mixer.LoadError is null) {
            Line("mixer", $"{mixer.Mixer.Buses.Length} bus(es), {mixer.Mixer.Snapshots.Length} snapshot(s). Builds.");
        }

        void Line(string stage, string message) {
            var row = Problems.Add("analysis-row");

            row.Add("analysis-stage").Text = stage;
            row.Add("analysis-message").Text = message;
        }
    }

    void Chosen(ClickEvent args) {
        if (document is not { } mixer) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddBus)) {
                mixer.AddBus("New Bus", selected);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, RemoveBus) && selected.Length > 0) {
                mixer.RemoveBus(selected);
                selected = string.Empty;
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddSnapshot)) {
                mixer.AddSnapshot("New Snapshot");
                args.Handled = true;

                return;
            }
        }
    }
}

/// <summary>Opens an audio mixer.</summary>
public sealed class AudioMixerEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Audio Mixer";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [AudioMixerDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new AudioMixerDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ A mixer is a row of strips that scrolls sideways, and its faders fill whatever height they
        // are given. Let the panel scroll and the faders stretch to their content instead of to the
        // window — which is a mixer with no bounded height at all, and a horizontal strip that has
        // lost the box it was scrolling inside.
        DockPanel.Fills(panel);

        var view = panel.Add<AudioMixerView>();
        view.Show((AudioMixerDocument) document);

        return view;
    }
}
