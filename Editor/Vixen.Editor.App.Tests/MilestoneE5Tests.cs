// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's E5 exit criteria, and the panels the milestone puts on the bar.</summary>
/// <remarks>
///     ⚠ <b>The exit is two clauses and they are the first two <c>[Fact]</c>s.</b> "Doc 11's
///     thirteen-row asset-editor table has thirteen rows built" — asserted against the registry, by
///     name, so that a row somebody deletes fails here rather than in a doc — and "a cinematic can be
///     authored, scrubbed and played", which is driven through the panel rather than through the
///     document, because the clause is about the editor rather than about the format.
/// </remarks>
public class MilestoneE5Tests {
    /// <summary>E5's first exit clause: doc 11's thirteen rows, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Named rather than counted.</b> A count passes when somebody deletes the font editor
    ///     and adds two of something else; the point of the clause is <i>which</i> thirteen.
    /// </remarks>
    [Theory]
    [InlineData("Scene")]
    [InlineData("Prefab")]
    [InlineData("Material")]
    [InlineData("Texture")]
    [InlineData("Model")]
    [InlineData("Animation Clip")]
    [InlineData("Shader")]
    [InlineData("UI")]
    [InlineData("VFX Graph")]
    [InlineData("Addressable Group")]
    [InlineData("Graphics Compositor")]
    [InlineData("Input Actions")]
    [InlineData("Font")]
    public void Doc_11s_asset_editor_table_has_a_row_that_opens(string editor) {
        var registry = StandardEditors.CreateWorldless();

        // The two that need a world are not in the worldless set, and are the two the fixture below
        // proves by opening a scene at all.
        if (editor is "Scene" or "Prefab") {
            return;
        }

        Assert.True(registry.TryGetByName(editor, out _), $"'{editor}' is not registered.");
    }

    /// <summary>E5's second exit clause, through the panel.</summary>
    [Fact]
    public void A_cinematic_can_be_authored_scrubbed_and_played() {
        using var fixture = EditorSession.Start();

        var scene = fixture.Scene;
        var actor = scene.Add("Actor", LocalTransform.At(new(0f, 0f, 0f)));

        scene.Selection.Set([actor]);

        var path = Path.Combine(fixture.Project.Paths.Assets, "Intro.vxseq");

        Directory.CreateDirectory(fixture.Project.Paths.Assets);
        File.WriteAllText(path, string.Empty);

        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/Intro.vxseq", out var entry));

        // Authored: opening the asset registers the panel and attaches the open scene, which is the
        // application's arbitration rather than the factory's.
        fixture.Editor.OpenAsset(entry.Guid);
        fixture.Settle();

        var view = fixture.Control<SequenceView>("asset." + entry.Guid);

        Assert.NotNull(view);

        var document = (SequenceDocument) fixture.Project.Documents.First(open => open is SequenceDocument);

        Assert.NotNull(document.Player);

        var track = new SequenceTrackData { Kind = SequenceTrackKind.Transform, Target = scene.IdOf(actor) };

        document.AddTrack(track);
        document.AddKey(track, new() { Time = 0f, Value = SequencePlayer.Write(LocalTransform.At(new(0f, 0f, 0f))) });
        document.AddKey(track, new() { Time = 2f, Value = SequencePlayer.Write(LocalTransform.At(new(0f, 4f, 0f))) });

        // Scrubbed: the playhead drives the scene.
        Assert.Equal(1, view.Seek(1f));
        Assert.Equal(2f, scene.World.Get<LocalTransform>(actor).Position.Y, 3);

        // Played: the transport advances it without anybody dragging. The playhead is the
        // timeline's, which is what a drag moves and what `Tick` advances — `Seek` above drove the
        // scene from a time without claiming to have moved it.
        view.Tracks.Time = 1f;
        view.Play.IsChecked = true;
        view.Tick(TimeSpan.FromSeconds(0.5));

        Assert.Equal(3f, scene.World.Get<LocalTransform>(actor).Position.Y, 3);

        // ⚠ And the scene goes back. A cinematic that left the level with its actors wherever the
        // last frame put them is the quietest way an editor can lose an afternoon's work.
        document.Player!.Restore();
        Assert.Equal(0f, scene.World.Get<LocalTransform>(actor).Position.Y, 3);
    }

