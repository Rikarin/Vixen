// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Tests;

/// <summary>A stylesheet, an element, and the bridge between the cascade and layout.</summary>
/// <remarks>
///     Deliberately the whole path rather than a hand-built <see cref="ComputedStyle" />. The bridge
///     is a wire, and a wire is worth testing with something plugged into both ends: these tests
///     write CSS and read a <see cref="LayoutStyle" />, so a property name that no rule can ever set
///     shows up as a test that will not pass.
/// </remarks>
sealed class BridgeFixture {
    readonly StyleEngine engine = new();

    public BridgeFixture() {
        Builder = new LayoutStyleBuilder(engine.Properties, engine.Values, engine.Names);
    }

    public LayoutStyleBuilder Builder { get; }

    /// <summary>Applies declarations to one element and returns the layout style they produce.</summary>
    /// <param name="declarations">The rule body, without the braces.</param>
    /// <param name="context">What relative lengths are measured against.</param>
    /// <returns>The layout style.</returns>
    public LayoutStyle Build(string declarations, LengthContext? context = null) {
        engine.Load($"div {{ {declarations} }}");

        var element = engine.Tree.CreateElement("div");
        var style = engine.Resolver.Resolve(engine.Tree, element, null);

        return Builder.Build(style, context ?? LengthContext.ForViewport(1000f, 500f));
    }

    /// <summary>Applies declarations written on the element itself, bypassing the CSS parser.</summary>
    /// <param name="declarations">Property/value text pairs.</param>
    /// <returns>The layout style they produce.</returns>
    /// <remarks>
    ///     ⚠ The only way to get an invalid value as far as the bridge. ExCSS validates while it
    ///     parses and drops what it does not recognise, so a stylesheet cannot deliver
    ///     <c>width: 4furlongs</c> to the cascade at all — which means a test written against a
    ///     stylesheet cannot reach the code that handles one. Inline declarations are interned
    ///     directly and get no such vetting, and neither will a <c>var()</c> substitution.
    /// </remarks>
    public LayoutStyle BuildInline(params (string Property, string Value)[] declarations) {
        var interned = new Declaration[declarations.Length];
        for (var i = 0; i < declarations.Length; i++) {
            interned[i] = new Declaration(
                engine.Properties.Intern(declarations[i].Property),
                engine.Values.Intern(declarations[i].Value),
                false
            );
        }

        var inline = engine.AddInlineStyle(interned);
        var element = engine.Tree.CreateElement("div");
        var style = engine.Resolver.Resolve(engine.Tree, element, null, inline);

        return Builder.Build(style, LengthContext.ForViewport(1000f, 500f));
    }

    /// <summary>Resolves an element's font size the way a tree walk would.</summary>
    /// <param name="declarations">The rule body.</param>
    /// <param name="parentFontSize">The parent's already-resolved size.</param>
    /// <returns>This element's size in pixels.</returns>
    public float FontSize(string declarations, float parentFontSize) {
        engine.Load($"div {{ {declarations} }}");

        var element = engine.Tree.CreateElement("div");
        var style = engine.Resolver.Resolve(engine.Tree, element, null);

        return Builder.ResolveFontSize(style, parentFontSize, LengthContext.ForViewport(1000f, 500f));
    }
}
