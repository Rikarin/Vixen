// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Core.Curves;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>
///     An environment query, open for editing: the generators, the tests in order, a curve, and the
///     points a preview run produced.
/// </summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5's environment-query editor. A list — generators, then tests in order — with
///         each test's purpose, curve and clamping inline, and a preview of the generated points
///         green through red by score with the filtered ones crossed out.
///     </para>
///     <para>
///         ⚠ <b>The test list is ordered and the order is editable, which is the one gesture this
///         editor has that the utility table does not.</b> A filtering test rejects a point and
///         everything below it is skipped, so moving a trace below a distance filter is the
///         difference between four hundred raycasts and forty — a real cost, made by a real gesture,
///         with the count of surviving points beside it saying what it bought.
///     </para>
///     <para>
///         ⚠ <b>The curve control is the utility editor's.</b> doc 37 § D14: a test's scoring equation
///         <i>is</i> a consideration's response curve, so the same
///         <see cref="Vixen.Ui.Controls.Advanced.CurveEditor" /> draws both and an author who has
///         tuned one has tuned the other.
///     </para>
/// </remarks>
public sealed class QueryView : Control {
    QueryDocument? document;
    QueryTestContent? test;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "query-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What makes the candidates.</summary>
    public UiElement Generators { get; private set; } = null!;

    /// <summary>What filters and scores them, in the order they run.</summary>
    public UiElement Tests { get; private set; } = null!;

    /// <summary>The selected test's response curve.</summary>
    public CurveEditor Curve { get; private set; } = null!;

    /// <summary>The points a preview run produced, best first.</summary>
    public UiElement Preview { get; private set; } = null!;

    /// <summary>What the last compile and the last run said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>The button that compiles the query.</summary>
    public Button Build { get; private set; } = null!;

    /// <summary>The test that is selected, or null.</summary>
    public QueryTestContent? Selected => test;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var body = Add("query-body");
        var left = body.Add("query-lists");

        var toolbar = left.Add("query-toolbar");

        Build = toolbar.Add<Button>();
        Build.Label = "Compile";

        left.Add("panel-title").Text = "Generators";
        Generators = left.Add("query-generators");

        left.Add("panel-title").Text = "Tests";
        Tests = left.Add("query-tests");

        var right = body.Add("query-detail");

        right.Add("panel-title").Text = "Response";
        Curve = right.Add<CurveEditor>();

        right.Add("panel-title").Text = "Points";
        Preview = right.Add("query-points");

