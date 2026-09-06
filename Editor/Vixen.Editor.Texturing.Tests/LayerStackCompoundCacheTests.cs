// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A layer stack reads its project's compounds when one changes and not when a picture is wanted
///     — <a href="https://github.com/Rikarin/Vixen/issues/956">#956</a>.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/924">#924</a>'s fix, one cost along.</b>
///         Passing the project's assets folder down <c>LayerStackCompiler.Compile</c> reaches
///         <c>TextureCompoundLibrary.Publish</c>, which is a recursive directory walk plus a
///         <c>File.ReadAllText</c> and a YAML parse per project compound.
///         <c>LayerStackPreview.Evaluate</c> runs from <c>LayerStackView.Edited</c>, which an opacity
///         slider raises once per frame of a drag — so the fix for a correctness gap put the
///         filesystem on the interactive path.
///     </para>
///     <para>
///         ⚠ <b>The instrument is a compound deleted behind the editor's back, and it has to be,
///         because "the disk was not read" has no counter.</b> A stack that re-walked the folder
///         would lose the node type and refuse the fill; one that kept the library it published when
///         the document opened goes on compiling. Nothing else here can tell the two apart: the
///         picture, the plan and the diagnostics are all identical either way while the file is
///         still there.
///     </para>
///     <para>
///         ⚠ <b>And the second half is what stops the first from being a description of a bug.</b>
///         A cache with no invalidation would pass the middle assertion and be worse than the walk —
///         a stack baking from a compound nobody can see is old. <c>OnProjectFileChanged</c> is what
///         separates them, and it is asserted in the same test rather than in a neighbouring one.
///     </para>
///     <para>
///         Everything goes through <c>LayerStackPreview.Evaluate</c>, because the whole content of
///         the issue is what a production caller does per frame. No device: the refusal this turns on
///         is reported before one is asked for, which is the state the editor is in at start-up.
///     </para>
/// </remarks>
public class LayerStackCompoundCacheTests {
    /// <summary>The node-type path the compound publishes under, from its file name.</summary>
    const string Published = "Ochre";

    /// <summary>Where the compound is, under <c>Assets/</c>.</summary>
    const string Relative =
        TextureNodeLibrary.CompoundFolder + "/" + Published + TextureGraphDocument.Extension;

    /// <summary>A picture wanted twice reads the compound folder once.</summary>
    [Fact]
    public void Evaluating_twice_does_not_re_read_the_compound_folder() {
        using var fixture = new TexturingFixture(graphics: true);

        var compound = Publish(fixture);
        var document = Open(fixture);

        using LentEvaluator evaluators = new();
        using LayerStackPreview preview = new(fixture.Graphics!, evaluators.Lease);

        Assert.Empty(preview.Evaluate(document).Problems);

        // ⚠ Deleted, and with nothing told about it. A `Publish` on this evaluation cannot find the
        // file, so the fill's node type is gone and `LayerStackGraph` refuses the layer by name.
        File.Delete(compound);

        var kept = preview.Evaluate(document);

        Assert.True(
            kept.Problems.IsEmpty,
            "the compound folder was walked again on an evaluation nothing had changed: "
            + string.Join("; ", kept.Problems.Select(problem => problem.Message))
        );

        // The other half: a change that is announced does reach the library, so the assertion above
        // is a cache with invalidation rather than a stack that never looks at the disk again.
        Route(fixture, Relative);

        var republished = preview.Evaluate(document);

        Assert.Contains(
            republished.Problems,
            problem => problem.Message.Contains($"'{Published}'", StringComparison.Ordinal)
        );
    }

    /// <summary>⚠ And a change to something else rebuilds nothing.</summary>
    /// <remarks>
    ///     <b>The half that makes the first test mean anything</b> — <c>ExternalCompoundTests</c>'
    ///     own third case, one document type along. A stack that marked itself stale on every drained
    ///     change would pass everything above and republish the whole node library whenever anybody
    ///     touches a file, which is the walk this is about arriving through the other door.
    /// </remarks>
    [Fact]
    public void A_change_outside_the_compound_folder_rebuilds_nothing() {
        using var fixture = new TexturingFixture(graphics: true);

        Publish(fixture);

        var document = Open(fixture);

        // Drained once, so the claim below is about this change rather than about opening.
        Assert.False(document.Republish());

        Route(fixture, "Elsewhere" + TextureGraphDocument.Extension);

        Assert.False(document.Republish());
    }

