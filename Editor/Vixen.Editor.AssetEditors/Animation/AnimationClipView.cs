// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core.Curves;
using Vixen.Editor.Assets.Animation;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>Which row of the dope sheet a timeline track is.</summary>
/// <param name="Target">The object it drives, or <see langword="null" /> for the event track.</param>
/// <param name="Property">Which of its numbers.</param>
/// <param name="Shape">
///     Which blend shape, when <paramref name="Property" /> is <see cref="AnimationProperty.Weight" />;
///     empty for every other property.
/// </param>
/// <remarks>
///     <para>
///         A tag on the track rather than a parallel list, because <see cref="Timeline" /> rebuilds its
///         header rows as it scrolls — the same pooling the outliner does — and an index into a list
///         beside it would name a different row after every rebuild.
///     </para>
///     <para>
///         ⚠ <b>Three parts and not two, because a face's node has one weight row per shape.</b>
///         Twenty rows all tagged <c>(Head, Weight)</c> would be twenty rows that compare equal, and
///         every one of this view's lookups — which track is this key on, which curve does the editor
///         show, which curve does a delete edit — would answer with the first of them.
///     </para>
/// </remarks>
public sealed record AnimationRow(AnimationTargetData? Target, AnimationProperty Property, string Shape = "");


/// <summary>A clip, open for editing: a dope sheet, a curve editor, and an event track.</summary>
/// <remarks>
///     <para>
///         Doc 11's row for this asset is "timeline + curve editor + event track", and the two views
///         are two <i>modes</i> over one document rather than two panels. A dope sheet answers "when
///         does anything happen" and a curve editor answers "what does this number do"; showing both
///         at once in a docked panel gives each of them half the height it needs, and every editor
///         that tried it grew a splitter people immediately dragged to one end.
///     </para>
///     <para>
///         ⚠ <b>The events are a track on the dope sheet rather than a strip of their own.</b> An
///         event's whole meaning is <i>when</i> it happens relative to the motion — a footstep is
///         placed against the frame the foot lands — so it has to share the ruler, the zoom and the
///         playhead with the keys. A second widget with its own scroll would be two rulers to keep
///         lined up by eye.
///     </para>
///     <para>
///         ⚠ <b>Every drag commits through the document rather than mutating the asset.</b> The
///         timeline moves its own <see cref="TimelineKey" /> objects while the pointer is down and
///         reports once, on <c>KeysMoved</c>; that report is the edit, which is what makes dragging
///         twenty keys one undo entry.
///     </para>
/// </remarks>
public sealed class AnimationClipView : Control {
    AnimationClipDocument? document;
    AnimationRow? row;

    /// <inheritdoc />
    protected override string TagName => "animation-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The bar across the top.</summary>
    public UiElement Bar { get; private set; } = null!;

    /// <summary>The dope sheet.</summary>
    public Timeline Sheet { get; private set; } = null!;

    /// <summary>The curve editor, shown instead of the sheet in curve mode.</summary>
    public CurveEditor Curves { get; private set; } = null!;

    /// <summary>The column beside them.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>What the selected key or event is.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>How long the clip is.</summary>
    public NumericInput Duration { get; private set; } = null!;

    /// <summary>What the timeline snaps to.</summary>
    public NumericInput FrameRate { get; private set; } = null!;

    /// <summary>What happens at the end.</summary>
    public Select Wrap { get; private set; } = null!;

    /// <summary>Whether the curve editor is showing instead of the sheet.</summary>
    public ToggleButton CurveMode { get; private set; } = null!;

    /// <summary>Adds a key to the selected row at the playhead.</summary>
    public Button Key { get; private set; } = null!;

    /// <summary>Adds an event at the playhead.</summary>
    public Button Event { get; private set; } = null!;

    /// <summary>Places a constraint on the track.</summary>
    public Button Constraint { get; private set; } = null!;

    /// <summary>Looks for contacts in the scene the clip was marked up against.</summary>
    public Button Propose { get; private set; } = null!;

    /// <summary>What the last proposal pass offered, one row each with a button.</summary>
    public UiElement Proposals { get; private set; } = null!;

    /// <summary>Removes whatever is selected.</summary>
    public Button Delete { get; private set; } = null!;

