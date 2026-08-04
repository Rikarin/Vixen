// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Ai;
using Vixen.Editor.AssetEditors.Ai;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The authoring half of doc 37 § P6: three tables, and a graph derived from them.</summary>
public class GoapDomainDocumentTests {
    [Fact]
    public void ANewDomainOpensCompilingRatherThanComplainingAboutItself() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Villager.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(document.LoadError);
        Assert.Equal("Villager", document.Content.Name);
        Assert.NotNull(document.Compile());
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void ADomainIsAuthoredSavedAndReopenedIdentically() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Orchard.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var written = document.ToYaml();

        document.Save();

        var reopened = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Equal(written, reopened.ToYaml());
        Assert.Equal(document.Content.Actions.Count, reopened.Content.Actions.Count);
        Assert.Equal(
            document.Content.Actions[0].Effects[0].Increases,
            reopened.Content.Actions[0].Effects[0].Increases
        );
    }

    /// <summary>
    ///     ⚠ The failure a designer cannot see: a condition on a key nobody declared never holds, so
    ///     the action it gates never runs and the goal it belongs to is never met.
    /// </summary>
    [Fact]
    public void AConditionOnAKeyThatIsNotThereIsADiagnostic() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Typo.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        Author(document);
        document.Edit("Typo", content => content.Actions[1].Conditions[0].Key = "pears-carrid");
        document.Compile();

        Assert.Contains(
            document.Diagnostics,
            problem => problem.Message.Contains("is not a world key on this domain", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void EveryTableEditIsUndoable() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Undo.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var authored = document.ToYaml();

        document.Edit("Cost", content => content.Actions[0].Cost = 9f);

        Assert.NotEqual(authored, document.ToYaml());
        Assert.True(document.Stack.Undo());
        Assert.Equal(authored, document.ToYaml());
    }

    /// <summary>The orchard, as a file: two world keys, two actions and one goal.</summary>
    internal static void Author(GoapDomainDocument document) {
        document.Edit("Author", content => {
            content.Blackboard.Clear();
            content.Keys.Clear();
            content.Actions.Clear();
            content.Goals.Clear();

            content.Keys.Add(new() { Name = "pears-on-ground", Source = GoapSourceKind.Constant, Value = 1 });
            content.Keys.Add(new() { Name = "pears-carried", Source = GoapSourceKind.Constant, Value = 0 });
            content.Keys.Add(new() { Name = "hunger", Source = GoapSourceKind.Constant, Value = 80 });

            content.Actions.Add(
                new() {
                    Name = "PickUpPear",
                    Task = "Wait",
                    Fields = { ["Seconds"] = "1" },
                    Conditions = { new() { Key = "pears-on-ground", Comparison = GoapComparison.Greater, Value = 0 } },
                    Effects = { new() { Key = "pears-carried", Increases = true } }
                }
            );

            content.Actions.Add(
                new() {
                    Name = "EatPear",
                    Task = "Wait",
                    Fields = { ["Seconds"] = "2" },
                    Conditions = { new() { Key = "pears-carried", Comparison = GoapComparison.Greater, Value = 0 } },
                    Effects = { new() { Key = "hunger", Increases = false } }
                }
            );

            content.Goals.Add(
                new() {
                    Name = "NotHungry",
                    Conditions = { new() { Key = "hunger", Comparison = GoapComparison.Less, Value = 20 } }
                }
            );
        });
    }
}

/// <summary>The viewer: a graph nobody authors, laid out by depth from a goal.</summary>
public class GoapGraphProjectionTests {
    [Fact]
    public void TheGraphIsDerivedFromTheTablesAndLaidOutByDepth() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Graph.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        GoapDomainDocumentTests.Author(document);

        var domain = document.Compile()!;
        var projection = new GoapGraphProjection();
        var graph = projection.Project(domain);

        // Two actions and one goal, and the two edges nobody drew: eating serves the goal, and
        // picking up serves eating.
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Wires.Count);

        // Eating is one step from the goal and picking up is two, which is the order the search
        // walks them in.
        Assert.Equal(0, projection.Depths[1]);
        Assert.Equal(1, projection.Depths[0]);
    }

    /// <summary>
    ///     ⚠ An action no goal can reach still gets a box. It is almost always a mistake — an effect
    ///     on a key nothing wants — and hiding it would hide the mistake.
    /// </summary>
    [Fact]
    public void AnActionNoGoalCanReachIsStillDrawn() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Orphan.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        GoapDomainDocumentTests.Author(document);

        document.Edit("Orphan", content => {
            content.Keys.Add(new() { Name = "tidiness", Source = GoapSourceKind.Constant });
            content.Actions.Add(
                new() {
                    Name = "Sweep",
                    Task = "Wait",
                    Fields = { ["Seconds"] = "1" },
                    Effects = { new() { Key = "tidiness", Increases = true } }
                }
            );
        });

        var domain = document.Compile()!;
        var projection = new GoapGraphProjection();
        var graph = projection.Project(domain);

        Assert.Equal(4, graph.Nodes.Count);
        Assert.NotNull(projection.BoxOf(2));
    }

    [Fact]
    public void APlanIsHighlightedOnTheGraph() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Plan.vxgoap", string.Empty);
        var document = new GoapDomainDocument(fixture.Project, AssetId.Empty, path);

        GoapDomainDocumentTests.Author(document);

        var domain = document.Compile()!;
        var plan = new GoapPlan();
        var planner = new GoapPlanner(domain);
        var world = new World("goap-view");
        var entity = new Entity(3, 1, 0);
        var context = new AgentContext(world, entity, new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

        Assert.Equal(PlanFailure.None, planner.Resolve(in context, plan));

        var projection = new GoapGraphProjection();

        projection.Project(domain, plan);

        Assert.Equal("planned", projection.BoxOf(0)!.Accent);
        Assert.Equal("planned", projection.BoxOf(1)!.Accent);
    }

    [Fact]
    public void AConditionReadsAsAPersonWouldWriteIt() {
        Assert.Equal(
            "hunger < 20",
            GoapDomainView.Describe(new() { Key = "hunger", Comparison = GoapComparison.Less, Value = 20 })
        );

        Assert.Equal(
            "pears ≥ 1",
            GoapDomainView.Describe(new() { Key = "pears", Comparison = GoapComparison.GreaterOrEqual, Value = 1 })
        );
    }
}
