// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The recent-documents list one level below the editor, keyed on <see cref="IEditableDocument.Location" /> (#656).</summary>
public class RecentDocumentsTests {
    sealed class Note(string name, string? location = null) : EditableDocument(name, location) {
        protected override bool OnSave() => true;

        protected override bool OnRevert() => true;
    }

    [Fact]
    public void A_location_noted_again_moves_to_the_front_once() {
        var recent = new RecentDocuments();
        var raised = 0;
        recent.Changed += _ => raised++;

        recent.Note("/p/a.txt");
        recent.Note("/p/b.txt");
        recent.Note("/p/a.txt");

        Assert.Equal(["/p/a.txt", "/p/b.txt"], recent.Locations);
        Assert.Equal(3, raised);

        // The one already at the front changes nothing, and says so by not raising.
        Assert.False(recent.Note("/p/a.txt"));
        Assert.Equal(3, raised);
    }

    [Fact]
    public void The_oldest_falls_off_past_the_capacity() {
        var recent = new RecentDocuments(capacity: 2);

        recent.Note("/p/a.txt");
        recent.Note("/p/b.txt");
        recent.Note("/p/c.txt");

        Assert.Equal(["/p/c.txt", "/p/b.txt"], recent.Locations);
    }

    [Fact]
    public void A_restored_list_keeps_its_order_without_duplicates_or_empties() {
        var recent = new RecentDocuments(capacity: 3);

        recent.Restore(["/p/b.txt", "", "/p/a.txt", "/p/b.txt", "/p/c.txt", "/p/d.txt"]);

        Assert.Equal(["/p/b.txt", "/p/a.txt", "/p/c.txt"], recent.Locations);
        Assert.True(recent.Forget("/p/a.txt"));
        Assert.False(recent.Forget("/p/a.txt"));
        Assert.Equal(["/p/b.txt", "/p/c.txt"], recent.Locations);
    }

    /// <summary>
    ///     ⚠ An untitled document is not recent until it is saved, and then it is — under the location
    ///     it was saved to, followed rather than read once.
    /// </summary>
    [Fact]
    public void A_tracked_document_arrives_when_it_first_has_a_location_and_follows_it() {
        using var document = new UiDocument(400f, 300f);
        var recent = new RecentDocuments();
        var note = new Note("Untitled");

        using var tracking = recent.Track(note, document.Effects);
        document.Update();

        Assert.Empty(recent.Locations);

        note.Rename("Chapter 3.md", "/tmp/Chapter 3.md");
        document.Update();

        Assert.Equal(["/tmp/Chapter 3.md"], recent.Locations);

        note.Rename("Chapter 4.md", "/tmp/Chapter 4.md");
        document.Update();

        Assert.Equal(["/tmp/Chapter 4.md", "/tmp/Chapter 3.md"], recent.Locations);
    }
}
