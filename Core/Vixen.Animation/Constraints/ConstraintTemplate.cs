// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>What re-applying a template would do to one tag.</summary>
public enum TemplateChangeKind : byte {
    /// <summary>A tag the template adds that the clip does not have.</summary>
    Added,

    /// <summary>A tag the clip has from this template that the template no longer produces.</summary>
    Removed,

    /// <summary>A tag both have, whose fields differ.</summary>
    Changed,

    /// <summary>A tag both have and agree about.</summary>
    Kept,

    /// <summary>
    ///     A tag from this template that somebody has since edited by hand, which a re-apply would
    ///     overwrite.
    /// </summary>
    Edited
}

/// <summary>One line of what a re-apply would do.</summary>
/// <param name="Kind">What would happen to it.</param>
/// <param name="Tag">Which tag, by name.</param>
/// <param name="Detail">What differs, for a person reading the list.</param>
public readonly record struct TemplateChange(TemplateChangeKind Kind, string Tag, string Detail) {
    /// <inheritdoc />
    public override string ToString() => Detail.Length == 0 ? $"{Kind}: {Tag}" : $"{Kind}: {Tag} — {Detail}";
}

/// <summary>What re-applying a template would do to a clip, before it does it.</summary>
/// <param name="Changes">Every tag it would touch.</param>
/// <remarks>
///     ⚠ <b>The diff is the feature, not the apply.</b> A template that silently rewrote twenty tags
///     across forty clips is a template nobody dares re-save, so what an author gets is a list they
///     can read and refuse. <see cref="Edited" /> is the line that matters: a tag the template made and
///     a person then adjusted is work a re-apply would destroy, and it is called out separately from an
///     ordinary change for exactly that reason.
/// </remarks>
public sealed record TemplateDiff(IReadOnlyList<TemplateChange> Changes) {
    /// <summary>How many tags it would add.</summary>
    public int Added => Count(TemplateChangeKind.Added);

    /// <summary>How many it would remove.</summary>
    public int Removed => Count(TemplateChangeKind.Removed);

    /// <summary>How many it would rewrite.</summary>
    public int Changed => Count(TemplateChangeKind.Changed);

    /// <summary>How many hand edits it would destroy.</summary>
    public int Edited => Count(TemplateChangeKind.Edited);

    /// <summary>Whether it would do nothing at all.</summary>
    public bool IsEmpty => Added == 0 && Removed == 0 && Changed == 0 && Edited == 0;

