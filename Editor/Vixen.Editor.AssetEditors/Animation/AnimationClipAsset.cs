// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Rendering;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>Which of a joint's ten numbers a curve drives.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ten scalar curves rather than three vector tracks, and that is the whole reason this
///         format exists.</b> <see cref="AnimationChannel" /> — what an import writes — holds arrays
///         of <c>Vector3</c> and <c>Quaternion</c>, which is exactly right for a file produced by a
///         DCC and exactly wrong for an editor: a curve editor edits <i>one</i> number against time,
///         and a dope sheet's row is one number's keys. A vector track cannot express "X has a key
///         here and Y does not", which is most of what hand animation is.
///     </para>
///     <para>
///         ⚠ <b>Rotation is stored as a quaternion's four components and not as Euler angles.</b>
///         Doc 08's own note on <see cref="AnimationChannel" /> gives the reason and it survives the
///         change of shape: a track sampled between two Euler triples takes a different path from one
///         sampled between the rotations they represent, and the difference is the gimbal artefact
///         everybody has seen. Showing Euler angles in the <i>editor</i> is a display decision and is
///         a thing this format leaves open; storing them is not.
///     </para>
/// </remarks>
public enum AnimationProperty {
    /// <summary>Position, X.</summary>
    PositionX,

    /// <summary>Position, Y.</summary>
    PositionY,

    /// <summary>Position, Z.</summary>
    PositionZ,

    /// <summary>Rotation, X.</summary>
    RotationX,

    /// <summary>Rotation, Y.</summary>
    RotationY,

    /// <summary>Rotation, Z.</summary>
    RotationZ,

    /// <summary>Rotation, W.</summary>
    RotationW,

    /// <summary>Scale, X.</summary>
    ScaleX,

    /// <summary>Scale, Y.</summary>
    ScaleY,

    /// <summary>Scale, Z.</summary>
    ScaleZ
}

/// <summary>One key of one curve.</summary>
/// <remarks>
///     The same five numbers <see cref="CurveKey" /> holds, because the curve control is what edits
///     them — a second key type that had to be translated at both ends would be a second place for
///     the tangent convention to drift.
/// </remarks>
[DataContract("AnimationKey")]
public sealed class AnimationKeyData {
    /// <summary>When it is, in seconds.</summary>
    public float Time { get; set; }

    /// <summary>What the number is there.</summary>
    public float Value { get; set; }

    /// <summary>The slope coming in.</summary>
    public float InTangent { get; set; }

    /// <summary>And going out.</summary>
    public float OutTangent { get; set; }

    /// <summary>How the two are decided.</summary>
    public TangentMode Mode { get; set; } = TangentMode.Auto;
}

/// <summary>One number of one joint, over time.</summary>
[DataContract("AnimationCurve")]
public sealed class AnimationCurveData {
    /// <summary>Which number it drives.</summary>
    public AnimationProperty Property { get; set; }

    /// <summary>Its keys, in time order.</summary>
    public List<AnimationKeyData> Keys { get; set; } = [];
}

/// <summary>What one clip does to one joint.</summary>
[DataContract("AnimationTarget")]
public sealed class AnimationTargetData {
    /// <summary>Which joint or node it drives, by name.</summary>
    /// <remarks>
    ///     By name for <see cref="AnimationClip" />'s own reason: an editor has no skeleton to
    ///     resolve against and no guarantee that the skeleton it would resolve against is the one the
    ///     clip will be played on. Resolution happens once, at load, against the rig it is baked for.
    /// </remarks>
    public string Target { get; set; } = string.Empty;

    /// <summary>The curves it carries, at most one per property.</summary>
    public List<AnimationCurveData> Curves { get; set; } = [];
}

/// <summary>One event on the clip's timeline.</summary>
[DataContract("AnimationEvent")]
public sealed class AnimationEventData {
    /// <summary>What the event is called. A game reads this and decides.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When it fires, in seconds.</summary>
    public float Time { get; set; }

    /// <summary>Its float payload.</summary>
    public float Float { get; set; }

    /// <summary>Its integer payload.</summary>
    public int Int { get; set; }

    /// <summary>Its string payload.</summary>
    public string String { get; set; } = string.Empty;
}

