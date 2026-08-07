// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Xml.Linq;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>One box in a fixture's input tree.</summary>
/// <param name="IsText">Whether this is a <c>&lt;text&gt;</c> leaf, which needs a measure function.</param>
/// <param name="Text">Its Ahem text content, if any.</param>
/// <param name="Attributes">Its style attributes, verbatim.</param>
/// <param name="Children">Its children, in document order.</param>
sealed record TaffyInput(
    bool IsText,
    string? Text,
    FrozenDictionary<string, string> Attributes,
    IReadOnlyList<TaffyInput> Children
);

/// <summary>What Chrome computed for one box.</summary>
sealed record TaffyExpected(float X, float Y, float Width, float Height, IReadOnlyList<TaffyExpected> Children);

/// <summary>One conformance fixture.</summary>
/// <remarks>
///     <see cref="ViewportWidth" /> and <see cref="ViewportHeight" /> are <see cref="float.NaN" /> for
///     Taffy's <c>max-content</c>, which is what 5 504 of the 5 524 fixtures use and what
///     <c>CalculateLayout</c> already means by an undefined owner size.
/// </remarks>
sealed record TaffyFixture(
    string Category,
    string Name,
    bool UseRounding,
    float ViewportWidth,
    float ViewportHeight,
    TaffyInput Input,
    TaffyExpected Expected
);

/// <summary>
///     Loads the committed Taffy fixture corpus.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The corpus is committed as XML rather than translated into C#, and that is the one
///         structural decision worth defending.</b> Yoga's fixtures had to be translated because they
///         were C++ — there was no way to run them otherwise. Taffy's are already language-neutral, so
///         translating them would <i>add</i> a step whose bugs are indistinguishable from layout bugs,
///         which is precisely the failure mode this corpus exists to catch. It is also a tenth the
///         size: 5.3 MB of XML against roughly 20 MB and 375 000 lines of generated C# at the rate
///         <c>Generated/</c> runs at, for a test project that would then have to compile it every
///         build.
///     </para>
///     <para>
///         The cost of that choice is that a mis-read attribute is a runtime bug rather than a compile
///         error. Two things pay it back: <see cref="TaffyStyleMap" /> throws on any attribute it does
///         not recognise instead of ignoring it, and <c>TaffyCorpusCoverageTests</c> re-derives the
///         attribute set from the corpus and asserts the map covers all of it.
///     </para>
/// </remarks>
static class TaffyCorpus {
    static readonly Dictionary<string, IReadOnlyList<TaffyFixture>> Cache = [];
    static readonly Lock Gate = new();

    /// <summary>The eight corpus files, by the layout mode each exercises.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["block", "blockflex", "blockgrid", "flex", "float", "grid", "gridflex", "leaf"];

    public static IReadOnlyList<TaffyFixture> Load(string category) {
        lock (Gate) {
            if (Cache.TryGetValue(category, out var cached)) {
                return cached;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "Taffy", "Corpus", category + ".xml");
            if (!File.Exists(path)) {
                throw new FileNotFoundException(
                    $"The '{category}' corpus is missing from '{path}'. It is committed — see "
                    + "Core/Vixen.Ui.Layout.Tests/Taffy/README.md — so this is a build problem, not a "
                    + "missing reference clone.",
                    path
                );
            }

            var fixtures = XDocument
                .Load(path)
                .Root!
                .Elements("test")
                .Select(test => ReadFixture(category, test))
                .ToList();

            Cache[category] = fixtures;
            return fixtures;
        }
    }

    static TaffyFixture ReadFixture(string category, XElement test) {
        var viewport = test.Element("viewport")!;
        var input = test.Element("input")!.Elements().Single();
        var expected = test.Element("expectations")!.Elements().Single();

        return new TaffyFixture(
            category,
            test.Attribute("name")!.Value,
            (bool?)test.Attribute("use-rounding") ?? true,
            ReadViewport(viewport.Attribute("width")?.Value),
            ReadViewport(viewport.Attribute("height")?.Value),
            ReadInput(input),
            ReadExpected(expected)
        );
    }

    /// <summary><c>max-content</c> — or an absent attribute — is an undefined available size.</summary>
    static float ReadViewport(string? value) =>
        value is null or "max-content" ? float.NaN : Number(value.EndsWith("px", StringComparison.Ordinal) ? value[..^2] : value);

    static TaffyInput ReadInput(XElement element) {
        var isText = element.Name.LocalName == "text";
        var children = element.Elements().Select(ReadInput).ToList();

        // Only a childless <text> carries measurable content; Taffy reads the text of leaves only.
        var text = isText && children.Count == 0 ? element.Value.Trim() : null;

        return new TaffyInput(
            isText,
            text,
            element.Attributes().ToFrozenDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value),
            children
        );
    }

    static TaffyExpected ReadExpected(XElement element) =>
        new(
            Number(element.Attribute("x")!.Value),
            Number(element.Attribute("y")!.Value),
            Number(element.Attribute("width")!.Value),
            Number(element.Attribute("height")!.Value),
            element.Elements().Select(ReadExpected).ToList()
        );

    static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
}
