// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector.Drawers;

/// <summary>One slot of a list, as a member so that the ordinary drawers can edit it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not generated, because there is nothing in the source to generate it from.</b> Every
///         other <see cref="InspectorMember" /> describes something somebody declared and named; a
///         list's third element is neither. So this is the one member type built at run time, and it
///         is deliberately the boxed kind: <see cref="IList" />'s indexer already boxes, so a generic
///         version would buy nothing and would need <c>MakeGenericType</c> — the one thing the
///         descriptor layer exists to avoid.
///     </para>
///     <para>
///         The owner is the list, not the object that holds it. That is what lets
///         <see cref="ListDrawer" /> hand each element row a field over a <i>working copy</i> and push
///         the whole copy back through the outer member as one undoable edit.
///     </para>
/// </remarks>
public sealed class ListElementMember : InspectorMember {
    readonly int index;

    /// <inheritdoc />
    public override Type MemberType { get; }

    /// <inheritdoc />
    public override Type OwnerType => typeof(IList);

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <summary>Describes one slot.</summary>
    /// <param name="index">Which slot.</param>
    /// <param name="elementType">What the list holds.</param>
    /// <param name="template">The declared member, whose presentation the elements inherit.</param>
    /// <remarks>
    ///     ⚠ <b>The element inherits the declared member's presentation.</b> A
    ///     <c>[Range(0, 1)] float[] Weights</c> means every weight is a slider — the attribute is a
    ///     statement about the values, and dropping it would make the elements of an annotated array
    ///     the one place the annotation did not apply.
    /// </remarks>
    public ListElementMember(int index, Type elementType, InspectorMember template)
        : base(Slot(index, template), "Element " + index.ToString(CultureInfo.InvariantCulture)) {
        ArgumentNullException.ThrowIfNull(elementType);

        this.index = index;
        MemberType = elementType;

        Tooltip = template.Tooltip;
        Range = template.Range;
        Color = template.Color;
        Curve = template.Curve;
        AssetType = template.AssetType;
        AllowNull = template.AllowNull;
        Lines = template.Lines;
        Attributes = template.Attributes;
        IsReadOnly = template.IsReadOnly;
    }

    /// <inheritdoc />
    public override object? GetBoxed(object owner) {
        ArgumentNullException.ThrowIfNull(owner);

        var list = (IList) owner;

        return index < list.Count ? list[index] : null;
    }

    /// <inheritdoc />
    public override void SetBoxed(object owner, object? value) {
        ArgumentNullException.ThrowIfNull(owner);

        var list = (IList) owner;

        if (index < list.Count) {
            list[index] = value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Reachable only if somebody binds an element field to a document, which
    ///     <see cref="ListDrawer" /> deliberately does not.</b> The drawer edits a copy and records
    ///     the whole list as one step, because a per-element command would make "clear the list" four
    ///     undos. This is here so that a caller who does bind one gets a working command rather than
    ///     an exception out of a code path nothing exercises.
    /// </remarks>
    public override IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        EditorDocument? document
    ) {
        ArgumentNullException.ThrowIfNull(targets);

        var slot = index;
        var lists = targets.Cast<IList>().ToArray();
        var previous = lists.Select(list => slot < list.Count ? list[slot] : null).ToArray();

        return new DelegateCommand(
            "Set " + DisplayName,
            _ => {
                foreach (var list in lists) {
                    if (slot < list.Count) {
                        list[slot] = value;
                    }
                }
            },
            _ => {
                for (var target = 0; target < lists.Length; target++) {
                    if (slot < lists[target].Count) {
                        lists[target][slot] = previous[target];
                    }
                }
            }
        );
    }

    static string Slot(int index, InspectorMember template) {
        ArgumentNullException.ThrowIfNull(template);

        return template.Name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
    }
}

/// <summary>A list or an array: a foldout of element rows, with add, remove and reorder.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20 calls a browser that cannot rename worse than no browser</b>, and the same
///         applies here: a list drawn as <c>System.Collections.Generic.List`1[Vixen.Core.AssetId]</c>
///         — which is what the last resort produces — is a promise the inspector breaks the first
///         time somebody needs a material slot.
///     </para>
///     <para>
///         ⚠ <b>Every change is copy-on-write, and that is what makes undo work at all.</b> A list is
///         a reference type, so mutating the one the object holds leaves the undo command recording
///         that same reference as its "before" — and the step would undo to the value it had just
///         been changed to. So the drawer copies on the way in, edits the copy, and writes a fresh
///         copy back through the outer member.
///     </para>
///     <para>
///         ⚠ <b>A mixed selection is refused rather than merged.</b> Two objects hold two different
///         list objects, so there is no shared row three — and an inspector that showed the first
///         one's would let an edit to it silently resize the others.
///     </para>
///     <para>
///         <b>Owed:</b> drag to reorder. The arrows are complete and a drag is better; it needs the
///         row to be a drop target, which is a control-level change rather than a drawer one.
///     </para>
/// </remarks>
public sealed class ListDrawer : IPropertyDrawer {
    /// <inheritdoc cref="NestedDrawer.Drawers" />
    public DrawerRegistry? Drawers { get; set; }