/// <summary>A whole <c>.vxanim</c> document: the format doc 20's E5 says did not exist.</summary>
/// <remarks>
///     <para>
///         <b>Authored clips and imported clips are the same runtime object and not the same
///         file.</b> An FBX produces <see cref="AnimationClipData" /> through the model importer, and
///         nothing about that changes; what was missing is a clip somebody can <i>write</i> — a
///         camera move, a door, a UI wobble, a hand-keyed idle — and that wants keys with tangents
///         rather than baked samples. <see cref="ToClipData" /> is the one-way door between them, so
///         everything downstream of an import stays unchanged.
///     </para>
///     <para>
///         ⚠ <b>The frame rate is a property of the editor, not of the clip.</b> It decides where the
///         timeline's snap lands and what "next frame" means; the keys are in seconds and sampling is
///         continuous, so changing it moves no key and re-times nothing. An editor that stored frames
///         would make a clip authored at 30 play at the wrong speed on a project that later chose 60.
///     </para>
/// </remarks>
[DataContract("AnimationClipAsset")]
public sealed class AnimationClipAsset {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What an authored clip is written as.</summary>
    public const string Extension = ".vxanim";

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the clip is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How long it plays for, in seconds.</summary>
    /// <remarks>
    ///     Stored rather than derived from the last key, because a clip whose last two seconds are a
    ///     deliberate hold has no key in them — and a duration that shrank to the last key would
    ///     silently delete that hold every time the file was saved.
    /// </remarks>
    public float Duration { get; set; } = 1f;

    /// <summary>What the timeline snaps to, in frames a second.</summary>
    public float FrameRate { get; set; } = 30f;

    /// <summary>What happens when it runs past the end.</summary>
    public WrapMode Wrap { get; set; } = WrapMode.Loop;

    /// <summary>What it moves.</summary>
    public List<AnimationTargetData> Targets { get; set; } = [];

    /// <summary>What it raises, in time order.</summary>
    public List<AnimationEventData> Events { get; set; } = [];

    /// <summary>Reads YAML into a clip.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The clip.</returns>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    public static AnimationClipAsset FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Trim().Length == 0) {
            // An empty file is a new clip rather than an error, for `AssetFile.Read`'s reason: the
            // ordinary way to make one of these is to create the file and open it.
            return new();
        }

        var clip = YamlSerializer.Parse<AnimationClipAsset>(yaml);

        return clip.Version <= Current
            ? clip
            : throw new NotSupportedException(
                $"This clip is version {clip.Version} and this build reads {Current}. Reading it would bind "
                + "the parts it recognises and drop the rest."
            );
    }

    /// <summary>Writes it as YAML.</summary>
    /// <returns>The text.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(this);

    /// <summary>The events, as the runtime's own record, in time order.</summary>
    /// <returns>The events.</returns>
    public AnimationEvent[] ToEvents() =>
        [.. Events.OrderBy(entry => entry.Time)
            .Select(entry => new AnimationEvent(entry.Name, entry.Time, entry.Float, entry.Int, entry.String))];

    /// <summary>Bakes the curves into the sampled channels an import would have produced.</summary>
    /// <returns>The clip data.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The union of key times per group, not a fixed sample rate.</b> Baking at the frame
    ///         rate would turn a two-key linear slide into sixty keys and would still be wrong for a
    ///         curve whose interesting moment falls between two frames. Sampling the curve at every
    ///         time <i>any</i> of the group's components has a key keeps the result exact wherever the
    ///         author put a key and interpolated everywhere else — which is what the curves say.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A group with no curves at all produces no keys</b>, rather than one key holding
    ///         the identity. A channel that writes an identity scale is not the same as one that does
    ///         not write scale: the first overwrites whatever a layer underneath set.
    ///     </para>
    /// </remarks>
    public AnimationClipData ToClipData() {
        List<AnimationChannel> channels = [];

        foreach (var target in Targets) {
            var channel = new AnimationChannel { Target = target.Target };

            var position = Times(target, AnimationProperty.PositionX, AnimationProperty.PositionY, AnimationProperty.PositionZ);
            var rotation = Times(target, AnimationProperty.RotationX, AnimationProperty.RotationY, AnimationProperty.RotationZ, AnimationProperty.RotationW);
            var scale = Times(target, AnimationProperty.ScaleX, AnimationProperty.ScaleY, AnimationProperty.ScaleZ);

            channel.PositionTimes = [.. position];
            channel.Positions = [.. position.Select(time => new Vector3(
                Sample(target, AnimationProperty.PositionX, time),
                Sample(target, AnimationProperty.PositionY, time),
                Sample(target, AnimationProperty.PositionZ, time)
            ))];

            channel.RotationTimes = [.. rotation];
            channel.Rotations = [.. rotation.Select(time => Normalised(
                Sample(target, AnimationProperty.RotationX, time),
                Sample(target, AnimationProperty.RotationY, time),
                Sample(target, AnimationProperty.RotationZ, time),
                Sample(target, AnimationProperty.RotationW, time, fallback: 1f)
            ))];

            channel.ScaleTimes = [.. scale];
            channel.Scales = [.. scale.Select(time => new Vector3(
                Sample(target, AnimationProperty.ScaleX, time, fallback: 1f),
                Sample(target, AnimationProperty.ScaleY, time, fallback: 1f),
                Sample(target, AnimationProperty.ScaleZ, time, fallback: 1f)
            ))];

            channels.Add(channel);
        }

        return new() { Name = Name, Duration = Duration, Channels = [.. channels] };
    }

    /// <summary>
    ///     ⚠ Normalised on the way out, because interpolating four components independently does not
    ///     produce a unit quaternion — and a rotation that is not unit is a scale nobody authored.
    /// </summary>
    static Quaternion Normalised(float x, float y, float z, float w) {
        var length = MathF.Sqrt((x * x) + (y * y) + (z * z) + (w * w));

        return length > 1e-6f
            ? new(x / length, y / length, z / length, w / length)
            : Quaternion.Identity;
    }

    /// <summary>Every time any of a group's components has a key, sorted and deduplicated.</summary>
    static List<float> Times(AnimationTargetData target, params ReadOnlySpan<AnimationProperty> properties) {
        SortedSet<float> times = [];

        foreach (var property in properties) {
            if (target.Curves.FirstOrDefault(curve => curve.Property == property) is not { } found) {
                continue;
            }

            foreach (var key in found.Keys) {
                times.Add(key.Time);
            }
        }

        return [.. times];
    }

    /// <summary>One component's value at a time, or its fallback when it has no curve.</summary>
    static float Sample(AnimationTargetData target, AnimationProperty property, float time, float fallback = 0f) =>
        target.Curves.FirstOrDefault(curve => curve.Property == property) is { Keys.Count: > 0 } found
            ? AnimationClipCurves.ToCurve(found).Evaluate(time)
            : fallback;
}

