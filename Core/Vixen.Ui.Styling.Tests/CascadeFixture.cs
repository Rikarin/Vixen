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
