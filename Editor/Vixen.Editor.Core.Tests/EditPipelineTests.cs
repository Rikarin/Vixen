// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>Something with two members, one of which a slider would drag.</summary>
sealed class Widget {
    public float Size { get; set; }

    public string Label { get; set; } = "none";
}

/// <summary>A member described by hand, standing in for what a generator emits.</summary>
/// <remarks>
///     ⚠ <b>It builds a <see cref="SetValuesCommand" />, which is the point of these tests.</b> An
///     implementation with no typed accessors — a graph port, a settings row, a plugin's own member —
///     gets undo, merging and per-object old values from the pipeline rather than writing them, and
///     that is what "one editing pipeline" has to mean for anyone outside this repository.
/// </remarks>
sealed class TestMember(
    string name,
    Func<Widget, object?> read,
    Action<Widget, object?> write,
    bool coalescesEdits
) : IEditMember {
    public string Name => name;

    public string DisplayName => name;

    public Type ValueType => typeof(object);

    public bool CanWrite { get; init; } = true;

    public bool CoalescesEdits => coalescesEdits;

    public object? Read(object owner) => read((Widget) owner);

    public void Write(object owner, object? value) => write((Widget) owner, value);

    public IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        EditorDocument? document
    ) {
        var previous = new object?[targets.Count];

        for (var index = 0; index < targets.Count; index++) {
            previous[index] = Read(targets[index]);
        }

        return new SetValuesCommand(this, targets, previous, value, document);
    }
}

/// <summary>The two members of <see cref="Widget" />, by name.</summary>
sealed class TestProvider : IEditProvider {
    readonly IEditMember[] members = [
        new TestMember("Size", static widget => widget.Size, static (widget, value) => widget.Size = (float) value!, true),
        new TestMember("Label", static widget => widget.Label, static (widget, value) => widget.Label = (string) value!, false)
    ];

    public IReadOnlyList<IEditMember> MembersOf(Type type) => type == typeof(Widget) ? members : [];

    public bool TryResolve(Type type, string path, [NotNullWhen(true)] out IEditMember? member) {
        member = type == typeof(Widget)
            ? Array.Find(members, candidate => candidate.Name == path)
            : null;

        return member is not null;
    }
}

/// <summary>Doc 36 § D1: one edit path, and what it has to answer to be worth having.</summary>
public class EditPipelineTests {
    static (TestDocument Document, EditTarget Target) Bound(params Widget[] widgets) {
        var document = new TestDocument(ModelFixture.Project());

        return (document, new EditTarget(widgets, new TestProvider(), document));
    }

    static EditProperty Property(EditTarget target, string name) =>
        target.Find(name) ?? throw new InvalidOperationException($"'{name}' is not a member of the target.");

    [Fact]
    public void Objects_that_agree_read_as_one_value_and_objects_that_disagree_read_as_mixed() {
        var (_, agreeing) = Bound(new Widget { Size = 2f }, new Widget { Size = 2f });

        Assert.Equal(new EditValue(2f, false), Property(agreeing, "Size").Read());

        var (_, disagreeing) = Bound(new Widget { Size = 2f }, new Widget { Size = 5f });
        var mixed = Property(disagreeing, "Size").Read();

        // ⚠ Not one of the two values. An inspector showing 2 for a selection that also holds 5 is
        // one where changing something else silently rewrites the object you were not looking at.
        Assert.True(mixed.IsMixed);
        Assert.Null(mixed.Value);
        Assert.Equal(-1f, mixed.Or(-1f));
    }

