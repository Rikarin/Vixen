// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Reflection;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One member's row: its name, its editor, and the button that puts it back.</summary>
public sealed partial class PropertyRow : Control {
    /// <inheritdoc />
    protected override string TagName => "property-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The member it edits.</summary>
    public MemberDescriptor? Member { get; internal set; }

    /// <summary>How to get from a target to <see cref="Member" />, ending with it.</summary>
    /// <remarks>
    ///     ⚠ <b>A path rather than a member, because a struct's member cannot be written in place.</b>
    ///     Editing <c>Transform.Position.X</c> reads a box of <c>Transform</c> out of the target and a
    ///     box of <c>Position</c> out of that; writing <c>X</c> into the innermost box changes a copy
    ///     nothing holds. The path is what lets the write be pushed back up — <c>Position</c> into
    ///     <c>Transform</c>, <c>Transform</c> into the target — which is the whole of what nested
    ///     structs needed.
    /// </remarks>
    public IReadOnlyList<MemberDescriptor> Path { get; internal set; } = [];

    /// <summary>How deep it is nested, for the indent.</summary>
    public int Depth { get; internal set; }

    /// <summary>The name on the left.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>Where the editor goes.</summary>
    public UiElement Editor { get; private set; } = null!;

    /// <summary>The button that restores the default, shown only when the value is not it.</summary>
    public IconButton Reset { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Label = Part("property-label");
        Editor = Part("property-editor");

        Reset = Part<IconButton>();
        Reset.LeadingIcon.Geometry = ControlIcons.Close;
        Reset.Variant = ControlVariant.Subtle;
        Reset.Label = ControlStrings.PropertyGridReset.Text;
        Reset.Size = ControlSize.Small;
        Reset.TabIndex = -1;
    }
}

/// <summary>An inspector: editors generated from what a type says about itself.</summary>
/// <remarks>
///     <para>
///         <b>Built on <c>Vixen.Core.Reflection</c>, which is generated rather than reflective.</b>
///         A <see cref="MemberDescriptor" />'s accessors are lambdas over a cast — <c>static
///         instance =&gt; ((Foo) instance).X</c> — so this reads and writes arbitrary members after
///         trimming and on iOS, which an inspector built on <c>PropertyInfo.GetValue</c> cannot. It
///         is the same bet doc 11 makes for the editor's inspector, one layer down.
///     </para>
///     <para>
///         <b>Several targets at once, which is what an inspector is for.</b> Selecting twenty
///         objects and setting one field on all of them is the operation; showing the first one's
///         values and silently editing only that is the bug. Where the targets disagree the editor
///         says so — an indeterminate checkbox, an empty field — and typing into it writes the new
///         value to every one of them.
///     </para>
///     <para>
///         ⚠ <b>A struct member of a struct member cannot be written back.</b> The accessors pass
///         values as <see cref="object" />, so editing a nested value type edits a box that nothing
///         holds. Nested <i>reference</i> types work, and nested structs are shown read-only rather
///         than accepting edits that would vanish. Closing it needs <c>ref</c> accessors, which is a
///         change to the descriptor rather than to this.
///     </para>
/// </remarks>
public sealed partial class PropertyGrid : Control {
    readonly List<object> targets = [];
    readonly List<PropertyRow> rows = [];
    readonly Dictionary<Type, object> defaults = [];

    /// <inheritdoc />
    protected override string TagName => "property-grid";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The search box above the rows.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>Where the rows go.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>What is being inspected.</summary>
    public IReadOnlyList<object> Targets => targets;

    /// <summary>The rows, in order.</summary>
    public IReadOnlyList<PropertyRow> Rows => rows;

    /// <summary>The type every target has in common, if they have one.</summary>
    public TypeDescriptor? Descriptor { get; private set; }

    /// <summary>Raised after an editor writes a member.</summary>
    public event Action<PropertyGrid, MemberDescriptor>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Search = Part<SearchBox>();
        Search.Placeholder = ControlStrings.PropertyGridSearch.Text;

