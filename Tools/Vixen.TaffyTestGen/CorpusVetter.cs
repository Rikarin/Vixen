// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml;

namespace Vixen.TaffyTestGen;

/// <summary>What a single fixture was found to contain, or why it was refused.</summary>
sealed record VettedFixture(string Name, string Category, string Text, IReadOnlySet<string> Attributes) {
    public int NodeCount { get; init; }
}

/// <summary>Why one fixture did not make it into the committed corpus.</summary>
sealed record RefusedFixture(string Name, string Category, string Reason);

/// <summary>
///     Reads one of Taffy's XML fixtures and decides whether it is a fixture this repository knows
///     how to carry.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole value of this class is that it refuses rather than ignores.</b> A conformance
///         corpus is only an oracle if a fixture that sets something the reader does not understand
///         <i>fails</i>. The opposite — quietly dropping an attribute — produces a test that passes
///         while proving nothing, and at 5 500 fixtures nobody would ever notice. So every element
///         name and every attribute name has to be in the tables below or the fixture is refused by
///         name and counted in the report.
///     </para>
///     <para>
///         ⚠ <b>This vets names, not meanings.</b> Whether a value is <i>interpreted</i> correctly is
///         the harness's problem, and `TaffyCorpusCoverageTests` is what stops the two drifting: it
///         re-derives the attribute set from the committed corpus and asserts the harness's map
///         covers it. That check runs in CI, where this tool does not — CI has no reference clone.
///     </para>
/// </remarks>
static class CorpusVetter {
    /// <summary>The seven element names Taffy's XML format uses. Anything else is a format change.</summary>
    static readonly HashSet<string> KnownElements = ["test", "viewport", "input", "expectations", "div", "text", "node"];

    /// <summary>
    ///     Every style attribute a <c>&lt;div&gt;</c> or <c>&lt;text&gt;</c> may carry.
    /// </summary>
    /// <remarks>
    ///     Taken from <c>build_style</c> in Taffy's own <c>tests/xml.rs</c> rather than from a survey of
    ///     the corpus, so that an attribute which exists in the format but happens to be unused today
    ///     is still recognised when a future fixture starts setting it.
    /// </remarks>
    static readonly HashSet<string> KnownStyleAttributes = [
        // Box and flow.
        "display", "direction", "box-sizing", "position", "overflow-x", "overflow-y", "scrollbar-width",
        "float", "clear", "writing-mode", "text-align",

        // Sizing.
        "width", "height", "min-width", "min-height", "max-width", "max-height", "aspect-ratio",

        // The four edge groups, each fully expanded — Taffy's format has no shorthands.
        "top", "left", "bottom", "right",
        "margin-top", "margin-left", "margin-bottom", "margin-right",
        "padding-top", "padding-left", "padding-bottom", "padding-right",
        "border-top", "border-left", "border-bottom", "border-right",
        "row-gap", "column-gap",

        // Alignment, shared by flex and grid.
        "align-items", "align-self", "align-content", "justify-items", "justify-self", "justify-content",

        // Flex.
        "flex-direction", "flex-wrap", "flex-grow", "flex-shrink", "flex-basis",

        // Grid.
        "grid-auto-flow", "grid-template-rows", "grid-template-columns", "grid-template-areas",
        "grid-auto-rows", "grid-auto-columns",
        "grid-row-start", "grid-row-end", "grid-column-start", "grid-column-end"
    ];

    /// <summary>What an <c>&lt;expectations&gt;</c> node may assert.</summary>
    static readonly HashSet<string> KnownExpectationAttributes = ["x", "y", "width", "height", "scroll_width", "scroll_height"];

    public static VettedFixture? Vet(string path, string category, List<RefusedFixture> refused) {
        var name = Path.GetFileNameWithoutExtension(path);
        var text = File.ReadAllText(path);

        try {
            var (attributes, inputShape, expectationShape) = Scan(text);

            // ⚠ The two trees are zipped positionally by the runner, so a shape mismatch would
            // silently drop the tail of the longer one rather than fail.
            if (!inputShape.SequenceEqual(expectationShape)) {
                refused.Add(new RefusedFixture(name, category, "input and expectation trees have different shapes"));
                return null;
            }

            return new VettedFixture(name, category, text, attributes) { NodeCount = inputShape.Count };
        } catch (Exception exception) {
            refused.Add(new RefusedFixture(name, category, exception.Message));
            return null;
        }
    }

    /// <summary>
    ///     One streaming pass: collect the attribute names, and record each tree's shape as the
    ///     depth-first sequence of child counts.
    /// </summary>
    static (HashSet<string> Attributes, List<int> InputShape, List<int> ExpectationShape) Scan(string text) {
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        var inputShape = new List<int>();
        var expectationShape = new List<int>();

        // One counter per open box element, holding how many children it has seen. Closing a box
        // pops its counter and appends it to that section's shape, so the shape is the post-order
        // sequence of child counts — enough to tell two trees apart, and cheap to compare.
        var openChildCounts = new Stack<int>();
        var section = string.Empty;

        using var reader = XmlReader.Create(
            new StringReader(text),
            new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true }
        );

        while (reader.Read()) {
            var element = reader.Name;
            var isBox = element is "div" or "text" or "node";

            switch (reader.NodeType) {
                case XmlNodeType.EndElement when isBox:
                    Close();
                    break;

                case XmlNodeType.Element:
                    if (!KnownElements.Contains(element)) {
                        throw new InvalidOperationException($"unknown element <{element}>");
                    }

                    if (element is "input" or "expectations") {
                        section = element;
                    }

                    var isEmpty = reader.IsEmptyElement;
                    VetAttributes(reader, element, attributes);

                    if (!isBox) {
                        break;
                    }

                    if (openChildCounts.Count > 0) {
                        openChildCounts.Push(openChildCounts.Pop() + 1);
                    }

                    openChildCounts.Push(0);

                    // A self-closing <div/> never raises an EndElement, so it closes here.
                    if (isEmpty) {
                        Close();
                    }

                    break;
            }
        }

        return (attributes, inputShape, expectationShape);

        void Close() => (section == "input" ? inputShape : expectationShape).Add(openChildCounts.Pop());
    }

    static void VetAttributes(XmlReader reader, string element, HashSet<string> attributes) {
        if (!reader.HasAttributes) {
            return;
        }

        for (var index = 0; index < reader.AttributeCount; index++) {
            reader.MoveToAttribute(index);
            var attribute = reader.Name;

            var known = element switch {
                "test" => attribute is "name" or "use-rounding",
                "viewport" => attribute is "width" or "height",
                "node" => KnownExpectationAttributes.Contains(attribute),
                "div" or "text" => KnownStyleAttributes.Contains(attribute),
                _ => false
            };

            if (!known) {
                throw new InvalidOperationException($"unknown attribute '{attribute}' on <{element}>");
            }

            if (element is "div" or "text") {
                attributes.Add(attribute);
            }

            if (reader.Value.Length == 0) {
                throw new InvalidOperationException($"empty value for '{attribute}' on <{element}>");
            }
        }

        reader.MoveToElement();
    }
}
