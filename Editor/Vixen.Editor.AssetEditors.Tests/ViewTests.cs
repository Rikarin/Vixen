// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A document laid out with all five sheets loaded, which is what a host gives these views.</summary>
/// <remarks>
///     ⚠ <b>All five, in order.</b> Every rule the asset editors add is written against tokens the
///     four below declare, and each <c>flex-direction: column</c> only reads as redundant until one is
///     missing — an element nothing styles lays its children out in a row, so a settings panel with no
///     sheet is every section beside the one before it.
/// </remarks>
public sealed class ViewHarness : IDisposable {
    /// <summary>The interface document.</summary>
    public UiTest Ui { get; }

    /// <summary>The project on disk.</summary>
    public EditorFixture Project { get; } = new();

    /// <summary>Builds a harness.</summary>
    public ViewHarness() {
        Ui = UiTest.Create(1200f, 800f);

        ControlTheme.Install(Ui.Document);
        AdvancedTheme.Install(Ui.Document);
        InspectorTheme.Install(Ui.Document);
        AssetEditorTheme.Install(Ui.Document);
    }

    /// <inheritdoc />
    public void Dispose() {
        Ui.Dispose();
        Project.Dispose();
    }
}

/// <summary>What the import-settings views build, once they have a document.</summary>
public class ImportViewTests {
    /// <summary>The matrix has a row per setting and a column per target, plus the base.</summary>
    [Fact]
    public void TheMatrixIsSettingsByTargets() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), path);
        document.AddTarget("Android");
        document.AddTarget("iOS");

        var matrix = harness.Ui.Document.Root.Add<TargetOverrideMatrix>();
        matrix.Show(document);
        harness.Ui.Frame();

        // Seven settings on a texture, three columns each: base, Android, iOS. Two of the seven are
        // the sprite ones — the sprite *rects* are not among them, because they are not a knob and
        // are drawn by the sprite editor instead.
        Assert.Equal(21, matrix.Cells.Count);
        Assert.All(matrix.Cells, cell => Assert.NotNull(cell.Field));
    }

    /// <summary>Only the target columns carry a tick; the base column decides by being the base.</summary>
    [Fact]
    public void OnlyTargetColumnsHaveATick() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), path);
        document.AddTarget("Android");

        var matrix = harness.Ui.Document.Root.Add<TargetOverrideMatrix>();
        matrix.Show(document);
        harness.Ui.Frame();

        Assert.Equal(7, matrix.Cells.Count(cell => cell.Toggle is null));
        Assert.Equal(7, matrix.Cells.Count(cell => cell.Toggle is not null));
    }

    /// <summary>Adding a target rebuilds the grid, because which cells exist has changed.</summary>
    [Fact]
    public void AddingATargetRebuildsTheGrid() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), path);

        var matrix = harness.Ui.Document.Root.Add<TargetOverrideMatrix>();
        matrix.Show(document);
        harness.Ui.Frame();

        Assert.Equal(7, matrix.Cells.Count);

        document.AddTarget("Android");
        harness.Ui.Frame();

        Assert.Equal(14, matrix.Cells.Count);
    }

    /// <summary>The texture editor's ladder shows a row per level and restates when a setting moves.</summary>
    [Fact]
    public void TheLadderFollowsTheSettings() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<TextureImportView>();

        view.Show(document);
        harness.Ui.Frame();

        // Nothing decoded the placeholder bytes, so the source is 0×0 and the chain is one level.
        Assert.Single(view.Levels);
        Assert.False(view.Undecodable.HasClass("hidden"));

        document.Texture.GenerateMips = false;
        view.Refresh();

        Assert.Single(view.Levels);
    }

    /// <summary>Turning a channel off is a state the host can read, and the buttons follow it.</summary>
    [Fact]
    public void ChannelsAreAState() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.png", "bytes");

        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<TextureImportView>();

        view.Show(document);

        var raised = 0;
        view.ViewChanged += _ => raised++;

        view.SetChannel(TextureChannels.Alpha, shown: false);

        Assert.Equal(TextureChannels.Colour, view.Channels);
        Assert.Equal(1, raised);

        // Setting it again is not a change, so nothing is raised and nothing redraws.
        view.SetChannel(TextureChannels.Alpha, shown: false);
        Assert.Equal(1, raised);
    }

    /// <summary>A model with no import behind it says so rather than showing an empty list.</summary>
    [Fact]
    public void AModelWithNoPartsSaysSo() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.fbx", "bytes");

        var document = new ModelImportDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<ModelImportView>();

        view.Show(document);
        harness.Ui.Frame();

        Assert.False(view.Empty.HasClass("hidden"));
        Assert.Equal(0, view.PartCount);
    }

    /// <summary>And one that has been imported lists its parts, grouped by what they are.</summary>
    [Fact]
    public void AModelListsItsParts() {
        using var harness = new ViewHarness();

        var path = harness.Project.WriteAsset(
            "Assets/hero.fbx",
            "bytes",
            "guid: 00000000000000000000000000000001\nmetaVersion: 1\nimporter: !ModelImporter\n"
            + "subAssets:\n  - id: 00000001\n    name: Body\n    type: Mesh\n"
            + "  - id: 00000002\n    name: Coat\n    type: Mesh\n"
            + "  - id: 00000003\n    name: Hero\n    type: Skeleton\n"
        );

        var document = new ModelImportDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<ModelImportView>();

        view.Show(document);
        harness.Ui.Frame();

        Assert.True(view.Empty.HasClass("hidden"));
        Assert.Equal(3, view.PartCount);
        Assert.Equal(2, view.Parts.Root.Children.Count);
    }
}

