// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>An element in a probe document other than the one being measured.</summary>
/// <param name="Classes">Its classes.</param>
/// <param name="State">Its pseudo state.</param>
/// <param name="Attributes">Its attributes.</param>
/// <param name="Tag">Its tag name.</param>
/// <remarks>
///     <para>
///         ⚠ <b>What half the variant table needs and <see cref="UtilityFixture.Computed" /> could
///         not express.</b> <c>group-*</c> wants an ancestor in a state, <c>peer-*</c> a preceding
///         sibling in one, <c>ltr:</c>/<c>rtl:</c> an ancestor carrying <c>dir</c>, <c>dark:</c>
///         under the class strategy an ancestor carrying <c>.dark</c>, and the structural variants a
///         place among siblings. A fixture whose only knobs were <c>ElementState</c> and
///         <c>MediaContext</c> could reach none of them, which is the mechanical reason none of them
///         had an end-to-end test.
///     </para>
///     <para>
///         ⚠ <b><paramref name="Tag" /> is the knob the of-type variants needed, and a fixture
///         without it would have passed them all while proving nothing.</b> Every probe used to be a
///         <c>div</c>, and in a document of one tag <c>:nth-of-type(2)</c> and <c>:nth-child(2)</c>
///         select the same element — so a scene built out of identical siblings cannot tell an
///         of-type test from the child test it must not be.
///     </para>
///     <para>
///         <paramref name="Children" /> is the knob the part variants needed: <c>placeholder:</c>
///         is a <i>child</i> combinator onto a tag, and the row that tells <c>&gt;</c> from a
///         descendant combinator is a grandchild carrying the tag — which a flat list of children
///         cannot express.
///     </para>
/// </remarks>
/// <param name="Children">Its own children, for a scene that needs a grandchild.</param>
sealed record Probe(
    string[] Classes,
    ElementState State = ElementState.None,
    (string Name, string Value)[]? Attributes = null,
    string Tag = "div",
    Probe[]? Children = null
);

