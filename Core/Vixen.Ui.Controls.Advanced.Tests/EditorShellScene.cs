// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using SceneViewport = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.EditorShell;

/// <summary>The four things doc 09 § Testing's Perf row names, in one document.</summary>
/// <remarks>
///     <para>
///         <b>"5 panels + viewport + 500-node graph + a 10⁶-row virtualised grid"</b>, composed
///         once and used by both the gate and the benchmark. ⚠ <b>The composition is the point and
///         the parts are not.</b> Virtualisation has its own tests, layout has a 100 000-node
///         throughput benchmark and the cascade has an incremental-restyle oracle; what none of them
///         can see is five panels' worth of style sharing against a graph's invalidation against a
///         grid realising a viewport's worth of rows, on one frame, on one thread.
///     </para>
///     <para>
///         ⚠ <b>Linked into <c>Vixen.Benchmarks.Ui</c> rather than copied.</b> A gate and a benchmark
///         that build "the same" scene from two files stop being about the same scene within a month,
///         and the drift is invisible — both go on passing, and only one of them is measuring what
///         the row says.
///     </para>
///     <para>
///         ⚠ <b>A million <i>items</i> are materialised and a million <i>rows</i> are not, and the
///         difference is the whole claim.</b> <c>DataGrid.SetItems</c> copies its source into a list
///         because sorting, grouping and random access all need one — that cost is inherent and is
///         paid in the fixture. What virtualisation avoids is an <i>element</i> per item, and that is
///         what the gate measures: <c>Grid.Rows</c> against <see cref="Rows" />.
///     </para>
/// </remarks>
public static class EditorShellScene {
    /// <summary>How many rows the grid is told about. Doc 09's number.</summary>
    public const int Rows = 1_000_000;

    /// <summary>How many nodes the graph holds. Also doc 09's.</summary>
    public const int Nodes = 500;

    /// <summary>And how many docked panels. Five, plus the viewport's own.</summary>
    public const int Panels = 5;

    /// <summary>What the shell's parts are called, so a test can find them without a selector.</summary>
    public sealed class Scene {
        public required UiDocument Document { get; init; }

        public required DockingHost Docking { get; init; }

        public required DataGrid Grid { get; init; }

        public required NodeCanvas Canvas { get; init; }

        public required TreeView Hierarchy { get; init; }

        public required PropertyGrid Inspector { get; init; }

        public required SceneViewport Viewport { get; init; }
    }

    /// <summary>Builds the shell and lays it out once.</summary>
    /// <param name="width">The viewport's width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The scene, already through one frame.</returns>
    public static Scene Build(float width = 1600f, float height = 1000f) {
        var document = new UiDocument(width, height);

        ControlTheme.Install(document);
        AdvancedTheme.Install(document);

        document.Load($"root {{ width: {width}px; height: {height}px; }}");

        var docking = document.Root.Add<DockingHost>();

        var hierarchyPanel = docking.AddPanel("hierarchy", "Hierarchy");
        var inspectorPanel = docking.AddPanel("inspector", "Inspector");
        var scenePanel = docking.AddPanel("scene", "Scene");
        var graphPanel = docking.AddPanel("graph", "Graph");
        var tablePanel = docking.AddPanel("table", "Table");

        var hierarchy = hierarchyPanel.Add<TreeView>();
        var inspector = inspectorPanel.Add<PropertyGrid>();
        var viewport = scenePanel.Add<SceneViewport>();
        var canvas = graphPanel.Add<NodeCanvas>();
        var grid = tablePanel.Add<DataGrid>();

        // ⚠ Docked into four regions rather than left as five tabs of one group, and that is the
        // difference between measuring the shell and measuring one panel. Panels added to a host go
        // into a single tab group, where four of the five are hidden and lay out to nothing — a
        // fixture that stopped there would report a very fast frame for a document that was drawing
        // a tree view and four empty rectangles.
        var centre = docking.Layout.Groups()[0];

        docking.Dock("inspector", centre, DockZone.Right);
        docking.Dock("table", docking.Layout.Groups()[0], DockZone.Bottom);
        docking.Dock("graph", docking.Layout.Groups()[0], DockZone.Right);

        Populate(hierarchy);
        Populate(canvas);
        Populate(grid);

        document.Update();
        document.Draw();

        return new Scene {
            Document = document,
            Docking = docking,
            Grid = grid,
            Canvas = canvas,
            Hierarchy = hierarchy,
            Inspector = inspector,
            Viewport = viewport
        };
    }

    /// <summary>A shallow scene tree, which is what a hierarchy panel holds.</summary>
    static void Populate(TreeView hierarchy) {
        for (var i = 0; i < 40; i++) {
            var folder = hierarchy.Root.Add($"Group {i}");

            for (var j = 0; j < 8; j++) {
                folder.Add($"Mesh {i}.{j}");
            }
        }

        hierarchy.Refresh();
    }

    /// <summary>
    ///     A material graph: <see cref="Nodes" /> nodes in a grid, each wired to the one before it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Wired, not merely added.</b> A graph of unconnected nodes exercises the item pool and
    ///     nothing else; the wire layer is the part whose cost is not proportional to what is on
    ///     screen, because a wire's two ends can be a screen apart.
    /// </remarks>
    static void Populate(NodeCanvas canvas) {
        var graph = canvas.Graph;
        GraphNode? previous = null;

        for (var i = 0; i < Nodes; i++) {
            var node = graph.AddNode($"Node {i}", new Vector2(i % 25 * 200f, i / 25 * 160f));

            node.AddInput("In");
            node.AddOutput("Out");

            if (previous is not null) {
                graph.Connect(previous.Outputs[0], node.Inputs[0]);
            }

            previous = node;
        }

        canvas.Refresh();
    }

    /// <summary>A million items, three columns, and no element for any of them.</summary>
    static void Populate(DataGrid grid) {
        grid.AddColumn("Index", static item => ((Row)item).Index.ToString());
        grid.AddColumn("Name", static item => ((Row)item).Name);
        grid.AddColumn("Kind", static item => ((Row)item).Kind);

        grid.SetItems(Enumerate());
        grid.Refresh();
    }

    static IEnumerable<object> Enumerate() {
        for (var i = 0; i < Rows; i++) {
            yield return new Row(i);
        }
    }

    /// <summary>One row of the table.</summary>
    /// <remarks>
    ///     A struct-shaped record boxed once per row rather than a string per column: the grid's
    ///     accessors are what turn it into text, and doing that work up front would move the cost
    ///     being measured into the fixture.
    /// </remarks>
    sealed record Row(int Index) {
        public string Name => $"Entity {Index}";

        public string Kind => (Index & 3) switch {
            0 => "Mesh",
            1 => "Light",
            2 => "Camera",
            _ => "Empty"
        };
    }
}
