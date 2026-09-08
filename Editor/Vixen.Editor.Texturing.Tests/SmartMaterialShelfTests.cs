// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A shelf entry opens, and the verbs it is now exposed to each have an answer.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1070">#1070</a>.</b> A
///         <c>.vxsmartmat</c> is a <c>.vxlayers</c> byte for byte and
///         <c>LayerStackEditorFactory</c> claimed one of the two, so an artist who wanted to rename a
///         layer inside <c>Rusted Iron</c> — or fix a mask in it — had to apply it to a scratch
///         stack, edit there and save over the shelf entry.
///     </para>
///     <para>
///         ⚠ <b>Opening it is one line and this file is about the rest.</b> The document becomes
///         <c>TexturingModule.stack</c>, and every verb reading that field then applies to it: a
///         shelf entry has no model, so <c>Bake Material from Layers</c> would hand the artist a wall
///         of mesh-map refusals for a gesture that should not have been offered, and
///         <c>Save as Smart Material</c> would write it back onto the shelf under its own name.
///     </para>
///     <para>
///         ⚠ <b>Each refusal is asserted with its <em>effect</em> and not only its sentence.</b> A
///         verb that said the right thing and then did the wrong thing anyway is the failure this
///         shape of guard produces, and a message assertion alone cannot see it.
///     </para>
/// </remarks>
public class SmartMaterialShelfTests {
    /// <summary>A shelf entry: a stack fragment, with no model and nothing painted.</summary>
    const string Shelf = """
        version: 1
        name: Rusted Iron
        baseWidth: 64
        baseHeight: 64
        sets:
          - name: Body
            channels:
              - usage: baseColor
                default: [0, 0, 0, 1]
            layers:
              - id: rust
                name: Rust
                kind: Fill
                values:
                  baseColor: [0.35, 0.15, 0.05, 1]
        """;

    /// <summary>⚠ The registry opens a shelf entry as a layer stack, which is what the file is.</summary>
    /// <remarks>
    ///     <b>Through <c>AssetEditorRegistry.TryOpen</c> — the double-click's own route — rather than
    ///     by constructing the document.</b> The claim is that the factory answers for the extension;
    ///     a test that built a <c>LayerStackDocument</c> over a <c>.vxsmartmat</c> would have passed
    ///     before this change, because the document never cared what the file was called.
    /// </remarks>
    [Fact]
    public void The_registry_opens_a_shelf_entry_as_a_layer_stack() {
        using var fixture = new TexturingFixture(editors: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var asset = Entry(fixture);

        Assert.True(fixture.Editors.TryOpen(fixture.Project, asset, out var opened));

        var document = Assert.IsType<LayerStackDocument>(opened);

        Assert.Empty(document.LoadDiagnostics);
        Assert.True(document.IsSmartMaterial);
        Assert.Equal(["rust"], Assert.Single(document.Document.Sets).Layers.Select(layer => layer.Id));
    }

    /// <summary>⚠ And the verb opens it too, or the shelf would be reachable by mouse and not by command.</summary>
    [Fact]
    public void The_open_verb_takes_a_shelf_entry() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(Entry(fixture));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        Assert.True(document.IsSmartMaterial);
    }

    /// <summary>⚠ Baking a shelf entry is refused by name, not answered with a wall of mesh-map refusals.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A smart material has no model, so every mesh-map mask in it has nothing to
    ///         measure</b> — and it would still write materials, named after a shelf entry nothing in
    ///         a scene refers to. The refusal names the file and says what to bake instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Device-free deliberately, and the assertion is the sentence rather than the
    ///         absence of files.</b> This fixture publishes an <c>IEditorGraphics</c> with a null
    ///         device, so "nothing was written" is equally true of a stack the route refused for
    ///         having nothing to run on — which is a refusal that arrives <em>later</em> in the same
    ///         method. The sentence is what says which of the two ran.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Baking_a_shelf_entry_is_refused_and_writes_nothing() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(Entry(fixture));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeStackCommand));

        var said = Say(fixture);

