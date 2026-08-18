// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>A watcher that reports exactly what a test hands it, and records what it was told to ignore.</summary>
/// <remarks>
///     The real one is <c>FileWatcher</c> and it is exercised by
///     <see cref="ExternalEditsTests.The_editors_own_save_does_not_come_back_through_a_real_watcher" />.
///     Everything else here is about the routing and the policy, and a platform's own opinion of when
///     it feels like reporting a write is not something a policy test should be waiting on.
/// </remarks>
sealed class StubWatcher : IFileWatcher {
    readonly Queue<FileChange> pending = new();

    public VirtualPath Root { get; init; } = VirtualPath.Root;
    public TimeSpan Debounce { get; set; } = TimeSpan.Zero;
    public bool HasOverflowed { get; set; }

    /// <summary>Every path <c>Suppress</c> was called with, in order.</summary>
    public List<VirtualPath> Suppressed { get; } = [];

    public void Report(string path, FileChangeKind kind = FileChangeKind.Changed, string? oldPath = null) =>
        pending.Enqueue(new(new(path), kind, oldPath is null ? default : new VirtualPath(oldPath)));

    public void Suppress(VirtualPath path) => Suppressed.Add(path);

    public int Drain(ICollection<FileChange> into) {
        var count = pending.Count;

        while (pending.Count > 0) {
            into.Add(pending.Dequeue());
        }

        return count;
    }

    public void ClearOverflow() => HasOverflowed = false;

    public void Dispose() {
    }
}

/// <summary>A document that is a text file, so that reloading it is observable.</summary>
sealed class TextFileDocument : EditorDocument {
    readonly string path;

    /// <summary>What it read, last time it read.</summary>
    public string Text { get; private set; }

    /// <summary>How many times <c>ReloadCore</c> reached the file.</summary>
    public int Reads { get; private set; }

    /// <summary>Whether it admits to being able to re-read itself.</summary>
    public bool Reloadable { get; set; } = true;

    /// <summary>Whether the next read declines, the way a file that will not parse does.</summary>
    public bool Refuse { get; set; }

    /// <summary>Whether the next read throws, the way a file being written underneath one does.</summary>
    public bool Unreadable { get; set; }

    public TextFileDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        this.path = path;
        Text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <inheritdoc />
    public override bool CanReload => Reloadable;

    /// <inheritdoc />
    protected override bool ReloadCore() {
        Reads++;

        if (Unreadable) {
            throw new IOException("The file is being written.");
        }

        if (Refuse) {
            return false;
        }

        Text = File.ReadAllText(path);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Through a temporary and a rename, which is <c>AssetFile.Write</c>'s shape and the reason
    ///     the suppression is two paths rather than one.
    /// </remarks>
    protected override void SaveCore() {
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, Text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Changes what it holds without touching the file, which is what an unsaved edit is.</summary>
    public void Edit(string text) {
        Text = text;
        Stack.Execute(new DelegateCommand("Edit", _ => { }, _ => { }));
    }
}

/// <summary>Routing a change on disk to the document open on it, and the policy over it.</summary>
public sealed class ExternalEditsTests : IDisposable {
    readonly ProjectFixture files = new();
    readonly EditorProject project;

    public ExternalEditsTests() {
        project = new(files.Paths);
        project.Assets.Scan();
    }

    public void Dispose() => files.Dispose();

    /// <summary>Puts a file in the project and opens a document on it.</summary>
    TextFileDocument Open(string name, string content = "on disk") {
        var guid = files.Add("Assets/" + name, content);

        project.Assets.Scan();

        return new(project, guid, files.Paths.Absolute("Assets/" + name));
    }

    [Fact]
    public void An_edit_made_outside_the_editor_reaches_the_document_open_on_the_file() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "changed by somebody else");
        watcher.Report("/Thing.txt");

        Assert.Equal(1, Apply(edits, watcher));
        Assert.Equal("changed by somebody else", document.Text);
    }

    /// <summary>
    ///     The undo history described the file's previous contents, so it goes — and the document is
    ///     then what is on disk, which is what clean means.
    /// </summary>
    [Fact]
    public void A_reload_discards_the_history_and_leaves_the_document_clean() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        document.Edit("mine");
        document.Save();

