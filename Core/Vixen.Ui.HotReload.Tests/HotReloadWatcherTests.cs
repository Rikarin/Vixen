// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.HotReload.Tests;

/// <summary>The style channel over a real directory: coalescing, rollback, and what is ignored.</summary>
/// <remarks>
///     ⚠ <b>The watcher had no tests at all and no caller anywhere in the repository</b>, which is
///     how a channel the README describes in detail stayed unexercised. These are written against
///     that README's claims rather than against the implementation, because the claims are what a
///     caller is entitled to.
/// </remarks>
public sealed class HotReloadWatcherTests : IDisposable {
    readonly string directory =
        Directory.CreateTempSubdirectory("vixen-hot-reload-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    // ------------------------------------------------------------ Coalescing

    /// <summary>
    ///     ⚠ <b>Editors write files more than once.</b> Save-to-temp-then-rename, a truncate
    ///     followed by a write, a tool that touches the timestamp afterwards — one save can raise
    ///     three events, and three reloads is two replays of every sheet nobody asked for.
    /// </summary>
    /// <remarks>
    ///     The notices are driven directly rather than by writing the file three times, because
    ///     what the operating system chooses to deliver is not this class's contract: a machine that
    ///     coalesced at the kernel would pass a filesystem-driven version of this test however
    ///     broken the set below was.
    /// </remarks>
    [Fact]
    public void Three_events_for_one_save_are_one_reload() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        Assert.Equal(10f, component.Root.Children[0].Width);

        File.WriteAllText(path, "box { width: 40px; }");

        watcher.Notify(path);
        watcher.Notify(path);
        watcher.Notify(path);

        var reports = watcher.Poll();
        document.Update();

        Assert.Single(reports);
        Assert.Equal(40f, component.Root.Children[0].Width);
    }