    /// <inheritdoc />
    public bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        // ⚠ Not a string, which is an `IEnumerable<char>` and has a drawer of its own; and not a
        // dictionary, which is a pair of columns rather than a column of values and is owed rather
        // than approximated.
        return ElementType(member.MemberType) is not null;
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var editor = parent.Add<CompositeEditor>();

        // ⚠ The outer row's own label is hidden by the theme when this class is on it. A list does
        // not fit in the editor column beside a label — it is a block of rows — so the foldout
        // carries the name and the row spans.
        if (parent.Parent is InspectorRow outer) {
            outer.AddClass("nested");
        }

        editor.Fold = editor.Add<Expander>();
        editor.Fold.Label = field.Member.DisplayName;
        editor.Fold.IsExpanded = true;

        Rebuild(field, editor);
        return editor;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        if (editor is not CompositeEditor composite) {
            return;
        }

        var list = Current(field);

        // ⚠ Rebuilt when the *shape* changed, not only when a value did. An undo of "remove element"
        // puts a row back, and a drawer that merely re-read the values would show four rows over a
        // list of five — with row three editing what is now row four.
        if (list is null || list.Count != composite.Rows.Count) {
            Rebuild(field, composite);
            return;
        }

        Working(composite, field, list);

        foreach (var row in composite.Rows) {
            InspectorRows.Show(row);
        }
    }

    void Rebuild(InspectorField field, CompositeEditor editor) {
        while (editor.Fold.Content.Children.Count > 0) {
            editor.Fold.Content.Children[^1].Remove();
        }

        editor.Rows.Clear();
        editor.Working.Clear();

        var element = ElementType(field.Member.MemberType);
        var list = element is null ? null : Current(field);

        if (element is null || list is null) {
            var absent = editor.Fold.Content.Add<TextBlock>();

            absent.AddClass("property-readonly");

            absent.Text = field.Objects.Count > 1
                ? "The selected objects hold different lists."
                : "None";

            return;
        }

        Working(editor, field, list);

        var summary = editor.Fold.Content.Add<TextBlock>();

        summary.AddClass("property-readonly");
        summary.Text = list.Count == 1 ? "1 element" : list.Count + " elements";

        for (var index = 0; index < list.Count; index++) {
            var slot = index;
            var member = new ListElementMember(slot, element, field.Member);

            // ⚠ No document. The element write lands in the working copy and the *whole copy* is
            // then pushed through the outer member, which is the one thing that reaches the undo
            // stack — otherwise editing one element would be two steps, one of which undoes nothing
            // anybody can see.
            var child = new InspectorField(field.Descriptor, member, editor.Working);

            var row = InspectorRows.Add(
                editor.Fold.Content,
                child,
                Drawers ?? DrawerRegistry.Default,
                made => child.Changed += _ => {
                    Commit(field, editor);
                    InspectorRows.Restate(made);
                }
            );

            if (row is null) {
                continue;
            }

            editor.Rows.Add(row);

            if (!field.CanWrite) {
                continue;
            }

            Button(row, "list-up", ControlIcons.ChevronUp, "Move up", () => Move(field, editor, slot, -1))
                .Disabled = slot == 0;

            Button(row, "list-down", ControlIcons.ChevronDown, "Move down", () => Move(field, editor, slot, 1))
                .Disabled = slot == list.Count - 1;

            Button(row, "list-remove", ControlIcons.Close, "Remove", () => Resize(field, editor, list.Count - 1, slot));
        }

        if (!field.CanWrite) {
            return;
        }

        var add = editor.Fold.Content.Add<Button>();

        add.Label = "Add Element";
        add.Variant = ControlVariant.Subtle;
        add.Size = ControlSize.Small;
        add.AddClass("list-add");
        add.Clicked += _ => Resize(field, editor, list.Count + 1, remove: -1);
    }

    /// <summary>Puts a fresh copy of the object's list in front of the element rows.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy, not the list itself.</b> The rows write straight into whatever is here, and
    ///     if that were the object's own list the undo command's "before" would be the same reference
    ///     as its "after".
    /// </remarks>
    static void Working(CompositeEditor editor, InspectorField field, IList list) {
        if (Copy(field.Member.MemberType, list, list.Count, remove: -1) is not { } copy) {
            return;
        }

        editor.Working.Clear();
        editor.Working.Add(copy);
    }

