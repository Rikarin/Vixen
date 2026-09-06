// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A compound changed outside the editor reaches the graph that inlines it.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/922">#922</a>.</b>
///         <c>TextureGraphDocument.Republish</c> hears a save made <em>in the editor</em>, through
///         <c>EditorProject.DocumentSaving</c>. A <c>git checkout</c>, a text editor and a tool that
///         writes <c>.vxtexgraph</c> files raise nothing at all — so a containing graph went on
///         inlining the version that was on disk when it opened, with the old ports and the old
///         defaults, and the bake made from it was that old graph with nothing saying so.
///     </para>
///     <para>
///         ⚠ <b>The shape chosen is a notification on <c>EditorDocument</c>, and the issue's other
///         option is not merely worse — it cannot be written.</b> "The host that owns the
///         <c>ExternalEdits</c> calls <c>Republish()</c> on every open <c>TextureGraphDocument</c>"
///         asks <c>EditorApplication</c> to name a type in a plugin it deliberately does not
///         reference, which is doc 48 § D14's whole claim. <c>OnProjectFileChanged</c> is the
///         project-level "a file changed" the issue's second option describes, expressed where every
///         document can already be reached.
///     </para>
///     <para>
///         ⚠ <b>Asserted on the compiled plan and not on the registry.</b> A node type whose ports
///         changed is bookkeeping; what an author loses is a bake made from the old graph, so the
///         claim is that the number inside the inlined compound is the new one. The compound is a
///         single <c>Source/Uniform</c> for that reason — its red is the whole oracle.
///     </para>
/// </remarks>
public class ExternalCompoundTests {
    /// <summary>The node-type path the compound publishes under, from its file name.</summary>
    const string Published = "Grunge";

    /// <summary>What the compound painted when the graph opened, and what it paints after.</summary>
    const float Before = 0.25f;

    const float After = 0.75f;

    [Fact]
    public void A_compound_written_by_something_other_than_the_editor_reaches_an_open_graph() {
        using var fixture = new TexturingFixture();

        var compound = Publish(fixture, Before);
        var wall = Containing(fixture);

        Assert.Equal(Before, Red(wall));

        // ⚠ Written straight to disk rather than through `Save`, which is the whole point: a save
        // raises `EditorProject.DocumentSaving` and the containing graph already hears that. What
        // nothing heard is a file that changed with no editor involved.
        Rewrite(compound, After);

        Assert.Equal(Before, Red(wall));

        Route(fixture, TextureNodeLibrary.CompoundFolder + "/" + Published + TextureGraphDocument.Extension);

        Assert.Equal(After, Red(wall));
    }

    /// <summary>⚠ And an overflow, where the watcher cannot say which file it was.</summary>
    /// <remarks>
    ///     <b>The conservative answer, and the same one <c>ExternalEdits.Rescan</c> gives about a
    ///     document's own file.</b> Losing events says nothing about what changed, so a graph that
    ///     assumed the best would go on inlining a compound nobody can see is old — the cost of being
    ///     wrong the other way is one republish.
    /// </remarks>
    [Fact]
    public void An_overflow_republishes_because_it_cannot_say_what_changed() {
        using var fixture = new TexturingFixture();

        var compound = Publish(fixture, Before);
        var wall = Containing(fixture);

        Assert.Equal(Before, Red(wall));

        Rewrite(compound, After);

        using ExternalEdits edits = new(fixture.Project);

        edits.Rescan();

        Assert.Equal(After, Red(wall));
    }

    /// <summary>⚠ And a change to something else does not rebuild the library.</summary>
    /// <remarks>
    ///     <b>The half that makes the first two mean anything.</b> A document that marked itself stale
    ///     on every drained change would pass both of them and would republish the whole node library
    ///     on every file anybody touches — a directory walk and a parse per compound, on the frame,
    ///     for a texture nobody edited. <c>Republish</c> hands back whether it rebuilt, which is the
    ///     only way to see the difference.
    /// </remarks>
    [Fact]
    public void A_change_outside_the_compound_folder_rebuilds_nothing() {
        using var fixture = new TexturingFixture();

        Publish(fixture, Before);

        var wall = Containing(fixture);

        // Drained once so that whatever opening the document left behind is settled, and the claim
        // below is about this change rather than about the state before it.
        Assert.False(wall.Republish());

        Route(fixture, "Elsewhere" + TextureGraphDocument.Extension);

        Assert.False(wall.Republish());
    }

