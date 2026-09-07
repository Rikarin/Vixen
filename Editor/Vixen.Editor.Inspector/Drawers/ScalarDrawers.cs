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

        // ⚠ The floor, not the rate. An unbounded member has no declared scale — a lux, a
        // centimetre and a byte all arrive here as a number with nothing said about it — so one is
        // the best absolute guess and it is a bad one on anything large: a directional light is a
        // hundred thousand lux, and a scrub worth one per pixel moved it by a thousandth of a
        // percent. `NumericInput.RelativeStep` is what carries the magnitude; this is only what
        // takes over when the member is small enough for a percentage to be smaller than useful,
        // and what a member sitting at nought scrubs by.
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

            // ⚠ The number, not the text, and the difference is not cosmetic. `NumericInput.Value` is
            // a rendering of `Number` and writing one does not read back into the other — that is
            // deliberate, because a field mid-edit holds `-` and `1.` and a control that reparsed on
            // every keystroke would fight every negative number ever typed. So a drawer that set only
            // the text left `Number` at whatever it was born as, which is zero: the box said 1 and
            // the control believed 0. A scrub reads `Number` for its origin, so dragging a member
            // that had never been typed into jumped it to nought and moved from there.
            //
            // Safe against a write-back loop because `InspectorRows.Show` wraps this in
            // `InspectorField.Refreshing`, and `Write` refuses while that is held.
            case NumericInput numeric:
                if (mixed) {
                    numeric.Value = string.Empty;
                } else {
                    numeric.Number = System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture);
                }

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
        box.Submitted += Commit;

        // ⚠ Focus loss is the other half and it was missing, which is the whole of what "renaming an
        // entity in the inspector does nothing until Enter" was. Clicking away from a field is how
        // most edits end — the pointer goes to the next thing rather than to the Return key — and a
        // box that kept the typing on screen while writing nothing reads as the edit having landed.
        // `Write` is a no-op when the value has not changed, so a click through a field nobody typed
        // in records nothing.
        box.AddHandler<FocusEvent>(
            (element, args) => {
                if (!args.Gained && ReferenceEquals(args.Source, element)) {
                    Commit((TextBox) element);
                }
            }
        );

        return box;

        void Commit(TextField control) {
            if (field.Write(control.Value ?? string.Empty)) {
                field.Seal();
            }
        }
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

        // The value is already written per keystroke here, so what focus loss ends is the undo entry
        // — the same thing Enter ends. Without it, everything typed before clicking away merges with
        // whatever is typed next.
        area.AddHandler<FocusEvent>(
            (element, args) => {
                if (!args.Gained && ReferenceEquals(args.Source, element)) {
                    field.Seal();
                }
            }
        );

        return area;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, TextArea editor, string? value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(editor);

        editor.Value = isMixed ? string.Empty : value;
        editor.Placeholder = isMixed ? "—" : null;
    }
}

/// <summary>A dropdown over the values a member states it accepts.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>EnumDrawer</c> for a set of legal values that is not a CLR type —
///         <a href="https://github.com/Rikarin/Vixen/issues/964">#964</a>.</b> A node setting is one
///         of nine measurements, a plugin's own row is one of the names a library published: legal
///         sets both, enumerable both, and neither is an <see langword="enum" />, so both drew as a
///         text box in which a typo is a diagnostic instead of an impossibility.
///     </para>
///     <para>
///         ⚠ <b>Registered for <see langword="string" /> after <see cref="StringDrawer" /> and
///         declining when there is no list</b>, which is the registry's own idiom: the most recently
///         registered wins and what it declines falls through untouched. So a string member that
///         states nothing is a text box exactly as it was, and this file adds no behaviour to any
///         member that did not ask for it.
///     </para>
///     <para>
///         ⚠ <b>A stored value the list does not hold is offered rather than dropped.</b> A dropdown
///         that silently showed the first option would <em>say</em> the member holds that, and then
///         write it on the next click — losing a value the author has not been told is wrong.
///         Whatever refuses it downstream is what says so; this control's job is to stop showing a
///         lie.
///     </para>
/// </remarks>
public sealed class ChoiceDrawer : PropertyDrawer<string, Select> {
    /// <inheritdoc />
    public override bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        // ⚠ Empty is the same answer as null and both decline: a dropdown with no options is a
        // control nobody can use, which is worse than the box it would have replaced.
        return base.CanDraw(member) && member.Choices is { Count: > 0 };
    }

    /// <inheritdoc />
    protected override Select Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var select = parent.Add<Select>();
        select.Disabled = !field.CanWrite;

        foreach (var choice in field.Member.Choices ?? []) {
            select.AddOption(choice);
        }

        select.SelectionChanged += (_, value) => {
            if (value is not null && field.Write(value)) {
                field.Seal();
            }
        };

        return select;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, Select editor, string? value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(editor);

        if (!isMixed && value is { Length: > 0 } written && !Offered(editor, written)) {
            // ⚠ Appended rather than inserted, so the declared order stays the declared order and
            // the stranger is visibly last. `AddOption` is idempotent only in the sense that
            // `Offered` above is what makes it so — a second identical option would be a duplicate
            // row in the popover.
            editor.AddOption(written);
        }

        editor.Value = isMixed ? null : value;
        editor.Placeholder = isMixed ? "—" : null;
    }

    static bool Offered(Select select, string value) {
        foreach (var option in select.Options) {
            if (string.Equals(option.Value, value, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
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
