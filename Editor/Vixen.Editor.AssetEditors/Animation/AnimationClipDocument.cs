// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core.Curves;
using Vixen.Core.Yaml;
using Vixen.Core;
using Vixen.Editor.Assets.Animation;
using Vixen.Editor.Core;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A hand-authored animation clip, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20 files this row as "the format does not exist", and this is the format plus the
///         two operations an editor needs on top of it.</b> Everything a person does to a clip —
///         adding a curve, moving a key, adding an event, changing the duration — has to be one
///         entry on the undo stack, and a document that let a view mutate the asset directly would
///         be the bug <see cref="IEditorCommand" /> exists to make obvious.
///     </para>
///     <para>
///         ⚠ <b>Every edit is expressed as replacing a curve, not as mutating one.</b> A curve is a
///         list of keys with tangents and a drag changes several of them at once; a command per key
///         would make undoing a box-select of twenty keys twenty presses. Replacing the whole curve
///         is one command whose undo is the previous list, and the lists are small — a hand-animated
///         curve is tens of keys, not thousands.
///     </para>
///     <para>
///         ⚠ <b>A file this build cannot read throws from <see cref="AnimationClipAsset.FromYaml" />
///         and is caught here.</b> The document opens empty with the failure in
///         <see cref="LoadError" />, for <c>CompositorDocument</c>'s reason: a panel that could show
///         the problem has to be reachable.
///     </para>
/// </remarks>
public sealed class AnimationClipDocument : EditorDocument {
    /// <summary>What an authored clip is written as.</summary>
    public const string Extension = AnimationClipAsset.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The clip.</summary>
    public AnimationClipAsset Clip { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>Raised after anything changes the clip.</summary>
    public event Action<AnimationClipDocument>? Changed;

    /// <summary>Opens a clip.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public AnimationClipDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            Clip = AnimationClipAsset.FromYaml(AssetFile.Read(path));
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Clip = new();
            LoadError = exception.Message;
        }

        if (Clip.Name.Length == 0) {
            Clip.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>The target of a given name, or <see langword="null" />.</summary>
    /// <param name="target">The joint or node name.</param>
    /// <returns>The target.</returns>
    public AnimationTargetData? Target(string target) =>
        Clip.Targets.Find(entry => string.Equals(entry.Target, target, StringComparison.Ordinal));

    /// <summary>Adds a target, undoably.</summary>
    /// <param name="target">What it drives, by name.</param>
    /// <returns>Whether it was added; a name already present is refused.</returns>
    public bool AddTarget(string target) {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (Target(target) is not null) {
            return false;
        }

        var added = new AnimationTargetData { Target = target };

        Run(
            "Add Animated Object",
            () => Clip.Targets.Add(added),
            () => Clip.Targets.Remove(added)
        );

        return true;
    }

    /// <summary>Removes a target and everything it carries, undoably.</summary>
    /// <param name="target">The target.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveTarget(AnimationTargetData target) {
        ArgumentNullException.ThrowIfNull(target);

        var index = Clip.Targets.IndexOf(target);

        if (index < 0) {
            return false;
        }

        // ⚠ Put back at the index it left rather than appended. The dope sheet's row order is the
        // list's order, so an undo that appended would move the row somebody was working in.
        Run(
            "Remove Animated Object",
            () => Clip.Targets.RemoveAt(index),
            () => Clip.Targets.Insert(index, target)
        );

        return true;
    }

    /// <summary>The curve driving one property of one target, or <see langword="null" />.</summary>
    /// <param name="target">The target.</param>
    /// <param name="property">Which number.</param>
    /// <returns>The curve.</returns>
    public static AnimationCurveData? Curve(AnimationTargetData target, AnimationProperty property) {
        ArgumentNullException.ThrowIfNull(target);

        return target.Curves.Find(curve => curve.Property == property);
    }

