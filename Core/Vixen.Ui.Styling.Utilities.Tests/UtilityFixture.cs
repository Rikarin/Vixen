// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>A theme, a generator, and a style engine to load the result into.</summary>
sealed class UtilityFixture {
    /// <summary>The theme doc 09 gives as the worked example, so tests read against the plan.</summary>
    public const string Theme = """
        theme:
          colors:
            surface:  { 1: "#101014", 2: "#17171d", 3: "#1f1f26" }
            accent:   { DEFAULT: "#4f7cff", hover: "#6a91ff" }
            muted:    "#8a8a99"
          spacing:    { base: 4 }
          radius:     { sm: 2, md: 4, lg: 8, full: 9999 }
          fontSize:   { xs: [11,16], sm: [12,18], base: [14,20], lg: [17,24], xl: [21,28] }
          fontWeight: { normal: 400, medium: 500, semibold: 600, bold: 700 }
          screens:    { sm: 640, md: 768, lg: 1024, xl: 1280 }
          shadow:
            DEFAULT: "0px 1px 2px rgba(0, 0, 0, 0.3)"
            lg:      "0px 8px 24px rgba(0, 0, 0, 0.45)"
        darkMode: media
        content: ["Assets/**/*.vxml", "Assets/**/*.cs"]
        """;

    public UtilityFixture(string? theme = null) {
        Tokens = ThemeTokens.Parse(theme ?? Theme);
        Generator = new UtilityGenerator(Tokens);
    }

    public ThemeTokens Tokens { get; }

    public UtilityGenerator Generator { get; }

    /// <summary>The declarations one utility produces, as <c>property: value</c> text.</summary>
    /// <param name="candidate">The class name.</param>
    /// <returns>The declarations, or null if it is not a utility.</returns>
    public string[]? Declarations(string candidate) {
        if (!UtilityParser.TryParse(candidate, out var parsed)) {
            return null;
        }

        var declarations = new List<UtilityDeclaration>();
        return UtilityFamilies.TryResolve(parsed, Tokens, declarations)
            ? [.. declarations.Select(declaration => $"{declaration.Property}: {declaration.Value}")]
            : null;
    }

    /// <summary>The declarations one utility produces, failing if it produces none.</summary>
    /// <param name="candidate">The class name.</param>
    /// <returns>The declarations.</returns>
    public string[] Emits(string candidate) =>
        Declarations(candidate) ?? throw new InvalidOperationException($"'{candidate}' is not a utility");

    /// <summary>Generates a stylesheet and returns its one rule's body.</summary>
    /// <param name="candidate">The class name.</param>
    /// <returns>The generated CSS.</returns>
    public string Generate(params string[] candidate) => Generator.Generate(candidate);

    /// <summary>
    ///     Resolves an element carrying some classes against the generated stylesheet, and returns
    ///     what one property came out as.
    /// </summary>
    /// <param name="classNames">The classes to put on the element.</param>
    /// <param name="property">The property to read.</param>
    /// <param name="extraCss">Stylesheet text to load after the utilities.</param>
    /// <param name="state">The element's pseudo state.</param>
    /// <param name="media">What to evaluate <c>@media</c> against.</param>
    /// <returns>The computed value, or null.</returns>
    /// <remarks>
    ///     The end-to-end path, and the only assertion that is worth much: it checks the generator
    ///     against the <i>style engine</i> rather than against an expectation of the text it should
    ///     produce. A generator that emitted syntactically valid CSS which the cascade then read
    ///     differently from intended would pass every string comparison and fail this.
    /// </remarks>
    public string? Computed(
        string[] classNames,
        string property,
        string extraCss = "",
        ElementState state = ElementState.None,
        MediaContext media = default
    ) {
        var engine = new StyleEngine();
        engine.Load(Generator.Generate(classNames), StyleOrigin.Author, media);

        if (extraCss.Length > 0) {
            engine.Load(extraCss, StyleOrigin.Author, media);
        }

        var element = engine.Tree.CreateElement("div", classNames: classNames);
        engine.Tree.SetState(element, state);

        var style = engine.Resolver.Resolve(engine.Tree, element);
        var id = engine.Properties.Lookup(property);

        return id != NameTable.None && style.TryGet(id, out var value) ? engine.Values.NameOf(value) : null;
    }
}
