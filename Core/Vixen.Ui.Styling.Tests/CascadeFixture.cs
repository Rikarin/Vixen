// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling.Tests;

/// <summary>An engine, a tree, and a way to ask what an element's <c>color</c> came out as.</summary>
/// <remarks>
///     Nearly every cascade test has the same shape — load some CSS, make an element, ask what won —
///     and nearly all of them use <c>color</c> to ask it, because a single property makes the
///     assertion about the <i>ordering</i> rather than about the property.
/// </remarks>
sealed class CascadeFixture {
    public StyleEngine Engine { get; } = new();

    public StyleTree Tree => Engine.Tree;

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <param name="media">What to evaluate <c>@media</c> against.</param>
    public void Load(string css, StyleOrigin origin = StyleOrigin.Author, MediaContext media = default) =>
        Engine.Load(css, origin, media);

    /// <summary>Makes an element a query container of a given size, as a layout pass would.</summary>
    /// <param name="element">The element that has the <c>container-type</c>.</param>
    /// <param name="width">Its measured inline size.</param>
    /// <param name="height">Its measured block size.</param>
    /// <param name="name">Its <c>container-name</c>, or empty.</param>
    /// <param name="kind">Which axes it may be asked about.</param>
    /// <remarks>
    ///     ⚠ <b>This is the wiring doc 43 § D3 still owes, written out by hand.</b> Nothing in
    ///     <c>UiDocument</c> calls <see cref="ContainerScopes.Enter" /> yet, so a test that built a
    ///     document and expected <c>@container</c> to answer would be asserting against a feature no
    ///     layout feeds — and would pass for the wrong reason the day somebody wired it up wrongly.
    ///     Driving the sizes here asserts the cascade half on its own terms: given a box of this size,
    ///     does the query resolve to this value.
    /// </remarks>
    public void Contain(
        StyleNodeId element,
        float width,
        float height = 0f,
        string name = "",
        ContainerKind kind = ContainerKind.InlineSize
    ) {
        var scope = Engine.ContainerScopes.Enter(
            Tree.GetContainerScope(element),
            name,
            new ContainerBox(width, height, kind)
        );

        Tree.SetContainerScope(element, scope);
    }

    /// <summary>Resolves an element and returns one property's value as text.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The property name.</param>
    /// <param name="parent">The parent's resolved style.</param>
    /// <param name="inline">Inline declarations.</param>
    /// <returns>The value, or null if nothing set it.</returns>
    public string? Value(
        StyleNodeId element,
        string property = "color",
        ComputedStyle? parent = null,
        InlineStyleId? inline = null
    ) {
        var style = Engine.Resolver.Resolve(Tree, element, parent, inline);
        return Read(style, property);
    }

    /// <summary>Reads a property out of an already-resolved style.</summary>
    /// <param name="style">The style.</param>
    /// <param name="property">The property name.</param>
    /// <returns>The value, or null if nothing set it.</returns>
    public string? Read(ComputedStyle style, string property) {
        var id = Engine.Properties.Lookup(property);
        return id != NameTable.None && style.TryGet(id, out var value) ? Engine.Values.NameOf(value) : null;
    }

    /// <summary>Builds an inline declaration block from CSS-like text.</summary>
    /// <param name="declarations">Pairs such as <c>("color", "inline", false)</c>.</param>
    /// <returns>A handle to hand to the resolver.</returns>
    public InlineStyleId Inline(params (string Property, string Value, bool Important)[] declarations) {
        var block = new Declaration[declarations.Length];
        for (var i = 0; i < declarations.Length; i++) {
            block[i] = new Declaration(
                Engine.Properties.Intern(declarations[i].Property),
                Engine.Values.Intern(declarations[i].Value),
                declarations[i].Important
            );
        }

        return Engine.AddInlineStyle(block);
    }
}