        Assert.Equal(1, document.Stack.Depth.Value);

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "theirs");
        watcher.Report("/Thing.txt");
        Apply(edits, watcher);

        Assert.Equal("theirs", document.Text);
        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.False(document.IsDirty.Value);
        Assert.False(document.IsStale.Value);
    }

    /// <summary>
    ///     The policy. Unsaved work is the only copy of itself and the file is not, so the document
    ///     is left exactly as it is and says so instead.
    /// </summary>
    [Fact]
    public void A_document_with_unsaved_edits_is_not_reloaded_over() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);
        var outcomes = new List<ExternalEditOutcome>();

        edits.Applied += edit => outcomes.Add(edit.Outcome);

        document.Edit("work in progress");

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "somebody else's version");
        watcher.Report("/Thing.txt");
        Apply(edits, watcher);

        Assert.Equal("work in progress", document.Text);
        Assert.Equal(0, document.Reads);
        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.True(document.IsStale.Value);
        Assert.Equal([ExternalEditOutcome.Kept], outcomes);
    }

    /// <summary>
    ///     ⚠ Staleness is not dirtiness. <see cref="EditorProject.SaveAll" /> writes every dirty
    ///     document, so a stale one that counted as dirty would have the editor's copy written over
    ///     the external edit by one keystroke.
    /// </summary>
    [Fact]
    public void A_stale_document_is_not_thereby_dirty_and_SaveAll_leaves_it_alone() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "theirs");
        document.Reloadable = false;
        watcher.Report("/Thing.txt");
        Apply(edits, watcher);

        Assert.True(document.IsStale.Value);
        Assert.False(document.IsDirty.Value);
        Assert.Equal(0, project.SaveAll());
        Assert.Equal("theirs", File.ReadAllText(files.Paths.Absolute("Assets/Thing.txt")));
    }

    /// <summary>Both answers settle it, and they are the two things a person was going to do anyway.</summary>
    [Fact]
    public void Saving_and_reloading_are_each_an_answer_to_a_stale_document() {
        var kept = Open("Kept.txt");
        var taken = Open("Taken.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        kept.Edit("mine");
        taken.Edit("mine");

        File.WriteAllText(files.Paths.Absolute("Assets/Kept.txt"), "theirs");
        File.WriteAllText(files.Paths.Absolute("Assets/Taken.txt"), "theirs");

        watcher.Report("/Kept.txt");
        watcher.Report("/Taken.txt");
        Apply(edits, watcher);

        Assert.True(kept.IsStale.Value);
        Assert.True(taken.IsStale.Value);

        kept.Save();
        Assert.False(kept.IsStale.Value);
        Assert.Equal("mine", File.ReadAllText(files.Paths.Absolute("Assets/Kept.txt")));

        Assert.True(taken.Reload());
        Assert.False(taken.IsStale.Value);
        Assert.False(taken.IsDirty.Value);
        Assert.Equal("theirs", taken.Text);
    }

    /// <summary>
    ///     ⚠ The whole point of <c>EditorProject.DocumentSaving</c> firing before the write. The
    ///     temporary goes with it because <c>AssetFile.Write</c> renames one over the target, and the
    ///     rename is the event being suppressed.
    /// </summary>
    [Fact]
    public void The_editors_own_save_is_suppressed_before_the_write_reaches_the_disk() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        document.Edit("mine");
        document.Save();

        Assert.Equal([new VirtualPath("/Thing.txt"), new VirtualPath("/Thing.txt.tmp")], watcher.Suppressed);
    }

    /// <summary>The same claim against the real watcher, which is the thing that has to believe it.</summary>
    /// <remarks>
    ///     ⚠ <b>Waits on the drain rather than on a clock.</b> A fixed sleep sized to the debounce is
    ///     the shape of test that passes on a developer's machine and fails on a loaded agent; this
    ///     polls until the window has certainly closed and then asserts on what came out.
    /// </remarks>
    [Fact]
    public void The_editors_own_save_does_not_come_back_through_a_real_watcher() {
        var document = Open("Thing.txt");

        using var watcher = new FileWatcher(files.Paths.Assets, VirtualPath.Root) {
            Debounce = TimeSpan.FromMilliseconds(20)
        };

        using var edits = new ExternalEdits(project, watcher);

        document.Edit("mine");
        document.Save();

        var drained = Settle(watcher, TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(drained, change => change.Path.Value.StartsWith("/Thing.txt", StringComparison.Ordinal));

        edits.Apply(drained);

        Assert.Equal(0, document.Reads);
        Assert.False(document.IsStale.Value);
        Assert.Equal("mine", document.Text);
    }

    /// <summary>And the same watcher does report somebody else's write, so the test above can fail.</summary>
    [Fact]
    public void A_write_the_editor_did_not_make_does_come_through_the_real_watcher() {
        var document = Open("Thing.txt");

        using var watcher = new FileWatcher(files.Paths.Assets, VirtualPath.Root) {
            Debounce = TimeSpan.FromMilliseconds(20)
        };

        using var edits = new ExternalEdits(project, watcher);

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "somebody else");

        var drained = Settle(watcher, TimeSpan.FromSeconds(3));

        edits.Apply(drained);

        Assert.Equal("somebody else", document.Text);
        Assert.Equal(1, document.Reads);
    }

    /// <summary>
    ///     ⚠ A deleted file would read back as empty — <c>AssetFile.Read</c> answers a missing file
    ///     with an empty string — so a document that reloaded over one would have emptied itself.
    /// </summary>
    [Fact]
    public void A_deleted_file_leaves_the_document_holding_the_only_copy() {
        var document = Open("Thing.txt", "the only copy");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        File.Delete(files.Paths.Absolute("Assets/Thing.txt"));
        watcher.Report("/Thing.txt", FileChangeKind.Deleted);

        Assert.Equal(0, Apply(edits, watcher));
        Assert.Equal("the only copy", document.Text);
        Assert.Equal(0, document.Reads);
    }

    /// <summary>A document that cannot re-read itself is reported rather than silently left behind.</summary>
    [Fact]
    public void A_document_that_cannot_reload_is_marked_stale_and_said_so() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);
        var outcomes = new List<ExternalEditOutcome>();

        edits.Applied += edit => outcomes.Add(edit.Outcome);

        document.Reloadable = false;
        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "theirs");
        watcher.Report("/Thing.txt");
        Apply(edits, watcher);

        Assert.Equal(0, document.Reads);
        Assert.True(document.IsStale.Value);
        Assert.Equal([ExternalEditOutcome.Unsupported], outcomes);
    }

    /// <summary>A read that declines, and one that throws, both leave what was on screen on screen.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_reload_that_does_not_work_keeps_what_the_document_had(bool refuse, bool unreadable) {
        var document = Open("Thing.txt", "what was opened");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);
        var outcomes = new List<ExternalEditOutcome>();

        edits.Applied += edit => outcomes.Add(edit.Outcome);

        document.Refuse = refuse;
        document.Unreadable = unreadable;

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "half written");
        watcher.Report("/Thing.txt");
        Apply(edits, watcher);

        Assert.Equal("what was opened", document.Text);
        Assert.True(document.IsStale.Value);
        Assert.Equal([ExternalEditOutcome.Failed], outcomes);
    }

    /// <summary>A change to a file nothing has open is nobody's business.</summary>
    [Fact]
    public void A_change_to_a_file_with_no_document_on_it_reaches_nothing() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        files.Add("Assets/Other.txt", "something else");
        project.Assets.Scan();
        watcher.Report("/Other.txt");

        Assert.Equal(0, Apply(edits, watcher));
        Assert.Equal(0, document.Reads);
    }

    /// <summary>
    ///     ⚠ The one ordering constraint, in both directions. A rename moves the entry in the GUID
    ///     index, so routing before the rescan looks the new path up in an index that still holds the
    ///     old one and finds nothing open.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What this deliberately does not claim is that the document now reads the new
    ///     file.</b> A document holds the path it was opened with — <c>CodeDocument.AssetPath</c>,
    ///     <c>StandardFrameDocument.AssetPath</c>, both set in a constructor — so a document whose
    ///     asset has moved re-reads and saves to where it used to be. That is a real gap and it is
    ///     older than this seam: it needs somewhere for a document to be told its file moved, which
    ///     no document model here has. Routing is what is being asserted, and routing works.
    /// </remarks>
    [Fact]
    public void A_renamed_file_finds_its_document_only_once_the_index_has_caught_up() {
        Open("Thing.txt");

        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        var from = files.Paths.Absolute("Assets/Thing.txt");
        var to = files.Paths.Absolute("Assets/Renamed.txt");

        File.Move(from, to);
        File.Move(from + ".meta", to + ".meta");

        watcher.Report("/Renamed.txt", FileChangeKind.Renamed, "/Thing.txt");

        // Routed against the index as it was: the new path is not in it yet.
        Assert.Equal(0, Apply(edits, watcher));

        // What FollowDisk does before it routes, and the reason it does it first.
        project.Assets.Scan();
        watcher.Report("/Renamed.txt", FileChangeKind.Renamed, "/Thing.txt");

        Assert.Equal(1, Apply(edits, watcher));
    }

    /// <summary>
    ///     An overflow re-reads what it can and deliberately says nothing about the rest: lost events
    ///     mean events were lost, not that this document's file changed.
    /// </summary>
    [Fact]
    public void An_overflow_rereads_the_clean_documents_and_does_not_accuse_the_dirty_ones() {
        var clean = Open("Clean.txt");
        var dirty = Open("Dirty.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        dirty.Edit("mine");

        File.WriteAllText(files.Paths.Absolute("Assets/Clean.txt"), "theirs");
        File.WriteAllText(files.Paths.Absolute("Assets/Dirty.txt"), "theirs");

        Assert.Equal(1, edits.Rescan());

        Assert.Equal("theirs", clean.Text);
        Assert.False(clean.IsStale.Value);

        Assert.Equal("mine", dirty.Text);
        Assert.False(dirty.IsStale.Value);
        Assert.Equal(0, dirty.Reads);
    }

    /// <summary>A closed document is not the project's any more, so nothing routes to it.</summary>
    [Fact]
    public void A_closed_document_is_not_reloaded() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();

        using var edits = new ExternalEdits(project, watcher);

        document.Close();

        File.WriteAllText(files.Paths.Absolute("Assets/Thing.txt"), "theirs");
        watcher.Report("/Thing.txt");

        Assert.Equal(0, Apply(edits, watcher));
        Assert.Equal(0, document.Reads);
        Assert.Equal(0, edits.Rescan());
    }

    /// <summary>Disposing stops the suppression, so a stopped editor is not still talking to a dead watcher.</summary>
    [Fact]
    public void Disposing_lets_go_of_the_projects_saves() {
        var document = Open("Thing.txt");
        var watcher = new StubWatcher();
        var edits = new ExternalEdits(project, watcher);

        edits.Dispose();
        document.Save();

        Assert.Empty(watcher.Suppressed);
    }

    /// <summary>Drains what the stub has and routes it, the way <c>FollowDisk</c> does.</summary>
    static int Apply(ExternalEdits edits, StubWatcher watcher) {
        var changes = new List<FileChange>();

        watcher.Drain(changes);

        return edits.Apply(changes);
    }

    /// <summary>Drains a real watcher until its debounce window has certainly closed.</summary>
    static List<FileChange> Settle(IFileWatcher watcher, TimeSpan budget) {
        var drained = new List<FileChange>();
        var clock = Stopwatch.StartNew();
        var quiet = 0;

        // Three consecutive empty drains after the debounce window, so that a platform which reports
        // late — FSEvents batches with a latency of its own — is waited for rather than raced.
        while (clock.Elapsed < budget && quiet < 3) {
            Thread.Sleep(40);
            quiet = watcher.Drain(drained) > 0 ? 0 : quiet + 1;
        }

        return drained;
    }
}
