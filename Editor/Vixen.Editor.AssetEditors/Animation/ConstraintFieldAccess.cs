// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>How the generated panel reads and writes one field of a tag.</summary>
/// <param name="Read">What it says now, as text the panel can show.</param>
/// <param name="Write">
///     Sets it from that text, undoably, and answers whether the text parsed at all.
/// </param>
public readonly record struct ConstraintFieldAccessor(
    Func<ConstraintTagRecord, string> Read,
    Func<AnimationClipDocument, ConstraintTagRecord, GoalField, string, bool> Write
);

/// <summary>The bridge between <see cref="GoalKindSchema" /> and the record it describes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An explicit table rather than reflection, and a test asserts it covers the schema.</b>
///         Reflection would be shorter and would be metadata a trimmed publish has already deleted —
///         the same argument <c>BuiltInImporters</c> makes about discovering importers by scanning. The
///         cost is that a field added to the schema and forgotten here is a field the panel cannot
///         write, so the test that walks the schema and demands an accessor is not optional.
///     </para>
///     <para>
///         <b>Text in and text out</b>, because the panel is generated: a field's control is chosen
///         from <see cref="GoalFieldKind" />, and every control this editor has reads and writes a
///         string. Parsing per kind here keeps the format of a vector in one place rather than in each
///         of the four panels that show one.
///     </para>
/// </remarks>
public static class ConstraintFieldAccess {
    static readonly Dictionary<string, ConstraintFieldAccessor> Accessors = Build();

    /// <summary>How to read and write a field, if the panel knows how.</summary>
    /// <param name="property">The property's name.</param>
    /// <param name="accessor">How.</param>
    /// <returns>Whether it is known.</returns>
    public static bool TryGet(string property, out ConstraintFieldAccessor accessor) =>
        Accessors.TryGetValue(property, out accessor);

    /// <summary>Every property this table can drive.</summary>
    /// <returns>The names.</returns>
    public static IReadOnlyCollection<string> Properties => Accessors.Keys;

    static Dictionary<string, ConstraintFieldAccessor> Build() {
        Dictionary<string, ConstraintFieldAccessor> table = new(StringComparer.Ordinal);

        Text(table, "Name", static tag => tag.Name, static (tag, value) => tag.Name = value);
        Text(table, "Effector", static tag => tag.Effector, static (tag, value) => tag.Effector = value);
        Text(table, "Chain", static tag => tag.Chain, static (tag, value) => tag.Chain = value);
        Text(table, "Priority", static tag => tag.Priority, static (tag, value) => tag.Priority = value);
        Text(table, "Label", static tag => tag.Label, static (tag, value) => tag.Label = value);
        Text(table, "Other", static tag => tag.Other, static (tag, value) => tag.Other = value);

        Number(table, "Begin", static tag => tag.Begin, static (tag, value) => tag.Begin = value);
        Number(table, "End", static tag => tag.End, static (tag, value) => tag.End = value);
        Number(table, "EaseIn", static tag => tag.EaseIn, static (tag, value) => tag.EaseIn = value);
        Number(table, "EaseOut", static tag => tag.EaseOut, static (tag, value) => tag.EaseOut = value);
        Number(table, "MaxWeight", static tag => tag.MaxWeight, static (tag, value) => tag.MaxWeight = value);
        Number(table, "Tolerance", static tag => tag.Tolerance, static (tag, value) => tag.Tolerance = value);
        Number(table, "AuthoredDistance", static tag => tag.AuthoredDistance, static (tag, value) => tag.AuthoredDistance = value);
        Number(table, "Min", static tag => tag.Min, static (tag, value) => tag.Min = value);
        Number(table, "Max", static tag => tag.Max, static (tag, value) => tag.Max = value);

        Vector(table, "Offset", static tag => tag.Offset, static (tag, value) => tag.Offset = value);
        Vector(table, "EffectorOffset", static tag => tag.EffectorOffset, static (tag, value) => tag.EffectorOffset = value);
        Vector(table, "Region", static tag => tag.Region, static (tag, value) => tag.Region = value);
        Vector(table, "Pole", static tag => tag.Pole, static (tag, value) => tag.Pole = value);
        Vector(table, "Axis", static tag => tag.Axis, static (tag, value) => tag.Axis = value);
        Vector(table, "Origin", static tag => tag.Origin, static (tag, value) => tag.Origin = value);

        Turn(table, "Rotation", static tag => tag.Rotation, static (tag, value) => tag.Rotation = value);
        Turn(table, "Deviation", static tag => tag.Deviation, static (tag, value) => tag.Deviation = value);

        Choice(table, "Mode", static tag => tag.Mode, static (tag, value) => tag.Mode = value);

        Level(table, "LodMin", static tag => tag.LodMin, static (tag, value) => tag.LodMin = value);
        Level(table, "LodMax", static tag => tag.LodMax, static (tag, value) => tag.LodMax = value);

        Place(table, "Goal", static tag => tag.Goal, static (tag, value) => tag.Goal = value);

        table["Reference"] = new(
            static tag => tag.Reference is null ? string.Empty : Describe(tag.Reference),
            static (document, tag, field, value) => {
                var previous = tag.Reference;
                var next = value.Length == 0 ? null : Parse(value);

                document.SetConstraintField(
                    tag,
                    $"Set {field.Label}",
                    static entry => entry.Reference,
                    static (entry, frame) => entry.Reference = frame,
                    next
                );

                return next is not null || previous is not null || value.Length == 0;
            }
        );

        return table;
    }

