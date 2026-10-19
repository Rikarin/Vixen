// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 50 § D2 one level down (#1306): the texturing verbs are a service a plugin can require.</summary>
/// <remarks>
///     ⚠ <b>The verbs are reached through <see cref="ITexturing" /> and never through a command id.</b>
///     The command tests elsewhere in this project are the proof the <i>wiring</i> holds; these are the
///     proof the <i>verb</i> does, by the door a second plugin would use.
/// </remarks>
public class TexturingServiceTests {
    /// <summary>A stack with a layer that travels onto the shelf.</summary>
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
        """;

    [Fact]
    public void The_module_publishes_itself_as_the_service_and_takes_it_back_on_unload() {
        using var fixture = new TexturingFixture();
        var module = new TexturingModule();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);

        Assert.Same(module, fixture.Services.Require<ITexturing>());

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));

        Assert.False(fixture.Services.Contains<ITexturing>());

        // ⚠ And a second activation can publish again, which is the case `Services.Add` fails: a
        // service left behind makes the next publish of the type throw, so the module could be
        // loaded exactly once (doc 50 § F7's TerrainPhysics finding).
        var again = fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Equal(PluginState.Active, again.State);
        Assert.NotSame(module, fixture.Services.Require<ITexturing>());
    }

    /// <summary>Save onto the shelf and apply it to a second stack, with no command run.</summary>
    [Fact]
    public void The_service_saves_and_applies_a_smart_material_without_a_command() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var texturing = fixture.Services.Require<ITexturing>();

        fixture.Project.Selection.Set(fixture.AddStack("Hull", Stack));
        texturing.OpenStack();
        texturing.SaveSmartMaterial();

        fixture.Project.Selection.Set(fixture.AddStack("Crate"));
        texturing.OpenStack();

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
            "the service's save put nothing on the shelf."
        );

        fixture.Project.Selection.Set(shelved.Guid);
        texturing.ApplySmartMaterial();

        Assert.Equal(before + 1, target.Document.Sets[0].Layers.Count);
        Assert.Equal(depth + 1, target.Stack.Depth.Value);
    }

    /// <summary>With nothing open a verb answers in a notification rather than throwing.</summary>
    [Fact]
    public void A_verb_with_nothing_open_says_so_rather_than_throwing() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var texturing = fixture.Services.Require<ITexturing>();

        texturing.BakeStackMaterial();
        texturing.BakeMaterial();

        Assert.Contains(fixture.Shell.Notifications.History, entry => entry.Message == "No layer stack is open");
        Assert.Contains(fixture.Shell.Notifications.History, entry => entry.Message == "No graph is open");
    }
}
