// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a cross-cutting value looks like when it is not threaded through props.</summary>
public sealed record AmbientTheme(string Accent);

/// <summary>
///     The code-behind half of <c>AmbientProvider.vxml</c>: one parameter, and one override that
///     turns it into an ambient value.
/// </summary>
public partial class AmbientProvider {
    /// <summary>The parameter a consumer sets on the tag.</summary>
    public string Name { get; set; } = "unset";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Reading <see cref="Name" /> here is the assertion that matters.</b> The hook runs
    ///     after the caller's parameter assignments and before any child exists; run a moment
    ///     earlier and this would provide a theme built from <c>"unset"</c>, a moment later and the
    ///     consumer would already have been built and already have injected nothing.
    /// </remarks>
    protected override void OnProvide() => Provide(new AmbientTheme(Name));
}

/// <summary>The code-behind half of <c>AmbientConsumer.vxml</c>.</summary>
/// <remarks>
///     ⚠ <b>The property the markup reads is no longer here.</b> It was
///     <c>public string Accent =&gt; Inject&lt;AmbientTheme&gt;()?.Accent ?? "none"</c>; the file now
///     writes <c>@inject AmbientTheme Theme</c> and the generator declares the same reading. What is
///     left below is the half a directive cannot replace, because it is about <i>when</i> rather
///     than <i>what</i>.
/// </remarks>
public partial class AmbientConsumer {
    /// <summary>What was injectable at the moment this component was mounted.</summary>
    /// <remarks>
    ///     ⚠ <b>Recorded because an <c>@expr</c> cannot witness the ordering.</b> Every markup
    ///     expression is a queued effect, so a label bound to <see cref="Accent" /> reads it at the
    ///     next flush — long after the whole tree, provider included, has been built. It is green
    ///     against a runtime that declares ambient values *after* <c>Build</c>, which is precisely
    ///     the arrangement the hook exists to avoid. This is read synchronously, inside the
    ///     provider's own build, and is the only assertion here that can go red for that reason.
    /// </remarks>
    public string? AtMount { get; private set; }

    /// <inheritdoc />
    protected override void OnProvide() => AtMount = Inject<AmbientTheme>()?.Accent;
}

/// <summary>An ambient value, spent from two real <c>.vxml</c> files on both sides of it.</summary>
/// <remarks>
///     ⚠ <b>Written against committed markup rather than a hand-built <c>Component</c>, for
///     <c>SlotProjectionTests</c>' reason.</b> The item this closes is that markup could not express
///     a cross-cutting value, so a hand-written <c>Build</c> body proves the half that was never in
///     question.
/// </remarks>
public class AmbientValueTests {
    /// <summary>A consumer two levels down and written with no attributes reads the provider's value.</summary>
    [Fact]
    public void A_component_injects_what_an_ancestor_provided_without_being_passed_it() {
        using var fixture = new ControlFixture(400f, 400f);

        var host = new AmbientHost();
        BuildContext.BuildInto(host, fixture.Document, fixture.Document.Root);
        fixture.Update();

        // ⚠ The `text` element and not `consumer-label`: an interpolation is a text node of its
        // own, so the label's own `Text` is null in a correct tree and an assertion on it would be
        // green against every implementation including one that injected nothing.
        var label = Find(host.Root, "text");

        Assert.NotNull(label);
        Assert.Equal("dark", label.Text);

        // ⚠ And the value was already there while the consumer was being built, not only by the time
        // an effect flushed. That is the half `label.Text` cannot see.
        var mounted = Find(host.Root, "ambientconsumer");
        var consumer = Assert.IsType<AmbientConsumer>(fixture.Document.ComponentAt(mounted!));

        Assert.Equal("dark", consumer.AtMount);
    }

    /// <summary>Nothing above providing one is "none" rather than a crash.</summary>
    [Fact]
    public void A_consumer_with_no_provider_above_it_injects_nothing() {
        using var fixture = new ControlFixture(400f, 400f);

        var consumer = new AmbientConsumer();
        BuildContext.BuildInto(consumer, fixture.Document, fixture.Document.Root);
        fixture.Update();

        var label = Find(consumer.Root, "text");

        Assert.NotNull(label);
        Assert.Equal("none", label.Text);
    }

    /// <summary>The document's is the last word, and an element's overrides it inside that element.</summary>
    [Fact]
    public void The_nearest_declaration_wins_and_the_document_is_the_fallback() {
        using var fixture = new ControlFixture(400f, 400f);
        var document = fixture.Document;

        var outer = document.Root.Add("div");
        var inner = outer.Add("div");
        var leaf = inner.Add("div");

        Assert.Null(leaf.Inject<AmbientTheme>());

        document.Provide(new AmbientTheme("application"));
        Assert.Equal("application", leaf.Inject<AmbientTheme>()?.Accent);

        outer.Provide(new AmbientTheme("panel"));
        Assert.Equal("panel", leaf.Inject<AmbientTheme>()?.Accent);

        inner.Provide(new AmbientTheme("preview"));
        Assert.Equal("preview", leaf.Inject<AmbientTheme>()?.Accent);

        // ⚠ Taking one back reveals the next one up rather than nothing, which is the whole reason
        // this is a removal and not an assignment of null.
        Assert.True(inner.Unprovide<AmbientTheme>());
        Assert.Equal("panel", leaf.Inject<AmbientTheme>()?.Accent);
        Assert.False(inner.Unprovide<AmbientTheme>());
    }

    /// <summary>An element reads what it provided itself.</summary>
    /// <remarks>
    ///     A panel that overrides the theme for its subtree is inside that subtree, so the walk has
    ///     to start at the element rather than at its parent.
    /// </remarks>
    [Fact]
    public void An_element_injects_its_own_declaration() {
        using var fixture = new ControlFixture(400f, 400f);
        var panel = fixture.Document.Root.Add("div");

        fixture.Document.Provide(new AmbientTheme("application"));
        panel.Provide(new AmbientTheme("panel"));

        Assert.True(panel.Provides<AmbientTheme>());
        Assert.Equal("panel", panel.Inject<AmbientTheme>()?.Accent);
    }

    /// <summary>The key is the type argument, not the value's runtime type.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes an interface the useful key</b>, and what stops a subclass silently
    ///     shadowing the base everything else asks for.
    /// </remarks>
    [Fact]
    public void The_key_is_the_type_it_was_provided_as() {
        using var fixture = new ControlFixture(400f, 400f);
        var panel = fixture.Document.Root.Add("div");

        panel.Provide<object>(new AmbientTheme("as an object"));

        Assert.True(panel.Provides<object>());
        Assert.False(panel.Provides<AmbientTheme>());
        Assert.Null(panel.Inject<AmbientTheme>());
        Assert.IsType<AmbientTheme>(panel.Inject<object>());
    }

    static UiElement? Find(UiElement from, string tag) {
        if (string.Equals(from.Tag, tag, StringComparison.Ordinal)) {
            return from;
        }

        foreach (var child in from.Children) {
            if (Find(child, tag) is { } found) {
                return found;
            }
        }

        return null;
    }
}
