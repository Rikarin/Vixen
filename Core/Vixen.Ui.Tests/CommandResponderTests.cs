// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The half of the chain that is not the element tree: the document, then the application.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every rule asserted here is a rule <c>CommandRouteTests</c> already asserts inside
///         the element walk.</b> That is the point of the file: extending the chain must not have
///         bought a second set of rules for the new levels. First to answer wins, only that one is
///         asked whether it can, silence means disabled — across the join, in both directions.
///     </para>
///     <para>
///         The ordering tests count rather than observe. "The document won" is satisfied by an
///         application responder that was asked and refused as well as by one that was never asked,
///         and only the second is what the rule says — so the further responder carries a counter
///         and the assertion is that it is still nought.
///     </para>
/// </remarks>
public class CommandResponderTests {
    /// <summary>A focusable element under a parent, which is what a view is for these purposes.</summary>
    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;
        return element;
    }

    /// <summary>A responder that records every question anybody asked it.</summary>
    /// <remarks>
    ///     ⚠ <b>Counts the lookup and the predicate separately</b>, because they are two different
    ///     claims. <c>Lookups</c> being nought says the chain never reached this level at all;
    ///     <c>Predicates</c> being nought while <c>Lookups</c> is one says it was reached and its
    ///     enablement was not consulted, which is the case a plain "did it run" assertion cannot
    ///     tell apart from the first.
    /// </remarks>
    sealed class CountingResponder : ICommandResponder {
        readonly string id;
        readonly Action execute;
        readonly bool can;

        public CountingResponder(string id, Action execute, bool can = true) {
            this.id = id;
            this.execute = execute;
            this.can = can;
        }

        public int Lookups { get; private set; }

        public int Predicates { get; private set; }

        public bool TryGetCommandHandler(string commandId, out CommandHandler handler) {
            Lookups++;

            if (!string.Equals(commandId, id, StringComparison.Ordinal)) {
                handler = default;
                return false;
            }

            handler = CommandHandler.For(
                commandId,
                this,
                execute,
                () => {
                    Predicates++;
                    return can;
                }
            );

            return true;
        }
    }

    [Fact]
    public void A_responder_answers_when_nothing_in_the_tree_does() {
        using var document = new UiDocument(100f, 100f);

        var ran = 0;
        var responder = new CommandResponder();
        responder.Add("edit.copy", () => ran++);

        var view = View(document.Root);
        document.Focus(view);

        // The chain as it was: the walk runs out of parents and the id is unhandled.
        Assert.Null(CommandRoute.Resolve(document, "edit.copy"));
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));

        document.CommandResponder = responder;

        Assert.NotNull(CommandRoute.Resolve(document, "edit.copy"));
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_responder_owns_no_element_and_says_so() {
        using var document = new UiDocument(100f, 100f);

        var responder = new CommandResponder();
        responder.Add("edit.copy", () => { });

        document.CommandResponder = responder;

        var handler = CommandRoute.Resolve(document, "edit.copy");

        Assert.NotNull(handler);

        // ⚠ The whole gap this closes, in one assertion: a view-model that owns `edit.copy` no
        // longer has to own a piece of the view tree in order to say so.
        Assert.Null(handler!.Value.Element);
        Assert.Same(responder, handler.Value.Responder);
    }

    [Fact]
    public void The_document_is_asked_after_the_root_and_the_root_wins() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var responder = new CountingResponder("file.save", () => ran = "document");

        document.Root.AddCommandHandler("file.save", () => ran = "root");
        document.CommandResponder = responder;

        Assert.True(CommandRoute.Execute(document, "file.save"));
        Assert.Equal("root", ran);

        // Reached only when the tree is silent, and never asked whether it could have done it.
        Assert.Equal(0, responder.Lookups);
        Assert.Equal(0, responder.Predicates);
    }

    [Fact]
    public void The_document_wins_over_the_application_and_the_application_is_never_asked() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var owner = new CountingResponder("edit.copy", () => ran = "document");
        var application = new CountingResponder("edit.copy", () => ran = "application");

        document.CommandResponder = owner;
        document.ApplicationCommandResponder = application;

        // ⚠ The focus is nowhere on purpose. With the tree out of the way, the only thing deciding
        // between these two is the order the chain asks them in.
        Assert.Null(document.CommandFocus);

        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("document", ran);

        Assert.Equal(1, owner.Lookups);
        Assert.Equal(1, owner.Predicates);

        // ⚠ The assertion the ordering actually rests on. "The document ran" would also be true of a
        // chain that asked the application, found it willing, and preferred the document anyway —
        // and that chain would behave differently the moment the application's predicate had a side
        // effect or cost something. Nought lookups is the stronger claim and the one the rule makes.
        Assert.Equal(0, application.Lookups);
        Assert.Equal(0, application.Predicates);
    }

    [Fact]
    public void The_application_answers_when_the_document_does_not() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var owner = new CountingResponder("file.save", () => ran = "document");
        var application = new CountingResponder("edit.copy", () => ran = "application");

        document.CommandResponder = owner;
        document.ApplicationCommandResponder = application;

        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("application", ran);

        // The document level was reached and declined the id — which is not the same as refusing it.
        Assert.Equal(1, owner.Lookups);
        Assert.Equal(0, owner.Predicates);
        Assert.Equal(1, application.Lookups);
    }

    [Fact]
    public void A_document_responder_that_refuses_does_not_fall_through_to_the_application() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var owner = new CountingResponder("edit.copy", () => ran = "document", can: false);
        var application = new CountingResponder("edit.copy", () => ran = "application");

        document.CommandResponder = owner;
        document.ApplicationCommandResponder = application;

        // ⚠ AppKit's rule at the far end of the chain: the first responder that *answers* wins, even
        // when what it answers is no. A chain that carried on looking for somebody more willing
        // would make "which handler runs" depend on how many things happen to be listening.
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));
        Assert.False(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("", ran);

        // Two asks, two lookups — and neither of them reached past the level that said no.
        Assert.Equal(2, owner.Lookups);
        Assert.Equal(0, application.Lookups);
    }

    [Fact]
    public void The_focused_element_still_beats_both_of_them() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var owner = new CountingResponder("edit.copy", () => ran = "document");
        var application = new CountingResponder("edit.copy", () => ran = "application");

        var view = View(document.Root);
        view.AddCommandHandler("edit.copy", () => ran = "view");

        document.CommandResponder = owner;
        document.ApplicationCommandResponder = application;
        document.Focus(view);

        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("view", ran);
        Assert.Equal(0, owner.Lookups);
        Assert.Equal(0, application.Lookups);

        // And the levels behind it are still there for the moment the focus leaves.
        document.Focus(null);
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("document", ran);
    }

    [Fact]
    public void An_id_no_level_answers_is_still_disabled_with_no_rule_written() {
        using var document = new UiDocument(100f, 100f);

        var owner = new CommandResponder();
        owner.Add("file.save", () => { });

        var application = new CommandResponder();
        application.Add("app.about", () => { });

        document.CommandResponder = owner;
        document.ApplicationCommandResponder = application;

        Assert.Null(CommandRoute.Resolve(document, "edit.copy"));
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));
        Assert.False(CommandRoute.Execute(document, "edit.copy"));
    }

    [Fact]
    public void Installing_a_level_invalidates_the_surfaces() {
        using var document = new UiDocument(100f, 100f);

        var raised = 0;
        var clock = TimeSpan.Zero;

        document.CommandsInvalidated += _ => raised++;

        void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Tick(clock);
        }

        // Settle whatever the document's construction asked for.
        Frame();
        raised = 0;

        document.CommandResponder = new CommandResponder();
        Frame();
        Assert.Equal(1, raised);

        document.ApplicationCommandResponder = new CommandResponder();
        Frame();
        Assert.Equal(2, raised);

        // ⚠ Setting the same one again is not a change, and must not cost a raise: a host that
        // reassigns its responder every frame would otherwise re-ask every visible command forever.
        var same = document.CommandResponder;
        document.CommandResponder = same;
        Frame();
        Assert.Equal(2, raised);

        document.CommandResponder = null;
        Frame();
        Assert.Equal(3, raised);
    }

    [Fact]
    public void A_responder_declaring_one_id_twice_throws_as_an_element_does() {
        var responder = new CommandResponder();
        responder.Add("edit.copy", () => { });

        Assert.Throws<ArgumentException>(() => responder.Add("edit.copy", () => { }));

        Assert.True(responder.Remove("edit.copy"));
        Assert.False(responder.Remove("edit.copy"));
        Assert.Empty(responder.Ids);
    }

    [Fact]
    public void A_disposed_document_lets_go_of_both_responders() {
        var document = new UiDocument(100f, 100f);

        var owner = new CommandResponder();
        var application = new CommandResponder();

        document.CommandResponder = owner;
        document.ApplicationCommandResponder = application;

        document.Dispose();

        // ⚠ The direction that leaks. A responder is a table of closures and a closure reaches
        // everything it captured, so a disposed document still pointing at the application's would
        // keep a view-model, its selection and — in the editor's case — an assembly in a collectible
        // load context reachable for as long as anything held the document.
        Assert.Null(document.CommandResponder);
        Assert.Null(document.ApplicationCommandResponder);
    }

    [Fact]
    public void A_long_lived_responder_does_not_keep_a_closed_document_alive() {
        // ⚠ The other direction, and the one a nulled field cannot prove. `ICommandResponder` has no
        // event and no back-reference by design: a responder never learns which documents it was
        // installed on, so there is nothing on it for a closed window to hang from. This asserts
        // that rather than trusting it — the editor keeps one registry across a shell being rebuilt,
        // and a shell that stayed reachable is a whole element tree per reload.
        var application = new CommandResponder();
        application.Add("app.about", () => { });

        var weak = Closed(application);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive);

        // And the responder is still usable, so the collection above was the document and not both.
        Assert.True(application.TryGetCommandHandler("app.about", out _));
    }

    /// <summary>Builds a document over a responder, uses it, closes it, and keeps only a weak handle.</summary>
    /// <remarks>
    ///     A separate method so the document has no live local in the caller's frame — a debug build
    ///     keeps locals alive to the end of their scope, which is enough to fail the assertion above
    ///     for a reason that has nothing to do with the responder.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Closed(ICommandResponder application) {
        var document = new UiDocument(100f, 100f);
        document.ApplicationCommandResponder = application;

        Assert.True(CommandRoute.CanExecute(document, "app.about"));

        document.Dispose();

        return new WeakReference(document);
    }
}
