// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Sequencing;

/// <summary>A cinematic, open for editing: tracks over time, scrubbed against the open scene.</summary>
/// <remarks>
///     <para>
///         Doc 20's exit criterion for E5 is "a cinematic can be authored, scrubbed and played", and
///         the three verbs are the three things here: a track per actor with keys on it, a playhead
///         that drives the scene as it moves, and a transport that advances the playhead.
///     </para>
///     <para>
///         ⚠ <b>Recording is "key what the actor is doing now", not a capture mode.</b> The way a
///         cinematic is actually authored is: move the playhead, pose the actor with the ordinary
///         gizmo in the ordinary viewport, press the key button. A dedicated record mode that
///         intercepted the gizmo would be a second way to move an object, and the first one already
///         has undo, snapping and numeric entry.
///     </para>
///     <para>
///         ⚠ <b>Leaving the panel restores the scene.</b> <see cref="SequencePlayer" /> takes a
///         snapshot of the actors a sequence names and puts them back — see its own remarks — and
///         this view is what tells it when. Without that, scrubbing a shot and saving the level
///         writes the actors where frame 47 left them.
///     </para>
/// </remarks>
public sealed class SequenceView : Control {
    SequenceDocument? document;
    SequenceTrackData? selected;
    float previous = -1f;

    /// <inheritdoc />
    protected override string TagName => "sequence-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The tracks.</summary>
    public Timeline Tracks { get; private set; } = null!;

    /// <summary>The column beside them.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The selected track's fields.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>What the last scrub raised.</summary>
    public UiElement Log { get; private set; } = null!;

    /// <summary>Runs the sequence.</summary>
    public ToggleButton Play { get; private set; } = null!;

    /// <summary>Adds a transform track for the selected entity.</summary>
    public Button AddTransform { get; private set; } = null!;

    /// <summary>Adds an event track.</summary>
    public Button AddEvent { get; private set; } = null!;

    /// <summary>Adds a camera track.</summary>
    public Button AddCamera { get; private set; } = null!;

    /// <summary>Keys the selected track at the playhead.</summary>
    public Button Key { get; private set; } = null!;

    /// <summary>Removes the selected track.</summary>
    public Button DeleteTrack { get; private set; } = null!;

    /// <summary>How long the sequence is.</summary>
    public NumericInput Duration { get; private set; } = null!;

    /// <summary>Which track is selected, or <see langword="null" />.</summary>
    public SequenceTrackData? Selected => selected;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("sequence-bar");

        Play = bar.Add<ToggleButton>();
        Play.Label = "Play";

        AddTransform = Push(bar, "Track: Transform");
        AddCamera = Push(bar, "Track: Camera");
        AddEvent = Push(bar, "Track: Event");
        Key = Push(bar, "Key");
        DeleteTrack = Push(bar, "Remove Track");

        bar.Add("fact-name").Text = "Length";
        Duration = bar.Add<NumericInput>();
        Duration.Decimals = 2;
        Duration.Minimum = SequenceDocument.MinimumDuration;

        var body = Part("sequence-body");

        Tracks = body.Add<Timeline>();
        Tracks.SnapToFrames = true;

        Side = body.Add("sequence-side");
        Fields = Side.Add("sequence-fields");
        Log = Side.Add("analysis-list");

        Duration.NumberChanged += (_, value) => document?.SetDuration((float) value);
        Tracks.TimeChanged += (_, time) => Seek(time);
        Tracks.SelectionChanged += _ => Restate();
        Tracks.KeysMoved += _ => Commit();

