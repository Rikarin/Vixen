// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Named slot projection, spent from a real <c>.vxml</c> on both sides of it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The declaring half was built and the consuming half was not, and for as long as that
///         was true the feature could not be exercised at all.</b> <c>&lt;slot name="footer"&gt;</c>
///         parsed, bound to a <c>BoundSlot</c>, emitted a <c>BuildContext.Slot</c> call and put an
///         element in <c>Component.Slots</c> — where exactly one line read the dictionary back, for
///         the key <c>default</c>, and no name but that one was ever looked up again. The tests that
///         existed asserted the emitted C# contained <c>.Slot(</c>, which is a substring check on
///         generated text and passes against a feature nothing can use.
///     </para>
///     <para>
///         So these are written against two committed <c>.vxml</c> files rather than against inline
///         source or a hand-built <c>Component</c>: the point of the item is that the language reaches
///         the runtime, and a hand-written <c>Build</c> body is the half that already worked.
///     </para>
/// </remarks>
public class SlotProjectionTests {
    /// <summary>Each piece lands under the slot it named, in the order the shell declared.</summary>
    /// <remarks>
    ///     ⚠ <b>Position, not membership.</b> The consumer writes the status line first and the
    ///     toolbar button second, and the shell draws them last and first — so an implementation that
    ///     appended every child to one parent would satisfy any assertion about which elements exist
    ///     and fail every one of these. That is the failure this shape of test is for.
    /// </remarks>
    [Fact]
    public void A_child_that_names_a_slot_is_built_under_that_slot() {
        using var fixture = new ControlFixture(400f, 400f);

        var consumer = new ShellConsumer();
        BuildContext.BuildInto(consumer, fixture.Document, fixture.Document.Root);
        fixture.Update();

        // ⚠ Two host elements deep, and neither is written in either file. A component's own element
        // is named after it — `shellconsumer`, then `toolbarshell` — and the shell's markup hangs off
        // the second. Worth spelling out here rather than reaching through with `Single().Single()`,
        // because those two elements are CSS-initial `row` with no `flex-grow`, which is the trap a
        // panel refactored onto a shell like this falls into: a correct tree drawn in a strip down
        // the left.
        var host = Assert.Single(consumer.Root.Children);
        var root = Assert.Single(host.Children).Children;

        Assert.Equal("toolbarshell", host.Tag);
        Assert.Equal(["shell-toolbar", "shell-body", "shell-status"], root.Select(child => child.Tag));

        var toolbar = Assert.Single(root[0].Children).Children;
        var body = Assert.Single(root[1].Children).Children;
        var status = Assert.Single(root[2].Children).Children;

        // The button was written second and is the toolbar's only child.
        Assert.IsType<Button>(Assert.Single(toolbar));

        // ⚠ The two unnamed children stay adjacent and in source order under the default slot, which
        // is what says the partition grouped them rather than emitting one call per child.
        Assert.Equal(["body-first", "body-second"], body.Select(child => child.Tag));

        // Written first, drawn last.
        Assert.Equal("shell-note", Assert.Single(status).Tag);
    }

    /// <summary>The slot elements are the ones the shell declared, not copies of them.</summary>
    /// <remarks>
    ///     ⚠ <b>Reference identity, because "an element with the tag <c>slot</c> is in the right
    ///     place" is satisfiable without any projection at all.</b> A shell that made a fresh
    ///     <c>&lt;slot&gt;</c> per hole and a consumer that made its own would produce the same tag in
    ///     the same position and share nothing — so the assertion is that
    ///     <c>BuildContext.Into</c> hands back the very element <c>BuildContext.Slot</c> registered.
    /// </remarks>
    [Fact]
    public void The_slot_a_consumer_fills_is_the_element_the_component_declared() {
        using var fixture = new ControlFixture(400f, 400f);

        var shell = new ToolbarShell();
        BuildContext.BuildInto(shell, fixture.Document, fixture.Document.Root);
        fixture.Update();

        var declared = Assert.Single(shell.Root.Children).Children;

        Assert.Same(Assert.Single(declared[0].Children), BuildContext.Into(shell, "toolbar"));
        Assert.Same(Assert.Single(declared[2].Children), BuildContext.Into(shell, "status"));

        // ⚠ And the default slot is reached by `Inner` rather than by `Into`, which is not the same
        // call: a component that declares no slot at all has no dictionary and `Inner` falls back to
        // its root, where `Into` would throw. Both must find this one.
        Assert.Same(BuildContext.Inner(shell), BuildContext.Into(shell, BuildContext.DefaultSlot));
        Assert.Same(Assert.Single(declared[1].Children), BuildContext.Inner(shell));
    }

    /// <summary>A name no slot answers to fails at compose, naming the ones that exist.</summary>
    /// <remarks>
    ///     ⚠ <b>The two silent readings are both worse than the throw.</b> Dropping the content —
    ///     which is what the web platform does — brings a panel up with a section missing;
    ///     defaulting it puts the footer at the top of the body. Either reads as a bug in the
    ///     component rather than as the misspelling in the consumer that it is, and the two sides are
    ///     compiled together here, so the author can fix it.
    /// </remarks>
    [Fact]
    public void A_slot_name_the_component_does_not_declare_is_refused_by_name() {
        using var fixture = new ControlFixture(400f, 400f);

        var shell = new ToolbarShell();
        BuildContext.BuildInto(shell, fixture.Document, fixture.Document.Root);
        fixture.Update();

        var error = Assert.Throws<InvalidOperationException>(() => BuildContext.Into(shell, "footer"));

        Assert.Contains("footer", error.Message, StringComparison.Ordinal);
        Assert.Contains("toolbar", error.Message, StringComparison.Ordinal);
        Assert.Contains("status", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The binder's spelling of the default slot is the runtime's.</summary>
    /// <remarks>
    ///     <c>Vixen.Ui.Markup</c> is a <c>netstandard2.1</c> analyser and cannot reference
    ///     <c>Vixen.Ui</c>, so <c>"default"</c> is written out in both and nothing but this holds the
    ///     two together. A rename on one side alone would make every unnamed <c>&lt;slot /&gt;</c>
    ///     unreachable from every consumer, and the two files would each look right.
    /// </remarks>
    [Fact]
    public void The_default_slot_is_spelled_the_same_on_both_sides_of_the_compiler() =>
        Assert.Equal("default", BuildContext.DefaultSlot);
}
