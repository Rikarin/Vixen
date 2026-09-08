// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A <c>.vxsmartmat</c> reached by the two gestures a person can make.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Driven through the verbs, never through <see cref="SmartMaterial" />.</b> The obvious
///         version of this file calls <c>Extract</c> and <c>Prepare</c> directly and is green against
///         a module that registers nothing — which is precisely this workstream's characteristic
///         defect, and the one <a href="https://github.com/Rikarin/Vixen/issues/575">#575</a> would
///         acquire at milestone scale if the file format landed with no route to it.
///         <c>Commands.Execute</c> answers <see langword="false" /> for a verb nothing registered.
///     </para>
///     <para>
///         ⚠ <b>Device-free, and legitimately so.</b> Neither verb evaluates anything: one writes a
///         file and the other pushes an undo entry. The fixture still publishes an
///         <c>IEditorGraphics</c> with no device, because that is the state the editor is in between
///         construction and its window coming up and it must not be the reason these refuse.
///     </para>
/// </remarks>
public class SmartMaterialWiringTests {
    /// <summary>A stack whose one set has a layer worth carrying and one that cannot travel.</summary>
    const string Stack = """
        version: 1
        name: Hull
        model: Assets/Hull.fbx
        baseWidth: 64
        baseHeight: 64
        sets:
          - name: Body
            channels:
              - usage: baseColor
                default: [0, 0, 0, 1]
            layers:
              - id: base
                name: Base
                kind: Fill
                values:
                  baseColor: [0.25, 0.5, 0.75, 1]
              - id: strokes
                name: Hand Painted
                kind: Paint
                paint: Hull.Body.strokes.vxpaint
        """;

    /// <summary>The stack on disk becomes a file on the shelf, through the verb.</summary>
    [Fact]
    public void The_save_verb_puts_a_vxsmartmat_on_the_shelf() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddStack("Hull", Stack));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.SaveSmartCommand),
            $"nothing answered '{TexturingModule.SaveSmartCommand}'. Doc 48 § M10's file format with "
            + "no gesture that makes one is #575 exactly."
        );

        var file = Path.Combine(
            fixture.Paths.Assets,
            SmartMaterial.ShelfFolder,
            "Hull" + SmartMaterial.Extension
        );

        Assert.True(File.Exists(file), Say(fixture) + $" There is no smart material at '{file}'.");

        // ⚠ Read back through the stack's own reader, because a file the shelf holds and nothing can
        // read is the same as no file. The mesh binding is gone and the paint layer stayed behind.
        var material = LayerStackYaml.Read(File.ReadAllText(file));

        Assert.Equal("", material.Model);
        Assert.Equal(["base"], Assert.Single(material.Sets).Layers.Select(layer => layer.Id));

        // ⚠ And the drop is disclosed in the notification the artist actually sees, which is the
        // difference between this design and "silently drops the artist's strokes".
        Assert.Contains("Hand Painted", Say(fixture), StringComparison.Ordinal);

        // The shelf is under `Assets/`, so what lands on it has to be an asset — otherwise the apply
        // verb, which reads the Project panel's selection, could never see it.
        Assert.True(
            fixture.Project.Assets.TryGetByPath(
                fixture.Paths.Relative(file),
                out _
            ),
            "the save did not scan the shelf entry in, so it cannot be selected and cannot be applied."
        );
    }

    /// <summary>The selected smart material goes onto the open stack as one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>The undo is half of the assertion, not a nicety.</b> A verb that mutated
    ///     <c>TextureSetAsset.Layers</c> directly would leave ten layers an artist could only remove
    ///     by hand, and would leave <c>CommandStack.Depth</c> unmoved — which is the edge
    ///     <c>LayerStackView</c> follows to notice the document changed
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>), so the rows would not
    ///     even redraw.
    /// </remarks>
    [Fact]
    public void The_apply_verb_is_one_undo_entry() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddStack("Hull", Stack));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.SaveSmartCommand));

        // A second stack, and the point of the whole feature: the material was authored on Hull and
        // is applied to something that never saw that model.
        fixture.Project.Selection.Set(fixture.AddStack("Crate"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var target = Assert.Single(
            fixture.Project.Documents.OfType<LayerStackDocument>(),
            document => document.AssetPath.EndsWith("Crate" + LayerStackDocument.Extension, StringComparison.Ordinal)
        );

        var before = target.Document.Sets[0].Layers.Count;
        var depth = target.Stack.Depth.Value;

        Assert.True(
            fixture.Project.Assets.TryGetByPath(
                "Assets/" + SmartMaterial.ShelfFolder + "/Hull" + SmartMaterial.Extension,
                out var shelved
            ),
            "the shelf entry is not an asset, so nothing can select it: " + Say(fixture)
        );

        fixture.Project.Selection.Set(shelved.Guid);

        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.ApplySmartCommand),
            $"nothing answered '{TexturingModule.ApplySmartCommand}': " + Say(fixture)
        );

        Assert.Equal(before + 1, target.Document.Sets[0].Layers.Count);
        Assert.Contains(target.Document.Sets[0].Layers, layer => layer.Id == "base-2");

        // ⚠ One entry for the whole material, which is what a person means by "undo that".
        Assert.Equal(depth + 1, target.Stack.Depth.Value);
        Assert.True(target.Stack.Undo());
        Assert.Equal(before, target.Document.Sets[0].Layers.Count);
        Assert.DoesNotContain(target.Document.Sets[0].Layers, layer => layer.Id == "base-2");

        // And a redo puts back the same ids rather than minting new ones, which is why the layers are
        // settled before the command is built rather than inside its `Do`.
        Assert.True(target.Stack.Redo());
        Assert.Contains(target.Document.Sets[0].Layers, layer => layer.Id == "base-2");
    }

    /// <summary>With nothing selected, the refusal says what the shelf holds.</summary>
    /// <remarks>
    ///     ⚠ <b>What <see cref="SmartMaterial.Shelf" /> is for, and its only caller.</b> "No smart
    ///     material is selected" leaves an artist unable to tell an empty shelf from a wrong
    ///     selection, and those two have different next steps.
    /// </remarks>
    [Fact]
    public void The_apply_verbs_refusal_names_what_the_shelf_holds() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddStack("Hull", Stack));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.SaveSmartCommand));

        // The stack is selected rather than the smart material, which is the ordinary way to arrive
        // here: an artist runs the verb straight after opening the thing they want to apply it to.
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.ApplySmartCommand));

        // ⚠ `[0]` and not `[^1]`: `NotificationCenter.History` is newest first, so the last index is
        // the *oldest* notification — which here is the save, and which also happens to contain the
        // word this asserts on. An assertion green on the wrong entry is the shape this file exists
        // to avoid one level up.
        var said = fixture.Shell.Notifications.History[0];

        Assert.Contains("Hull", said.Detail, StringComparison.Ordinal);

        // ⚠ And nothing was applied, which the sentence alone does not say. A refusal that also put
        // the layers on would be the worst of both.
        var stack = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        Assert.Equal(2, stack.Document.Sets[0].Layers.Count);
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
