// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector.Drawers;

/// <summary>A row of numeric boxes, one per component, each labelled with its axis.</summary>
/// <remarks>
///     <para>
///         <b>One drawer for every fixed-width vector.</b> Two, three or four boxes differ only in
///         how many there are and what the boxes are called, and three near-identical drawers is
///         three places for a mixed-value bug to hide in.
///     </para>
///     <para>
///         ⚠ <b>Mixed is per component.</b> Twenty objects that agree on X and differ on Y show a
///         number and a dash, not two dashes — because the thing the user wants to know is which axis
///         they disagree about, and blanking the row throws that away. This is the affordance that is
///         most often skipped and most missed.
///     </para>
/// </remarks>
/// <typeparam name="TValue">The vector type.</typeparam>
public abstract class ComponentDrawer<TValue> : IPropertyDrawer where TValue : struct {
    /// <summary>What each box is called.</summary>
    protected abstract IReadOnlyList<string> Axes { get; }

    /// <summary>Pulls a component out of a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="axis">Which component.</param>
    /// <returns>The number.</returns>
    protected abstract float Component(TValue value, int axis);

    /// <summary>Puts a component back into a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="axis">Which component.</param>
    /// <param name="component">The number.</param>
    /// <returns>The new value.</returns>
    protected abstract TValue Replace(TValue value, int axis, float component);

    /// <inheritdoc />
    public virtual bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return member.MemberType == typeof(TValue);
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var row = parent.Add("vector-editor");

        for (var axis = 0; axis < Axes.Count; axis++) {
            var which = axis;
            var group = row.Add("vector-component");
            group.AddClass("axis-" + Axes[axis].ToLowerInvariant());

            group.Add<TextBlock>().Text = Axes[axis];

            var box = group.Add<NumericInput>();
            box.ReadOnly = !field.CanWrite;
            box.Decimals = 3;
            box.Step = 0.1d;

            box.NumberChanged += (_, number) => {
                // Read-modify-write against what is on each object, not against the box's siblings:
                // with several selected the other components differ, and rebuilding the value from
                // the boxes would write the primary object's X to all twenty.
                var (value, mixed) = field.Read();
                var current = mixed ? default : (TValue) (value ?? default(TValue));

                field.Write(Replace(current, which, (float) number));
            };

            box.Submitted += _ => field.Seal();
        }

        return row;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(editor);

        for (var axis = 0; axis < Axes.Count && axis < editor.Children.Count; axis++) {
            if (editor.Children[axis] is not { } group || Find(group) is not { } box) {
                continue;
            }

            var (number, mixed) = ReadComponent(field, axis);

            box.Value = mixed
                ? string.Empty
                : number.ToString("0.###", CultureInfo.InvariantCulture);

            box.Placeholder = mixed ? "—" : null;
        }
    }

    (float Number, bool Mixed) ReadComponent(InspectorField field, int axis) {
        if (field.Targets.Count == 0) {
            return (0f, false);
        }

        var first = Component(Convert(field.Member.GetBoxed(field.Targets[0])), axis);

        for (var index = 1; index < field.Targets.Count; index++) {
            var other = Component(Convert(field.Member.GetBoxed(field.Targets[index])), axis);

            if (!first.Equals(other)) {
                return (0f, true);
            }
        }

        return (first, false);
    }

    static TValue Convert(object? value) => value is TValue typed ? typed : default;

    static NumericInput? Find(UiElement group) {
        foreach (var child in group.Children) {
            if (child is NumericInput box) {
                return box;
            }
        }

        return null;
    }
}

/// <summary>Two boxes: X and Y.</summary>
public sealed class Vector2Drawer : ComponentDrawer<Vector2> {
    /// <inheritdoc />
    protected override IReadOnlyList<string> Axes { get; } = ["X", "Y"];

    /// <inheritdoc />
    protected override float Component(Vector2 value, int axis) => axis == 0 ? value.X : value.Y;

    /// <inheritdoc />
    protected override Vector2 Replace(Vector2 value, int axis, float component) =>
        axis == 0 ? new(component, value.Y) : new(value.X, component);
}

/// <summary>Three boxes: X, Y and Z.</summary>
public sealed class Vector3Drawer : ComponentDrawer<Vector3> {
    /// <inheritdoc />
    protected override IReadOnlyList<string> Axes { get; } = ["X", "Y", "Z"];

    /// <inheritdoc />
    protected override float Component(Vector3 value, int axis) =>
        axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };

    /// <inheritdoc />
    protected override Vector3 Replace(Vector3 value, int axis, float component) =>
        axis switch {
            0 => new(component, value.Y, value.Z),
            1 => new(value.X, component, value.Z),
            _ => new(value.X, value.Y, component)
        };
}

/// <summary>Four boxes: X, Y, Z and W.</summary>
public sealed class Vector4Drawer : ComponentDrawer<Vector4> {
    /// <inheritdoc />
    protected override IReadOnlyList<string> Axes { get; } = ["X", "Y", "Z", "W"];

    /// <inheritdoc />
    protected override float Component(Vector4 value, int axis) =>
        axis switch { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W };

    /// <inheritdoc />
    protected override Vector4 Replace(Vector4 value, int axis, float component) =>
        axis switch {
            0 => new(component, value.Y, value.Z, value.W),
            1 => new(value.X, component, value.Z, value.W),
            2 => new(value.X, value.Y, component, value.W),
            _ => new(value.X, value.Y, value.Z, component)
        };
}

/// <summary>Three boxes of degrees over a rotation that is stored as a quaternion.</summary>
/// <remarks>
///     ⚠ <b>The angles are a view and the quaternion is the value.</b> Editing one box converts the
///     three back into a rotation, so a value that came from a gizmo and cannot be expressed exactly
///     in Euler angles is only ever approximated <i>on screen</i> — see <see cref="EulerAngles" /> for
///     what gimbal lock costs and why it costs it here rather than in the model.
/// </remarks>
public sealed class QuaternionDrawer : ComponentDrawer<Quaternion> {
    /// <inheritdoc />
    protected override IReadOnlyList<string> Axes { get; } = ["X", "Y", "Z"];

    /// <inheritdoc />
    protected override float Component(Quaternion value, int axis) {
        var degrees = EulerAngles.FromRotation(value);

        return axis switch { 0 => degrees.X, 1 => degrees.Y, _ => degrees.Z };
    }

    /// <inheritdoc />
    protected override Quaternion Replace(Quaternion value, int axis, float component) {
        var degrees = EulerAngles.FromRotation(value);

        degrees = axis switch {
            0 => new Vector3(component, degrees.Y, degrees.Z),
            1 => new Vector3(degrees.X, component, degrees.Z),
            _ => new Vector3(degrees.X, degrees.Y, component)
        };

        return EulerAngles.ToRotation(degrees);
    }
}