        Assert.Contains("smart material", said, StringComparison.Ordinal);
        Assert.Contains("Rusted Iron" + SmartMaterial.Extension, said, StringComparison.Ordinal);
        Assert.DoesNotContain("no graphics device", said, StringComparison.Ordinal);

        Assert.False(
            Directory.Exists(Path.Combine(fixture.Paths.Assets, "Materials")),
            "the bake wrote a materials folder for a stack fragment that belongs to no asset."
        );
    }

    /// <summary>⚠ And saving one onto the shelf again is refused, rather than being harmlessly confusing.</summary>
    /// <remarks>
    ///     <b>The file it would replace is the file the artist is editing.</b> Extracting a fragment
    ///     from a fragment writes it back under its own name and reports it as a save of something
    ///     new; what the artist wants is the ordinary document save, and the refusal says so.
    /// </remarks>
    [Fact]
    public void Saving_a_shelf_entry_onto_the_shelf_is_refused() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(Entry(fixture));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var before = File.ReadAllText(Path.Combine(fixture.Paths.Assets, SmartMaterial.ShelfFolder, File_));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.SaveSmartCommand));

        Assert.Contains("already a smart material", Say(fixture), StringComparison.Ordinal);

        // ⚠ And it did not rewrite the file. A refusal that also saved would be the worst of both:
        // the artist reads a warning and their entry has been replaced anyway.
        Assert.Equal(
            before,
            File.ReadAllText(Path.Combine(fixture.Paths.Assets, SmartMaterial.ShelfFolder, File_))
        );
    }

    /// <summary>⚠ Applying one shelf entry into another is allowed, which is the third verb's answer.</summary>
    /// <remarks>
    ///     <b>Its two neighbours refuse a shelf entry and this one deliberately does not.</b> Rust
    ///     over panel wear is a real thing to author, and <c>SmartMaterial.Prepare</c> already drops
    ///     everything a fragment must not carry — so the result is a fragment either way. The
    ///     difference between the three verbs is only which of them should have been offered.
    /// </remarks>
    [Fact]
    public void Applying_a_shelf_entry_into_a_shelf_entry_is_allowed() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var entry = Entry(fixture);

        fixture.Project.Selection.Set(entry);
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());
        var depth = document.Stack.Depth.Value;

        // The same entry applied onto itself, which is the smallest script in which "a shelf entry is
        // a legal target" is a claim rather than an arrangement of two files.
        fixture.Project.Selection.Set(entry);
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.ApplySmartCommand));

        Assert.Contains("Applied", Say(fixture), StringComparison.Ordinal);
        Assert.Equal(depth + 1, document.Stack.Depth.Value);
        Assert.Equal(2, document.Document.Sets[0].Layers.Count);
    }

    /// <summary>What the shelf entry is called on disk.</summary>
    const string File_ = "Rusted Iron" + SmartMaterial.Extension;

    /// <summary>Writes the fixture entry onto the project's shelf and scans it in.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     ⚠ <b>Under <c>Assets/SmartMaterials/</c> rather than anywhere</b>, because that is where
    ///     the apply verb's own refusal tells an artist to look and where the save verb puts one — a
    ///     fixture that scattered them would be testing a shelf nothing else agrees about.
    /// </remarks>
    static AssetId Entry(TexturingFixture fixture) {
        Directory.CreateDirectory(Path.Combine(fixture.Paths.Assets, SmartMaterial.ShelfFolder));

        return fixture.AddAsset(
            SmartMaterial.ShelfFolder + "/Rusted Iron",
            SmartMaterial.Extension,
            Shelf
        );
    }

    /// <summary>What the module last said, for a message that would otherwise only say "no file".</summary>
    /// <param name="fixture">The host, whose notifications the verbs write into.</param>
    /// <returns>The sentences, or a note that there were none.</returns>
    static string Say(TexturingFixture fixture) =>
        fixture.Shell.Notifications.History.Count > 0
            ? string.Join(
                " · ",
                fixture.Shell.Notifications.History.Select(one => one.Message + ": " + one.Detail)
            )
            : "The module said nothing at all, which is itself the finding.";
}