    [Fact]
    public void One_write_across_a_mixed_selection_undoes_each_object_to_its_own_value() {
        var first = new Widget { Size = 2f };
        var second = new Widget { Size = 5f };
        var (document, target) = Bound(first, second);

        Assert.True(Property(target, "Size").Write(9f));
        Assert.Equal(9f, first.Size);
        Assert.Equal(9f, second.Size);

        // One entry for the whole selection, not one per object: editing a field with two things
        // selected is one edit and taking it back is one keystroke.
        Assert.Equal(1, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(2f, first.Size);
        Assert.Equal(5f, second.Size);
    }

    [Fact]
    public void A_mixed_property_writes_even_the_value_the_first_object_already_holds() {
        var first = new Widget { Size = 2f };
        var second = new Widget { Size = 5f };
        var (_, target) = Bound(first, second);

        // ⚠ Typing the value you were looking at must still apply to all. Short-circuiting on the
        // first object's value is how "apply to all" silently does nothing.
        Assert.True(Property(target, "Size").Write(2f));
        Assert.Equal(2f, second.Size);
    }

    [Fact]
    public void Writing_what_they_all_already_hold_records_nothing() {
        var (document, target) = Bound(new Widget { Size = 2f }, new Widget { Size = 2f });

        Assert.False(Property(target, "Size").Write(2f));
        Assert.Equal(0, document.Stack.Depth.Value);
    }

    [Fact]
    public void A_drag_is_one_entry_and_sealing_ends_it() {
        var widget = new Widget();
        var (document, target) = Bound(widget);
        var size = Property(target, "Size");

        for (var step = 1; step <= 20; step++) {
            size.Write(step * 0.1f);
        }

        // Twenty mouse-moves, one entry, and undoing it goes back to before the drag rather than to
        // one frame ago.
        Assert.Equal(1, document.Stack.Depth.Value);

        size.Seal();
        size.Write(5f);
        Assert.Equal(2, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(2f, widget.Size, 3);

        document.Stack.Undo();
        Assert.Equal(0f, widget.Size, 3);
    }

    [Fact]
    public void A_member_that_does_not_coalesce_records_every_write() {
        var (document, target) = Bound(new Widget());
        var label = Property(target, "Label");

        Assert.True(label.Write("one"));
        Assert.True(label.Write("two"));

        // Two edits to a dropdown are two decisions, and collapsing them takes away an undo the
        // user is entitled to.
        Assert.Equal(2, document.Stack.Depth.Value);
    }

    [Fact]
    public void A_per_object_write_is_still_one_undo_step() {
        var first = new Widget { Size = 1f };
        var second = new Widget { Size = 2f };
        var (document, target) = Bound(first, second);

        Assert.True(Property(target, "Size").WriteEach([10f, 20f]));

        Assert.Equal(10f, first.Size);
        Assert.Equal(20f, second.Size);
        Assert.Equal(1, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(1f, first.Size);
        Assert.Equal(2f, second.Size);
    }

    [Fact]
    public void A_per_object_write_refuses_a_list_that_does_not_cover_every_object() {
        var (_, target) = Bound(new Widget(), new Widget());

        Assert.Throws<ArgumentException>(() => Property(target, "Size").WriteEach([1f]));
    }

    [Fact]
    public void Filling_a_control_in_from_the_model_does_not_write_back() {
        var widget = new Widget { Size = 3f };
        var (document, target) = Bound(widget);
        var size = Property(target, "Size");

        using (size.Refreshing()) {
            // What a control raises while it is being given its value. Without the guard this is the
            // write that flattens a mixed selection the moment its row is drawn.
            Assert.False(size.Write(0f));
        }

        Assert.Equal(3f, widget.Size);
        Assert.Equal(0, document.Stack.Depth.Value);

        // And the guard lifts.
        Assert.True(size.Write(0f));
    }

    [Fact]
    public void The_same_member_asked_for_twice_is_the_same_binding() {
        var (_, target) = Bound(new Widget());
        var raised = 0;

        // ⚠ The whole reason `Changed` is subscribable: a panel that asked for its members every
        // frame would otherwise hand out an event nothing is ever raised on.
        target.Find("Size")!.Changed += _ => raised++;

        Assert.Same(target.Find("Size"), target.Find("Size"));
        Assert.True(Property(target, "Size").Write(1f));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_selection_of_two_different_types_has_nothing_in_common_to_edit() {
        var document = new TestDocument(ModelFixture.Project());
        var target = new EditTarget([new Widget(), "not a widget"], new TestProvider(), document);

        // One type, not a common base: falling back to what a widget and a string both derive from
        // would make which editors appear depend on what else happened to be selected.
        Assert.Null(target.CommonType);
        Assert.True(target.IsEmpty);
        Assert.Empty(target.Members);
        Assert.Null(target.Find("Size"));
    }

    [Fact]
    public void A_target_with_no_document_writes_and_is_not_undoable() {
        var widget = new Widget();
        var target = new EditTarget([widget], new TestProvider());

        // The case where something is being previewed rather than edited. Refusing to draw it or
        // manufacturing a document for it are both worse.
        Assert.True(Property(target, "Size").Write(4f));
        Assert.Equal(4f, widget.Size);
    }

    [Fact]
    public void A_target_with_no_provider_describes_nothing_rather_than_throwing() {
        var target = new EditTarget([new Widget()]);

        Assert.Empty(target.Members);
        Assert.False(target.TryFind("Size", out _));
    }

    [Fact]
    public void Every_listed_member_binds() {
        var (_, target) = Bound(new Widget());

        Assert.Equal(["Size", "Label"], target.Properties().Select(static property => property.Member.Name));
    }
}
