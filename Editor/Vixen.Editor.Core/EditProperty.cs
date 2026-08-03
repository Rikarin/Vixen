// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>One member bound to the objects being edited: read it, write it, undo it.</summary>
/// <remarks>
///     <para>
///         <b>The single edit path doc 36 § D1 asks for.</b> Every editing surface — the inspector,
///         a graph editor, a settings page, a plugin's own panel — writes through one of these, and
///         gets undo, multi-object editing, mixed-value reporting and change notification without
///         any of them implementing those four again. An editor that has five of these has five
///         answers to "what happens when twenty things are selected", which is how the editor got
///         five of them.
///     </para>
///     <para>
///         ⚠ <b>Not to be confused with <see cref="EditorProperty{T}" />.</b> That is a signal on one
///         object, part of the document model, and it is what a <c>SceneDocument</c>'s own fields
///         are. This is a <i>binding</i>: one member, N objects, no storage of its own. They meet at
///         the command stack and nowhere else.
///     </para>
///     <para>
///         <b>Every write goes through the document's command stack</b>, which is the whole reason
///         this type sits between a control and a member. A property with no document writes
///         directly and is not undoable; that is the case where something is being previewed rather
///         than edited — a material thumbnail, a fixture in a test — and the alternatives are
///         refusing to draw it or manufacturing a document for it.
///     </para>
/// </remarks>
public class EditProperty {
    int refreshing;

    /// <summary>The member being edited.</summary>
    public IEditMember Member { get; }

    /// <summary>What it is being edited on, in selection order.</summary>
    public IReadOnlyList<object> Objects { get; }

    /// <summary>Where the writes are recorded, if anywhere.</summary>
    public EditorDocument? Document { get; }

    /// <summary>Raised after a write of any kind lands.</summary>
    /// <remarks>
    ///     What a view listens to so a row restates itself, and what a viewport listens to so a gizmo
    ///     follows a number typed into a panel. It fires for every path that writes, because a view
    ///     that only heard about some of them shows a stale reset button after the others.
    /// </remarks>
    public event Action<EditProperty>? Changed;

    /// <summary>Whether this member will be written at all.</summary>
    public virtual bool CanWrite => Member.CanWrite && Objects.Count > 0;

    /// <summary>Which of <see cref="Objects" /> a write actually reaches.</summary>
    /// <remarks>
    ///     Every object, unless a deriving type says otherwise. The inspector's is the case that
    ///     motivated it: a row shown because <i>any</i> selected object satisfies its condition must
    ///     not write to the ones whose flag is off, because the user cannot see their state.
    /// </remarks>
    protected virtual IReadOnlyList<object> Reached => Objects;

    /// <summary>Binds a member to a selection.</summary>
    /// <param name="member">The member.</param>
    /// <param name="objects">The objects, in selection order.</param>
    /// <param name="document">Where writes are recorded, or <see langword="null" />.</param>
    public EditProperty(IEditMember member, IReadOnlyList<object> objects, EditorDocument? document = null) {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(objects);

        Member = member;
        Objects = objects;
        Document = document;
    }

    /// <summary>Reads the member off every object.</summary>
    /// <returns>The shared value, or the fact that they differ.</returns>
    public EditValue Read() {
        if (Objects.Count == 0) {
            return EditValue.None;
        }

        var first = Member.Read(Objects[0]);

        for (var index = 1; index < Objects.Count; index++) {
            if (!Equals(first, Member.Read(Objects[index]))) {
                return new(null, true);
            }
        }

        return new(first, false);
    }

    /// <summary>Writes the member on every object it reaches, undoably.</summary>
    /// <param name="value">The new value, boxed.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     <para>
    ///         Writing the value they all already hold does nothing and records nothing — a slider
    ///         that re-emits its position every frame of a drag it is not moving in leaves no trace,
    ///         the same rule <see cref="EditorProperty{T}.Set" /> follows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A mixed property always writes.</b> The values differ, so there is no "already
    ///         holds it" to short-circuit on, and short-circuiting on the first object's value would
    ///         make "apply to all" silently do nothing whenever the value you typed happened to match
    ///         the one you were looking at.
    ///     </para>
    /// </remarks>
    public bool Write(object? value) {
        if (!CanWrite || refreshing > 0) {
            return false;
        }

        var current = Read();

        if (!current.IsMixed && Equals(current.Value, value)) {
            return false;
        }

        var reached = Reached;

        return reached.Count != 0 && Apply(reached, value);
    }

