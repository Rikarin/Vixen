// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO.Watch;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What an open stack does about a file it does not own moving.</summary>
/// <remarks>
///     <para>
///         <b>Two answers to one notification, and they are deliberately different shapes.</b> A
///         compound moving means the node library is stale, which is a republish of every node type —
///         so that one asks <em>which</em> file. The mesh picker moving means "re-ask", which is a
///         flag its own reader clears — so that one asks nothing at all.
///     </para>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/975">#975</a> is the second of
///         those getting the first one's shape by mistake.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/954">#954</a> set the picker's flag for a
///         path with a <em>model</em> extension, and <c>ProjectMeshSource.Declared</c> — where the
///         mesh names come from — reads the <c>.meta</c> sidecar, which does not have one. The write
///         that creates the names was the write the test excluded.
///     </para>
///     <para>
///         ⚠ <b>Driven through <c>ExternalEdits.Apply</c>, never by setting the flag.</b> The
///         notification is the mechanism: a test that assigned <c>ModelsChanged</c> would be green
///         against a document with no <c>OnProjectFileChanged</c> override at all, which is #954's
///         own warning one issue later.
///     </para>
/// </remarks>
public class LayerStackWatchTests {
    /// <summary>⚠ A <c>.meta</c> written beside a model is what tells the picker to re-ask.</summary>
    /// <remarks>
    ///     <b>Red before <a href="https://github.com/Rikarin/Vixen/issues/975">#975</a>'s fix</b>,
    ///     because <c>.meta</c> is not in <c>LayerStackMesh.Extensions</c> — which is the whole
    ///     defect: an import writes the model's bytes once and rewrites the sidecar every time it
    ///     declares a sub-asset, and the sidecar is the file the picker's contents come out of.
    /// </remarks>
    [Fact]
    public void A_meta_sidecar_tells_the_picker_to_re_ask() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, "Hull");

        // What a refill does, and the reason it is not `Assert.True` above: the flag starts raised so
        // that a picker is filled once before anything has happened.
        document.ModelsChanged = false;

        using var edits = new ExternalEdits(fixture.Project);

        edits.Apply([new FileChange(new("/Hull.obj.meta"), FileChangeKind.Changed)]);

        Assert.True(
            document.ModelsChanged,
            "an import rewrote the sidecar the mesh names are read from and the picker was not told."
        );
    }

    /// <summary>⚠ And that widening did not turn every notification into a library republish.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half that says the two flags are still different.</b> Making
    ///         <c>ModelsChanged</c> unconditional is cheap because its reader refills once and clears
    ///         it; making the <em>compound</em> flag unconditional would rebuild every node type
    ///         because somebody's text editor saved a file elsewhere in the project, which is the
    ///         trap <c>Republish</c>'s own remarks name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both directions, because "never republishes" passes the first assertion alone</b>
    ///         — and never republishing is the worse defect: it is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/922">#922</a>, in which a compound
    ///         edited outside the editor went on being inlined as it was when the document opened.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sidecar_does_not_republish_the_library_and_a_compound_does() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, "Hull");

        using var edits = new ExternalEdits(fixture.Project);

        edits.Apply([new FileChange(new("/Hull.obj.meta"), FileChangeKind.Changed)]);

        Assert.False(
            document.Republish(),
            "a sidecar outside the compound folder rebuilt the whole node library."
        );

        edits.Apply([
            new FileChange(
                new("/" + TextureNodeLibrary.CompoundFolder + "/Grunge" + TextureGraphDocument.Extension),
                FileChangeKind.Changed
            )
        ]);

        Assert.True(
            document.Republish(),
            "a compound changed outside the editor did not reach the library — #922, one document over."
        );
    }

    /// <summary>Opens a stack through the module, so the project is holding the document.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the verb rather than by constructing one.</b> <c>ExternalEdits</c> announces
    ///     to <c>EditorProject.Documents</c>, so a document the project is not holding hears nothing
    ///     and every assertion here would be about a document nothing notified.
    /// </remarks>
    static LayerStackDocument Open(TexturingFixture fixture, string name) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
    }
}