/// <summary>Between a stored curve and the control that edits one.</summary>
/// <remarks>
///     ⚠ <b>Two functions rather than a wrapper, and they are not symmetrical by accident.</b> The
///     control's <see cref="AnimationCurve" /> is mutable and raises events; the stored form is a
///     record the YAML binder can write. Handing the editor a live view over the document would make
///     dragging a key an untracked edit — the undo stack would have nothing to record — so the view
///     copies out, edits, and commits through a command.
/// </remarks>
public static class AnimationClipCurves {
    /// <summary>The control's curve for a stored one.</summary>
    /// <param name="data">The stored curve.</param>
    /// <returns>The curve.</returns>
    public static AnimationCurve ToCurve(AnimationCurveData data) {
        ArgumentNullException.ThrowIfNull(data);

        var curve = new AnimationCurve();

        foreach (var key in data.Keys.OrderBy(entry => entry.Time)) {
            curve.Add(new(key.Time, key.Value, key.Mode) {
                InTangent = key.InTangent,
                OutTangent = key.OutTangent
            });
        }

        return curve;
    }

    /// <summary>The stored form of a control's curve.</summary>
    /// <param name="property">Which number it drives.</param>
    /// <param name="curve">The curve.</param>
    /// <returns>The stored curve.</returns>
    public static AnimationCurveData ToData(AnimationProperty property, AnimationCurve curve) {
        ArgumentNullException.ThrowIfNull(curve);

        var data = new AnimationCurveData { Property = property };

        foreach (var key in curve.Keys) {
            data.Keys.Add(new() {
                Time = key.Time,
                Value = key.Value,
                InTangent = key.InTangent,
                OutTangent = key.OutTangent,
                Mode = key.Mode
            });
        }

        return data;
    }

    /// <summary>What a property is called in a dope sheet's row.</summary>
    /// <param name="property">The property.</param>
    /// <returns>The label.</returns>
    public static string Label(AnimationProperty property) => property switch {
        AnimationProperty.PositionX => "Position X",
        AnimationProperty.PositionY => "Position Y",
        AnimationProperty.PositionZ => "Position Z",
        AnimationProperty.RotationX => "Rotation X",
        AnimationProperty.RotationY => "Rotation Y",
        AnimationProperty.RotationZ => "Rotation Z",
        AnimationProperty.RotationW => "Rotation W",
        AnimationProperty.ScaleX => "Scale X",
        AnimationProperty.ScaleY => "Scale Y",
        _ => "Scale Z"
    };

    /// <summary>What a property rests at when nothing keys it.</summary>
    /// <param name="property">The property.</param>
    /// <returns>The value.</returns>
    public static float Rest(AnimationProperty property) => property switch {
        AnimationProperty.RotationW or AnimationProperty.ScaleX or AnimationProperty.ScaleY
            or AnimationProperty.ScaleZ => 1f,
        _ => 0f
    };
}