    /// <summary>Hands one drained change to a real <see cref="ExternalEdits" />.</summary>
    /// <param name="fixture">The project.</param>
    /// <param name="relative">The path under <c>Assets/</c> that changed.</param>
    /// <remarks>
    ///     ⚠ <b>No watcher, which is a state the constructor documents and a project on an unwatched
    ///     share is really in.</b> What a watcher buys is suppression of the editor's own saves, and
    ///     there is no save here; the routing is the same code either way, and the virtual path is
    ///     then relative to the root, which is what a watcher mounted on <c>Assets/</c> reports.
    /// </remarks>
    static void Route(TexturingFixture fixture, string relative) {
        using ExternalEdits edits = new(fixture.Project);

        edits.Apply([new FileChange(new VirtualPath("/" + relative), FileChangeKind.Changed, default)]);
    }

    /// <summary>The red the graph's one inlined <c>Uniform</c> writes, as the compiler produced it.</summary>
    static float Red(TextureGraphDocument document) {
        var compilation = document.Compile();

        Assert.NotNull(compilation.Plan);

        var uniform = Assert.Single(
            compilation.Plan!.Ops.Where(op => string.Equals(op.Kernel, "Uniform", StringComparison.Ordinal))
        );

        return uniform.Find("red")!.Value.Value;
    }

    /// <summary>Writes a compound into the project's own <c>Assets/Compounds</c> and hands it back.</summary>
    static TextureGraphDocument Publish(TexturingFixture fixture, float red) {
        var folder = Path.Combine(fixture.Paths.Assets, TextureNodeLibrary.CompoundFolder);

        Directory.CreateDirectory(folder);

        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph(TextureNodeLibrary.CompoundFolder + "/" + Published),
            Path.Combine(folder, Published + TextureGraphDocument.Extension)
        );

        // ⚠ The starter goes first: a new document is a `Source/Uniform` into an `Output/Output`,
        // and inlining that second output into a containing graph is a TG0006 about two nodes
        // writing one usage rather than anything to do with this test.
        foreach (var node in compound.Graph.Nodes.ToArray()) {
            compound.Graph.Remove(node.Id, out _);
        }

        compound.Graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var colour = compound.Graph.Add("Source/Uniform");
        var exit = compound.Graph.Add(SubGraphs.OutputType);

        colour.SetValue("Colour", red, red, red, 1f);
        compound.Graph.Connect(new(colour.Id, "Out"), new(exit.Id, "Out"));

        Rewrite(compound, red);

        return compound;
    }

    /// <summary>Puts the compound on disk with a new colour, without saving it.</summary>
    static void Rewrite(TextureGraphDocument compound, float red) {
        var colour = compound.Graph.Nodes.Single(node => node.Type == "Source/Uniform");

        colour.SetValue("Colour", red, red, red, 1f);

        File.WriteAllText(compound.AssetPath, compound.ToYaml());
    }

    /// <summary>A graph containing the published compound, opened after the compound exists.</summary>
    static TextureGraphDocument Containing(TexturingFixture fixture) {
        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Wall"),
            fixture.Paths.Absolute("Assets/Wall" + TextureGraphDocument.Extension)
        );

        Assert.True(document.Registry.TryGet(Published, out _), "the compound is not a node type this graph has");

        var output = document.Graph.Nodes.Single(node => node.Type == "Output/Output");

        foreach (var node in document.Graph.Nodes.Where(node => node.Type == "Source/Uniform").ToArray()) {
            document.Graph.Remove(node.Id, out _);
        }

        var used = document.Graph.Add(Published);

        document.Graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        return document;
    }
}