    /// <summary>Two files that changed are two reloads, which is what makes the set a set of paths.</summary>
    [Fact]
    public void Two_files_that_changed_are_two_reloads() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);

        var first = Write("a.vcss", "box { width: 10px; }");
        var second = Write("b.vcss", "box { height: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(first);
        watcher.Load(second);

        File.WriteAllText(first, "box { width: 20px; }");
        File.WriteAllText(second, "box { height: 20px; }");

        watcher.Notify(first);
        watcher.Notify(second);
        watcher.Notify(first);

        Assert.Equal(2, watcher.Poll().Count);
    }

    /// <remarks>
    ///     ⚠ The file is written <i>before</i> the watcher exists, here and below. Creating it
    ///     afterwards raises a `Created` the watcher is entitled to queue — so a test that did the
    ///     two in the other order would be asserting "nothing is pending" against a save it had just
    ///     made itself, and would fail whenever the notification happened to be quick.
    /// </remarks>
    [Fact]
    public void A_poll_with_nothing_pending_reports_nothing() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);

        Assert.Empty(watcher.Poll());
    }

    /// <summary>
    ///     A file the watcher was never told to load is a file it cannot replace: the sheet index a
    ///     reload needs came from <see cref="HotReloadWatcher.Load" /> and there is none.
    /// </summary>
    [Fact]
    public void A_file_that_was_never_loaded_is_ignored_rather_than_guessed_at() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);

        var known = Write("theme.vcss", "box { width: 10px; }");
        var stranger = Write("stranger.vcss", "box { width: 99px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(known);
        watcher.Notify(stranger);

        Assert.Empty(watcher.Poll());
    }

    // ------------------------------------------------------------ Rollback

    /// <summary>
    ///     ⚠ <b>A stylesheet that does not load puts the previous one back</b>, and a broken
    ///     <i>selector</i> is the case that matters: half a stylesheet drops the colour off
    ///     everything the rule used to match, and the rule somebody is midway through typing is
    ///     usually a selector.
    /// </summary>
    [Fact]
    public void A_saved_stylesheet_with_a_broken_selector_puts_the_previous_one_back() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        File.WriteAllText(path, "box:nonsense-pseudo { width: 99px; }");
        watcher.Notify(path);

        var report = Assert.Single(watcher.Poll());
        document.Update();

        Assert.False(report.Succeeded);
        Assert.NotEmpty(report.Errors);
        Assert.Equal(10f, component.Root.Children[0].Width);
    }

    /// <summary>
    ///     ⚠ <b>The sabotage behind the test above, made explicit: the diagnostics come from
    ///     <i>two</i> lists.</b> The loader reports what it could not use and the selector compiler
    ///     reports separately, and for the input above the loader has nothing to say — so a rollback
    ///     that consulted only the loader would look at an empty list, conclude the sheet was fine
    ///     and leave the mangled one in place. This asserts the shape of the failure directly rather
    ///     than relying on somebody re-running the sabotage by hand.
    /// </summary>
    [Fact]
    public void The_loader_says_nothing_about_a_broken_selector_and_the_compiler_does() {
        using var document = new UiDocument(200f, 200f);
        var sheet = document.Load("box { width: 10px; }");

        document.ReloadStyles(sheet, "box:nonsense-pseudo { width: 99px; }");

        Assert.Empty(document.Styles.Loader.Diagnostics);
        Assert.NotEmpty(document.Styles.Compiler.Diagnostics);
    }

    /// <summary>
    ///     ⚠ <b>A rule the selector compiler cannot use, in a sheet nobody is editing, must not roll
    ///     back every save of every other sheet.</b> A reload replays all of them — that is what
    ///     makes a deleted rule stop applying — so the diagnostics afterwards belong to the whole
    ///     document, and a rollback that read them as this save's would undo itself for ever.
    /// </summary>
    /// <remarks>
    ///     Not hypothetical, and not found by reasoning about it: the editor's chrome at the time
    ///     contained a <c>:empty</c> the compiler did not yet implement, and the style channel wired
    ///     to a real directory did nothing at all until this was understood. The file was saved, the
    ///     event arrived, the reload ran and put the old text straight back.
    ///     <para>
    ///         ⚠ <c>:empty</c> is implemented now, which is exactly why this test uses
    ///         <c>box:nonsense-pseudo</c> instead. A regression test pinned to whichever selector
    ///         happened to be missing on the day would quietly stop testing anything the moment that
    ///         selector landed — and the defect it guards has nothing to do with which selector it is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_complaint_about_somebody_else_s_sheet_does_not_roll_this_one_back() {
        using var document = new UiDocument(200f, 200f);

        // The editor's case in miniature: a sheet loaded at start-up that the compiler objects to,
        // and which is never touched again.
        document.Load("box:nonsense-pseudo { color: #ff0000; }");

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        File.WriteAllText(path, "box { width: 40px; }");
        watcher.Notify(path);

        var report = Assert.Single(watcher.Poll());
        document.Update();

        Assert.True(report.Succeeded);
        Assert.Empty(report.Errors);
        Assert.Equal(40f, component.Root.Children[0].Width);
    }

    /// <summary>And the broken save is still caught, with the same standing complaint present.</summary>
    [Fact]
    public void A_broken_save_is_still_caught_beside_a_standing_complaint() {
        using var document = new UiDocument(200f, 200f);
        document.Load("box:nonsense-pseudo { color: #ff0000; }");

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        File.WriteAllText(path, "box:another-nonsense-pseudo { width: 99px; }");
        watcher.Notify(path);

        var report = Assert.Single(watcher.Poll());
        document.Update();

        Assert.False(report.Succeeded);
        Assert.Equal(10f, component.Root.Children[0].Width);
    }

    /// <summary>A save that is fine is applied, which is the other half of the rollback's claim.</summary>
    [Fact]
    public void A_saved_stylesheet_that_loads_is_applied() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        File.WriteAllText(path, "box { width: 40px; }");
        watcher.Notify(path);

        var report = Assert.Single(watcher.Poll());
        document.Update();

        Assert.True(report.Succeeded);
        Assert.Equal(ReloadChannel.Styles, report.Channel);
        Assert.Equal(40f, component.Root.Children[0].Width);
    }

    /// <summary>
    ///     Nothing is rebuilt, which is what makes this the channel a designer uses all day: the
    ///     element keeps its identity and therefore its focus, its scroll offset and its animation.
    /// </summary>
    [Fact]
    public void A_reloaded_stylesheet_does_not_touch_a_single_element() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        var box = component.Root.Children[0];

        File.WriteAllText(path, "box { width: 40px; }");
        watcher.Notify(path);
        watcher.Poll();
        document.Update();

        Assert.Same(box, component.Root.Children[0]);
        Assert.Equal(40f, box.Width);
    }

    // ------------------------------------------------------------ Replacing rather than layering

    /// <summary>
    ///     ⚠ <b>The assertion is a deleted rule's effect going away, because that is the only thing
    ///     an overlay cannot do.</b> A watcher that loads the file again puts an <c>Author</c> copy
    ///     on top of the shipped <c>UserAgent</c> one: every value the new text states wins, so a
    ///     test that changed a number would pass against both the overlay and the replacement and
    ///     prove nothing. Deleting the rule is what separates them — the copy underneath still has
    ///     it, and the element keeps a width nothing in the file says any more.
    /// </summary>
    [Fact]
    public void A_rule_deleted_from_a_shipped_sheet_stops_applying() {
        using var document = new UiDocument(200f, 200f);

        // The shipped sheet: embedded from the file the developer is about to edit, and installed at
        // the origin every theme in this repository is installed at.
        const string Shipped = "box { width: 10px; }\nbox { height: 20px; }\n";
        document.Load(Shipped, StyleOrigin.UserAgent);

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("EditorTheme.vcss", Shipped);

        using var watcher = new HotReloadWatcher(host, directory);
        var sheet = watcher.Load(path);
        document.Update();

        Assert.Equal(0, sheet);
        Assert.True(watcher.Replaces(path));
        Assert.Equal(1, document.Styles.SheetCount);
        Assert.Equal(20f, component.Root.Children[0].Height);

        // The edit: the height rule is gone.
        File.WriteAllText(path, "box { width: 40px; }\n");
        watcher.Notify(path);

        Assert.True(Assert.Single(watcher.Poll()).Succeeded);
        document.Update();

        Assert.Equal(40f, component.Root.Children[0].Width);
        Assert.NotEqual(20f, component.Root.Children[0].Height);
    }

    /// <summary>
    ///     ⚠ <b>And a file the document does not already hold is loaded exactly as it always was.</b>
    ///     A scratch directory of overrides is an overlay on purpose — it is written to beat the
    ///     shipped sheets without out-specifying them — so recognising one sheet must not turn every
    ///     other one into a replacement of whatever it happened to resemble.
    /// </summary>
    [Fact]
    public void A_file_the_document_does_not_have_is_added_on_top_as_before() {
        using var document = new UiDocument(200f, 200f);
        document.Load("box { width: 10px; }", StyleOrigin.UserAgent);

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("overrides.vcss", "box { width: 33px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        var sheet = watcher.Load(path);
        document.Update();

        Assert.Equal(1, sheet);
        Assert.False(watcher.Replaces(path));
        Assert.Equal(2, document.Styles.SheetCount);
        Assert.Equal(33f, component.Root.Children[0].Width);
    }

    /// <summary>
    ///     ⚠ <b>A failed save on an adopted sheet has to put the <i>shipped</i> text back</b>, which
    ///     is the rollback working at the origin it was adopted at rather than one the watcher chose.
    /// </summary>
    [Fact]
    public void A_broken_save_restores_the_sheet_that_was_adopted() {
        using var document = new UiDocument(200f, 200f);

        const string Shipped = "box { width: 10px; }";
        document.Load(Shipped, StyleOrigin.UserAgent);

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("EditorTheme.vcss", Shipped);

        using var watcher = new HotReloadWatcher(host, directory);
        var sheet = watcher.Load(path);
        document.Update();

        Assert.Equal(0, sheet);
        Assert.Equal(1, document.Styles.SheetCount);

        File.WriteAllText(path, "box:nonsense-pseudo { width: 99px; }");
        watcher.Notify(path);

        Assert.False(Assert.Single(watcher.Poll()).Succeeded);
        document.Update();

        // The shipped sheet, at the index it was adopted at, holding the text it shipped with.
        Assert.Equal(10f, component.Root.Children[0].Width);
        Assert.Equal(Shipped, document.Styles.SheetText(0));
        Assert.Equal(1, document.Styles.SheetCount);
    }

    // ------------------------------------------------------------ Reporting

    [Fact]
    public void Every_reload_is_announced_to_whoever_asked() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);

        var announced = new List<ReloadReport>();
        watcher.Reloaded += announced.Add;

        File.WriteAllText(path, "box { width: 40px; }");
        watcher.Notify(path);
        watcher.Poll();

        Assert.Equal(ReloadChannel.Styles, Assert.Single(announced).Channel);
    }

    // ------------------------------------------------------------ The filesystem, once

    /// <summary>
    ///     One test that goes through <c>FileSystemWatcher</c> rather than the seam, because the
    ///     wiring — the filter, the notify flags, the three events subscribed to — is not covered by
    ///     anything above and is exactly the part that is wrong when a save does nothing at all.
    /// </summary>
    [Fact]
    public async Task A_real_save_reaches_the_document() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var path = Write("theme.vcss", "box { width: 10px; }");

        using var watcher = new HotReloadWatcher(host, directory);
        watcher.Load(path);
        document.Update();

        await File.WriteAllTextAsync(path, "box { width: 40px; }", TestContext.Current.CancellationToken);

        // Polled rather than slept once: a filesystem notification has no deadline, and a single
        // sleep long enough to be reliable on a loaded machine is a second added to every run.
        for (var attempt = 0; attempt < 100 && component.Root.Children[0].Width != 40f; attempt++) {
            await Task.Delay(50, TestContext.Current.CancellationToken);

            watcher.Poll();
            document.Update();
        }

        Assert.Equal(40f, component.Root.Children[0].Width);
    }

    // ------------------------------------------------------------ Fixtures

    string Write(string name, string css) {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, css);
        return path;
    }

    sealed class Boxes : Component {
        protected override void Build(BuildContext ctx) => ctx.Element(null, "box");
    }
}
