// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Animation;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>Between a stored curve and the control that edits one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two functions rather than a wrapper, and they are not symmetrical by accident.</b> The
///         control's <see cref="AnimationCurve" /> is mutable and raises events; the stored form is a
///         record the YAML binder can write. Handing the editor a live view over the document would make
///         dragging a key an untracked edit — the undo stack would have nothing to record — so the view
///         copies out, edits, and commits through a command.
///     </para>
///     <para>
///         <b>The stored types themselves are in <c>Vixen.Editor.Assets</c>.</b> They moved there
///         when <c>AnimationClipImporter</c> was written, because an importer cannot see this
///         assembly — the dependency runs the other way — and the alternative was a second parser for
///         the same format. What is left here is the part that is genuinely about the editor: the
///         translation to and from the curve control.
///     </para>
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
