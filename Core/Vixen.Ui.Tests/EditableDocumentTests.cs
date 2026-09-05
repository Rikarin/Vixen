// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A document below the editor: dirty as a signal, save and revert through the route.</summary>
public class EditableDocumentTests {
    /// <summary>What an application's own document looks like: three lines and two overrides.</summary>
    sealed class Note(string name, string? location = null) : EditableDocument(name, location) {
        public int Saves { get; private set; }

        public int Reverts { get; private set; }

        public bool Refuse { get; set; }

        protected override bool OnSave() {
            Saves++;

            return !Refuse;
        }

        protected override bool OnRevert() {
            Reverts++;

            return !Refuse;
        }
    }

    sealed class FakeWindow : IUiWindow {
        public UiSurface Surface => throw new NotSupportedException();

        public string Title { get; set; } = "";

        public (float X, float Y, float Width, float Height) Bounds { get; set; }

        public float DpiScale => 1f;

        public bool IsClosed => false;

        public void Focus() { }

        // Never raised here: the title binding reads a document and writes `Title`, and neither
        // event is on that path. Declared as add/remove so an unused backing field is not a warning.
        public event Action<IUiWindow>? CloseRequested {
            add { }
            remove { }
        }

        public event Action<IUiWindow>? DidBecomeKey {
            add { }
            remove { }
        }

        public event Action<IUiWindow>? Moved {
            add { }
            remove { }
        }

        public void Dispose() { }
    }

