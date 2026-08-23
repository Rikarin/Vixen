// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>What happened when one fixture was run.</summary>
enum TaffyOutcome {
    /// <summary>Every box matched Chrome, within a tenth of a pixel.</summary>
    Pass,

    /// <summary>The tree laid out, and at least one box is in the wrong place or the wrong size.</summary>
    Fail,

    /// <summary>The fixture sets something <see cref="LayoutTree" /> has no field for. Not a failure.</summary>
    Unsupported
}

/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="Detail">The mismatching box, or the missing feature. Empty on a pass.</param>
sealed record TaffyResult(TaffyOutcome Outcome, string Detail) {
    public static readonly TaffyResult Passed = new(TaffyOutcome.Pass, string.Empty);
}

/// <summary>Builds a <see cref="LayoutTree" /> from a fixture, lays it out, and compares.</summary>
static class TaffyFixtureRunner {
    /// <summary>
    ///     Taffy's own tolerance, from the <c>PartialEq</c> on its <c>OutputNode</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Two orders of magnitude looser than <c>Generated/</c>'s 0.0001, and deliberately the same
    ///     number Taffy uses rather than a tighter one of our own. Chrome's own output is rounded to
    ///     a sixteenth of a pixel in places, and the corpus records it as decimal text; a tolerance
    ///     tighter than the source data's own precision would fail on the transcription rather than
    ///     on the layout.
    /// </remarks>
    public const float Tolerance = 0.1f;

    public static TaffyResult Run(TaffyFixture fixture) {
        using var tree = new LayoutTree();

        // ⚠ Rounding is a property of the whole tree, not of a call. Taffy's `use-rounding="false"`
        // fixtures (20 of them) assert unrounded geometry, and a scale factor of zero is how this
        // store spells "do not round" — see LayoutTree.PixelGrid.
        tree.PointScaleFactor = fixture.UseRounding ? 1f : 0f;

        LayoutNodeId root;
        try {
            root = Build(tree, fixture.Input, default, isRoot: true);
        } catch (TaffyUnsupportedException unsupported) {
            return new TaffyResult(TaffyOutcome.Unsupported, unsupported.Feature);
        }

        var direction = fixture.Input.Attributes.GetValueOrDefault("direction") == "rtl" ? Direction.Rtl : Direction.Ltr;
        tree.CalculateLayout(root, fixture.ViewportWidth, fixture.ViewportHeight, direction);

        var failures = new StringBuilder();
        Compare(tree, root, fixture.Expected, "root", failures);

        return failures.Length == 0 ? TaffyResult.Passed : new TaffyResult(TaffyOutcome.Fail, failures.ToString());
    }

    static LayoutNodeId Build(LayoutTree tree, TaffyInput input, TaffyBox parent, bool isRoot) {
        var node = tree.CreateNode();
        var self = TaffyBox.From(input);
        TaffyStyleMap.ApplyCssInitialValues(tree, node, isRoot);

        foreach (var (name, value) in input.Attributes) {
            TaffyStyleMap.Apply(tree, node, name, value, self, parent);
        }

        if (input.Children.Count == 0) {
            // ⚠ Only a <text> leaf with content measures. A childless <div> is a genuinely empty box,
            // and giving it a measure function would make it 0×10 instead of 0×0.
            if (input is { IsText: true, Text: { Length: > 0 } text }) {
                var isVertical = input.Attributes.GetValueOrDefault("writing-mode", string.Empty).Contains("vertical", StringComparison.Ordinal);
                tree.SetContext(node, new TaffyText(text, isVertical));
                tree.SetMeasureFunction(node, TaffyAhemMeasure.Measure);
            }

            return node;
        }

        // ⚠ A self-relative `align-items` resolves against each child's own direction, so it is not
        // one container value — an rtl child and an ltr child of the same ltr column go to opposite
        // edges from the same declaration. Pushing it down as align-self is exactly equivalent, and
        // a child that states its own align-self overrides it as the cascade requires.
        var selfRelativeItems = input.Attributes.GetValueOrDefault("align-items") is { } items && TaffyStyleMap.IsSelfRelative(items)
            ? items
            : null;

        // ⚠ The same is true of a grid's `justify-items`, and for a stronger reason: on the inline
        // axis `self-start` and `start` disagree whenever the child's `direction` differs from its
        // container's, which is a thing four fixtures do on purpose —
        // `grid_justify_items_self_end_child_rtl` gives one container an rtl child and an ltr child
        // and expects them at opposite edges of their areas.
        var selfRelativeJustify = input.Attributes.GetValueOrDefault("justify-items") is { } inline
            && TaffyStyleMap.IsSelfRelative(inline)
                ? inline
                : null;

        for (var index = 0; index < input.Children.Count; index++) {
            var childInput = input.Children[index];
            var child = Build(tree, childInput, self, isRoot: false);
            var childBox = TaffyBox.From(childInput);

            if (selfRelativeItems is not null && !childInput.Attributes.ContainsKey("align-self")) {
                tree.SetAlignSelf(child, TaffyStyleMap.AlignItemsForChild(selfRelativeItems, self, childBox));
            }

            if (selfRelativeJustify is not null && !childInput.Attributes.ContainsKey("justify-self")) {
                tree.SetJustifySelf(child, TaffyStyleMap.JustifyItemsForChild(selfRelativeJustify, self, childBox));
            }

            tree.InsertChild(node, child, index);
        }

        return node;
    }

    static void Compare(LayoutTree tree, LayoutNodeId node, TaffyExpected expected, string path, StringBuilder failures) {
        Check(tree.GetLeft(node), expected.X, "x");
        Check(tree.GetTop(node), expected.Y, "y");
        Check(tree.GetWidth(node), expected.Width, "width");
        Check(tree.GetHeight(node), expected.Height, "height");

        var children = tree.GetChildCount(node);
        if (children != expected.Children.Count) {
            failures.AppendLine(
                CultureInfo.InvariantCulture,
                $"  {path}: {children} children laid out, {expected.Children.Count} expected"
            );

            return;
        }

        for (var index = 0; index < children; index++) {
            Compare(tree, tree.GetChild(node, index), expected.Children[index], $"{path}.{index}", failures);
        }

        return;

        void Check(float actual, float want, string what) {
            if (!(MathF.Abs(actual - want) > Tolerance)) {
                return;
            }

            failures.AppendLine(CultureInfo.InvariantCulture, $"  {path}.{what}: expected {want}, got {actual}");
        }
    }
}
