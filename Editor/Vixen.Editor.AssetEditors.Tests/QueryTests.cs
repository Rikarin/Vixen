// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>doc 37 § Part 5's environment-query editor: two lists, in the order they run.</summary>
public class QueryDocumentTests {
    [Fact]
    public void ANewQueryOpensCompilingRatherThanComplainingAboutItself() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Cover.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(document.LoadError);
        Assert.Equal("Cover", document.Content.Name);
        Assert.NotNull(document.Compile());
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void AQueryIsAuthoredSavedAndReopenedIdentically() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Flank.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var written = document.ToYaml();

        document.Save();

        var reopened = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Equal(written, reopened.ToYaml());
        Assert.Equal(document.Content.Tests.Count, reopened.Content.Tests.Count);
        Assert.Equal(document.Content.Tests[0].Purpose, reopened.Content.Tests[0].Purpose);
    }

    /// <summary>
    ///     ⚠ The one gesture this editor has that a utility table does not, and it is the one that
    ///     matters: a filtering test rejects a point and everything below it is skipped, so where a
    ///     trace sits in the list is the difference between four hundred raycasts and forty.
    /// </summary>
    [Fact]
    public void ReorderingTheTestsIsAnUndoableGesture() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Order.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var first = document.Content.Tests[0];

        Assert.True(document.MoveTest(0, 1));
        Assert.NotSame(first, document.Content.Tests[0]);
        Assert.True(document.Stack.Undo());
        Assert.Equal(first.Kind, document.Content.Tests[0].Kind);
        Assert.Equal(first.Purpose, document.Content.Tests[0].Purpose);
    }

    [Fact]
    public void MovingATestOffTheEndOfTheListDoesNothing() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Edge.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        Assert.False(document.MoveTest(0, -1));
        Assert.False(document.MoveTest(document.Content.Tests.Count - 1, 1));
        Assert.False(document.MoveTest(0, 0));
    }

    [Fact]
    public void EveryEditIsUndoable() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Undo.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var authored = document.ToYaml();

        document.Edit("Extent", content => content.Generators[0].Extent = 25f);

        Assert.NotEqual(authored, document.ToYaml());
        Assert.True(document.Stack.Undo());
        Assert.Equal(authored, document.ToYaml());
    }

    /// <summary>
    ///     ⚠ The preview is Unreal's testing pawn minus the pawn: it runs from where the editor says
    ///     the agent is standing, so "why is this query picking that corner" is a question an author
    ///     answers here rather than by launching the game.
    /// </summary>
    [Fact]
    public void ThePreviewGeneratesScoresAndKeepsTheFactorsBehindEachPoint() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Preview.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        using var world = new World("query-preview");

        Assert.True(document.Preview(world, Vector3.Zero, new Vector3(0f, 0f, 12f)));
        Assert.True(document.Results.Generated > 0);
        Assert.True(document.Results.Surviving > 0);
        Assert.True(document.Results.TryBest(out var best), "nothing survived.");

        // The query prefers points near the target, so the winner is on the target's side.
        Assert.True(best.Position.Z > 0f, best.ToString());

        // Detailed, so the per-test factors are there for the table beside the preview.
        Assert.Equal(document.Content.Tests.Count, document.Results.DetailOf(0).Length);
    }

    [Fact]
    public void AConditionReadsAsAPersonWouldWriteIt() {
        Assert.Equal(
            "10 m out, every 1 m",
            QueryView.Describe(new QueryGeneratorContent { Kind = QueryGeneratorKind.Grid, Extent = 10f, Inner = 1f })
        );

        Assert.Equal(
            "keep −∞…8",
            QueryView.Describe(new QueryTestContent { Purpose = QueryTestPurpose.Filter, Ceiling = 8f })
        );
    }

    /// <summary>A grid around the agent, kept near, and scored by how close to the target it is.</summary>
    internal static void Author(QueryDocument document) {
        document.Edit(
            "Author",
            content => {
                content.Generators.Clear();
                content.Tests.Clear();

                content.Generators.Add(
                    new() { Kind = QueryGeneratorKind.Grid, Extent = 8f, Inner = 2f, AroundQuerier = true }
                );

                content.Tests.Add(
                    new() {
                        Kind = QueryTestKind.Distance,
                        Purpose = QueryTestPurpose.Filter,
                        Ceiling = 8f
                    }
                );

                content.Tests.Add(
                    new() {
                        Kind = QueryTestKind.Distance,
                        FromContext = true,
                        Purpose = QueryTestPurpose.Score,
                        Maximum = 30f,
                        Curve = ResponseCurveKind.Linear,
                        Slope = -1f,
                        Shift = 1f
                    }
                );
            }
        );
    }
}

/// <summary>The panel: the lists, the curve and the preview, over one document.</summary>
public class QueryViewTests {
    [Fact]
    public void EveryListIsBuiltFromTheDocument() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/View.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        QueryDocumentTests.Author(document);

        using var world = new World("query-view");

        document.Preview(world, Vector3.Zero, new Vector3(0f, 0f, 12f));

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<QueryView>();

        view.Show(document);

        // ⚠ A frame, because the lists are a projection of four signals and writing a signal only
        // queues (ADR-007). These assertions are here for the reason `CompiledSceneTests` states —
        // "a projection that produced the right numbers and drew none of them would pass every test
        // about the numbers" — which is a claim about coverage rather than about timing. The
        // synchronous contract that is real is `Compile()`'s return value, and it still is.
        ui.Frame();

        Assert.Single(view.Generators.Children);
        Assert.Equal(2, view.Tests.Children.Count);
        Assert.NotEmpty(view.Preview.Children);
        Assert.NotEmpty(view.Diagnostics.Children);
    }

    [Fact]
    public void ShowingAgainRebuildsRatherThanAppends() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Rebuild.vxquery", string.Empty);
        var document = new QueryDocument(fixture.Project, AssetId.Empty, path);

        QueryDocumentTests.Author(document);

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<QueryView>();

        view.Show(document);
        ui.Frame();

        var tests = view.Tests.Children.Count;

        Assert.Equal(2, tests);

        view.Refresh();
        view.Refresh();
        ui.Frame();

        Assert.Equal(tests, view.Tests.Children.Count);
    }
}