        // ⚠ **Named as well as prompted, and the two come from the same declaration.** A
        // `SearchBox` is a `TextField`, whose accessible name is deliberately `null` — a
        // placeholder is a hint rather than a name and vanishes the moment there is a value, so an
        // unlabelled field reports nothing and a gate can fail it. This one is not unlabelled: it
        // filters the members of whatever is being inspected, the grid knows that, and there is no
        // caption element beside it to point a `LabelledBy` at. Reading the same `StringId` the
        // placeholder reads, on the same line, is what makes it impossible for the words shown and
        // the words announced to be in two different languages.
        Search.AccessibleName = ControlStrings.PropertyGridSearch.Text;

        Search.ValueChanged += (_, _) => Filter();

        Body = Part("property-body");

        AddHandler<ClickEvent>(static (element, args) => ((PropertyGrid) element).Chosen(args));
    }

    /// <summary>Shows the members of some objects.</summary>
    /// <param name="objects">What to inspect. All of one type, or nothing is shown.</param>
    /// <remarks>
    ///     ⚠ <b>One type, not a common base.</b> Inspecting a light and a camera together would have
    ///     to show the members of <c>object</c>, which is nothing anybody wants — and working out
    ///     the most derived common base is a rule that produces a different set of editors depending
    ///     on what else happened to be selected. Mixed selections show nothing, which is honest and
    ///     is what every inspector that tried the alternative ended up doing.
    /// </remarks>
    public void Inspect(params ReadOnlySpan<object> objects) {
        targets.Clear();

        foreach (var target in objects) {
            ArgumentNullException.ThrowIfNull(target);
            targets.Add(target);
        }

        Descriptor = Common();
        Rebuild();
    }

    /// <summary>Shows nothing.</summary>
    public void Clear() => Inspect([]);

    /// <summary>Reads every editor back from the objects, without rebuilding anything.</summary>
    /// <remarks>
    ///     What a gizmo dragging an object calls. The rows and their editors already exist and their
    ///     bindings are already wired; all that has changed is the numbers, and rebuilding would
    ///     take the focus out of whatever the user was typing into.
    /// </remarks>
    public void Reload() {
        foreach (var row in rows) {
            Show(row);
        }
    }

    TypeDescriptor? Common() {
        if (targets.Count == 0) {
            return null;
        }

        var type = targets[0].GetType();

        foreach (var target in targets) {
            if (target.GetType() != type) {
                return null;
            }
        }

        return TypeRegistry.TryGet(type, out var descriptor) ? descriptor : null;
    }

    void Rebuild() {
        while (Body.Children.Count > 0) {
            Body.Children[^1].Remove();
        }

        rows.Clear();

        if (Descriptor is not { } descriptor) {
            return;
        }

        UiElement section = Body;
        string? category = null;

        foreach (var member in descriptor.Members) {
            if (!member.Presentation.IsEditorVisible) {
                continue;
            }

            // ⚠ Members are grouped by the category they declare, and a change of category starts a
            // new section. That makes the *order* of the members the order of the sections, which is
            // the order the author wrote them in — an inspector that sorted its categories would
            // reorder somebody's carefully arranged component every time they added a field.
            if (!string.Equals(member.Presentation.Category, category, StringComparison.Ordinal)) {
                category = member.Presentation.Category;

                section = category is null
                    ? Body
                    : Group(category);
            }

            Emit(section, member, [], 0);
        }
    }

    /// <summary>How deep the grid will follow a member into another descriptor.</summary>
    /// <remarks>
    ///     ⚠ A reference type may contain itself — a node with a parent, a component with an owner —
    ///     so the recursion needs a bound that is not "until it stops". Four is deeper than any
    ///     inspector anybody reads and shallower than any cycle can hide in.
    /// </remarks>
    const int MaxDepth = 4;

    /// <summary>Adds a row for a member, and rows for its own members when it has any worth showing.</summary>
    void Emit(UiElement section, MemberDescriptor member, List<MemberDescriptor> prefix, int depth) {
        var path = new List<MemberDescriptor>(prefix.Count + 1);
        path.AddRange(prefix);
        path.Add(member);

        var row = section.Add<PropertyRow>();
        row.Member = member;
        row.Path = path;
        row.Depth = depth;
        row.Label.Text = member.Presentation.DisplayName ?? member.Name;

        if (depth > 0) {
            row.Label.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");
        }

        Build(row, member);
        Show(row);

        rows.Add(row);

        // ⚠ Only when nothing else claimed the member. A `Vector3` with a registered descriptor is
        // still three numbers if the grid has an editor for it, and expanding one that already has an
        // editor would show the value twice and let two controls fight over it.
        if (depth >= MaxDepth
            || row.Editor.Children is not [TextBlock { } placeholder]
            || !placeholder.HasClass("property-readonly")
            || !TypeRegistry.TryGet(member.MemberType, out var nested)) {
            return;
        }

        placeholder.Remove();
        row.AddClass("property-group");

        foreach (var inner in nested.Members) {
            if (inner.Presentation.IsEditorVisible) {
                Emit(section, inner, path, depth + 1);
            }
        }
    }

    UiElement Group(string category) {
        var expander = Body.Add<Expander>();
        expander.Label = category;
        expander.IsExpanded = true;

        return expander.Content;
    }

    /// <summary>Makes the editor a member's type asks for.</summary>
    /// <remarks>
    ///     ⚠ <b>The type decides, and the presentation refines.</b> A <c>float</c> is a numeric field;
    ///     a <c>float</c> with both ends of a range declared is a slider, because a bounded number is
    ///     a different thing to edit from an unbounded one. Everything with no editor is shown
    ///     read-only rather than omitted — a member the inspector cannot edit is still a member
    ///     somebody needs to see the value of.
    /// </remarks>
    void Build(PropertyRow row, MemberDescriptor member) {
        var type = Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType;
        var editable = member.CanWrite && !member.Presentation.IsEditorReadOnly;

        if (type == typeof(bool)) {
            var checkbox = row.Editor.Add<CheckBox>();
            checkbox.Disabled = !editable;
            checkbox.CheckedChanged += (_, value) => Write(row, value);

            return;
        }

        if (type == typeof(string)) {
            var field = row.Editor.Add<TextBox>();
            field.ReadOnly = !editable;
            field.Submitted += box => Write(row, box.Value);

            return;
        }

        if (IsNumeric(type)) {
            var presentation = member.Presentation;

            if (presentation.Minimum is { } low && presentation.Maximum is { } high) {
                var slider = row.Editor.Add<Slider>();
                slider.Minimum = (float) low;
                slider.Maximum = (float) high;
                slider.Step = (float) presentation.Step;
                slider.Disabled = !editable;
                slider.ValueChanged += (_, value) => Write(row, Convert(value, type));

                return;
            }

            var numeric = row.Editor.Add<NumericInput>();
            numeric.ReadOnly = !editable;
            numeric.Minimum = presentation.Minimum ?? double.NegativeInfinity;
            numeric.Maximum = presentation.Maximum ?? double.PositiveInfinity;
            numeric.Step = presentation.Step > 0 ? presentation.Step : 1d;
            numeric.Decimals = IsIntegral(type) ? 0 : 3;
            numeric.NumberChanged += (_, value) => Write(row, Convert(value, type));

            return;
        }

        // ⚠ `member.MemberType`, not the unwrapped `type`. The annotation that makes `Enum.GetNames`
        // provably safe travels with the property, and `Nullable.GetUnderlyingType` returns a plain
        // `Type` — so unwrapping loses it and the trimmer is right to complain. The cost is that a
        // nullable enum falls through to the read-only rendering below, which is a narrow case and a
        // visible one rather than a warning suppressed.
        if (member.MemberType.IsEnum) {
            var select = row.Editor.Add<Select>();
            select.Disabled = !editable;

            foreach (var name in Enum.GetNames(member.MemberType)) {
                select.AddOption(name);
            }

            select.SelectionChanged += (_, value) => {
                if (value is not null) {
                    Write(row, Enum.Parse(member.MemberType, value));
                }
            };

            return;
        }

        // Anything else: a read-only rendering. A nested descriptor would be an inspector of its
        // own, and that is the next thing this grows — see the type's remarks about `ref` accessors,
        // which is what nested *structs* need before it would be honest.
        row.Editor.Add<TextBlock>().AddClass("property-readonly");
    }

    /// <summary>Reads a member off every target and puts the answer in the editor.</summary>
    /// <remarks>
    ///     ⚠ <b>Disagreement is a state, not an average.</b> Twenty objects with three different
    ///     values must not show one of them as though it were the answer — the user would change
    ///     something else and never know. An indeterminate checkbox and an empty field are what say
    ///     "these differ", and writing into either sets all of them.
    /// </remarks>
    void Show(PropertyRow row) {
        if (row.Member is not { } member) {
            return;
        }

        var (value, mixed) = Read(row.Path);
        var editor = row.Editor.Children.Count > 0 ? row.Editor.Children[0] : null;

        switch (editor) {
            case CheckBox checkbox:
                checkbox.IsIndeterminate = mixed;
                checkbox.IsChecked = !mixed && value is true;

                break;

            case NumericInput numeric:
                numeric.Value = mixed ? string.Empty : System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture)
                    .ToString("0.###", CultureInfo.InvariantCulture);

                numeric.Placeholder = mixed ? "—" : null;
                break;

            case Slider slider:
                slider.Value = mixed ? slider.Minimum : System.Convert.ToSingle(value ?? 0f, CultureInfo.InvariantCulture);
                break;

            case Select select:
                select.Value = mixed ? null : value?.ToString();
                select.Placeholder = mixed ? "—" : null;

                break;

            case TextBox field:
                field.Value = mixed ? string.Empty : value as string;
                field.Placeholder = mixed ? "—" : null;

                break;

            case TextBlock text:
                text.Text = mixed ? "—" : value?.ToString();
                break;

            default:
                break;
        }

        Restate(row, member, value, mixed);
    }

    (object? Value, bool Mixed) Read(IReadOnlyList<MemberDescriptor> path) {
        if (targets.Count == 0 || path.Count == 0) {
            return (null, false);
        }

        var first = Through(targets[0], path);

        for (var i = 1; i < targets.Count; i++) {
            if (!Equals(first, Through(targets[i], path))) {
                return (null, true);
            }
        }

        return (first, false);
    }

    /// <summary>Follows a path from a target to the value at its end.</summary>
    static object? Through(object target, IReadOnlyList<MemberDescriptor> path) {
        object? current = target;

        foreach (var member in path) {
            if (current is null) {
                return null;
            }

            current = member.GetValue(current);
        }

        return current;
    }

    /// <summary>Writes a value at the end of a path, and pushes every box back up behind it.</summary>
    /// <remarks>
    ///     ⚠ <b>The walk back up is the whole feature.</b> An accessor takes its instance as
    ///     <see cref="object" />, so reading a struct member gives a <i>box</i> — writing into it
    ///     changes a copy that nothing holds, which is why nested structs used to be shown read-only.
    ///     Setting the leaf and then writing each owner into its own owner, innermost first, is
    ///     read-modify-write and needs no <c>ref</c> accessors at all.
    ///     <para>
    ///         For a reference-type owner the write-back is a call that changes nothing, and it is
    ///         made anyway: telling the two apart would mean asking whether a type is a value type at
    ///         every level, to save a setter call on a path a person is typing into.
    ///     </para>
    /// </remarks>
    static bool Through(object target, IReadOnlyList<MemberDescriptor> path, object? value) {
        var owners = new object[path.Count];
        object? current = target;

        for (var i = 0; i < path.Count; i++) {
            if (current is null || !path[i].CanWrite) {
                return false;
            }

            owners[i] = current;
            current = i == path.Count - 1 ? null : path[i].GetValue(current);
        }

        var carried = value;

        for (var i = path.Count - 1; i >= 0; i--) {
            path[i].SetValue(owners[i], carried);
            carried = owners[i];
        }

        return true;
    }

    void Write(PropertyRow row, object? value) {
        if (row.Path.Count == 0 || row.Member is not { CanWrite: true } member) {
            return;
        }

        foreach (var target in targets) {
            Through(target, row.Path, value);
        }

        Restate(row, member, value, false);

        // ⚠ Every row whose path *starts with* this one's owner is re-read, not just this one. A
        // struct member edited through a path replaces the whole owner, so a sibling field of the
        // same struct is showing a value that came out of a box which no longer exists.
        foreach (var other in rows) {
            if (!ReferenceEquals(other, row) && other.Member is not null && Shares(other.Path, row.Path)) {
                Show(other);
            }
        }

        ValueChanged?.Invoke(this, member);
    }

    /// <summary>Whether two paths pass through the same owner at some point.</summary>
    static bool Shares(IReadOnlyList<MemberDescriptor> left, IReadOnlyList<MemberDescriptor> right) =>
        left.Count > 0 && right.Count > 0 && ReferenceEquals(left[0], right[0]);

    /// <summary>Shows the reset button only when there is something to reset to.</summary>
    /// <remarks>
    ///     The default comes from a fresh instance of the type, made once and kept. A type that
    ///     cannot be constructed — an abstract one, or one without the generated factory — has no
    ///     default to offer, and the button stays hidden rather than resetting to <c>null</c>.
    /// </remarks>
    void Restate(PropertyRow row, MemberDescriptor member, object? value, bool mixed) {
        var isDefault = !mixed && Default(row.Path) is var (known, found) && found && Equals(known, value);

        if (isDefault || !member.CanWrite) {
            row.Reset.AddClass("hidden");
        } else {
            row.Reset.RemoveClass("hidden");
        }
    }

    /// <summary>What a member would hold on a freshly made target.</summary>
    /// <remarks>
    ///     ⚠ <b>Read through the row's path, not off the member.</b> A nested member belongs to
    ///     another type — <c>Origin</c> is a member of <c>Placement</c>, not of the thing being
    ///     inspected — so handing it the fresh top-level instance throws. It cost four failing tests
    ///     to find, and every one of them failed in the same place for the same reason.
    /// </remarks>
    (object? Value, bool Found) Default(IReadOnlyList<MemberDescriptor> path) {
        if (Descriptor is not { CanCreate: true } descriptor || path.Count == 0) {
            return (null, false);
        }

        if (!defaults.TryGetValue(descriptor.Type, out var instance)) {
            instance = descriptor.Create();
            defaults[descriptor.Type] = instance;
        }

        return (Through(instance, path), true);
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not IconButton button) {
            return;
        }

        foreach (var row in rows) {
            if (!ReferenceEquals(row.Reset, button) || row.Member is not { } member) {
                continue;
            }

            if (Default(row.Path) is var (value, found) && found) {
                Write(row, value);
                Show(row);
            }

            args.Handled = true;
            return;
        }
    }

    /// <summary>Hides the rows whose names do not match what is in the search box.</summary>
    /// <remarks>
    ///     ⚠ <b>Hidden rather than removed</b>, so that clearing the box costs a restyle instead of
    ///     rebuilding every editor — and so that a field the user was halfway through typing into
    ///     survives a stray keystroke in the search box.
    /// </remarks>
    void Filter() {
        var text = Search.Value;

        foreach (var row in rows) {
            var name = row.Label.Text ?? string.Empty;

            var matches = string.IsNullOrEmpty(text)
                || name.Contains(text, StringComparison.OrdinalIgnoreCase);

            if (matches) {
                row.RemoveClass("filtered");
            } else {
                row.AddClass("filtered");
            }
        }
    }

    static bool IsNumeric(Type type) =>
        type == typeof(float) || type == typeof(double) || IsIntegral(type);

    static bool IsIntegral(Type type) =>
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
        || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte);

    /// <summary>Converts an edited number back to the member's own type.</summary>
    /// <remarks>
    ///     ⚠ Every numeric editor works in <c>double</c> or <c>float</c>, and a member declared
    ///     <c>int</c> that was handed one would throw inside the generated setter's cast. The
    ///     conversion is here rather than in the editor because the editor does not know what it is
    ///     editing.
    /// </remarks>
    static object Convert(double value, Type type) =>
        System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
}