    /// <summary>Which row the curve editor is showing, or <see langword="null" />.</summary>
    public AnimationRow? Row => row;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Bar = Part("animation-bar");

        CurveMode = Bar.Add<ToggleButton>();
        CurveMode.Label = "Curves";

        Key = Bar.Add<Button>();
        Key.Label = "Add Key";

        Event = Bar.Add<Button>();
        Event.Label = "Add Event";

        Constraint = Bar.Add<Button>();
        Constraint.Label = "Add Constraint";

        Propose = Bar.Add<Button>();
        Propose.Label = "Propose Contacts";

        Delete = Bar.Add<Button>();
        Delete.Label = "Delete";

        Bar.Add("fact-name").Text = "Length";
        Duration = Bar.Add<NumericInput>();
        Duration.Decimals = 3;
        Duration.Minimum = AnimationClipDocument.MinimumDuration;

        Bar.Add("fact-name").Text = "FPS";
        FrameRate = Bar.Add<NumericInput>();
        FrameRate.Decimals = 0;
        FrameRate.Minimum = 1;
        FrameRate.Maximum = 240;

        Bar.Add("fact-name").Text = "Wrap";
        Wrap = Bar.Add<Select>();

        foreach (var mode in Enum.GetValues<WrapMode>()) {
            Wrap.AddOption(mode.ToString());
        }

        var body = Part("animation-body");
        var stage = body.Add("animation-stage");

        Sheet = stage.Add<Timeline>();
        Sheet.SnapToFrames = true;

        Curves = stage.Add<CurveEditor>();
        Curves.AddClass("hidden");

        Side = body.Add("animation-side");
        Fields = Side.Add("animation-fields");

        // Below the fields rather than in a panel of its own: reading a proposal and looking at the
        // tag it would add are one action, and two panels would make them two places to look.
        Proposals = Side.Add("animation-proposals");

        CurveMode.CheckedChanged += (_, on) => ShowCurves(on);
        Duration.NumberChanged += (_, value) => document?.SetDuration((float) value);
        FrameRate.NumberChanged += (_, value) => document?.SetFrameRate((float) value);

        Wrap.SelectionChanged += (_, value) => {
            if (Enum.TryParse<WrapMode>(value, out var mode)) {
                document?.SetWrap(mode);
            }
        };

        Sheet.SelectionChanged += _ => Restate();
        Sheet.KeysMoved += _ => Commit();
        Sheet.SpanMoved += (_, span) => CommitSpan(span);

        // ⚠ A double-tap takes the bar off the track, and the tag has to go with it. Without this the
        // row would vanish and come straight back on the next reload, which reads as the editor
        // ignoring the gesture rather than as the edit not having happened.
        Sheet.SpanRemoved += (_, _, span) => {
            if (document is { } clip && span.Tag is ConstraintTagRecord tag) {
                clip.RemoveConstraint(tag);
            }
        };
        Curves.CurveChanged += _ => CommitCurve();
        Curves.SelectionChanged += _ => Restate();