    static UiDocument Laid() {
        var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; }
            .panel { width: 200px; height: 100px; }
            .field { width: 50px; height: 20px; }
        """);

        return document;
    }

    [Fact]
    public void A_document_is_clean_until_something_says_otherwise_and_saving_makes_it_clean_again() {
        var note = new Note("Untitled 1");

        Assert.False(note.IsDirty.Value);
        Assert.Null(note.Location.Value);

        note.MarkDirty();
        Assert.True(note.IsDirty.Value);

        Assert.True(note.Save());
        Assert.Equal(1, note.Saves);
        Assert.False(note.IsDirty.Value);
    }

    /// <summary>A save that could not write leaves the changes where they were.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a base class gets wrong by being helpful.</b> Marking clean before asking
    ///     the subclass, or marking clean regardless of what it answered, turns a full disk into a
    ///     document that says it is saved and is not — and the next thing that happens is the user
    ///     closing it without a prompt.
    /// </remarks>
    [Fact]
    public void A_save_that_fails_leaves_the_document_dirty() {
        var note = new Note("Notes.txt", "/tmp/Notes.txt") { Refuse = true };
        note.MarkDirty();

        Assert.False(note.Save());
        Assert.True(note.IsDirty.Value);

        Assert.False(note.Revert());
        Assert.True(note.IsDirty.Value);

        note.Refuse = false;
        Assert.True(note.Revert());
        Assert.False(note.IsDirty.Value);
        Assert.Equal(2, note.Reverts);
    }

    /// <summary>Save writes even when there is nothing to write, and the menu item is what greys.</summary>
    [Fact]
    public void Saving_a_clean_document_still_writes_it() {
        var note = new Note("Notes.txt", "/tmp/Notes.txt");

        Assert.True(note.Save());
        Assert.Equal(1, note.Saves);
    }

    /// <summary>Renaming is what a Save As does once it has a path.</summary>
    [Fact]
    public void Renaming_moves_the_name_and_optionally_the_location() {
        var note = new Note("Untitled 1");

        note.Rename("Chapter 3.md", "/tmp/Chapter 3.md");
        Assert.Equal("Chapter 3.md", note.Name.Value);
        Assert.Equal("/tmp/Chapter 3.md", note.Location.Value);

        note.Rename("Chapter 4.md");
        Assert.Equal("/tmp/Chapter 3.md", note.Location.Value);
    }

    /// <summary>The nearest host answers, which is what makes two open documents work.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason these are commands and not a service.</b> An application with two panels
    ///     has two answers to ⌘S and the right one is decided by where the focus is. A save routed to
    ///     "the application's document" writes the wrong file, quietly.
    /// </remarks>
    [Fact]
    public void Save_goes_to_the_document_the_focus_is_in() {
        using var document = Laid();
        var left = document.Root.Add("div", classNames: "panel");
        var leftField = left.Add("div", classNames: "field");
        var right = document.Root.Add("div", classNames: "panel");
        var rightField = right.Add("div", classNames: "field");
        leftField.Focusable = true;
        rightField.Focusable = true;

        var first = new Note("First");
        var second = new Note("Second");
        left.HostedDocument = first;
        right.HostedDocument = second;
        DocumentCommands.Install(left);
        DocumentCommands.Install(right);
        document.Update();

        first.MarkDirty();
        second.MarkDirty();

        document.Focus(leftField);
        Assert.True(CommandRoute.Execute(document, DocumentCommands.Save));
        Assert.Equal(1, first.Saves);
        Assert.Equal(0, second.Saves);

        document.Focus(rightField);
        Assert.True(CommandRoute.Execute(document, DocumentCommands.Save));
        Assert.Equal(1, first.Saves);
        Assert.Equal(1, second.Saves);
    }

    /// <summary>Save is greyed while there is nothing to write, and lives the moment there is.</summary>
    [Fact]
    public void Save_is_greyed_while_the_document_is_clean() {
        using var document = Laid();
        var panel = document.Root.Add("div", classNames: "panel");
        var field = panel.Add("div", classNames: "field");
        field.Focusable = true;
        var note = new Note("Notes.txt", "/tmp/Notes.txt");
        panel.HostedDocument = note;
        DocumentCommands.Install(panel);
        document.Update();
        document.Focus(field);

        Assert.NotNull(CommandRoute.Resolve(document, DocumentCommands.Save));
        Assert.False(CommandRoute.CanExecute(document, DocumentCommands.Save));

        note.MarkDirty();
        Assert.True(CommandRoute.CanExecute(document, DocumentCommands.Save));

        Assert.True(CommandRoute.Execute(document, DocumentCommands.Save));
        Assert.False(CommandRoute.CanExecute(document, DocumentCommands.Save));
    }

    /// <summary>A dirty change tells the route it is out of date, so a greyed item can come back.</summary>
    /// <remarks>
    ///     ⚠ <b>Command state is pulled, once per raise, by whatever is showing it.</b> A signal that
    ///     changed and raised nothing leaves Save greyed until something unrelated invalidates —
    ///     which looks exactly like Save being broken. What proves it here is the raise, not the
    ///     predicate: <c>CanExecute</c> above would answer correctly with no invalidation at all,
    ///     because a test asks the handler directly and a menu does not.
    /// </remarks>
    [Fact]
    public void Marking_a_document_dirty_raises_the_command_invalidation_a_menu_listens_for() {
        using var document = Laid();
        var panel = document.Root.Add("div", classNames: "panel");
        var note = new Note("Notes.txt", "/tmp/Notes.txt");
        panel.HostedDocument = note;
        DocumentCommands.Install(panel);

        // Settled first: registering a handler invalidates the route on its own, and so does the
        // effect's queued first run, so both raises belong to the setup and not to the assertion.
        document.Update();
        document.Tick(TimeSpan.Zero);

        var raised = 0;
        document.CommandsInvalidated += _ => raised++;

        // ⚠ `Tick` and not `Update`: the raise is coalesced to one per *frame* and the frame is what
        // `Tick` opens, so a suite that only calls `Update` never sees a command invalidation at all.
        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(16));
        Assert.Equal(0, raised);

        note.MarkDirty();
        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(32));
        Assert.Equal(1, raised);
    }

    /// <summary>An element finds the nearest document above it, then the tree's.</summary>
    [Fact]
    public void An_element_finds_the_nearest_document_and_falls_back_to_the_trees() {
        using var document = Laid();
        var panel = document.Root.Add("div", classNames: "panel");
        var field = panel.Add("div", classNames: "field");
        document.Update();

        Assert.Null(field.FindHostedDocument());

        var wide = new Note("Wide");
        document.HostedDocument = wide;
        Assert.Same(wide, field.FindHostedDocument());

        var near = new Note("Near");
        panel.HostedDocument = near;
        Assert.Same(near, field.FindHostedDocument());
    }

    /// <summary>Installing on an element that hosts nothing is a mistake, not a silent no-op.</summary>
    [Fact]
    public void Installing_the_commands_without_a_document_throws() {
        using var document = Laid();
        var panel = document.Root.Add("div", classNames: "panel");
        document.Update();

        Assert.Throws<InvalidOperationException>(() => DocumentCommands.Install(panel));
    }

    /// <summary>The title follows the name, and says so while there are unsaved changes.</summary>
    /// <remarks>
    ///     ⚠ <b>The first run is queued rather than immediate</b>, because <c>Effect</c> schedules in
    ///     its constructor. So the title is still the window's own until the next flush — which is
    ///     asserted here rather than worked around, since a caller that reads it in between is
    ///     reading the request's title and would otherwise never find out.
    /// </remarks>
    [Fact]
    public void A_window_title_follows_the_document_and_marks_it_while_it_is_dirty() {
        using var document = Laid();
        var note = new Note("Chapter 3.md", "/tmp/Chapter 3.md");
        using var window = new FakeWindow { Title = "opened with this" };

        using var bound = UiWindowTitle.Bind(window, note, document.Effects);
        Assert.Equal("opened with this", window.Title);

        document.Update();
        Assert.Equal("Chapter 3.md", window.Title);

        note.MarkDirty();
        document.Update();
        Assert.Equal("• Chapter 3.md", window.Title);

        Assert.True(note.Save());
        document.Update();
        Assert.Equal("Chapter 3.md", window.Title);

        note.Rename("Chapter 4.md");
        document.Update();
        Assert.Equal("Chapter 4.md", window.Title);
    }

    /// <summary>A disposed binding stops following, so a closed window is not written to.</summary>
    [Fact]
    public void A_disposed_title_binding_stops_following() {
        using var document = Laid();
        var note = new Note("Chapter 3.md");
        using var window = new FakeWindow();

        var bound = UiWindowTitle.Bind(window, note, document.Effects);
        document.Update();
        Assert.Equal("Chapter 3.md", window.Title);

        bound.Dispose();
        note.Rename("Chapter 4.md");
        document.Update();
        Assert.Equal("Chapter 3.md", window.Title);
    }
}