    /// <summary>⚠ And a compound saved in the editor is heard, which no watcher is needed for.</summary>
    /// <remarks>
    ///     <c>EditorProject.DocumentSaving</c> is the in-editor half, and it is a separate path from
    ///     <c>OnProjectFileChanged</c> — a project on an unwatched share has only this one.
    /// </remarks>
    [Fact]
    public void A_compound_saved_in_the_editor_marks_the_library_stale() {
        using var fixture = new TexturingFixture(graphics: true);

        Publish(fixture);

        var document = Open(fixture);

        Assert.False(document.Republish());

        // Reopened rather than kept from `Publish`, because what is being asserted is that a save of
        // some *other* open document reaches this one.
        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.Project.Assets.TryGetByPath("Assets/" + Relative, out var entry) ? entry.Guid : default,
            fixture.Paths.Absolute("Assets/" + Relative)
        );

        compound.Save();

        Assert.True(document.Republish());
    }

    /// <summary>Hands one drained change to a real <see cref="ExternalEdits" />.</summary>
    /// <param name="fixture">The project.</param>
    /// <param name="relative">The path under <c>Assets/</c> that changed.</param>
    static void Route(TexturingFixture fixture, string relative) {
        using ExternalEdits edits = new(fixture.Project);

        edits.Apply([new FileChange(new VirtualPath("/" + relative), FileChangeKind.Changed, default)]);
    }

    /// <summary>Opens the stack through the module, with a graph fill naming the compound in it.</summary>
    /// <param name="fixture">The project, with the compound already on disk.</param>
    /// <returns>The document the module opened.</returns>
    /// <remarks>
    ///     ⚠ <b>The compound has to exist before this runs.</b> The document publishes its library in
    ///     its constructor, so a compound written afterwards is a different test — the one
    ///     <c>OnProjectFileChanged</c> exists for.
    /// </remarks>
    static LayerStackDocument Open(TexturingFixture fixture) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        document.Document = Filled();

        return document;
    }

    /// <summary>Writes a compound into the project's own <c>Assets/Compounds</c>.</summary>
    /// <param name="fixture">The project.</param>
    /// <returns>Where the file is, absolute.</returns>
    static string Publish(TexturingFixture fixture) {
        var folder = Path.Combine(fixture.Paths.Assets, TextureNodeLibrary.CompoundFolder);

        Directory.CreateDirectory(folder);

        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph(TextureNodeLibrary.CompoundFolder + "/" + Published),
            Path.Combine(folder, Published + TextureGraphDocument.Extension)
        );

        // ⚠ The starter goes first: a new document is a `Source/Uniform` into an `Output/Output`, and
        // inlining that second output into a containing graph is a TG0006 about two nodes writing one
        // usage rather than anything to do with this suite.
        foreach (var node in compound.Graph.Nodes.ToArray()) {
            compound.Graph.Remove(node.Id, out _);
        }

        compound.Graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var colour = compound.Graph.Add("Source/Uniform");
        var exit = compound.Graph.Add(SubGraphs.OutputType);

        colour.SetValue("Colour", 0.25f, 0.5f, 0.75f, 1f);
        compound.Graph.Connect(new(colour.Id, "Out"), new(exit.Id, "Out"));
        compound.Save();

        return compound.AssetPath;
    }

    /// <summary>A one-layer stack whose fill is the published compound.</summary>
    /// <returns>The stack.</returns>
    static LayerStackAsset Filled() =>
        new() {
            Name = "Hull",
            BaseWidth = 64,
            BaseHeight = 64,
            Seed = 5u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "fill",
                            Name = "Fill",
                            Kind = LayerKind.Fill,
                            Fill = LayerFillSource.Graph,
                            Graph = Published
                        }
                    ]
                }
            ]
        };
}