    /// <summary>Writes the working copy back through the outer member.</summary>
    /// <remarks>
    ///     A copy per target, so that two objects never end up sharing one list object — which would
    ///     make editing one of them silently edit the other, and would make the next undo's "before"
    ///     wrong for both.
    /// </remarks>
    static void Commit(InspectorField field, CompositeEditor editor) {
        if (editor.Working.Count == 0 || editor.Working[0] is not IList edited) {
            return;
        }

        var copies = new object?[field.Objects.Count];

        for (var index = 0; index < copies.Length; index++) {
            copies[index] = Copy(field.Member.MemberType, edited, edited.Count, remove: -1);
        }

        field.WriteEach(copies);
        field.Seal();
    }

    void Move(InspectorField field, CompositeEditor editor, int from, int delta) {
        if (editor.Working.Count == 0 || editor.Working[0] is not IList list) {
            return;
        }

        var to = from + delta;

        if (to < 0 || to >= list.Count) {
            return;
        }

        (list[from], list[to]) = (list[to], list[from]);

        Commit(field, editor);
        Rebuild(field, editor);
    }

    void Resize(InspectorField field, CompositeEditor editor, int count, int remove) {
        if (editor.Working.Count == 0 || editor.Working[0] is not IList list) {
            return;
        }

        if (Copy(field.Member.MemberType, list, Math.Max(count, 0), remove) is not { } resized) {
            return;
        }

        var copies = new object?[field.Objects.Count];

        for (var index = 0; index < copies.Length; index++) {
            copies[index] = index == 0
                ? resized
                : Copy(field.Member.MemberType, list, Math.Max(count, 0), remove);
        }

        field.WriteEach(copies);
        field.Seal();

        Rebuild(field, editor);
    }

    /// <summary>The list every target holds, or <see langword="null" /> when they disagree.</summary>
    static IList? Current(InspectorField field) {
        var (value, mixed) = field.Read();

        return mixed ? null : value as IList;
    }

    /// <summary>A new collection of the declared type, resized and with one slot dropped.</summary>
    /// <param name="type">The declared member type — an array, a list, or a list interface.</param>
    /// <param name="source">What to copy from.</param>
    /// <param name="count">How many elements the result holds.</param>
    /// <param name="remove">The index to leave out, or −1 to keep them all.</param>
    /// <returns>The new collection, or <see langword="null" /> when nothing can make one.</returns>
    /// <remarks>
    ///     ⚠ <b>The dynamic-code calls this makes are the reason a runtime binder cannot.</b>
    ///     <c>Array.CreateInstance</c> and <c>MakeGenericType</c> are <c>RequiresDynamicCode</c> and
    ///     throw on a NativeAOT target, which is why <c>CollectionFactory</c> exists on the
    ///     serialization side — a generator writes the constructor there. An editor is not a
    ///     NativeAOT target and never will be, its build profile says so, and doing this here means a
    ///     list works in the inspector even for a type no generator ever saw.
    /// </remarks>
    static object? Copy(Type type, IList source, int count, int remove) {
        if (ElementType(type) is not { } element) {
            return null;
        }

        var made = type.IsArray
            ? Array.CreateInstance(element, count)
            : Activator.CreateInstance(type.IsInterface || type.IsAbstract ? typeof(List<>).MakeGenericType(element) : type);

        if (made is not IList target) {
            return null;
        }

        var written = 0;

        for (var index = 0; index < source.Count && written < count; index++) {
            if (index == remove) {
                continue;
            }

            Put(target, written++, source[index]);
        }

        // A grown list is padded with the element type's own default, which for a struct is a zeroed
        // value and for a class is null. Not a fresh instance: a list of behaviours whose Add button
        // constructed one would be the inspector deciding what "a new one" means.
        while (written < count) {
            Put(target, written++, element.IsValueType ? Activator.CreateInstance(element) : null);
        }

        return target;
    }

    static void Put(IList target, int index, object? value) {
        if (index < target.Count) {
            target[index] = value;
        } else {
            target.Add(value);
        }
    }

    /// <summary>What a member's list holds, or <see langword="null" /> if it is not a list at all.</summary>
    static Type? ElementType(Type type) {
        if (type == typeof(string)) {
            return null;
        }

        if (type.IsArray) {
            return type.GetArrayRank() == 1 ? type.GetElementType() : null;
        }

        if (!typeof(IList).IsAssignableFrom(type)) {
            return null;
        }

        foreach (var candidate in type.GetInterfaces()) {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IList<>)) {
                return candidate.GetGenericArguments()[0];
            }
        }

        // A non-generic IList holds objects, which is drawable and rarely what anybody meant.
        return typeof(object);
    }

    static IconButton Button(UiElement row, string className, PathBuilder icon, string label, Action clicked) {
        var button = row.Add<IconButton>();

        button.LeadingIcon.Geometry = icon;
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.Label = label;
        button.TabIndex = -1;
        button.AddClass(className);
        button.Clicked += _ => clicked();

        return button;
    }
}