        AddHandler<ClickEvent>(static (element, args) => ((AnimationClipView) element).Chosen(args));
    }

    /// <summary>Shows a clip.</summary>
    /// <param name="clip">The document.</param>
    public void Show(AnimationClipDocument clip) {
        ArgumentNullException.ThrowIfNull(clip);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = clip;
        clip.Changed += Reload;

        Reload(clip);
    }

    /// <summary>Rebuilds the sheet from the document.</summary>
    /// <param name="clip">The document.</param>
    /// <remarks>
    ///     ⚠ <b>Called on every edit, including the ones this view made.</b> An undo is an edit
    ///     nothing in this view initiated, and a sheet that only rebuilt on its own gestures would
    ///     show the keys it had before the undo — which reads as undo not working.
    /// </remarks>
    public void Reload(AnimationClipDocument clip) {
        ArgumentNullException.ThrowIfNull(clip);

        Duration.Number = clip.Clip.Duration;
        FrameRate.Number = clip.Clip.FrameRate;
        Wrap.Value = clip.Clip.Wrap.ToString();

        Sheet.Duration = clip.Clip.Duration;
        Sheet.FrameRate = clip.Clip.FrameRate;

        while (Sheet.Tracks.Count > 0) {
            Sheet.Remove(Sheet.Tracks[^1]);
        }

        foreach (var target in clip.Clip.Targets) {
            foreach (var curve in target.Curves) {
                var track = Sheet.AddTrack(
                    $"{target.Target} · {AnimationClipCurves.Label(curve.Property, curve.Shape)}"
                );

                track.Tag = new AnimationRow(target, curve.Property, curve.Shape);

                foreach (var key in curve.Keys) {
                    track.Add(key.Time, key);
                }
            }
        }

        // ⚠ Last, so the events are the bottom row whatever the clip drives. A track that moved as
        // objects were added would be one people lose, and it is the row they come back to.
        var events = Sheet.AddTrack("Events");
        events.Tag = EventRow;

        foreach (var entry in clip.Clip.Events) {
            events.Add(entry.Time, entry);
        }

        // ⚠ A bar with its ramps drawn on it, and not a pair of keys. The shape of the activation is
        // the thing an author is trying to see — a tag that never reaches full weight looks obviously
        // wrong as a triangle and looks like two numbers as a pair of numbers. The span's ends,
        // ramps and peak are the tag's own, so what is drawn is what the runtime applies.
        var length = MathF.Max(clip.Clip.Duration, AnimationClipDocument.MinimumDuration);

        foreach (var tag in clip.Clip.Constraints) {
            var track = Sheet.AddTrack(
                $"⟨{(tag.Name.Length > 0 ? tag.Name : tag.Effector)}⟩",
                TimelineTrackKind.Spans
            );

            track.Tag = tag;

            var span = track.AddSpan(tag.Begin * length, tag.End * length, tag);

            span.EaseIn = tag.EaseIn * length;
            span.EaseOut = tag.EaseOut * length;
            span.Peak = tag.MaxWeight;
        }

        Sheet.Refresh();

        if (row is not null && Find(row) is null) {
            row = null;
        }

        ShowCurves(CurveMode.IsChecked);
        ShowProposals();
        Restate();
    }

    /// <summary>The tag the event track carries.</summary>
    static AnimationRow EventRow { get; } = new(null, default);

    /// <summary>Swaps between the dope sheet and the curve editor.</summary>
    /// <param name="on">Whether the curve editor is showing.</param>
    public void ShowCurves(bool on) {
        Sheet.SetClass("hidden", on);
        Curves.SetClass("hidden", !on);

        if (!on) {
            return;
        }

        // ⚠ A copy, not a live view. `CurveEditor` mutates what it is given as the pointer moves, and
        // handing it the document's own curve would make a drag an edit nothing recorded — see
        // `AnimationClipCurves`' own note.
        Curves.Curve = row is { Target: { } target }
            && AnimationClipDocument.Curve(target, row.Property, row.Shape) is { } curve
            ? AnimationClipCurves.ToCurve(curve)
            : new AnimationCurve();

        Curves.Frame();
    }

    /// <summary>The track showing a row, or <see langword="null" />.</summary>
    TimelineTrack? Find(AnimationRow wanted) {
        foreach (var track in Sheet.Tracks) {
            if (track.Tag is AnimationRow tag
                && tag.Target == wanted.Target
                && tag.Property == wanted.Property
                && string.Equals(tag.Shape, wanted.Shape, StringComparison.Ordinal)) {
                return track;
            }
        }

        return null;
    }

    /// <summary>Writes a dragged bar's ends back onto its tag, as one edit.</summary>
    /// <remarks>
    ///     ⚠ <b>Back into fractions of the clip, which is what a tag stores.</b> A retimed clip keeps
    ///     its contacts where they were authored because the span is a fraction and not a number of
    ///     seconds — so a drag that wrote seconds would make every subsequent length change move the
    ///     contacts.
    /// </remarks>
    void CommitSpan(TimelineSpan span) {
        if (document is not { } clip || span.Tag is not ConstraintTagRecord tag) {
            return;
        }

        var length = MathF.Max(clip.Clip.Duration, AnimationClipDocument.MinimumDuration);
        clip.MoveConstraint(tag, span.Begin / length, span.End / length);
    }

    /// <summary>Writes the sheet's key times back into the document, as one edit per curve.</summary>
    void Commit() {
        if (document is not { } clip) {
            return;
        }

        foreach (var track in Sheet.Tracks) {
            if (track.Tag is not AnimationRow tag) {
                continue;
            }

            if (tag.Target is null) {
                foreach (var key in track.Keys) {
                    if (key.Tag is AnimationEventData entry && Math.Abs(entry.Time - key.Time) > 1e-6f) {
                        clip.MoveEvent(entry, key.Time);
                    }
                }

                continue;
            }

            // ⚠ Only the curves whose keys actually moved. Rewriting every curve on every drag would
            // put one undo entry per row on the stack for a gesture that touched one of them.
            var moved = track.Keys.Any(key => key.Tag is AnimationKeyData data && Math.Abs(data.Time - key.Time) > 1e-6f);

            if (!moved) {
                continue;
            }

            List<AnimationKeyData> keys = [];

            foreach (var key in track.Keys) {
                if (key.Tag is not AnimationKeyData data) {
                    continue;
                }

                keys.Add(new() {
                    Time = key.Time,
                    Value = data.Value,
                    InTangent = data.InTangent,
                    OutTangent = data.OutTangent,
                    Mode = data.Mode
                });
            }

            clip.SetCurve(tag.Target, tag.Property, keys, tag.Shape);
        }
    }

    /// <summary>Writes the curve editor's keys back into the document.</summary>
    void CommitCurve() {
        if (document is not { } clip || row is not { Target: { } target }) {
            return;
        }

        clip.SetCurve(
            target,
            row.Property,
            AnimationClipCurves.ToData(row.Property, Curves.Curve, row.Shape).Keys,
            row.Shape
        );
    }

    /// <summary>Rebuilds the list of what the proposal pass offered.</summary>
    /// <remarks>
    ///     ⚠ <b>Every row has its own Add and there is no "accept all".</b> The failure mode of
    ///     proximity heuristics is confident nonsense, and a button that took twenty of them at once
    ///     would be the button everybody presses. The confidence is shown because it is a product of
    ///     three things — near, still and long — so a low one usually means exactly one of them is bad.
    /// </remarks>
    void ShowProposals() {
        Proposals.Empty();

        if (document is not { } clip) {
            return;
        }

        if (clip.Proposals.Count == 0) {
            if (clip.ProposalError.Length > 0) {
                Proposals.Add("animation-title").Text = "Proposals";
                Proposals.Add("text").Text = clip.ProposalError;
            }

            return;
        }

        Proposals.Add("animation-title").Text = $"{clip.Proposals.Count} proposal(s)";

        foreach (var proposal in clip.Proposals) {
            var row = Proposals.Add("fact-row");

            row.Add("fact-name").Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{proposal.Tag.Effector} on {proposal.Shape} · {proposal.Closest * 100f:0.#} cm · {proposal.Confidence:0.00}"
            );

            var accept = row.Add("fact-value").Add<Button>();

            accept.Label = "Add";
            accept.Size = ControlSize.Small;

            var offered = proposal;

            accept.Clicked += _ => clip.Accept(offered);
        }
    }

    /// <summary>Rebuilds the fields for whatever is selected.</summary>
    public void Restate() {
        while (Fields.Children.Count > 0) {
            Fields.Children[^1].Remove();
        }

        if (document is not { } clip) {
            return;
        }

        Fields.Add("animation-title").Text = clip.Clip.Name;

        Fields.Add("fact-row").Add("fact-name").Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{clip.Clip.Targets.Count} object(s), {clip.KeyCount} key(s), {clip.Clip.Events.Count} event(s)"
        );

        if (clip.LoadError is { } error) {
            Fields.Add("animation-error").Text = error;
        }

        if (Sheet.SpanSelection.FirstOrDefault()?.Tag is ConstraintTagRecord constraint) {
            ConstraintFields(clip, constraint);
            return;
        }

        if (Sheet.Selection.FirstOrDefault() is not { } selected) {
            Fields.Add("text").Text = "Select a key, an event or a constraint.";

            return;
        }

        switch (selected.Tag) {
            case AnimationEventData entry:
                EventFields(clip, entry);
                break;

            case AnimationKeyData key:
                KeyFields(clip, key, selected);
                break;

            default:
                Fields.Add("text").Text = "Select a key, an event or a constraint.";
                break;
        }
    }

    void EventFields(AnimationClipDocument clip, AnimationEventData entry) {
        Fields.Add("animation-title").Text = "Event";

        Line("Name", entry.Name, value => Replace(clip, entry, value, entry.Float, entry.Int, entry.String));
        Number("Time", entry.Time, value => clip.MoveEvent(entry, value));
        Number("Float", entry.Float, value => Replace(clip, entry, entry.Name, value, entry.Int, entry.String));
        Number("Int", entry.Int, value => Replace(clip, entry, entry.Name, entry.Float, (int) value, entry.String));
        Line("String", entry.String, value => Replace(clip, entry, entry.Name, entry.Float, entry.Int, value));
    }

    /// <summary>Replacing an event's payload is a remove and an add, so it is one undo step.</summary>
    /// <remarks>
    ///     ⚠ <b>Rather than five separately undoable field writes.</b> An event is a small record and
    ///     nobody thinks of "renamed the footstep and changed its payload" as two edits — but the
    ///     stack has to be told, and a composite is how.
    /// </remarks>
    static void Replace(
        AnimationClipDocument clip,
        AnimationEventData entry,
        string name,
        float number,
        int integer,
        string text
    ) {
        var time = entry.Time;

        clip.RemoveEvent(entry);

        var added = clip.AddEvent(name.Length > 0 ? name : "Event", time);

        added.Float = number;
        added.Int = integer;
        added.String = text;
    }

    void KeyFields(AnimationClipDocument clip, AnimationKeyData key, TimelineKey selected) {
        if (Sheet.Tracks.FirstOrDefault(track => track.Keys.Contains(selected))?.Tag is not AnimationRow tag
            || tag.Target is not { } target) {
            return;
        }

        Fields.Add("animation-title").Text = AnimationClipCurves.Label(tag.Property, tag.Shape);

        Number("Time", key.Time, value => Rewrite(clip, target, tag, key, value, key.Value));
        Number("Value", key.Value, value => Rewrite(clip, target, tag, key, key.Time, value));

        var mode = Fields.Add("fact-row");
        mode.Add("fact-name").Text = "Tangents";

        var select = mode.Add("fact-value").Add<Select>();

        foreach (var option in Enum.GetValues<TangentMode>()) {
            select.AddOption(option.ToString());
        }

        select.Value = key.Mode.ToString();

        select.SelectionChanged += (_, value) => {
            if (!Enum.TryParse<TangentMode>(value, out var chosen)) {
                return;
            }

            key.Mode = chosen;
            Rewrite(clip, target, tag, key, key.Time, key.Value);
        };

        // Selecting a key in the sheet is what the curve editor follows, so switching to curve mode
        // lands on the row somebody was already looking at rather than on nothing.
        row = tag;
    }

    static void Rewrite(
        AnimationClipDocument clip,
        AnimationTargetData target,
        AnimationRow tag,
        AnimationKeyData key,
        float time,
        float value
    ) {
        if (AnimationClipDocument.Curve(target, tag.Property, tag.Shape) is not { } curve) {
            return;
        }

        List<AnimationKeyData> keys = [];

        foreach (var existing in curve.Keys) {
            keys.Add(
                ReferenceEquals(existing, key)
                    ? new() {
                        Time = time,
                        Value = value,
                        InTangent = key.InTangent,
                        OutTangent = key.OutTangent,
                        Mode = key.Mode
                    }
                    : existing
            );
        }

        clip.SetCurve(target, tag.Property, keys, tag.Shape);
    }

    /// <summary>The panel for one constraint, built from the schema rather than written per kind.</summary>
    /// <remarks>
    ///     ⚠ <b>A position goal has no aim axis and this panel does not show one.</b> Which fields a
    ///     kind has is <c>GoalKindSchema</c>'s answer and not this method's, so a field added to the
    ///     record appears here as soon as the schema names it — and a field the schema names with no
    ///     accessor fails a test rather than appearing dead.
    /// </remarks>
    void ConstraintFields(AnimationClipDocument clip, ConstraintTagRecord tag) {
        Fields.Add("animation-title").Text = $"{tag.Kind} constraint";

        foreach (var field in GoalKindSchema.Common) {
            Field(clip, tag, field);
        }

        Fields.Add("fact-row").Add("fact-name").Text = $"— {tag.Kind} —";

        foreach (var field in GoalKindSchema.For(tag.Kind)) {
            Field(clip, tag, field);
        }

        if (tag.Template.Length > 0) {
            Fields.Add("text").Text =
                $"From the '{tag.Template}' template, revision {tag.TemplateVersion}. Editing it here is a change a "
                + "re-apply would overwrite.";
        }
    }

    void Field(AnimationClipDocument clip, ConstraintTagRecord tag, GoalField field) {
        if (!ConstraintFieldAccess.TryGet(field.Property, out var accessor)) {
            return;
        }

        var line = Fields.Add("fact-row");

        line.Add("fact-name").Text = field.Advanced ? $"{field.Label} ·" : field.Label;

        var box = line.Add("fact-value").Add<TextBox>();

        box.Value = accessor.Read(tag);
        box.ValueChanged += (control, text) => {
            if (!accessor.Write(clip, tag, field, text ?? string.Empty)) {
                // Rejected rather than swallowed: a number that did not parse leaves the field showing
                // what the tag still says, which is the only honest thing to show.
                ((TextBox) control).Value = accessor.Read(tag);
            }
        };
    }

    void Number(string label, float value, Action<float> write) {
        var line = Fields.Add("fact-row");
        line.Add("fact-name").Text = label;

        var box = line.Add("fact-value").Add<NumericInput>();

        box.Decimals = 3;
        box.Number = value;
        box.NumberChanged += (_, number) => write((float) number);
    }

    void Line(string label, string value, Action<string> write) {
        var line = Fields.Add("fact-row");
        line.Add("fact-name").Text = label;

        var box = line.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    void Chosen(ClickEvent args) {
        if (document is not { } clip) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, Constraint)) {
                clip.AddConstraint(
                    new() {
                        Name = "constraint",
                        Kind = GoalKind.Position,
                        Effector = clip.Clip.Targets.Count > 0 ? clip.Clip.Targets[0].Target : string.Empty,
                        Begin = 0f,
                        End = 1f
                    }
                );

                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Propose)) {
                clip.Propose();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Key)) {
                AddKey(clip);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Event)) {
                clip.AddEvent("Event", Sheet.Time);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Delete)) {
                DeleteSelection(clip);
                args.Handled = true;

                return;
            }
        }
    }

    /// <summary>Puts a key on the selected row at the playhead, holding whatever it says there.</summary>
    /// <remarks>
    ///     ⚠ <b>The value is the curve's value at that time, not zero.</b> Keying a curve mid-motion
    ///     to hold it is the ordinary reason to press this, and a key that dropped the value to zero
    ///     would be one nobody ever wants and everybody would have to fix by hand.
    /// </remarks>
    void AddKey(AnimationClipDocument clip) {
        if (row is not { Target: { } target } current) {
            return;
        }

        clip.AddKey(
            target,
            current.Property,
            Sheet.Snap(Sheet.Time),
            AnimationClipDocument.Evaluate(target, current.Property, Sheet.Time, current.Shape),
            current.Shape
        );
    }

    void DeleteSelection(AnimationClipDocument clip) {
        foreach (var selected in Sheet.Selection.ToList()) {
            switch (selected.Tag) {
                case AnimationEventData entry:
                    clip.RemoveEvent(entry);
                    break;

                case AnimationKeyData key when Sheet.Tracks.FirstOrDefault(track => track.Keys.Contains(selected))
                    ?.Tag is AnimationRow { Target: { } target } tag:
                    clip.SetCurve(
                        target,
                        tag.Property,
                        [.. AnimationClipDocument.Curve(target, tag.Property, tag.Shape)
                            ?.Keys.Where(entry => !ReferenceEquals(entry, key)) ?? []],
                        tag.Shape
                    );

                    break;

                default:
                    break;
            }
        }

        foreach (var span in Sheet.SpanSelection.ToList()) {
            if (span.Tag is ConstraintTagRecord tag) {
                clip.RemoveConstraint(tag);
            }
        }
    }
}

/// <summary>Opens an authored animation clip.</summary>
public sealed class AnimationClipEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Animation Clip";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [AnimationClipDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new AnimationClipDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // A `Timeline` and a `CurveEditor`, both of which scroll and pan themselves — see
        // `SequenceView.CreateView`.
        DockPanel.Fills(panel);

        var view = panel.Add<AnimationClipView>();
        view.Show((AnimationClipDocument) document);

        return view;
    }
}
