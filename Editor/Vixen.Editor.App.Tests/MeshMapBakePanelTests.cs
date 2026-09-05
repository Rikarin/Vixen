// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 48 § D12's bake panel: the settings are somebody's, and the bake reads them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The claim under test is not "the panel draws".</b> It is that the fields on it are
///         the fields the bake uses — doc 20's A4 rule — which is invisible from the panel's own
///         source and from the verb's. A panel editing a copy of the settings looks correct in every
///         screenshot and produces a bake at whatever the constants were.
///     </para>
///     <para>
///         ⚠ <b>And that the button can be pressed at all.</b> Before the panel, § D12's maps were
///         baked at a hard-coded 1024 by a menu item; a panel whose Bake button nothing wired would
///         be the same state one surface further on, which is the defect this area keeps producing.
///     </para>
/// </remarks>
public sealed class MeshMapBakePanelTests {
    /// <summary>The verb opens the panel rather than baking with constants nobody chose.</summary>
    [Fact]
    public void The_bake_verb_opens_the_panel() {
        using var session = EditorSession.Start();

        Assert.True(session.Shell.Commands.Execute("assets.bake-mesh-maps"));

        session.Settle();

        Assert.Contains(session.Panels, panel => string.Equals(panel.Id, "mesh-map-bake", StringComparison.Ordinal));
    }

    /// <summary>What the panel writes is what the bake reads, and not a copy of it.</summary>
    [Fact]
    public void The_panel_and_the_bake_share_one_settings_object() {
        using var session = EditorSession.Start();
        var view = session.Control<MeshMapBakeView>("mesh-map-bake");
        var settings = session.Editor.MeshMapBakeOptions;

        Assert.Equal(1024, settings.Resolution);

        view.ResolutionPicker.Value = "2048";
        view.SamplesPicker.Value = "256";
        view.GutterBox.Value = "9";
        view.RadiusBox.Value = "0.2";
        view.Identifiers.IsChecked = false;

        Assert.Equal(2048, settings.Resolution);
        Assert.Equal(256, settings.OcclusionSamples);
        Assert.Equal(9, settings.Gutter);
        Assert.Equal(0.2f, settings.SearchRadius, 0.001f);
        Assert.False(settings.Wants(MeshMapUsage.Id));

        // And they reach the bake's own settings rather than stopping at this object.
        var bake = settings.ToBake();

        Assert.Equal(2048, bake.Resolution);
        Assert.Equal(256, bake.OcclusionSamples);
        Assert.Equal(9, bake.Gutter);
        Assert.Equal(0.2f, bake.SearchRadius, 0.001f);
        Assert.False(bake.Maps.HasFlag(Vixen.Geometry.Remeshing.MeshMaps.Id));
    }

    /// <summary>Opening the panel shows the settings rather than rewriting them.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this stops is a panel that resets what it displays.</b> Filling a control
    ///     raises its own change event, which the panel reads as "the user chose this" — so a reopen
    ///     that did not guard would write the control's default back over a resolution somebody set.
    /// </remarks>
    [Fact]
    public void Reopening_the_panel_does_not_rewrite_the_settings() {
        using var session = EditorSession.Start();
        var view = session.Control<MeshMapBakeView>("mesh-map-bake");

        view.ResolutionPicker.Value = "4096";
        view.Curvature.IsChecked = false;

        session.Close("mesh-map-bake");
        session.Control<MeshMapBakeView>("mesh-map-bake");

        var settings = session.Editor.MeshMapBakeOptions;

        Assert.Equal(4096, settings.Resolution);
        Assert.False(settings.Wants(MeshMapUsage.Curvature));
    }