    /// <summary>Writes a different value to each object, as one undo step.</summary>
    /// <param name="values">One value per object, in <see cref="Objects" />' order.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentException">There is not exactly one value per object.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What a composite editor needs and <see cref="Write" /> cannot express.</b> A
    ///         nested struct is read as a box per object; editing one leaf of it changes a different
    ///         box for every object, and writing the <i>first</i> object's whole struct to all of them
    ///         — which is what one <see cref="Write" /> would do — silently rewrites the other leaves
    ///         as well. Twenty objects at twenty different positions, one of which you nudged along X,
    ///         must not all end up at the first one's Y and Z.
    ///     </para>
    ///     <para>One transaction, so the whole thing is one Ctrl+Z.</para>
    /// </remarks>
    public bool WriteEach(IReadOnlyList<object?> values) {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count != Objects.Count) {
            throw new ArgumentException(
                $"There are {Objects.Count} objects and {values.Count} values. A per-object write has to "
                + "say what each object gets, and a short list would silently leave some of them alone.",
                nameof(values)
            );
        }

        if (!CanWrite || refreshing > 0) {
            return false;
        }

        var reached = Reached;
        using var transaction = Document?.Stack.BeginTransaction($"Set {Member.DisplayName}");
        var changed = false;

        for (var index = 0; index < values.Count; index++) {
            var target = Objects[index];

            // Reference equality rather than `Contains`, because a boxed value target would compare
            // equal to a different object holding the same value.
            if (!ReferenceEquals(reached, Objects) && !Reaches(reached, target)) {
                continue;
            }

            if (Equals(Member.Read(target), values[index])) {
                continue;
            }

            changed |= Apply([target], values[index]);
        }

        return changed;

        static bool Reaches(IReadOnlyList<object> reached, object target) {
            foreach (var candidate in reached) {
                if (ReferenceEquals(candidate, target)) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Holds off writes while a control is being filled in from the model.</summary>
    /// <returns>A scope to dispose when the control is done.</returns>
    /// <remarks>
    ///     ⚠ <b>Without this a mixed property destroys the values it was showing.</b> Putting a value
    ///     into a control raises the control's own changed event, which calls <see cref="Write" />;
    ///     ordinarily that is harmless because the value is the one already held. A <i>mixed</i>
    ///     property has no such value — it parks the control at a neutral position, unchecked or
    ///     empty — and that neutral position would be written to every selected object the moment the
    ///     control was drawn. Every inspector has this bug once.
    ///     <para>
    ///         Here rather than in each control, so a third-party editor gets it without knowing it
    ///         exists, and reference-counted so a composite refreshing its children nests.
    ///     </para>
    /// </remarks>
    public IDisposable Refreshing() {
        refreshing++;
        return new RefreshScope(this);
    }

    /// <summary>Ends the run of edits that were collapsing into one undo entry.</summary>
    /// <remarks>
    ///     Called on mouse-up and on focus loss. A time window would make how many undo steps an edit
    ///     produced depend on how fast somebody moved a mouse — see <see cref="CommandStack.Seal" />.
    /// </remarks>
    public void Seal() => Document?.Stack.Seal();

    /// <summary>Records the edit on the document's stack, or writes straight through when there is none.</summary>
    /// <param name="reached">The objects the write lands on.</param>
    /// <param name="value">The new value, boxed.</param>
    /// <returns>Whether anything was written, which at this point is always.</returns>
    protected bool Apply(IReadOnlyList<object> reached, object? value) {
        if (Document?.Stack is { } stack) {
            stack.Execute(Member.CreateSetCommand(reached, value, Document));
            Changed?.Invoke(this);

            return true;
        }

        foreach (var target in reached) {
            Member.Write(target, value);
        }

        Changed?.Invoke(this);
        return true;
    }

    sealed class RefreshScope(EditProperty property) : IDisposable {
        bool done;

        public void Dispose() {
            if (!done) {
                done = true;
                property.refreshing--;
            }
        }
    }
}