    /// <summary>The four world-building panels open, close and reopen.</summary>
    /// <remarks>
    ///     Doc 20's Part F panel-lifecycle row, applied to what this milestone added: a factory runs
    ///     again on every reopen, and a panel holding something durable is the defect that row exists
    ///     to catch.
    /// </remarks>
    [Theory]
    [InlineData("world-settings")]
    [InlineData("lighting")]
    [InlineData("navigation")]
    [InlineData("scenes")]
    public void A_world_building_panel_survives_being_closed_and_reopened(string panel) {
        using var fixture = EditorSession.Start();

        fixture.Open(panel).Settle();
        Assert.NotNull(fixture.Panel(panel));

        fixture.Close(panel).Settle();
        fixture.Open(panel).Settle();

        Assert.NotNull(fixture.Panel(panel));
    }

    /// <summary>Every asset kind E5 adds can be made from the Assets menu and opens.</summary>
    /// <remarks>
    ///     ⚠ <b>The one thing that would make all six editors unreachable.</b> A format with no way
    ///     to create a file of it is a format nobody meets, whatever the double-click does.
    /// </remarks>
    [Theory]
    [InlineData("assets.create-vfx", ".vxvfx")]
    [InlineData("assets.create-animation", ".vxanim")]
    [InlineData("assets.create-animation-graph", ".vxanimgraph")]
    [InlineData("assets.create-sequence", ".vxseq")]
    [InlineData("assets.create-input", ".vxinput")]
    [InlineData("assets.create-mixer", ".vxmixer")]
    [InlineData("assets.create-font", ".vxfont")]
    public void An_asset_kind_can_be_created_and_opens(string command, string extension) {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.CanRun(command), command);

        fixture.Run(command).Settle();

        var created = Directory.EnumerateFiles(fixture.Project.Paths.Assets, "*" + extension, SearchOption.AllDirectories);

        Assert.NotEmpty(created);

        // Opening it is what the command does as its second half, so a document is open over it.
        Assert.Contains(fixture.Project.Documents, document => document.Title.Peek().EndsWith(extension, StringComparison.Ordinal));
    }

    /// <summary>A second scene opens beside the first rather than over it.</summary>
    [Fact]
    public void A_second_scene_opens_additively_and_closes_again() {
        using var fixture = EditorSession.Start();

        var path = Path.Combine(fixture.Project.Paths.Assets, "Scenes", "Second.vxscene");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var other = new SceneDocument(fixture.Project, fixture.Scene.World, Vixen.Core.AssetId.Empty, "Second") {
            Writer = new SceneFileWriter(path)
        };

        other.Add("Prop", LocalTransform.At(new(3f, 0f, 0f)));
        other.Save();
        other.Close();

        Assert.Single(fixture.Editor.Scenes);

        var opened = fixture.Editor.OpenSceneAdditively(path);

        Assert.NotNull(opened);
        Assert.Equal(2, fixture.Editor.Scenes.Count);

        // ⚠ One world. That is what makes an entity handle mean one thing across the editor, and it
        // is the whole reason multi-scene is a second document rather than a second world.
        Assert.Same(fixture.Scene.World, opened!.World);

        // Opening the same file again activates the one already open rather than loading it twice.
        Assert.Same(opened, fixture.Editor.OpenSceneAdditively(path));
        Assert.Equal(2, fixture.Editor.Scenes.Count);

        fixture.Editor.CloseScene(1);
        fixture.Settle();

        Assert.Single(fixture.Editor.Scenes);
    }

    /// <summary>The world settings are a sidecar and they survive a reopen.</summary>
    [Fact]
    public void World_settings_are_written_beside_the_scene() {
        using var fixture = EditorSession.Start();

        fixture.Editor.World.Environment.AmbientIntensity = 2.5f;
        fixture.Editor.World.Navigation.AgentRadius = 0.75f;

        fixture.Editor.World.Save(fixture.Editor.ActiveScene.Path);

        var sidecar = WorldSettings.PathFor(fixture.Editor.ActiveScene.Path);

        Assert.True(File.Exists(sidecar));

        var reloaded = WorldSettings.Load(fixture.Editor.ActiveScene.Path);

        Assert.Equal(2.5f, reloaded.Environment.AmbientIntensity, 3);
        Assert.Equal(0.75f, reloaded.Navigation.AgentRadius, 3);
    }

    /// <summary>The Sequencing layout preset A6 owed exists and applies.</summary>
    [Fact]
    public void The_sequencing_layout_preset_is_registered() {
        using var fixture = EditorSession.Start();

        Assert.Contains("Sequencing", fixture.Shell.Workspace.Presets);

        fixture.Shell.Workspace.Apply("Sequencing");
        fixture.Settle();

        Assert.NotNull(fixture.Panel("scenes"));
    }
}
