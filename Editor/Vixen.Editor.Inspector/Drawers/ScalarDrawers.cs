// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector.Drawers;

/// <summary>A checkbox, whose third state is "the selected objects disagree".</summary>
/// <remarks>
///     The indeterminate state is not decoration. Twenty objects with the flag half on must not show
///     an unchecked box, because the next click would turn it on everywhere and the user would have
///     no way to know that is what they did.
/// </remarks>
public sealed class BooleanDrawer : PropertyDrawer<bool, CheckBox> {
    /// <inheritdoc />
    protected override CheckBox Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var checkbox = parent.Add<CheckBox>();
        checkbox.Disabled = !field.CanWrite;

        checkbox.CheckedChanged += (_, value) => {
            if (field.Write(value)) {
                field.Seal();
            }
        };

        return checkbox;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, CheckBox editor, bool value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(editor);

        editor.IsIndeterminate = isMixed;
        editor.IsChecked = !isMixed && value;
    }
}

/// <summary>A number, as a slider when it has bounds and a spin field when it does not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The type decides, and the range refines.</b> A bounded number is a different thing to
///         edit from an unbounded one — the bounds are the affordance, not a validation rule — which
///         is why <c>[Range]</c> changes the control rather than adding a clamp to it.
///     </para>
///     <para>
///         ⚠ <b>Every editor works in <c>double</c>, and the member may be an <c>int</c>.</b> The
///         conversion back is here rather than in the control, because the control does not know what
///         it is editing and a generated setter handed a <c>double</c> for an <c>int</c> field throws
///         inside the cast.
///     </para>
/// </remarks>
public sealed class NumberDrawer : IPropertyDrawer {
    /// <summary>The member types this draws.</summary>
    public static IReadOnlyList<Type> SupportedTypes { get; } = [
        typeof(float), typeof(double), typeof(decimal),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(short), typeof(ushort), typeof(byte), typeof(sbyte)
    ];

    /// <inheritdoc />
    public bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return SupportedTypes.Contains(Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType);
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var type = Nullable.GetUnderlyingType(field.Member.MemberType) ?? field.Member.MemberType;

        if (field.Member.Range is { } range) {
            var slider = parent.Add<Slider>();
            slider.Minimum = (float) range.Minimum;
            slider.Maximum = (float) range.Maximum;
            slider.Step = (float) (range.Step > 0 ? range.Step : IsIntegral(type) ? 1d : 0d);
            slider.Disabled = !field.CanWrite;
            slider.ValueChanged += (_, value) => field.Write(Convert(value, type));

            // A drag is one undo entry and the seal is what ends it. Without this every mouse-move
            // that happened to land on a different value would still merge — until the user did
            // something else — and undo after a drag would go back further than the drag did.
            slider.AddHandler<PointerEvent>((_, args) => {
                if (args.Action == PointerAction.Released) {
                    field.Seal();
                }
            });

            return slider;
        }

        var numeric = parent.Add<NumericInput>();
        numeric.ReadOnly = !field.CanWrite;
        numeric.Decimals = IsIntegral(type) ? 0 : 3;
        numeric.Step = 1d;
        numeric.NumberChanged += (_, value) => field.Write(Convert(value, type));
        numeric.Submitted += _ => field.Seal();

        return numeric;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        var (value, mixed) = field.Read();

        switch (editor) {
            case Slider slider:
                // A mixed slider parks at its low end and says so through the row's label rather than
                // its own position: a slider has no third state, and any position it took would be a
                // value one of the objects does not hold.
                slider.Value = mixed
                    ? slider.Minimum
                    : System.Convert.ToSingle(value ?? 0f, CultureInfo.InvariantCulture);

                break;

            case NumericInput numeric:
                numeric.Value = mixed
                    ? string.Empty
                    : System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture)
                        .ToString("0.###", CultureInfo.InvariantCulture);

                numeric.Placeholder = mixed ? "—" : null;
                break;

            default:
                break;
        }
    }

    static bool IsIntegral(Type type) =>
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
        || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte);

    static object Convert(double value, Type type) =>
        System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
}

/// <summary>A text box.</summary>
public sealed class StringDrawer : PropertyDrawer<string, TextBox> {
    /// <inheritdoc />
    protected override TextBox Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var box = parent.Add<TextBox>();
        box.ReadOnly = !field.CanWrite;