        Add("panel-title").Text = "Diagnostics";
        Diagnostics = Add("query-diagnostics");
    }

    /// <summary>Shows a document.</summary>
    /// <param name="value">The query.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public void Show(QueryDocument value) {
        ArgumentNullException.ThrowIfNull(value);

        document = value;

        if (!listening) {
            document.Changed += _ => Refresh();
            listening = true;
        }

        test = document.Content.Tests.Count > 0 ? document.Content.Tests[0] : null;

        Refresh();
        Compile();
    }

    /// <summary>Selects a test.</summary>
    /// <param name="value">The test, or null.</param>
    public void Select(QueryTestContent? value) {
        test = value;

        Refresh();
    }

    /// <summary>Compiles the query and lists what it said.</summary>
    /// <returns>The query, or null.</returns>
    public EnvironmentQuery? Compile() {
        if (document is null) {
            return null;
        }

        var query = document.Compile();

        Empty(Diagnostics);

        foreach (var diagnostic in document.Diagnostics) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = diagnostic.Node.IsSome ? diagnostic.Node.ToString() : "query";
            row.Add("analysis-message").Text = diagnostic.Message;
        }

        // Said on success too: a list that empties itself when everything is fine cannot be told apart
        // from one that never ran.
        if (query is not null && document.Diagnostics.Count == 0) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = "query";
            row.Add("analysis-message").Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{query.Name} compiles: {query.Generators.Length} generator(s) making about "
                + $"{query.Estimate} point(s), {query.Tests.Length} test(s)."
            );
        }

        return query;
    }

    /// <summary>Rebuilds every list from the document.</summary>
    public void Refresh() {
        if (document is null) {
            return;
        }

        RefreshGenerators();
        RefreshTests();
        RefreshCurve();
        RefreshPreview();
    }

    void RefreshGenerators() {
        Empty(Generators);

        foreach (var generator in document!.Content.Generators) {
            var row = Generators.Add("query-row");

            row.Add("query-kind").Text = generator.Kind.ToString();
            row.Add("query-detail").Text = Describe(generator);
        }
    }

    void RefreshTests() {
        Empty(Tests);

        var order = 0;

        foreach (var authored in document!.Content.Tests) {
            var row = Tests.Add(ReferenceEquals(authored, test) ? "query-row-selected" : "query-row");

            // The running order is a number on every row, because "why is this query expensive" is
            // answered by which test is above which — and an author cannot see that from a list that
            // does not say where each one sits.
            row.Add("query-order").Text = order.ToString(CultureInfo.InvariantCulture);
            row.Add("query-kind").Text = authored.Kind == QueryTestKind.Registered
                ? authored.Source
                : authored.Kind.ToString();

            row.Add("query-purpose").Text = authored.Purpose.ToString();
            row.Add("query-detail").Text = Describe(authored);
            order++;
        }
    }

    void RefreshCurve() {
        if (test is null) {
            return;
        }

        var shape = test.BuildCurve();
        var curve = new AnimationCurve();

        // Sampled at a fixed rate rather than shown as its own keys, for the reason UtilitySetView's
        // does it: five of the six shapes have no keys at all.
        for (var step = 0; step <= 32; step++) {
            var time = step / 32f;

            curve.Add(time, shape.Evaluate(time));
        }

        Curve.Apply(curve);
    }

    void RefreshPreview() {
        Empty(Preview);

        var results = document!.Results;

        if (results.Count == 0) {
            return;
        }

        var summary = Preview.Add("query-summary");

        summary.Add("query-detail").Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{results.Generated} generated, {results.Surviving} survived."
        );

        var index = 0;

        foreach (var point in results.Points) {
            // ⚠ Filtered points are listed and struck through rather than dropped. "Why is my query
            // returning nothing" is answered by seeing which test rejected everything, and a preview
            // that only showed survivors would answer it with an empty list.
            var row = Preview.Add(
                point.Filtered ? "query-point-filtered"
                : index == results.Best ? "query-point-best"
                : "query-point"
            );

            row.Add("query-position").Text = point.Position.ToString();
            row.Add("query-score").Text = point.Filtered
                ? "filtered"
                : point.Score.ToString("0.###", CultureInfo.InvariantCulture);

            var detail = results.DetailOf(index);

            if (!detail.IsEmpty) {
                var factors = new string[detail.Length];

                for (var factor = 0; factor < detail.Length; factor++) {
                    factors[factor] = detail[factor].ToString("0.##", CultureInfo.InvariantCulture);
                }

                row.Add("query-factors").Text = string.Join(" · ", factors);
            }

            index++;
        }
    }

    /// <summary>One generator, as a person reads it.</summary>
    /// <param name="generator">The generator.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator" /> is null.</exception>
    public static string Describe(QueryGeneratorContent generator) {
        ArgumentNullException.ThrowIfNull(generator);

        return generator.Kind switch {
            QueryGeneratorKind.Grid => string.Create(
                CultureInfo.InvariantCulture,
                $"{generator.Extent:0.#} m out, every {generator.Inner:0.##} m"
            ),
            QueryGeneratorKind.Circle => string.Create(
                CultureInfo.InvariantCulture,
                $"{generator.Points} points at {generator.Extent:0.#} m"
            ),
            QueryGeneratorKind.Donut => string.Create(
                CultureInfo.InvariantCulture,
                $"{generator.Rings} × {generator.Points} between {generator.Inner:0.#} and {generator.Extent:0.#} m"
            ),
            QueryGeneratorKind.Cone => string.Create(
                CultureInfo.InvariantCulture,
                $"{generator.Degrees:0}° wide, {generator.Extent:0.#} m deep"
            ),
            QueryGeneratorKind.CurrentLocation => "where the agent is standing",
            _ => generator.Source
        };
    }

    /// <summary>One test, as a person reads it.</summary>
    /// <param name="test">The test.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="test" /> is null.</exception>
    public static string Describe(QueryTestContent test) {
        ArgumentNullException.ThrowIfNull(test);

        var range = string.Create(CultureInfo.InvariantCulture, $"{test.Minimum:0.##}…{test.Maximum:0.##}");

        if (test.Purpose == QueryTestPurpose.Score) {
            return string.Create(CultureInfo.InvariantCulture, $"{range} through {test.Curve}");
        }

        var floor = float.IsNegativeInfinity(test.Floor)
            ? "−∞"
            : test.Floor.ToString("0.##", CultureInfo.InvariantCulture);

        var ceiling = float.IsPositiveInfinity(test.Ceiling)
            ? "∞"
            : test.Ceiling.ToString("0.##", CultureInfo.InvariantCulture);

        return test.Purpose == QueryTestPurpose.Filter
            ? $"keep {floor}…{ceiling}"
            : string.Create(CultureInfo.InvariantCulture, $"keep {floor}…{ceiling}, then {range} through {test.Curve}");
    }

    static void Empty(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}

/// <summary>Opens a <c>.vxquery</c>.</summary>
public sealed class QueryEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Environment Query";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [QueryDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new QueryDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<QueryView>();

        view.Show((QueryDocument)document);

        return view;
    }
}