        AddHandler<ClickEvent>(static (element, args) => ((SequenceView) element).Chosen(args));
    }

    static Button Push(UiElement bar, string label) {
        var button = bar.Add<Button>();

        button.Label = label;
        button.Size = ControlSize.Small;
        button.Variant = ControlVariant.Subtle;

        return button;
    }

    /// <summary>Shows a sequence.</summary>
    /// <param name="sequence">The document.</param>
    public void Show(SequenceDocument sequence) {
        ArgumentNullException.ThrowIfNull(sequence);

        if (document is { } current) {
            current.Changed -= Reload;
        }

        document = sequence;
        sequence.Changed += Reload;

        Reload(sequence);
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        base.OnRemoved();

        // ⚠ The scene goes back when the tab does. A panel's factory runs again on every reopen, so
        // this is the only moment the view knows it is leaving.
        document?.Player?.Restore();
    }

    /// <summary>Rebuilds the tracks from the document.</summary>
    /// <param name="sequence">The document.</param>
    public void Reload(SequenceDocument sequence) {
        ArgumentNullException.ThrowIfNull(sequence);

        Duration.Number = sequence.Sequence.Duration;

        Tracks.Duration = sequence.Sequence.Duration;
        Tracks.FrameRate = sequence.Sequence.FrameRate;

        while (Tracks.Tracks.Count > 0) {
            Tracks.Remove(Tracks.Tracks[^1]);
        }

        foreach (var track in sequence.Sequence.Tracks) {
            var row = Tracks.AddTrack(Label(sequence, track));

            row.Tag = track;
            row.Muted = track.Muted;

            foreach (var key in track.Keys) {
                row.Add(key.Time, key);
            }
        }

        Tracks.Refresh();

        if (selected is not null && !sequence.Sequence.Tracks.Contains(selected)) {
            selected = null;
        }

        Restate();
    }

    /// <summary>What a track's header says: what it drives, and whether that still exists.</summary>
    /// <remarks>
    ///     ⚠ <b>A track naming an entity the scene no longer has says so in its own row.</b> A
    ///     sequence outlives the actors it was authored against — deleting an entity is one keystroke
    ///     — and a row that silently did nothing would be indistinguishable from a track with no keys.
    /// </remarks>
    static string Label(SequenceDocument sequence, SequenceTrackData track) {
        if (track.Name.Length > 0) {
            return track.Name;
        }

        if (track.Kind is not (SequenceTrackKind.Transform or SequenceTrackKind.Activation)) {
            return track.Kind.ToString();
        }

        if (sequence.Player is not { Scene: { } scene }) {
            return $"{track.Kind} · {track.Target}";
        }

        return scene.TryGetEntity(track.Target, out var entity) && scene.World.IsAlive(entity)
            ? $"{track.Kind} · {scene.NameOf(entity)}"
            : $"{track.Kind} · (missing)";
    }

    /// <summary>Moves the playhead and drives the scene.</summary>
    /// <param name="time">Where to, in seconds.</param>
    /// <returns>How many tracks were applied.</returns>
    public int Seek(float time) {
        if (document is not { Player: { } player }) {
            Report(["No scene is attached, so there is nothing to drive."]);

            return 0;
        }

        player.Begin();

        var applied = player.Apply(time, previous);

        previous = time;

        Report([.. player.Signals.Select(signal =>
            string.Create(CultureInfo.InvariantCulture, $"{signal.Time:0.00}s  {signal.Track}: {signal.Name}"))]);

        return applied;
    }

    /// <summary>Advances the playhead if the transport is running.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>Pulled by whoever owns the frame.</b> A timer started by the view would outlive the
    ///     panel, for the reason every other pulled surface in this editor gives.
    /// </remarks>
    public void Tick(TimeSpan delta) {
        if (document is not { } sequence || !Play.IsChecked) {
            return;
        }

        var time = Tracks.Time + (float) delta.TotalSeconds;

        if (time >= sequence.Sequence.Duration) {
            time = sequence.Sequence.Duration;
            Play.IsChecked = false;
        }

        Tracks.Time = time;
        Seek(time);
    }

    void Commit() {
        if (document is not { } sequence) {
            return;
        }

        foreach (var row in Tracks.Tracks) {
            if (row.Tag is not SequenceTrackData track) {
                continue;
            }

            foreach (var key in row.Keys) {
                if (key.Tag is SequenceKeyData data && Math.Abs(data.Time - key.Time) > 1e-6f) {
                    sequence.MoveKey(track, data, key.Time);
                }
            }
        }
    }

    /// <summary>Rebuilds the fields for the selected track.</summary>
    public void Restate() {
        while (Fields.Children.Count > 0) {
            Fields.Children[^1].Remove();
        }

        if (document is not { } sequence) {
            return;
        }

        // The timeline reports the selected *key*; the track it is on is what the fields are about,
        // because a track is what somebody adds, mutes and removes.
        if (Tracks.Selection.FirstOrDefault() is { } key) {
            selected = Tracks.Tracks.FirstOrDefault(row => row.Keys.Contains(key))?.Tag as SequenceTrackData ?? selected;
        }

        Fields.Add("sequence-title").Text = sequence.Sequence.Name;

        Fields.Add("fact-row").Add("fact-name").Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{sequence.Sequence.Tracks.Count} track(s) · {sequence.Sequence.Duration:0.00}s"
        );

        if (sequence.Player is null) {
            var warning = Fields.Add("fact-row");

            warning.AddClass("warning");
            warning.Add("fact-name").Text = "No scene attached";
        }

        if (selected is not { } track) {
            Fields.Add("text").Text = "Select a track.";

            return;
        }

        Fields.Add("sequence-title").Text = track.Kind.ToString();

        var name = Fields.Add("fact-row");
        name.Add("fact-name").Text = "Name";

        var box = name.Add("fact-value").Add<TextBox>();
        box.Value = track.Name;
        box.ValueChanged += (_, value) => track.Name = value ?? string.Empty;

        var mute = Fields.Add("fact-row");
        mute.Add("fact-name").Text = "Muted";

        var toggle = mute.Add("fact-value").Add<CheckBox>();
        toggle.IsChecked = track.Muted;
        toggle.CheckedChanged += (_, on) => sequence.SetMuted(track, on);

        Fields.Add("fact-row").Add("fact-name").Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{track.Keys.Count} key(s)"
        );
    }

    void Report(IReadOnlyList<string> lines) {
        while (Log.Children.Count > 0) {
            Log.Children[^1].Remove();
        }

        foreach (var line in lines) {
            var row = Log.Add("analysis-row");

            row.Add("analysis-stage").Text = "signal";
            row.Add("analysis-message").Text = line;
        }
    }

    void Chosen(ClickEvent args) {
        if (document is not { } sequence) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddTransform)) {
                AddForSelection(sequence, SequenceTrackKind.Transform);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddCamera)) {
                sequence.AddTrack(new() { Kind = SequenceTrackKind.Camera, Name = "Camera" });
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddEvent)) {
                sequence.AddTrack(new() { Kind = SequenceTrackKind.Event, Name = "Events" });
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Key)) {
                KeyHere(sequence);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, DeleteTrack) && selected is { } track) {
                sequence.RemoveTrack(track);
                selected = null;
                args.Handled = true;

                return;
            }
        }
    }

    /// <summary>Adds a track for whatever the scene has selected.</summary>
    void AddForSelection(SequenceDocument sequence, SequenceTrackKind kind) {
        if (sequence.Player is not { Scene: { } scene } || scene.Selection.Count == 0) {
            Report(["Select an entity in the scene first."]);

            return;
        }

        foreach (var entity in scene.Selection) {
            sequence.AddTrack(new() { Kind = kind, Target = scene.IdOf(entity) });
        }
    }

    /// <summary>Records the selected track's value at the playhead.</summary>
    void KeyHere(SequenceDocument sequence) {
        if (selected is not { } track) {
            Report(["Select a track first."]);

            return;
        }

        var time = Tracks.Snap(Tracks.Time);

        switch (track.Kind) {
            case SequenceTrackKind.Transform when sequence.Player is { Scene: { } scene }
                && scene.TryGetEntity(track.Target, out var entity)
                && scene.World.IsAlive(entity)
                && scene.World.Has<LocalTransform>(entity):

                sequence.AddKey(track, new() { Time = time, Value = SequencePlayer.Write(scene.World.Get<LocalTransform>(entity)) });
                break;

            case SequenceTrackKind.Activation:
                sequence.AddKey(track, new() { Time = time, Value = [1f] });
                break;

            default:
                sequence.AddKey(track, new() { Time = time, Text = track.Name });
                break;
        }
    }
}

/// <summary>Opens a sequence.</summary>
/// <remarks>
///     ⚠ <b>The scene is attached afterwards, by the host.</b> Which scene a cinematic is authored
///     against is the application's arbitration — the same one that decides which world an opened
///     scene goes into — and a factory that reached for one would be a factory that knows what the
///     editor has open.
/// </remarks>
public sealed class SequenceEditorFactory : IAssetEditorFactory {
    /// <summary>Where the scene a sequence drives comes from, or <see langword="null" /> for none.</summary>
    public Func<SceneDocument?>? Scene { get; set; }

    /// <inheritdoc />
    public string Name => "Sequence";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [SequenceDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var document = new SequenceDocument(request.Project, request.Asset, request.Path);

        document.Attach(Scene?.Invoke());
        return document;
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<SequenceView>();
        view.Show((SequenceDocument) document);

        return view;
    }
}