/// <summary>What the material and preview views build.</summary>
public class EditorViewTests {
    /// <summary>Every parameter gets a section, and the section names its kind.</summary>
    [Fact]
    public void EveryParameterGetsASection() {
        using var harness = new ViewHarness();

        var material = new MaterialAsset {
            Parameters = [
                new ScalarParameter { Name = "roughness" },
                new ColourParameter { Name = "tint" }
            ]
        };

        var path = harness.Project.Write("Assets/hero.vxmat", material.ToYaml());
        var document = new MaterialDocument(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<MaterialView>();
        view.Show(document);
        harness.Ui.Frame();

        Assert.Equal(2, view.Parameters.Children.Count);
    }

    /// <summary>A material naming no graph offers no button to open one.</summary>
    [Fact]
    public void NoGraphMeansNoButton() {
        using var harness = new ViewHarness();
        var path = harness.Project.Write("Assets/hero.vxmat", string.Empty);

        var document = new MaterialDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frame();

        Assert.True(view.OpenGraph.HasClass("hidden"));
    }

    /// <summary>⚠ One naming a graph the project has lost offers a button that says why it cannot.</summary>
    [Fact]
    public void AMissingGraphSaysSo() {
        using var harness = new ViewHarness();
        var path = harness.Project.Write("Assets/hero.vxmat", string.Empty);

        var document = new MaterialDocument(harness.Project.Project, AssetId.New(), path);
        document.Header.Graph = AssetId.New();

        var view = harness.Ui.Document.Root.Add<MaterialView>();
        view.Show(document);
        harness.Ui.Frame();

        Assert.False(view.OpenGraph.HasClass("hidden"));
        Assert.True(view.OpenGraph.Disabled);
    }

    /// <summary>The markup preview builds the element tree the file describes.</summary>
    [Fact]
    public void TheMarkupPreviewBuildsAStructure() {
        using var harness = new ViewHarness();

        var path = harness.Project.Write(
            "Assets/Counter.vxml",
            "@component Counter\n\n<panel class=\"card\">\n  <text>Hello</text>\n</panel>\n"
        );

        var document = new MarkupDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<PreviewCodeEditorView>();

        view.Show(document);
        harness.Ui.Frame();

        // A panel, its text element, and the text node inside it.
        Assert.Equal(3, view.ElementCount);
    }

    /// <summary>The stylesheet preview loads the file's rules against a sample tree.</summary>
    [Fact]
    public void TheStylesheetPreviewLoadsTheRules() {
        using var harness = new ViewHarness();
        var path = harness.Project.Write("Assets/theme.vcss", ".preview-sample { flex-direction: column; }\n");

        var document = new StyleSheetDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<PreviewCodeEditorView>();

        view.Show(document);
        harness.Ui.Frame();

        Assert.True(view.ElementCount > 0);
        Assert.Equal("Live cascade over the sample tree below.", view.Status.Text);
    }

    /// <summary>The group list is the project's group files, ordered by path.</summary>
    [Fact]
    public void TheGroupListIsTheProjectsGroups() {
        using var harness = new ViewHarness();

        harness.Project.Write("Assets/UiCore.vxgroup", "name: UiCore\n");
        harness.Project.Write("Assets/Levels.vxgroup", "name: Levels\n");
        harness.Project.Project.Assets.Scan();

        var view = harness.Ui.Document.Root.Add<AddressableGroupsView>();
        view.Show(harness.Project.Project);
        harness.Ui.Frame();

        Assert.Equal(2, view.Count);
        Assert.True(view.Analyse.Disabled);
    }

    /// <summary>A panel with no analyser says so rather than showing an empty list.</summary>
    [Fact]
    public void NoAnalyserIsASentence() {
        using var harness = new ViewHarness();

        var view = harness.Ui.Document.Root.Add<AddressableGroupsView>();
        view.Show(harness.Project.Project);

        Assert.Equal(-1, view.Run());
        Assert.Equal(1, view.AnalysisRows);
    }
}

/// <summary>The mixer's strips, and the two dropdowns that used to take the editor down with them.</summary>
public class AudioMixerViewTests {
    static AudioMixerView Open(ViewHarness harness, out AudioMixerDocument document) {
        document = new AudioMixerDocument(
            harness.Project.Project,
            AssetId.Empty,
            harness.Project.Write("Assets/Game.vxmixer", string.Empty)
        );

        var view = harness.Ui.Document.Root.Add<AudioMixerView>();

        view.Show(document);
        harness.Ui.Frame();

        return view;
    }