/// <summary>A theme, a generator, and a style engine to load the result into.</summary>
sealed class UtilityFixture {
    /// <summary>The theme doc 09 gives as the worked example, so tests read against the plan.</summary>
    /// <remarks>
    ///     ⚠ <b><c>--*: initial;</c> first, and it is not tidiness.</b> Every namespace has a shipped
    ///     default now — v4's twenty-six colour ramps, its type scale, its radii — and a fixture that
    ///     inherited them would stop being doc 09's worked example and start being doc 09's example
    ///     <i>plus</i> three hundred tokens nobody chose. Tests that ask "what does this theme not
    ///     have" would then answer differently for a reason unrelated to what they measure. The
    ///     shipped default gets its own tests; this one is the plan's, verbatim.
    /// </remarks>
    public const string Theme = """
        @theme {
            --*: initial;

            --color-surface-1: #101014;
            --color-surface-2: #17171d;
            --color-surface-3: #1f1f26;
            --color-accent: #4f7cff;
            --color-accent-hover: #6a91ff;
            --color-muted: #8a8a99;

            --spacing: 4px;

            --radius-sm: 2px;
            --radius-md: 4px;
            --radius-lg: 8px;
            --radius-full: 9999px;

            --text-xs: 11px;   --text-xs--line-height: 16px;
            --text-sm: 12px;   --text-sm--line-height: 18px;
            --text-base: 14px; --text-base--line-height: 20px;
            --text-lg: 17px;   --text-lg--line-height: 24px;
            --text-xl: 21px;   --text-xl--line-height: 28px;

            --font-weight-normal: 400;
            --font-weight-medium: 500;
            --font-weight-semibold: 600;
            --font-weight-bold: 700;

            --breakpoint-sm: 640px;
            --breakpoint-md: 768px;
            --breakpoint-lg: 1024px;
            --breakpoint-xl: 1280px;

            --shadow: 0px 1px 2px rgba(0, 0, 0, 0.3);
            --shadow-lg: 0px 8px 24px rgba(0, 0, 0, 0.45);

            --dark-mode: media;
        }
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

    /// <summary>The theme's breakpoints in pixels at 100 % text size, in the theme's order.</summary>
    /// <remarks>
    ///     What a <see cref="MediaContext" /> built without a font measures them at. The theme keeps
    ///     each one's unit since #1417, so a test that wants a window just either side of one asks
    ///     here rather than reading a number off <see cref="ThemeTokens.Screens" />.
    /// </remarks>
    public Dictionary<string, float> ScreenPixels =>
        Tokens.Screens.ToDictionary(pair => pair.Key, pair => AtDefaultText(pair.Value), StringComparer.Ordinal);

    /// <summary>The theme's container sizes in pixels at 100 % text size, in the theme's order.</summary>
    public Dictionary<string, float> ContainerPixels =>
        Tokens.Containers.ToDictionary(pair => pair.Key, pair => AtDefaultText(pair.Value), StringComparer.Ordinal);

    /// <summary>A theme width in pixels at sixteen pixels a <c>rem</c>.</summary>
    /// <param name="width">A length in <c>rem</c> or pixels.</param>
    /// <returns>The pixels.</returns>
    public static float AtDefaultText(StyleValue width) =>
        width.Unit == StyleUnit.Rem ? width.Number * 16f : width.Number;

    /// <summary>
    ///     Resolves an element carrying some classes against the generated stylesheet, and returns
    ///     what one property came out as.
    /// </summary>
    /// <param name="classNames">The classes to put on the element.</param>
    /// <param name="property">The property to read.</param>
    /// <param name="extraCss">Stylesheet text to load after the utilities.</param>
    /// <param name="state">The element's pseudo state.</param>
    /// <param name="media">What to evaluate <c>@media</c> against.</param>
    /// <param name="attributes">Attributes to put on the element.</param>
    /// <param name="ancestor">A parent to put it under.</param>
    /// <param name="before">Siblings preceding it.</param>
    /// <param name="after">Siblings following it.</param>
    /// <param name="container">A box to make the parent a query container of, as layout would.</param>
    /// <param name="containerName">That container's <c>container-name</c>, or empty.</param>
    /// <param name="tag">The element's own tag name.</param>
    /// <param name="children">Children to give it, which is what <c>:empty</c> is about.</param>
    /// <param name="measure">
    ///     The probe to read the property off instead of the element — one of
    ///     <paramref name="children" /> or of their children — for a variant whose whole claim is
    ///     that it styles a part and not the element carrying the class.
    /// </param>
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
        MediaContext media = default,
        (string Name, string Value)[]? attributes = null,
        Probe? ancestor = null,
        Probe[]? before = null,
        Probe[]? after = null,
        ContainerBox? container = null,
        string containerName = "",
        string tag = "div",
        Probe[]? children = null,
        Probe? measure = null
    ) {
        var engine = new StyleEngine();

        // By reference, because two probes with the same classes and tag are equal as records and
        // a scene of identical siblings is the normal case.
        var placed = new Dictionary<Probe, StyleNodeId>(ReferenceEqualityComparer.Instance);
        engine.Load(Generator.Generate(classNames), StyleOrigin.Author, media);

        if (extraCss.Length > 0) {
            engine.Load(extraCss, StyleOrigin.Author, media);
        }

        // A parent only when something asks for one. A wrapper nobody wanted would change what
        // `:first-child` and `:only-child` answer for every existing caller.
        StyleNodeId? parent = null;

        if (ancestor is not null || before is { Length: > 0 } || after is { Length: > 0 } || container is not null) {
            parent = Add(engine, ancestor ?? new Probe([]), null, placed);
        }

        // ⚠ The scope goes on the *parent* and never on the element under test, which is CSS
        // Containment 3 § 5.1: a container query is about what is inside the container. Putting it
        // on the element would make every one of these pass and would make the family's commonest
        // real defect — a container answering its own query — invisible here.
        if (container is { } box) {
            engine.Tree.SetContainerScope(
                parent!.Value,
                engine.ContainerScopes.Enter(ContainerScopes.Root, containerName, box)
            );
        }

        foreach (var sibling in before ?? []) {
            Add(engine, sibling, parent, placed);
        }

        var element = Add(engine, new Probe(classNames, state, attributes, tag), parent, placed);

        foreach (var child in children ?? []) {
            Add(engine, child, element, placed);
        }

        foreach (var sibling in after ?? []) {
            Add(engine, sibling, parent, placed);
        }

        // ⚠ `ResolveAll` rather than resolving the one element, and it is not tidiness: a descendant
        // rule such as `.group:hover .x` is matched against the *tree*, but the inherited half of the
        // cascade reads the parent's already-resolved table — which only exists if the parent was
        // resolved first. Resolving the element alone hands it a null parent, so anything inherited
        // silently reads as unset.
        var styles = engine.ResolveAll();
        var id = engine.Properties.Lookup(property);

        // A probe to measure that was never placed is a test asking about an element that is not in
        // the document, which is a null that would read exactly like "the variant did not apply".
        var measured = measure is null
            ? element
            : placed.TryGetValue(measure, out var placedMeasure)
                ? placedMeasure
                : throw new ArgumentException("The probe to measure is not in the scene.", nameof(measure));

        return id != NameTable.None && styles[measured.Index].TryGet(id, out var value)
            ? engine.Values.NameOf(value)
            : null;
    }

    static StyleNodeId Add(StyleEngine engine, Probe probe, StyleNodeId? parent, Dictionary<Probe, StyleNodeId> placed) {
        var element = engine.Tree.CreateElement(probe.Tag, parent, classNames: probe.Classes);
        engine.Tree.SetState(element, probe.State);

        foreach (var (name, value) in probe.Attributes ?? []) {
            engine.Tree.SetAttribute(element, name, value);
        }

        placed[probe] = element;

        foreach (var child in probe.Children ?? []) {
            Add(engine, child, element, placed);
        }

        return element;
    }
}