    /// <summary>Replaces one curve's keys, undoably.</summary>
    /// <param name="target">The target it belongs to.</param>
    /// <param name="property">Which number it drives.</param>
    /// <param name="keys">Its new keys.</param>
    /// <remarks>
    ///     ⚠ <b>An empty list removes the curve rather than leaving one with no keys.</b> The two are
    ///     different things downstream — see <see cref="AnimationClipAsset.ToClipData" />'s note about
    ///     a group with no curves — and an editor that left empty curves behind would turn "delete
    ///     every key" into "hold this joint at its rest pose", which is the opposite instruction.
    /// </remarks>
    public void SetCurve(AnimationTargetData target, AnimationProperty property, IReadOnlyList<AnimationKeyData> keys) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keys);

        var previous = Curve(target, property);
        var index = previous is null ? target.Curves.Count : target.Curves.IndexOf(previous);

        var replacement = keys.Count == 0
            ? null
            : new AnimationCurveData {
                Property = property,
                Keys = [.. keys.OrderBy(key => key.Time)]
            };

        Run(
            "Edit Curve",
            () => {
                if (previous is not null) {
                    target.Curves.Remove(previous);
                }

                if (replacement is not null) {
                    target.Curves.Insert(Math.Min(index, target.Curves.Count), replacement);
                }
            },
            () => {
                if (replacement is not null) {
                    target.Curves.Remove(replacement);
                }

                if (previous is not null) {
                    target.Curves.Insert(Math.Min(index, target.Curves.Count), previous);
                }
            }
        );
    }

    /// <summary>Adds one key to a curve, making the curve if it is not there, undoably.</summary>
    /// <param name="target">The target.</param>
    /// <param name="property">Which number.</param>
    /// <param name="time">When.</param>
    /// <param name="value">What.</param>
    public void AddKey(AnimationTargetData target, AnimationProperty property, float time, float value) {
        ArgumentNullException.ThrowIfNull(target);

        List<AnimationKeyData> keys = Curve(target, property) is { } curve
            ? [.. curve.Keys.Where(key => Math.Abs(key.Time - time) > KeyEpsilon).Select(Copy)]
            : [];

        keys.Add(new() { Time = time, Value = value });
        SetCurve(target, property, keys);
    }

    /// <summary>How close two keys have to be before one replaces the other, in seconds.</summary>
    /// <remarks>
    ///     ⚠ <b>Half a frame at 240 Hz.</b> Two keys at the same time are a curve with no defined
    ///     value there, and a key set by clicking a timeline lands on a float that will not compare
    ///     equal to the one already there — so "at the same time" has to mean "within a tolerance no
    ///     frame rate anybody uses can fall inside".
    /// </remarks>
    public const float KeyEpsilon = 1f / 480f;

    static AnimationKeyData Copy(AnimationKeyData key) => new() {
        Time = key.Time,
        Value = key.Value,
        InTangent = key.InTangent,
        OutTangent = key.OutTangent,
        Mode = key.Mode
    };

    /// <summary>Adds an event, undoably.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="time">When it fires.</param>
    /// <returns>The event.</returns>
    public AnimationEventData AddEvent(string name, float time) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var added = new AnimationEventData { Name = name, Time = time };

        Run("Add Animation Event", () => Clip.Events.Add(added), () => Clip.Events.Remove(added));

        return added;
    }

    /// <summary>Removes an event, undoably.</summary>
    /// <param name="entry">The event.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveEvent(AnimationEventData entry) {
        ArgumentNullException.ThrowIfNull(entry);

        var index = Clip.Events.IndexOf(entry);

        if (index < 0) {
            return false;
        }

        Run("Remove Animation Event", () => Clip.Events.RemoveAt(index), () => Clip.Events.Insert(index, entry));

        return true;
    }

    /// <summary>Moves an event, undoably.</summary>
    /// <param name="entry">The event.</param>
    /// <param name="time">Where to.</param>
    public void MoveEvent(AnimationEventData entry, float time) {
        ArgumentNullException.ThrowIfNull(entry);

        var previous = entry.Time;

        Run("Move Animation Event", () => entry.Time = time, () => entry.Time = previous);
    }

    // ------------------------------------------------------------------ constraints

    /// <summary>Adds a constraint, undoably.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The tag, so a caller can select it.</returns>
    public ConstraintTagRecord AddConstraint(ConstraintTagRecord tag) {
        ArgumentNullException.ThrowIfNull(tag);

        Run("Add Constraint", () => Clip.Constraints.Add(tag), () => Clip.Constraints.Remove(tag));

        return tag;
    }

    /// <summary>Removes a constraint, undoably.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveConstraint(ConstraintTagRecord tag) {
        ArgumentNullException.ThrowIfNull(tag);

        var index = Clip.Constraints.IndexOf(tag);

        if (index < 0) {
            return false;
        }

        // Put back where it left, for `RemoveTarget`'s reason: the track's row order is the list's
        // order and an undo that appended would move the bar somebody was working on.
        Run(
            "Remove Constraint",
            () => Clip.Constraints.RemoveAt(index),
            () => Clip.Constraints.Insert(index, tag)
        );

        return true;
    }

    /// <summary>Drags a constraint's ends, undoably.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="begin">Where it now starts, in <c>[0, 1]</c>.</param>
    /// <param name="end">Where it now ends.</param>
    /// <remarks>
    ///     ⚠ <b>The ends are not sorted.</b> An end before a begin is a span that straddles the loop
    ///     point, which is the ordinary case for a foot that plants near the end of a cycle and lifts
    ///     near the start of the next — so a drag that crossed over would be silently turned into a
    ///     different constraint by an editor trying to be helpful.
    /// </remarks>
    public void MoveConstraint(ConstraintTagRecord tag, float begin, float end) {
        ArgumentNullException.ThrowIfNull(tag);

        var wasBegin = tag.Begin;
        var wasEnd = tag.End;
        var toBegin = Math.Clamp(begin, 0f, 1f);
        var toEnd = Math.Clamp(end, 0f, 1f);

        if (Math.Abs(toBegin - wasBegin) < 1e-6f && Math.Abs(toEnd - wasEnd) < 1e-6f) {
            return;
        }

        Run(
            "Move Constraint",
            () => {
                tag.Begin = toBegin;
                tag.End = toEnd;
            },
            () => {
                tag.Begin = wasBegin;
                tag.End = wasEnd;
            }
        );
    }

    /// <summary>Changes one field of a constraint, undoably.</summary>
    /// <typeparam name="T">What the field holds.</typeparam>
    /// <param name="tag">The tag.</param>
    /// <param name="label">What the undo entry is called.</param>
    /// <param name="read">How to read the field.</param>
    /// <param name="write">How to write it.</param>
    /// <param name="value">Its new value.</param>
    /// <remarks>
    ///     ⚠ <b>One generic command rather than one per field, because the panel is generated.</b>
    ///     <c>GoalKindSchema</c> decides which fields a kind shows, so a document with a method per
    ///     field would have to grow one every time the schema does — and the field that was forgotten
    ///     would be the one that silently stopped being undoable.
    /// </remarks>
    public void SetConstraintField<T>(
        ConstraintTagRecord tag,
        string label,
        Func<ConstraintTagRecord, T> read,
        Action<ConstraintTagRecord, T> write,
        T value
    ) {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var previous = read(tag);

        if (EqualityComparer<T>.Default.Equals(previous, value)) {
            return;
        }

        Run(label, () => write(tag, value), () => write(tag, previous));
    }

    /// <summary>Applies a template over a span of the clip, undoably.</summary>
    /// <param name="template">The template.</param>
    /// <param name="begin">Where it starts, in <c>[0, 1]</c>.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>The tags it added.</returns>
    public IReadOnlyList<ConstraintTagRecord> ApplyTemplate(
        ConstraintTemplateContent template,
        float begin = 0f,
        float end = 1f
    ) {
        ArgumentNullException.ThrowIfNull(template);

        var added = template.Instantiate(begin, end);

        // One entry for twenty tags. A seated interaction applied as twenty undo steps is twenty
        // presses to take back one decision.
        Run(
            $"Apply {template.Name}",
            () => Clip.Constraints.AddRange(added),
            () => {
                foreach (var tag in added) {
                    Clip.Constraints.Remove(tag);
                }
            }
        );

        return added;
    }

    /// <summary>What re-applying a template would do, without doing it.</summary>
    /// <param name="template">The template.</param>
    /// <param name="begin">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>The diff.</returns>
    public TemplateDiff CompareTemplate(ConstraintTemplateContent template, float begin = 0f, float end = 1f) {
        ArgumentNullException.ThrowIfNull(template);
        return template.Compare(Clip.Constraints, begin, end);
    }

    /// <summary>Re-applies a template, replacing the tags it produced and leaving the rest, undoably.</summary>
    /// <param name="template">The template.</param>
    /// <param name="begin">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>What it did.</returns>
    /// <remarks>
    ///     ⚠ <b>Hand-placed tags are not touched, and neither is a tag from another template.</b> A
    ///     re-apply that removed a tag because this template does not produce it would delete the
    ///     author's own work on the grounds that a template did not predict it. Call
    ///     <see cref="CompareTemplate" /> first and show it to somebody: what this destroys is the
    ///     edits made to tags this template made, and that is a decision, not a side effect.
    /// </remarks>
    public TemplateDiff ReapplyTemplate(ConstraintTemplateContent template, float begin = 0f, float end = 1f) {
        ArgumentNullException.ThrowIfNull(template);

        var diff = template.Compare(Clip.Constraints, begin, end);

        if (diff.IsEmpty) {
            return diff;
        }

        var before = new List<ConstraintTagRecord>(Clip.Constraints);
        var after = new List<ConstraintTagRecord>();

        foreach (var tag in Clip.Constraints) {
            if (!string.Equals(tag.Template, template.Name, StringComparison.Ordinal)) {
                after.Add(tag);
            }
        }

        after.AddRange(template.Instantiate(begin, end));

        Run(
            $"Re-apply {template.Name}",
            () => {
                Clip.Constraints.Clear();
                Clip.Constraints.AddRange(after);
            },
            () => {
                Clip.Constraints.Clear();
                Clip.Constraints.AddRange(before);
            }
        );

        return diff;
    }

    /// <summary>Which constraints are live at a moment in the clip.</summary>
    /// <param name="phase">Where in the clip, in <c>[0, 1]</c>.</param>
    /// <param name="into">Where the live ones go, with their activation.</param>
    /// <remarks>What the track draws, and what the viewport draws the goals of.</remarks>
    public void ConstraintsAt(float phase, ICollection<(ConstraintTagRecord Tag, float Weight)> into) {
        ArgumentNullException.ThrowIfNull(into);

        foreach (var tag in Clip.Constraints) {
            var activation = Activation(tag, phase);

            if (activation > 0f) {
                into.Add((tag, activation));
            }
        }
    }

    /// <summary>How much of a tag is live at a phase, matching what the runtime will do.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="phase">Where in the clip.</param>
    /// <returns>The activation, in <c>[0, 1]</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Deferred to <see cref="ConstraintTag" /> rather than re-implemented.</b> The bar on the
    ///     track draws the shape of the activation, and a track that drew a different ramp from the one
    ///     the game plays is worse than a track with no ramp drawn at all.
    /// </remarks>
    public static float Activation(ConstraintTagRecord tag, float phase) {
        ArgumentNullException.ThrowIfNull(tag);

        return new ConstraintTag {
            Goal = new PositionGoal { Effector = 0 },
            Begin = tag.Begin,
            End = tag.End,
            EaseIn = tag.EaseIn,
            EaseOut = tag.EaseOut,
            MaxWeight = tag.MaxWeight
        }.Activation(phase);
    }

    /// <summary>Changes how long the clip is, undoably.</summary>
    /// <param name="duration">The new length, in seconds.</param>
    /// <remarks>
    ///     ⚠ <b>Keys past the new end are kept.</b> Shortening a clip to try something and lengthening
    ///     it again is an ordinary thing to do, and a shortening that deleted keys would make the
    ///     second half of that a retype. What a key past the end means is that it is not reached,
    ///     which is exactly what the editor draws.
    /// </remarks>
    public void SetDuration(float duration) {
        var clamped = Math.Max(duration, MinimumDuration);
        var previous = Clip.Duration;

        if (Math.Abs(clamped - previous) < 1e-6f) {
            return;
        }

        Run("Set Clip Length", () => Clip.Duration = clamped, () => Clip.Duration = previous);
    }

    /// <summary>The shortest a clip may be, in seconds.</summary>
    /// <remarks>A clip of zero length is one whose normalised time is a division by zero.</remarks>
    public const float MinimumDuration = 1f / 240f;

    /// <summary>Changes what the timeline snaps to, undoably.</summary>
    /// <param name="rate">Frames a second.</param>
    public void SetFrameRate(float rate) {
        var clamped = Math.Clamp(rate, 1f, 240f);
        var previous = Clip.FrameRate;

        if (Math.Abs(clamped - previous) < 1e-6f) {
            return;
        }

        Run("Set Frame Rate", () => Clip.FrameRate = clamped, () => Clip.FrameRate = previous);
    }

    /// <summary>Changes what happens at the end, undoably.</summary>
    /// <param name="wrap">The mode.</param>
    public void SetWrap(WrapMode wrap) {
        var previous = Clip.Wrap;

        if (wrap == previous) {
            return;
        }

        Run("Set Wrap Mode", () => Clip.Wrap = wrap, () => Clip.Wrap = previous);
    }

    /// <summary>What one property is worth at a time, or its rest value.</summary>
    /// <param name="target">The target.</param>
    /// <param name="property">Which number.</param>
    /// <param name="time">When.</param>
    /// <returns>The value.</returns>
    public static float Evaluate(AnimationTargetData target, AnimationProperty property, float time) {
        ArgumentNullException.ThrowIfNull(target);

        return Curve(target, property) is { Keys.Count: > 0 } curve
            ? AnimationClipCurves.ToCurve(curve).Evaluate(time)
            : AnimationClipCurves.Rest(property);
    }

    /// <summary>How many keys the clip carries, across every curve.</summary>
    public int KeyCount => Clip.Targets.Sum(target => target.Curves.Sum(curve => curve.Keys.Count));

    /// <summary>Records an edit on this document's stack and tells whoever is drawing it.</summary>
    void Run(string name, Action apply, Action revert) {
        Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    apply();
                    Changed?.Invoke(this);
                },
                _ => {
                    revert();
                    Changed?.Invoke(this);
                }
            )
        );
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, Clip.ToYaml());
}