    /// <summary>With nothing selected the button is greyed and the panel says what to do about it.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves.</b> A greyed button with no sentence beside it is doc 20's complaint —
    ///     it reads as an editor that cannot bake — and a sentence beside a button that works anyway
    ///     is worse.
    /// </remarks>
    [Fact]
    public void A_bake_with_nothing_selected_is_refused_out_loud() {
        using var session = EditorSession.Start();
        var view = session.Control<MeshMapBakeView>("mesh-map-bake");

        view.Refresh();

        Assert.True(view.BakeButton.Disabled);
        Assert.Contains("model", view.Note.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(view.Subject.Text ?? string.Empty);
    }

    /// <summary>The panel counts the maps a bake would write, the two that are not optional included.</summary>
    /// <remarks>
    ///     ⚠ <b>Nine and not seven.</b> The normal and the displacement are not in the bake's flags
    ///     at all — they fall out of the one ray it already casts — so a panel that counted the
    ///     checkboxes would tell an artist to expect seven files and produce nine.
    /// </remarks>
    [Fact]
    public void The_panel_counts_the_maps_a_bake_would_write() {
        var settings = new MeshMapBakeSettings();

        Assert.Equal(MeshMapNaming.Every.Count, settings.Count);

        settings.Want(MeshMapUsage.Thickness, on: false);

        Assert.Equal(MeshMapNaming.Every.Count - 1, settings.Count);

        // ⚠ And the two that are not optional cannot be turned off through this door.
        settings.Want(MeshMapUsage.Normal, on: false);
        settings.Want(MeshMapUsage.Displacement, on: false);

        Assert.Equal(MeshMapNaming.Every.Count - 1, settings.Count);
    }

    /// <summary>A finished bake's files and warnings are what the panel shows afterwards.</summary>
    /// <remarks>
    ///     ⚠ <b>The warnings are the point.</b> <c>BakedMaps.Warnings</c> has always carried what a
    ///     bake could not do and the only place it reached was a toast that is gone in four seconds
    ///     — which is why "the curvature map looks wrong" was unanswerable without re-running it.
    /// </remarks>
    [Fact]
    public void The_panel_shows_what_the_last_bake_produced() {
        using var session = EditorSession.Start();
        var view = session.Control<MeshMapBakeView>("mesh-map-bake");

        view.ShowResult(
            new MeshMapSet(
                "Cube",
                new Dictionary<MeshMapUsage, Vixen.Core.AssetReference>(),
                ["/Assets/MeshMaps/Cube_ao.png", "/Assets/MeshMaps/Cube_curvature.png"],
                ["Nine texels were reached by no ray."]
            )
        );

        Assert.Equal(2, view.Results.Items.Count);
        Assert.Contains("no ray", view.Status.Text ?? string.Empty, StringComparison.Ordinal);

        // ⚠ Cleared rather than left, so that a failed bake does not leave the previous one's rows
        // on screen under this one's name.
        view.ShowResult(null);

        Assert.Empty(view.Results.Items);
    }

    /// <summary>The Bake button reaches the bake, which is the assertion this area keeps needing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>"Grep for callers" turned into a press.</b> Everything above proves the panel
    ///         holds the right numbers; none of it proves the button is wired to anything.
    ///         <c>MapBaker.Bake</c> sat with no caller in the whole repository until batch 2, and a
    ///         panel whose <c>BakeRequested</c> nothing subscribed to would be the same defect one
    ///         surface further out and would look identical in every screenshot.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A model that cannot be read, deliberately, because that path is synchronous.</b>
    ///         A bake that starts runs on the pool and finishes whenever it finishes; a model whose
    ///         bytes are not a model fails inside <c>ModelReader.Read</c> on the frame thread and
    ///         says so — so what this waits for is a message rather than a thread, and the thing it
    ///         proves is the same: the press arrived.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_bake_button_reaches_the_bake() {
        using var session = EditorSession.Start();
        var file = Path.Combine(session.Project.Paths.Assets, "Broken.fbx");

        File.WriteAllText(file, "this is not a model");
        session.Project.Assets.Scan();

        var view = session.Control<MeshMapBakeView>("mesh-map-bake");
        var entry = session.Project.Assets.Entries.First(
            candidate => candidate.Path.EndsWith("Broken.fbx", StringComparison.Ordinal)
        );

        session.Project.Selection.Set([entry.Guid]);
        session.Settle();

        Assert.False(view.BakeButton.Disabled, "a selected model left Bake greyed.");

        session.Click(view.BakeButton);
        session.Settle();

        Assert.Contains(
            session.Shell.Notifications.History,
            message => message.Message.Contains("bake", StringComparison.OrdinalIgnoreCase)
        );
    }
}