        // On submit and on focus loss, not on every keystroke. A string is not a slider: recording an
        // undo entry per character makes the history unusable, and merging them all would make a name
        // typed in two sittings one entry.
        box.Submitted += control => {
            if (field.Write(control.Value ?? string.Empty)) {
                field.Seal();
            }
        };

        return box;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, TextBox editor, string? value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(editor);

        editor.Value = isMixed ? string.Empty : value;
        editor.Placeholder = isMixed ? "—" : null;
    }
}

/// <summary>A string over several lines.</summary>
public sealed class MultilineDrawer : PropertyDrawer<string, TextArea> {
    /// <inheritdoc />
    protected override TextArea Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var area = parent.Add<TextArea>();
        area.ReadOnly = !field.CanWrite;

        // How tall it starts is a theme decision the attribute parameterises, not something this
        // sets in pixels: `textarea.lines-6` is a selector a user's own stylesheet can reach.
        area.AddClass("lines-" + (field.Member.Lines > 0 ? field.Member.Lines : 4)
            .ToString(CultureInfo.InvariantCulture));

        area.ValueChanged += (control, _) => field.Write(control.Value ?? string.Empty);
        area.Submitted += _ => field.Seal();

        return area;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, TextArea editor, string? value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(editor);

        editor.Value = isMixed ? string.Empty : value;
        editor.Placeholder = isMixed ? "—" : null;
    }
}

/// <summary>A dropdown over an enum's names.</summary>
/// <remarks>
///     ⚠ <b>A <c>[Flags]</c> enum gets a multi-select</b>, because a dropdown that lets you choose
///     one of "None, Read, Write, ReadWrite" is one that cannot express "Read and Execute" and that
///     lists combinations somebody happened to name.
/// </remarks>
public sealed class EnumDrawer : IPropertyDrawer {
    /// <inheritdoc />
    public bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return member.MemberType.IsEnum;
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var type = field.Member.MemberType;

        if (type.IsDefined(typeof(FlagsAttribute), false)) {
            var multi = parent.Add<MultiSelect>();
            multi.Disabled = !field.CanWrite;

            foreach (var name in Enum.GetNames(type)) {
                multi.AddOption(name);
            }

            multi.SelectionChanged += control => {
                if (field.Write(Combine(type, control.Values))) {
                    field.Seal();
                }
            };

            return multi;
        }

        var select = parent.Add<Select>();
        select.Disabled = !field.CanWrite;

        foreach (var name in Enum.GetNames(type)) {
            select.AddOption(name);
        }

        select.SelectionChanged += (_, value) => {
            if (value is not null && field.Write(Enum.Parse(type, value))) {
                field.Seal();
            }
        };

        return select;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        var (value, mixed) = field.Read();

        switch (editor) {
            case MultiSelect multi: {
                var chosen = mixed ? [] : Split(field.Member.MemberType, value);

                foreach (var name in Enum.GetNames(field.Member.MemberType)) {
                    multi.Select(name, chosen.Contains(name));
                }

                multi.Placeholder = mixed ? "—" : null;
                break;
            }

            case Select select:
                select.Value = mixed ? null : value?.ToString();
                select.Placeholder = mixed ? "—" : null;

                break;

            default:
                break;
        }
    }

    static object Combine(Type type, IReadOnlyCollection<string> names) {
        var accumulated = 0L;

        foreach (var name in names) {
            accumulated |= System.Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture);
        }

        return Enum.ToObject(type, accumulated);
    }

    static string[] Split(Type type, object? value) {
        if (value is null) {
            return [];
        }

        var bits = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
        List<string> chosen = [];

        foreach (var name in Enum.GetNames(type)) {
            var flag = System.Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture);

            // Zero is "None" and is only shown when the whole value is zero — otherwise every
            // selection would include it, because zero is a subset of everything.
            if (flag == 0 ? bits == 0 : (bits & flag) == flag) {
                chosen.Add(name);
            }
        }

        return [.. chosen];
    }
}

/// <summary>The last resort: the value as text, unwritable.</summary>
/// <remarks>
///     A member with no drawer is shown rather than omitted. A field the inspector cannot edit is
///     still a field somebody needs to see the value of, and a silently missing row is how a type
///     ends up with a member nobody knows exists.
/// </remarks>
public sealed class ReadOnlyDrawer : IPropertyDrawer {
    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(parent);

        var text = parent.Add<TextBlock>();
        text.AddClass("property-readonly");

        return text;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        var (value, mixed) = field.Read();

        if (editor is TextBlock text) {
            text.Text = mixed ? "—" : value?.ToString() ?? "None";
        }
    }
}