    int Count(TemplateChangeKind kind) {
        var found = 0;

        foreach (var change in Changes) {
            if (change.Kind == kind) {
                found++;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty
            ? "Nothing would change."
            : $"{Added} added, {Removed} removed, {Changed} rewritten, {Edited} hand edits overwritten.";
}

/// <summary>A named, versioned bundle of tags with relative timings.</summary>
/// <remarks>
///     <para>
///         <b>A seated interaction is twenty constraints and nobody authors twenty constraints
///         repeatedly.</b> This is the thing that makes the authoring cost bearable, and
///         <see cref="Revision" /> is what makes it maintainable: a template that improves can be
///         pushed back across every clip that used it, with a diff first.
///     </para>
///     <para>
///         ⚠ <b>Timings are relative to the template's own span, in <c>[0, 1]</c>.</b> A template
///         applied over the last three seconds of a ten-second clip has to place its tags inside
///         those three seconds, and a template that stored absolute phases would only ever fit the
///         clip it was captured from.
///     </para>
/// </remarks>
[DataContract("ConstraintTemplateContent")]
public sealed class ConstraintTemplateContent {
    /// <summary>The version this build writes.</summary>
    public const int Current = 1;

    /// <summary>The file extension.</summary>
    public const string Extension = ".vxconstraints";

    /// <summary>Which version of the format wrote it.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the template is called. Written into every tag it produces.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The template's own version, bumped whenever it is re-saved.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>What it is for, in a sentence an author picking from a list can read.</summary>
    public string Meaning { get; set; } = string.Empty;

    /// <summary>The tags, with <c>Begin</c> and <c>End</c> relative to the template's own span.</summary>
    public List<ConstraintTagRecord> Tags { get; set; } = [];

    /// <summary>Markup this build did not interpret.</summary>
    public Dictionary<string, string> Extensions { get; set; } = [];

    /// <summary>The tags this template would place over a span of a clip.</summary>
    /// <param name="begin">Where the template starts in the clip, in <c>[0, 1]</c>.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>The tags, stamped with this template's name and revision.</returns>
    public IReadOnlyList<ConstraintTagRecord> Instantiate(float begin = 0f, float end = 1f) {
        var from = MathUtil.Saturate(MathF.Min(begin, end));
        var span = MathUtil.Saturate(MathF.Max(begin, end)) - from;

        List<ConstraintTagRecord> built = [];

        foreach (var tag in Tags) {
            var copy = Copy(tag);

            copy.Begin = from + (tag.Begin * span);
            copy.End = from + (tag.End * span);
            copy.EaseIn = tag.EaseIn * span;
            copy.EaseOut = tag.EaseOut * span;
            copy.Template = Name;
            copy.TemplateVersion = Revision;

            built.Add(copy);
        }

        return built;
    }

    /// <summary>What re-applying this template to a clip's tags would do.</summary>
    /// <param name="existing">The clip's tags.</param>
    /// <param name="begin">Where the template starts in the clip.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>The diff.</returns>
    /// <remarks>
    ///     ⚠ <b>Only the tags this template produced are considered.</b> A hand-placed tag is nobody
    ///     else's business, and a re-apply that removed one because the template does not produce it
    ///     would delete the author's own work on the grounds that a template did not predict it.
    /// </remarks>
    public TemplateDiff Compare(IReadOnlyList<ConstraintTagRecord> existing, float begin = 0f, float end = 1f) {
        ArgumentNullException.ThrowIfNull(existing);

        var wanted = Instantiate(begin, end);

        List<TemplateChange> changes = [];
        HashSet<string> matched = new(StringComparer.Ordinal);

        foreach (var tag in wanted) {
            var found = Find(existing, tag.Name);

            if (found is null) {
                changes.Add(new(TemplateChangeKind.Added, tag.Name, $"a {tag.Kind} goal on {tag.Effector}"));
                continue;
            }

            matched.Add(tag.Name);

            var difference = Difference(found, tag);

            if (difference.Length == 0) {
                changes.Add(new(TemplateChangeKind.Kept, tag.Name, string.Empty));
                continue;
            }

            // A tag stamped with an older revision differs because the template moved on; one stamped
            // with *this* revision differs because a person changed it, and those are not the same
            // news.
            var kind = found.TemplateVersion == Revision ? TemplateChangeKind.Edited : TemplateChangeKind.Changed;

            changes.Add(new(kind, tag.Name, difference));
        }

        foreach (var tag in existing) {
            if (string.Equals(tag.Template, Name, StringComparison.Ordinal) && !matched.Contains(tag.Name)) {
                changes.Add(new(TemplateChangeKind.Removed, tag.Name, "this template no longer produces it"));
            }
        }

        return new(changes);
    }

    ConstraintTagRecord? Find(IReadOnlyList<ConstraintTagRecord> existing, string name) {
        foreach (var tag in existing) {
            if (string.Equals(tag.Template, Name, StringComparison.Ordinal)
                && string.Equals(tag.Name, name, StringComparison.Ordinal)) {
                return tag;
            }
        }

        return null;
    }

    /// <summary>What differs between two tags, in words rather than as a bool.</summary>
    static string Difference(ConstraintTagRecord have, ConstraintTagRecord want) {
        List<string> differs = [];

        if (have.Kind != want.Kind) {
            differs.Add($"kind {have.Kind} → {want.Kind}");
        }

        if (!string.Equals(have.Effector, want.Effector, StringComparison.Ordinal)) {
            differs.Add($"effector {have.Effector} → {want.Effector}");
        }

        if (MathF.Abs(have.Begin - want.Begin) > 1e-4f || MathF.Abs(have.End - want.End) > 1e-4f) {
            differs.Add($"span {have.Begin:0.###}–{have.End:0.###} → {want.Begin:0.###}–{want.End:0.###}");
        }

        if (MathF.Abs(have.MaxWeight - want.MaxWeight) > 1e-4f) {
            differs.Add($"weight {have.MaxWeight:0.##} → {want.MaxWeight:0.##}");
        }

        if (!string.Equals(have.Priority, want.Priority, StringComparison.Ordinal)) {
            differs.Add($"priority {have.Priority} → {want.Priority}");
        }

        if (have.Goal.Kind != want.Goal.Kind) {
            differs.Add($"goal {have.Goal.Kind} → {want.Goal.Kind}");
        }

        if (have.Offset != want.Offset || have.Region != want.Region) {
            differs.Add("offset or region");
        }

        return string.Join(", ", differs);
    }

    /// <summary>A field-for-field copy, so a template's own tags are never handed out.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a shared reference.</b> Two clips instantiated from one template would otherwise
    ///     hold the same objects, and editing a tag on one would edit it on the other and on the
    ///     template — which is the sort of bug that is discovered a week later in a file nobody opened.
    /// </remarks>
    public static ConstraintTagRecord Copy(ConstraintTagRecord tag) {
        ArgumentNullException.ThrowIfNull(tag);

        return new() {
            Name = tag.Name,
            Kind = tag.Kind,
            Mode = tag.Mode,
            Effector = tag.Effector,
            Chain = tag.Chain,
            Begin = tag.Begin,
            End = tag.End,
            EaseIn = tag.EaseIn,
            EaseOut = tag.EaseOut,
            MaxWeight = tag.MaxWeight,
            Priority = tag.Priority,
            Label = tag.Label,
            LodMin = tag.LodMin,
            LodMax = tag.LodMax,
            Goal = Copy(tag.Goal),
            Reference = tag.Reference is null ? null : Copy(tag.Reference),
            Offset = tag.Offset,
            EffectorOffset = tag.EffectorOffset,
            Region = tag.Region,
            Pole = tag.Pole,
            Rotation = tag.Rotation,
            Tolerance = tag.Tolerance,
            Axis = tag.Axis,
            Origin = tag.Origin,
            Deviation = tag.Deviation,
            AuthoredDistance = tag.AuthoredDistance,
            Other = tag.Other,
            Min = tag.Min,
            Max = tag.Max,
            Template = tag.Template,
            TemplateVersion = tag.TemplateVersion
        };
    }

    /// <summary>A field-for-field copy of a frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The copy.</returns>
    public static ConstraintFrameRecord Copy(ConstraintFrameRecord frame) {
        ArgumentNullException.ThrowIfNull(frame);

        return new() {
            Kind = frame.Kind,
            Slot = frame.Slot,
            Socket = frame.Socket,
            Joint = frame.Joint,
            Name = frame.Name,
            Shape = frame.Shape,
            Tag = frame.Tag,
            Position = frame.Position,
            Rotation = frame.Rotation,
            Origin = frame.Origin,
            Face = frame.Face,
            U = frame.U,
            V = frame.V,
            Direction = frame.Direction,
            LimbFrom = frame.LimbFrom,
            LimbTo = frame.LimbTo,
            Along = frame.Along,
            Residual = frame.Residual,
            Orientation = frame.Orientation,
            Scale = frame.Scale
        };
    }
}