    static void Text(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, string> read,
        Action<ConstraintTagRecord, string> write
    ) =>
        table[property] = new(
            read,
            (document, tag, field, value) => {
                document.SetConstraintField(tag, $"Set {field.Label}", read, write, value);
                return true;
            }
        );

    static void Number(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, float> read,
        Action<ConstraintTagRecord, float> write
    ) =>
        table[property] = new(
            tag => read(tag).ToString("0.####", CultureInfo.InvariantCulture),
            (document, tag, field, value) => {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) {
                    return false;
                }

                document.SetConstraintField(tag, $"Set {field.Label}", read, write, parsed);
                return true;
            }
        );

    static void Vector(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, Vector3> read,
        Action<ConstraintTagRecord, Vector3> write
    ) =>
        table[property] = new(
            tag => Describe(read(tag)),
            (document, tag, field, value) => {
                if (!TryVector(value, out var parsed)) {
                    return false;
                }

                document.SetConstraintField(tag, $"Set {field.Label}", read, write, parsed);
                return true;
            }
        );

    static void Turn(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, Quaternion> read,
        Action<ConstraintTagRecord, Quaternion> write
    ) =>
        table[property] = new(
            tag => Describe(Euler(read(tag))),
            (document, tag, field, value) => {
                if (!TryVector(value, out var degrees)) {
                    return false;
                }

                // Degrees in, radians stored. Nobody types a quaternion, and a panel that showed four
                // numbers would be a panel authors leave alone.
                var parsed = Quaternion.FromYawPitchRoll(
                    MathUtil.DegreesToRadians(degrees.Y),
                    MathUtil.DegreesToRadians(degrees.X),
                    MathUtil.DegreesToRadians(degrees.Z)
                );

                document.SetConstraintField(tag, $"Set {field.Label}", read, write, parsed);
                return true;
            }
        );

    static void Choice(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, GoalMode> read,
        Action<ConstraintTagRecord, GoalMode> write
    ) =>
        table[property] = new(
            tag => read(tag).ToString(),
            (document, tag, field, value) => {
                if (!Enum.TryParse<GoalMode>(value, out var parsed)) {
                    return false;
                }

                document.SetConstraintField(tag, $"Set {field.Label}", read, write, parsed);
                return true;
            }
        );

    static void Level(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, byte> read,
        Action<ConstraintTagRecord, byte> write
    ) =>
        table[property] = new(
            tag => read(tag).ToString(CultureInfo.InvariantCulture),
            (document, tag, field, value) => {
                if (!byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
                    return false;
                }

                document.SetConstraintField(tag, $"Set {field.Label}", read, write, parsed);
                return true;
            }
        );

    static void Place(
        Dictionary<string, ConstraintFieldAccessor> table,
        string property,
        Func<ConstraintTagRecord, ConstraintFrameRecord> read,
        Action<ConstraintTagRecord, ConstraintFrameRecord> write
    ) =>
        table[property] = new(
            tag => Describe(read(tag)),
            (document, tag, field, value) => {
                if (Parse(value) is not { } parsed) {
                    return false;
                }

                document.SetConstraintField(tag, $"Set {field.Label}", read, write, parsed);
                return true;
            }
        );

    /// <summary>A frame as one line: <c>Surface belly 0.25 0.6</c>, <c>Socket held-item grip</c>.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The line.</returns>
    /// <remarks>
    ///     A picker is what this wants and a line is what it has. The format is the one the frame
    ///     picker will write when it exists, so the field does not change under anybody.
    /// </remarks>
    public static string Describe(ConstraintFrameRecord frame) {
        ArgumentNullException.ThrowIfNull(frame);

        return frame.Kind switch {
            ConstraintFrameKind.World => $"World {Describe(frame.Position)}",
            ConstraintFrameKind.Joint => $"Joint {frame.Joint}",
            ConstraintFrameKind.Entity => $"Entity {frame.Slot}",
            ConstraintFrameKind.Socket => $"Socket {frame.Slot} {frame.Socket}",
            ConstraintFrameKind.Provided => $"Provided {frame.Name}",
            ConstraintFrameKind.Attachment => $"Attachment {frame.Socket}",
            _ => $"Surface {frame.Shape} {frame.U.ToString("0.###", CultureInfo.InvariantCulture)} "
                + frame.V.ToString("0.###", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Reads a frame back from that line.</summary>
    /// <param name="text">The line.</param>
    /// <returns>The frame, or <see langword="null" /> when the line makes no sense.</returns>
    public static ConstraintFrameRecord? Parse(string? text) {
        var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0 || !Enum.TryParse<ConstraintFrameKind>(parts[0], true, out var kind)) {
            return null;
        }

        var frame = new ConstraintFrameRecord { Kind = kind };

        switch (kind) {
            case ConstraintFrameKind.World when parts.Length >= 4:
                frame.Position = new(Number(parts[1]), Number(parts[2]), Number(parts[3]));
                break;

            case ConstraintFrameKind.Joint when parts.Length >= 2:
                frame.Joint = parts[1];
                break;

            case ConstraintFrameKind.Entity when parts.Length >= 2:
                frame.Slot = parts[1];
                break;

            case ConstraintFrameKind.Socket when parts.Length >= 3:
                frame.Slot = parts[1];
                frame.Socket = parts[2];
                break;

            case ConstraintFrameKind.Provided when parts.Length >= 2:
                frame.Name = parts[1];
                break;

            case ConstraintFrameKind.Attachment when parts.Length >= 2:
                frame.Socket = parts[1];
                break;

            case ConstraintFrameKind.Surface when parts.Length >= 2:
                frame.Shape = parts[1];
                frame.U = parts.Length >= 3 ? Number(parts[2]) : 0f;
                frame.V = parts.Length >= 4 ? Number(parts[3]) : 0.5f;
                break;

            default:
                return kind is ConstraintFrameKind.World ? frame : null;
        }

        return frame;
    }

    static float Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;

    static string Describe(Vector3 value) =>
        $"{value.X.ToString("0.####", CultureInfo.InvariantCulture)} "
        + $"{value.Y.ToString("0.####", CultureInfo.InvariantCulture)} "
        + value.Z.ToString("0.####", CultureInfo.InvariantCulture);

    static bool TryVector(string text, out Vector3 value) {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 3) {
            value = default;
            return false;
        }

        value = new(Number(parts[0]), Number(parts[1]), Number(parts[2]));
        return true;
    }

    /// <summary>A rotation as pitch, yaw and roll in degrees.</summary>
    static Vector3 Euler(Quaternion rotation) {
        var q = Quaternion.Normalize(rotation);
        var sinPitch = 2f * ((q.W * q.X) - (q.Y * q.Z));

        var pitch = MathF.Abs(sinPitch) >= 0.9999f
            ? MathF.CopySign(MathUtil.PiOverTwo, sinPitch)
            : MathF.Asin(sinPitch);

        var yaw = MathF.Atan2(2f * ((q.W * q.Y) + (q.X * q.Z)), 1f - (2f * ((q.X * q.X) + (q.Y * q.Y))));
        var roll = MathF.Atan2(2f * ((q.W * q.Z) + (q.X * q.Y)), 1f - (2f * ((q.X * q.X) + (q.Z * q.Z))));

        return new(
            MathUtil.RadiansToDegrees(pitch),
            MathUtil.RadiansToDegrees(yaw),
            MathUtil.RadiansToDegrees(roll)
        );
    }
}
