// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>One invalidation per frame, from three sources, and the two ways it can be wrong.</summary>
/// <remarks>
///     ⚠ <b>Both directions are asserted, and the second is the one that matters.</b> "At most once
///     per frame" is trivially satisfied by an event that never fires at all, which is this
///     repository's commonest defect and the reason step 5 was deliberately not built with step 1.
///     So every test that counts a coalesced raise is paired with one that would fail if the raise
///     stopped happening.
/// </remarks>
public class CommandInvalidationTests {
    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;

        return element;
    }

    /// <summary>Counts raises, and drives the clock the way a host's frame loop does.</summary>
    sealed class Counter {
        readonly UiDocument document;
        TimeSpan clock;

        public Counter(UiDocument document) {
            this.document = document;
            document.CommandsInvalidated += _ => Count++;
        }

        public int Count { get; private set; }

        public void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Tick(clock);
            document.Update();
        }
    }

    [Fact]
    public void Fifty_mutations_in_one_tick_raise_it_once() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);
        var counter = new Counter(document);

        counter.Frame();
        var before = counter.Count;

        for (var i = 0; i < 50; i++) {
            document.InvalidateCommands();
            view.AddCommandHandler($"test.command-{i}", () => { });
        }

        // ⚠ A hundred mutations, not fifty: each turn of the loop is an explicit invalidation *and*
        // a registration, so the two sources are coalesced against each other and not merely each
        // against itself.
        counter.Frame();

        Assert.Equal(before + 1, counter.Count);

        // And it is one raise per frame rather than one ever: a still document asks for nothing.
        counter.Frame();
        counter.Frame();
        Assert.Equal(before + 1, counter.Count);
    }

    [Fact]
    public void A_frame_with_nothing_to_say_raises_nothing() {
        using var document = new UiDocument(100f, 100f);

        var counter = new Counter(document);
        counter.Frame();

        var settled = counter.Count;

        for (var i = 0; i < 10; i++) {
            counter.Frame();
        }

        Assert.Equal(settled, counter.Count);
    }

    [Fact]
    public void Each_of_the_three_sources_raises_it() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);
        var other = View(document.Root);

        var counter = new Counter(document);
        counter.Frame();

        var raises = counter.Count;

        // ⚠ Asserted one at a time with a frame between, because a test that made all three changes
        // and saw one raise cannot tell three working sources from one working source.
        view.AddCommandHandler("edit.copy", () => { });
        counter.Frame();
        Assert.Equal(++raises, counter.Count);

        document.Focus(other);
        counter.Frame();
        Assert.Equal(++raises, counter.Count);

        document.InvalidateCommands();
        counter.Frame();
        Assert.Equal(++raises, counter.Count);

        // The fourth thing that changes an answer, and the one a surface would otherwise miss.
        view.RemoveCommandHandler("edit.copy");
        counter.Frame();
        Assert.Equal(++raises, counter.Count);
    }

    [Fact]
    public void A_focus_change_that_leaves_the_route_alone_says_nothing() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);

        var menu = document.Root.Add("div");
        menu.IsCommandTransparent = true;

        var item = View(menu);

        document.Focus(view);

        var counter = new Counter(document);
        counter.Frame();

        var settled = counter.Count;

        // Opening a menu moves the focus into it, and cannot have changed a single answer — see
        // `UiDocument.CommandFocus`. Telling forty items to re-ask would be exactly the churn this
        // whole mechanism exists to avoid.
        document.Focus(item);
        counter.Frame();

        Assert.Same(item, document.Focused);
        Assert.Equal(settled, counter.Count);
    }

    [Fact]
    public void A_handler_that_invalidates_again_is_heard_on_the_next_frame() {
        using var document = new UiDocument(100f, 100f);

        var runs = 0;
        var greedy = 3;

        document.CommandsInvalidated += invalidated => {
            runs++;

            if (--greedy > 0) {
                invalidated.InvalidateCommands();
            }
        };

        var clock = TimeSpan.Zero;

        void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Tick(clock);
        }

        document.InvalidateCommands();

        Frame();
        Assert.Equal(1, runs);

        // ⚠ The flag is cleared *before* the handlers run, so a handler's own invalidation survives
        // to the next frame instead of being swallowed by the clear. Clearing afterwards passes the
        // fifty-mutations test above and loses this one silently.
        Frame();
        Assert.Equal(2, runs);

        Frame();
        Assert.Equal(3, runs);

        // And it stops when the handler stops asking, rather than spinning for ever.
        Frame();
        Assert.Equal(3, runs);
    }

    [Fact]
    public void It_is_raised_from_the_tick_because_a_still_document_runs_no_pass() {
        using var document = new UiDocument(100f, 100f);

        var counter = new Counter(document);
        counter.Frame();

        var settled = counter.Count;

        // ⚠ `Update` returns false here: nothing dirtied the document, and a command becoming
        // executable is not a thing that does. A surface hung on the pass would go stale for
        // exactly as long as the interface was still, which is most of the time.
        document.InvalidateCommands();
        Assert.False(document.Update());

        document.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(settled + 1, counter.Count);
    }
}
