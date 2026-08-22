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

    // ================================================================== The markup port

    /// <summary>One strip's key, which is what a <c>refs</c> handle files its controls under.</summary>
    static MixerColumn Column(string bus, string parent = "") => new(bus, parent, false);

    /// <summary>
    ///     ⚠ <b>The bounds are literals in the markup and constants in C#, and this is what stops
    ///     them drifting.</b> They have to be literals: an attribute written as <c>@MinimumDb</c> is
    ///     an effect, and <c>Slider.Value</c>'s coerce would have clamped a −3.5 dB fader into the
    ///     default 0–1 range before the bounds effect ever ran.
    /// </summary>
    [Fact]
    public void TheFaderBoundsAreTheConstantsTheMarkupWroteOut() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);
        var faders = Descendants(view.Strips).OfType<Slider>().ToList();

        Assert.NotEmpty(faders);
        Assert.All(faders, fader => Assert.Equal(AudioMixerView.MinimumDb, fader.Minimum));
        Assert.All(faders, fader => Assert.Equal(AudioMixerView.MaximumDb, fader.Maximum));
    }

    /// <summary>
    ///     ⚠ <b>A strip's fader handler reads <i>its own</i> mute, which is the line <c>refs</c> was
    ///     built for.</b> A <c>ref</c> in a loop body is one member for every row (<c>VXML2010</c>),
    ///     so the answer would have been whichever strip was built last — and this asserts it with
    ///     one strip muted and another not, so a handle that answered with the wrong row would say
    ///     so.
    /// </summary>
    /// <remarks>
    ///     Assigned rather than dragged, unlike <c>MarkupTests</c>. What is on trial there is whether
    ///     a binding hears a real gesture at all; here it is which row's controls the handler reaches,
    ///     and <c>change:</c> rides <c>PropertyChanged</c>, so an assignment is reported exactly as a
    ///     drag is.
    /// </remarks>
    [Fact]
    public void AFadersHandlerReadsItsOwnStripsMuteAndNotAnotherStripsOne() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        view.Mutes[Column("Music")].IsChecked = true;
        harness.Ui.Frame();

        view.Faders[Column("SFX")].Value = -9f;
        harness.Ui.Frame();

        Assert.Equal(-9f, document.Mixer.Buses.First(bus => bus.Name == "SFX").GainDb);
        Assert.False(document.Mixer.Buses.First(bus => bus.Name == "SFX").Muted);

        view.Faders[Column("Music")].Value = -18f;
        harness.Ui.Frame();

        Assert.Equal(-18f, document.Mixer.Buses.First(bus => bus.Name == "Music").GainDb);
        Assert.True(document.Mixer.Buses.First(bus => bus.Name == "Music").Muted);
    }

    /// <summary>And the other way round: a mute writes the gain its own fader is showing.</summary>
    [Fact]
    public void AMutesHandlerWritesItsOwnFadersGain() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        view.Mutes[Column("SFX")].IsChecked = true;
        harness.Ui.Frame();

        var sfx = document.Mixer.Buses.First(bus => bus.Name == "SFX");

        Assert.True(sfx.Muted);
        Assert.Equal(0f, sfx.GainDb);

        // Music is authored at −6 dB, so a mute that read the wrong fader would flatten it to SFX's.
        view.Mutes[Column("Music")].IsChecked = true;
        harness.Ui.Frame();

        Assert.Equal(-6f, document.Mixer.Buses.First(bus => bus.Name == "Music").GainDb);
    }

    /// <summary>
    ///     ⚠ <b>The <c>@for</c> key rule, asserted where getting it wrong would be worst.</b> A key
    ///     carrying the gain would tear down and rebuild the fader's region on every step of a drag —
    ///     the element under the pointer, mid-gesture. <c>MixerColumn</c> is name, parent and
    ///     master-ness, so the row survives and the binding inside it does the moving.
    /// </summary>
    [Fact]
    public void ChangingAGainKeepsTheStripsOwnFaderElement() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);
        var fader = view.Faders[Column("SFX")];

        fader.Value = -4f;
        harness.Ui.Frame();
        harness.Ui.Frame();

        Assert.Same(fader, view.Faders[Column("SFX")]);
        Assert.Equal(-4f, view.Faders[Column("SFX")].Value);
    }

    /// <summary>
    ///     ⚠ <b>Opening a mixer posts nothing to the undo stack, and that is the one rule
    ///     <c>change:</c> does not share with <c>bind:</c>.</b> Every fader has both a forward
    ///     binding and a change handler, and the forward binding first writes one flush <i>after</i>
    ///     the subscription exists — so without "a change made while effects are draining is not
    ///     reported", every panel open would record a gain nobody touched, once per strip.
    /// </summary>
    [Fact]
    public void OpeningAMixerPostsNoUndoEntry() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        harness.Ui.Frame();
        harness.Ui.Frame();

        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.Empty(document.Stack.History);

        // ⚠ And a real change straight afterwards does post one — otherwise the assertion above
        // would pass just as well on a panel whose faders were never wired to anything.
        view.Faders[Column("SFX")].Value = -2f;
        harness.Ui.Frame();

        Assert.Equal(1, document.Stack.Depth.Value);
    }

    /// <summary>
    ///     ⚠ <b>The dropdown's options follow the selected bus, which is build-list item 4 worked
    ///     around rather than fixed.</b> A <c>Select</c>'s options are not its children, so there is
    ///     no markup spelling for them; <c>OptionCell</c> makes the list a property, and binding a
    ///     property is an ordinary effect.
    /// </summary>
    [Fact]
    public void TheSendDropdownOffersEveryBusButTheOneItWouldLeave() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);

        Choose(harness, view, "SFX");
        Assert.Equal(["Music", "Voice"], Dropdown(view, "Send to…").Options.Select(option => option.Value));

        Choose(harness, view, "Music");
        Assert.Equal(["SFX", "Voice"], Dropdown(view, "Send to…").Options.Select(option => option.Value));
    }

    /// <summary>
    ///     ⚠ <b>Which of the two a fader writes to is the whole of what "editing a snapshot"
    ///     means</b>, and it is the one behaviour of this panel with no element to look at.
    /// </summary>
    [Fact]
    public void WithASnapshotChosenAFaderRecordsALineAndLeavesTheAuthoredMixAlone() {
        using var harness = new ViewHarness();

        var view = Open(harness, out var document);

        document.AddSnapshot("Underwater");
        harness.Ui.Frame();

        var row = view.Snapshots.Children.First(
            child => child.Text is { } text && text.StartsWith("Underwater", StringComparison.Ordinal)
        );

        harness.Ui.Document.Dispatch(
            new PointerEvent {
                X = row.AbsoluteLeft + 2f,
                Y = row.AbsoluteTop + 2f,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary
            }
        );

        harness.Ui.Frame();
        Assert.Equal("Underwater", view.Snapshot);

        view.Faders[Column("Music")].Value = -30f;
        harness.Ui.Frame();

        Assert.Equal(-6f, document.Mixer.Buses.First(bus => bus.Name == "Music").GainDb);

        var line = document.Mixer.Snapshots.First(entry => entry.Name == "Underwater").Buses
            .First(entry => entry.Bus == "Music");

        Assert.Equal(-30f, line.GainDb);
    }

    /// <summary>
    ///     The master strip is drawn, is not in the file, and is not selectable — which the panel
    ///     this replaces achieved by never registering a handler on it, and this one by the handler
    ///     declining. Same answer; written down because the two are not the same code.
    /// </summary>
    [Fact]
    public void TheMasterStripIsNotSelectable() {
        using var harness = new ViewHarness();

        var view = Open(harness, out _);

        Choose(harness, view, "Master");

        Assert.Equal(string.Empty, view.Selected);
    }
}