    static Select Dropdown(AudioMixerView view, string placeholder) =>
        Descendants(view.Fields).OfType<Select>().FirstOrDefault(select => select.Placeholder == placeholder)
        ?? throw new InvalidOperationException($"the side panel has no '{placeholder}' dropdown");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    /// <summary>Clicks a bus's strip, the way a designer selects one.</summary>
    /// <remarks>
    ///     ⚠ Through `Dispatch` rather than by raising on the element. The strips listen for a raw
    ///     `PointerEvent`, and a test that hands one straight to the handler proves the handler runs
    ///     rather than that a click reaches it.
    /// </remarks>
    static void Choose(ViewHarness harness, AudioMixerView view, string bus) {
        var strip = view.Strips.Children.FirstOrDefault(
            candidate => candidate.Children.Any(child => child.Text == bus)
        ) ?? throw new InvalidOperationException($"the mixer has no '{bus}' strip");

        var x = strip.AbsoluteLeft + (strip.Width / 2f);
        var y = strip.AbsoluteTop + 4f;

        harness.Ui.Document.Dispatch(
            new PointerEvent { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
        );

        harness.Ui.Frame();
    }

    /// <summary>
    ///     ⚠ <b>A fader is a thing you compare against its neighbours, and the strips have always been
    ///     columns.</b> `mixer-strip` is `flex-direction: column` and hands the slider a `min-height`,
    ///     so the layout was asking for a vertical control the whole time — the slider only had one
    ///     axis, so what a designer actually got was a row of narrow boxes each holding a horizontal
    ///     fader with about sixty pixels of travel.
    /// </summary>
    [Fact]
    public void TheFadersAreVerticalAndTallerThanTheyAreWide() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);
        var faders = Descendants(view.Strips).OfType<Slider>().ToList();

        Assert.NotEmpty(faders);
        Assert.All(faders, fader => Assert.Equal(Orientation.Vertical, fader.Orientation));
        Assert.All(faders, fader => Assert.True(
            fader.Height > fader.Width,
            $"a fader is {fader.Width}×{fader.Height}, which is a horizontal control in a vertical hole"
        ));
    }

    /// <summary>
    ///     ⚠ <b>The crash was a click on a dropdown that redraws the panel it is in.</b> Choosing a
    ///     send raises `SelectionChanged`, the handler adds the send, the document announces itself,
    ///     the side panel is rebuilt — and the `Select` that is halfway through `OnOptionChosen` has
    ///     been removed, along with the popover it was about to close. See
    ///     `SelectBase.CloseList`.
    /// </summary>
    [Fact]
    public void AddingASendSurvivesTheSidePanelBeingRebuiltUnderIt() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, "SFX");

        var sends = Dropdown(view, "Send to…");
        var target = sends.Options.First(option => option.Value == "Music");

        target.Activate();
        harness.Ui.Frame();

        Assert.Single(document.Mixer.Buses.First(bus => bus.Name == "SFX").Sends);
        Assert.Equal("Music", document.Mixer.Buses.First(bus => bus.Name == "SFX").Sends[0].Target);
    }

    /// <summary>The same click, on the other dropdown.</summary>
    [Fact]
    public void AddingAnInsertSurvivesTheSidePanelBeingRebuiltUnderIt() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        Choose(harness, view, "Music");

        Dropdown(view, "Add insert…").Options.First(option => option.Value == "Reverb").Activate();
        harness.Ui.Frame();

        Assert.Single(document.Mixer.Buses.First(bus => bus.Name == "Music").Effects);
    }
}
