// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Imaging;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The addressable groups panel (#89), held to what the hand-written control built.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Recorded before the port and not after it</b>, <c>MessageLogViewDumpTests</c>' shape:
///         every string below was produced by the C# <c>AddressableGroupsView</c>, so the markup is
///         held to it rather than to itself. Each state is reached the way a person reaches it — a
///         click on a group's row, a click on Analyse build — except the one no pointer can reach:
///         with no planner the button is disabled, and <c>Run</c> is what a host calls.
///     </para>
///     <para>
///         ⚠ <b>Two dumps per state, because a tree dump is blind.</b> <c>UiTest.Tree</c> prints
///         tags, classes, rectangles and text; the button's label, its disabled bit and the empty
///         state's two sentences live where only <c>UiTest.Flags</c> looks.
///     </para>
///     <para>
///         ⚠ <b>And the analysis is run twice with two different plans</b>, because the port's model
///         decision is that list: a <c>@for</c> over a plain field would draw the first run for the
///         life of the panel, and only a second run with different rows can tell.
///     </para>
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "UiTest.Flags reads nine properties by name; tests are not trimmed.")]
public sealed class AddressableGroupsViewDumpTests {
    /// <summary>Where a run writes its dumps and pictures, when somebody asks it to.</summary>
    static readonly string? Record = Environment.GetEnvironmentVariable("VIXEN_ADDRESSABLE_GROUPS_RECORD");

    /// <summary>Two groups listed, none chosen, the analysis not yet run.</summary>
    [Fact]
    public void Two_groups_listed_and_none_chosen() {
        using var harness = new Harness(Plans.First);

        Check(harness, "listed", ListedTree, ListedFlags);
    }

    /// <summary>A click on a group's row opens its policy and hides the empty state.</summary>
    [Fact]
    public void A_click_on_a_group_opens_its_policy() {
        using var harness = new Harness(Plans.First);

        harness.Click(harness.Row("UiCore"));

        Assert.NotNull(harness.View.Selected);
        Check(harness, "chosen", ChosenTree, ChosenFlags);
    }

    /// <summary>Analyse build lists what the planner said, worst first, and a summary line.</summary>
    [Fact]
    public void Analyse_lists_what_the_planner_said() {
        using var harness = new Harness(Plans.First);

        harness.Click(harness.View.Analyse);

        Assert.Equal(3, harness.View.AnalysisRows);
        Check(harness, "analysed", AnalysedTree, AnalysedFlags);
    }

    /// <summary>A second run replaces the first run's rows rather than adding to them.</summary>
    [Fact]
    public void A_second_run_replaces_the_rows_of_the_first() {
        using var harness = new Harness(Plans.First);

        harness.Click(harness.View.Analyse);
        harness.Plan = Plans.Second;
        harness.Click(harness.View.Analyse);

        Assert.Equal(2, harness.View.AnalysisRows);
        Check(harness, "reanalysed", ReanalysedTree, ReanalysedFlags);
    }

    /// <summary>With no planner the button is disabled, and a host's <c>Run</c> says why in a sentence.</summary>
    [Fact]
    public void No_planner_is_a_sentence() {
        using var harness = new Harness(plan: null);

        Assert.True(harness.View.Analyse.Disabled);
        Assert.Equal(-1, harness.View.Run());
        harness.Ui.Frame();

        Check(harness, "unavailable", UnavailableTree, UnavailableFlags);
    }

    static void Check(Harness harness, string state, string tree, string flags) {
        var actualTree = harness.Ui.Tree(harness.View);
        var actualFlags = harness.Ui.Flags(harness.View);

        if (Record is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{state}.tree.txt"), actualTree);
            File.WriteAllText(Path.Combine(directory, $"{state}.flags.txt"), actualFlags);
            PngCodec.Save(Path.Combine(directory, $"{state}.png"), harness.Ui.Capture());
        }

        Assert.Equal(Normalise(tree), Normalise(actualTree));
        Assert.Equal(Normalise(flags), Normalise(actualFlags));
    }

    static string Normalise(string text) => text.ReplaceLineEndings("\n").Trim();

    /// <summary>The two plans the analyser hands back, the second shorter than the first.</summary>
    static class Plans {
        public static BuildPlan First { get; } = new(
            [],
            [],
            [
                new ImportDiagnostic(ImportSeverity.Error, "Assets/Levels/Boss.vxscene depends on Assets/Shared/Rock.png, which is not shipped."),
                new ImportDiagnostic(ImportSeverity.Warning, "Assets/Scratch/Old.png could not be imported and is not shipped.")
            ]
        );

        public static BuildPlan Second { get; } = new(
            [],
            [],
            [new ImportDiagnostic(ImportSeverity.Warning, "Assets/Scratch/Old.png could not be imported and is not shipped.")]
        );
    }

    /// <summary>A project with two groups, the panel over it, and the editor's own face.</summary>
    sealed class Harness : IDisposable {
        readonly ViewHarness harness = new();

        public Harness(BuildPlan? plan) {
            // The editor's own face, so a dump's text widths are the ones a person sees rather than
            // the zero a document with no font measures everything at.
            var face = FontFace.Load(File.ReadAllBytes(TypefacePath()), name: "OpenSans");

            harness.Ui.Document.Fonts.Register(face.Name, face);
            harness.Ui.Document.Fonts.Default = face;

            harness.Project.Write("Assets/UiCore.vxgroup", "name: UiCore\n");
            harness.Project.Write("Assets/Levels.vxgroup", "name: Levels\n");
            harness.Project.Project.Assets.Scan();

            Plan = plan;

            View = harness.Ui.Document.Root.Add<AddressableGroupsView>();
            View.SetStyle("height", "760px");
            View.Show(harness.Project.Project, plan is null ? null : () => Plan!);
            harness.Ui.Frame();
        }

        public Vixen.Ui.Testing.UiTest Ui => harness.Ui;

        public AddressableGroupsView View { get; }

        /// <summary>What the analyser returns next.</summary>
        public BuildPlan? Plan { get; set; }

        /// <summary>The tree row whose label is a group's name.</summary>
        public UiElement Row(string label) =>
            Descendants(View.Groups).OfType<TreeRow>().FirstOrDefault(row => Descendants(row).Any(element => element.Text == label))
            ?? throw new InvalidOperationException($"no row says '{label}'");

        public void Click(UiElement element) {
            var x = element.AbsoluteLeft + (element.Width / 2f);
            var y = element.AbsoluteTop + (element.Height / 2f);

            harness.Ui.MovePointer(x, y);
            harness.Ui.PressPointer();
            harness.Ui.ReleasePointer();
            harness.Ui.Frame();
        }

        public void Dispose() => harness.Dispose();

        static IEnumerable<UiElement> Descendants(UiElement element) {
            foreach (var child in element.Children) {
                yield return child;

                foreach (var below in Descendants(child)) {
                    yield return below;
                }
            }
        }

        static string TypefacePath() {
            const string Relative = "Editor/Vixen.Editor.App/Fonts/OpenSans-Regular.ttf";

            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
                var candidate = Path.Combine(directory.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate)) {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"'{Relative}' was not found above '{AppContext.BaseDirectory}'.");
        }
    }

    // ── The reference dumps, taken from the hand-written control ─────────────

    const string ListedTree = """
        <group-editor .size-md .variant-default> 0,0 1200×760
          <tree-view .size-md .variant-default> 6,6 1188×240
            <virtualizing-panel .size-md .variant-default> 0,0 1188×240
              <scroll-view .size-md .variant-default> 0,0 1188×240
                <scroll-content .virtual-content> 0,0 1188×44
                  <tree-row .leaf .size-md .variant-default> 0,0 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "Levels"
                  <tree-row .leaf .size-md .variant-default> 0,22 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "UiCore"
                <scrollbar .size-md .variant-default .vertical> 1178,0 10×240
                <scrollbar .horizontal .size-md .variant-default> 0,230 1188×10
            <tree-drop-indicator .hidden> 0,0 0×0
          <empty-state .size-md .variant-default> 6,254 1188×124
            <empty-title> 525,32 137×22 "No group selected"
            <empty-description> 316,62 555×22 "A .vxgroup is a policy: how its assets are packed, compressed and shipped."
            <empty-actions> 594,92 0×0
          <inspector .hidden .size-md .variant-default> 0,0 0×0
            <inspector-header> 0,0 0×0
              <search-box .empty .size-md .variant-default> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <field-placeholder> 0,0 0×0 "Search"
                <field-text> 0,0 0×0
                <icon-button .size-md .variant-subtle> 0,0 0×0
                  <icon .size-md .variant-default> 0,0 0×0
                  <label> 0,0 0×0 "Clear"
              <toggle-button .inspector-lock .size-sm .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Lock"
            <scroll-view .size-md .variant-default> 0,0 0×0
              <scroll-content> 0,0 0×0
                <inspector-body> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 0,0 0×0
              <scrollbar .horizontal .size-md .variant-default> 0,0 0×0
            <empty-state .hidden .size-md .variant-default> 0,0 0×0
              <empty-title> 0,0 0×0
              <empty-description> 0,0 0×0
              <empty-actions> 0,0 0×0
          <button .size-md .variant-default> 6,386 126×34
            <label> 13,6 100×22 "Analyse build"
          <analysis-list> 6,428 1188×0
        """;

    const string ListedFlags = """
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Search"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <toggle-button .inspector-lock .size-sm .variant-subtle> Label="Lock"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <button .size-md .variant-default> Label="Analyse build"
        """;

    const string ChosenTree = """
        <group-editor .size-md .variant-default> 0,0 1200×760
          <tree-view .size-md .variant-default> 6,6 1188×167
            <virtualizing-panel .size-md .variant-default> 0,0 1188×167
              <scroll-view .size-md .variant-default> 0,0 1188×167
                <scroll-content .virtual-content> 0,0 1188×44
                  <tree-row .leaf .size-md .variant-default> 0,0 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "Levels"
                  <tree-row .leaf .size-md .variant-default> 0,22 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "UiCore"
                <scrollbar .size-md .variant-default .vertical> 1178,0 10×167
                <scrollbar .horizontal .size-md .variant-default> 0,157 1188×10
            <tree-drop-indicator .hidden> 0,0 0×0
          <empty-state .hidden .size-md .variant-default> 0,0 0×0
            <empty-title> 0,0 0×0 "No group selected"
            <empty-description> 0,0 0×0 "A .vxgroup is a policy: how its assets are packed, compressed and shipped."
            <empty-actions> 0,0 0×0
          <inspector .size-md .variant-default> 6,181 1188×523
            <inspector-header> 2,2 1184×23
              <search-box .empty .size-md .variant-default> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <field-placeholder> 0,0 0×0 "Search"
                <field-text> 0,0 0×0
                <icon-button .size-md .variant-subtle> 0,0 0×0
                  <icon .size-md .variant-default> 0,0 0×0
                  <label> 0,0 0×0 "Clear"
              <toggle-button .inspector-lock .size-sm .variant-subtle> 0,0 23×23
                <icon .size-md .variant-default> 4,4 15×15
                <label> 0,0 0×0 "Lock"
            <scroll-view .size-md .variant-default> 2,31 1184×490
              <scroll-content> 0,0 1184×323
                <inspector-body> 0,0 1184×323
                  <inspector-row .size-md .variant-default> 0,0 1184×30
                    <inspector-label> 4,4 110×22 "Name"
                    <inspector-editor> 122,0 1028×30
                      <textbox .size-md .variant-default> 0,0 1028×30
                        <field-placeholder> 0,0 0×0
                        <field-text> 9,4 51×22 "UiCore"
                    <icon-button .size-sm .variant-subtle> 1158,0 22×22
                      <icon .size-md .variant-default> 3,3 16×16
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "Assets name this in their sidecar, and folders inherit it downwards."
                  <inspector-row .size-md .variant-default> 0,30 1184×30
                    <inspector-label> 4,4 110×22 "Load Path"
                    <inspector-editor> 122,0 1058×30
                      <select .size-md .variant-default> 0,0 1058×30
                        <select-field> 9,4 1022×22 "Local"
                        <icon .size-md .variant-default> 1037,9 12×12
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "Local ships inside the application. Remote is fetched from RemoteUrl."
                  <inspector-row .size-md .variant-default> 0,60 1184×30
                    <inspector-label> 4,4 110×22 "Packing"
                    <inspector-editor> 122,0 1058×30
                      <select .size-md .variant-default> 0,0 1058×30
                        <select-field> 9,4 1022×22 "PackTogether"
                        <icon .size-md .variant-default> 1037,9 12×12
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "The single most consequential setting here: a bundle is the unit of both download and residency."
                  <inspector-row .size-md .variant-default> 0,90 1184×30
                    <inspector-label> 4,4 110×22 "Compression"
                    <inspector-editor> 122,0 1058×30
                      <select .size-md .variant-default> 0,0 1058×30
                        <select-field> 9,4 1022×22 "Lz4"
                        <icon .size-md .variant-default> 1037,9 12×12
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                  <inspector-row .size-md .variant-default> 0,120 1184×44
                    <inspector-label> 4,0 110×44 "Bundle Naming"
                    <inspector-editor> 122,7 1058×30
                      <select .size-md .variant-default> 0,0 1058×30
                        <select-field> 9,4 1022×22 "FilenameHash"
                        <icon .size-md .variant-default> 1037,9 12×12
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "FilenameHash is the only naming a CDN cache cannot serve a stale copy of."
                  <inspector-row .size-md .variant-default> 0,164 1184×44
                    <inspector-label> 4,0 110×44 "Include In Build"
                    <inspector-editor> 122,14 1058×16
                      <checkbox .size-md .variant-default> 0,0 24×16
                        <box> 0,0 16×16
                          <icon .size-md .variant-default> 2,2 12×12
                        <label> 24,8 0×0
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "A group of work in progress is turned off rather than deleted."
                  <inspector-row .size-md .variant-default> 0,208 1184×44
                    <inspector-label> 4,0 110×44 "Include In Server Build"
                    <inspector-editor> 122,14 1058×16
                      <checkbox .size-md .variant-default> 0,0 24×16
                        <box> 0,0 16×16
                          <icon .size-md .variant-default> 2,2 12×12
                        <label> 24,8 0×0
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "Doc 17's server content profile. A realm never asks for it, so a server build leaves it out."
                  <inspector-row .size-md .variant-default> 0,252 1184×44
                    <inspector-label> 4,0 110×44 "Update Restriction"
                    <inspector-editor> 122,7 1058×30
                      <select .size-md .variant-default> 0,0 1058×30
                        <select-field> 9,4 1022×22 "CanChangePostRelease"
                        <icon .size-md .variant-default> 1037,9 12×12
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "A group baked into the binary cannot be replaced by a download, six months after shipping."
                  <inspector-row .size-md .variant-default> 0,296 1184×27
                    <inspector-label> 4,2 110×22 "Remote Url"
                    <inspector-editor> 122,0 1058×27
                      <textbox .empty .size-md .variant-default> 0,0 1058×27
                        <field-placeholder> 9,14 0×0
                        <field-text> 9,4 0×19
                    <icon-button .hidden .size-sm .variant-subtle> 0,0 0×0
                      <icon .size-md .variant-default> 0,0 0×0
                      <label> 0,0 0×0 "Reset"
                    <tooltip .closed .size-md .variant-default> 0,0 0×0 "A prefix the bundle's file name is appended to. Empty for a local group."
              <scrollbar .size-md .variant-default .vertical> 1174,0 10×490
              <scrollbar .horizontal .size-md .variant-default> 0,480 1184×10
            <empty-state .hidden .size-md .variant-default> 0,0 0×0
              <empty-title> 0,0 0×0
              <empty-description> 0,0 0×0
              <empty-actions> 0,0 0×0
          <button .size-md .variant-default> 6,712 126×34
            <label> 13,6 100×22 "Analyse build"
          <analysis-list> 6,754 1188×0
        """;

    const string ChosenFlags = """
        <group-editor .size-md .variant-default> State=Hover, FocusWithin
        <tree-view .size-md .variant-default> State=Hover, Focus, FocusWithin
        <virtualizing-panel .size-md .variant-default> State=Hover
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content .virtual-content> State=Hover
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <tree-row .leaf .size-md .variant-default> State=Hover, Checked Label=Vixen.Ui.UiElement
        <tree-label> State=Hover
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Search"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <toggle-button .inspector-lock .size-sm .variant-subtle> Label="Lock"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <textbox .size-md .variant-default> State=Valid Value="UiCore"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="Assets name this in their sidecar, and folders inherit it downwards."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Local"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="Local ships inside the application. Remote is fetched from RemoteUrl."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="PackTogether"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="The single most consequential setting here: a bundle is the unit of both download and residency."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Lz4"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="FilenameHash"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="FilenameHash is the only naming a CDN cache cannot serve a stale copy of."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <checkbox .size-md .variant-default> State=Checked, Valid IsChecked=True
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="A group of work in progress is turned off rather than deleted."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <checkbox .size-md .variant-default> State=Checked, Valid IsChecked=True
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="Doc 17's server content profile. A realm never asks for it, so a server build leaves it out."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="CanChangePostRelease"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="A group baked into the binary cannot be replaced by a download, six months after shipping."
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <textbox .empty .size-md .variant-default> State=Valid
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <tooltip .closed .size-md .variant-default> Label="A prefix the bundle's file name is appended to. Empty for a local group."
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <button .size-md .variant-default> Label="Analyse build"
        """;

    const string AnalysedTree = """
        <group-editor .size-md .variant-default> 0,0 1200×760
          <tree-view .size-md .variant-default> 6,6 1188×240
            <virtualizing-panel .size-md .variant-default> 0,0 1188×240
              <scroll-view .size-md .variant-default> 0,0 1188×240
                <scroll-content .virtual-content> 0,0 1188×44
                  <tree-row .leaf .size-md .variant-default> 0,0 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "Levels"
                  <tree-row .leaf .size-md .variant-default> 0,22 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "UiCore"
                <scrollbar .size-md .variant-default .vertical> 1178,0 10×240
                <scrollbar .horizontal .size-md .variant-default> 0,230 1188×10
            <tree-drop-indicator .hidden> 0,0 0×0
          <empty-state .size-md .variant-default> 6,254 1188×124
            <empty-title> 525,32 137×22 "No group selected"
            <empty-description> 316,62 555×22 "A .vxgroup is a policy: how its assets are packed, compressed and shipped."
            <empty-actions> 594,92 0×0
          <inspector .hidden .size-md .variant-default> 0,0 0×0
            <inspector-header> 0,0 0×0
              <search-box .empty .size-md .variant-default> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <field-placeholder> 0,0 0×0 "Search"
                <field-text> 0,0 0×0
                <icon-button .size-md .variant-subtle> 0,0 0×0
                  <icon .size-md .variant-default> 0,0 0×0
                  <label> 0,0 0×0 "Clear"
              <toggle-button .inspector-lock .size-sm .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Lock"
            <scroll-view .size-md .variant-default> 0,0 0×0
              <scroll-content> 0,0 0×0
                <inspector-body> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 0,0 0×0
              <scrollbar .horizontal .size-md .variant-default> 0,0 0×0
            <empty-state .hidden .size-md .variant-default> 0,0 0×0
              <empty-title> 0,0 0×0
              <empty-description> 0,0 0×0
              <empty-actions> 0,0 0×0
          <button .size-md .variant-default> 6,386 126×34
            <label> 13,6 100×22 "Analyse build"
          <analysis-list> 6,428 1188×70
            <analysis-row .error> 0,0 1188×22
              <analysis-stage> 4,0 72×22 "error"
              <analysis-message> 84,0 1100×22 "Assets/Levels/Boss.vxscene depends on Assets/Shared/Rock.png, which is not shipped."
            <analysis-row .warning> 0,24 1188×22
              <analysis-stage> 4,0 72×22 "warning"
              <analysis-message> 84,0 1100×22 "Assets/Scratch/Old.png could not be imported and is not shipped."
            <analysis-row .warning> 0,48 1188×22
              <analysis-stage> 4,0 72×22 "plan"
              <analysis-message> 84,0 1100×22 "0 addressable assets across 0 groups."
        """;

    const string AnalysedFlags = """
        <group-editor .size-md .variant-default> State=Hover, FocusWithin
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Search"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <toggle-button .inspector-lock .size-sm .variant-subtle> Label="Lock"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <button .size-md .variant-default> State=Hover, Focus, FocusWithin Label="Analyse build"
        <label> State=Hover
        """;

    const string ReanalysedTree = """
        <group-editor .size-md .variant-default> 0,0 1200×760
          <tree-view .size-md .variant-default> 6,6 1188×240
            <virtualizing-panel .size-md .variant-default> 0,0 1188×240
              <scroll-view .size-md .variant-default> 0,0 1188×240
                <scroll-content .virtual-content> 0,0 1188×44
                  <tree-row .leaf .size-md .variant-default> 0,0 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "Levels"
                  <tree-row .leaf .size-md .variant-default> 0,22 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "UiCore"
                <scrollbar .size-md .variant-default .vertical> 1178,0 10×240
                <scrollbar .horizontal .size-md .variant-default> 0,230 1188×10
            <tree-drop-indicator .hidden> 0,0 0×0
          <empty-state .size-md .variant-default> 6,254 1188×124
            <empty-title> 525,32 137×22 "No group selected"
            <empty-description> 316,62 555×22 "A .vxgroup is a policy: how its assets are packed, compressed and shipped."
            <empty-actions> 594,92 0×0
          <inspector .hidden .size-md .variant-default> 0,0 0×0
            <inspector-header> 0,0 0×0
              <search-box .empty .size-md .variant-default> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <field-placeholder> 0,0 0×0 "Search"
                <field-text> 0,0 0×0
                <icon-button .size-md .variant-subtle> 0,0 0×0
                  <icon .size-md .variant-default> 0,0 0×0
                  <label> 0,0 0×0 "Clear"
              <toggle-button .inspector-lock .size-sm .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Lock"
            <scroll-view .size-md .variant-default> 0,0 0×0
              <scroll-content> 0,0 0×0
                <inspector-body> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 0,0 0×0
              <scrollbar .horizontal .size-md .variant-default> 0,0 0×0
            <empty-state .hidden .size-md .variant-default> 0,0 0×0
              <empty-title> 0,0 0×0
              <empty-description> 0,0 0×0
              <empty-actions> 0,0 0×0
          <button .size-md .variant-default> 6,386 126×34
            <label> 13,6 100×22 "Analyse build"
          <analysis-list> 6,428 1188×46
            <analysis-row .warning> 0,0 1188×22
              <analysis-stage> 4,0 72×22 "warning"
              <analysis-message> 84,0 1100×22 "Assets/Scratch/Old.png could not be imported and is not shipped."
            <analysis-row .warning> 0,24 1188×22
              <analysis-stage> 4,0 72×22 "plan"
              <analysis-message> 84,0 1100×22 "0 addressable assets across 0 groups."
        """;

    const string ReanalysedFlags = """
        <group-editor .size-md .variant-default> State=Hover, FocusWithin
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Search"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <toggle-button .inspector-lock .size-sm .variant-subtle> Label="Lock"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <button .size-md .variant-default> State=Hover, Focus, FocusWithin Label="Analyse build"
        <label> State=Hover
        """;

    const string UnavailableTree = """
        <group-editor .size-md .variant-default> 0,0 1200×760
          <tree-view .size-md .variant-default> 6,6 1188×240
            <virtualizing-panel .size-md .variant-default> 0,0 1188×240
              <scroll-view .size-md .variant-default> 0,0 1188×240
                <scroll-content .virtual-content> 0,0 1188×44
                  <tree-row .leaf .size-md .variant-default> 0,0 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "Levels"
                  <tree-row .leaf .size-md .variant-default> 0,22 1188×22
                    <tree-indent> 6,11 14×0
                    <icon .size-md .tree-chevron .variant-default> 24,6 10×10
                    <icon .size-md .tree-glyph .variant-default> 38,5 13×13
                    <tree-label> 55,0 1127×22 "UiCore"
                <scrollbar .size-md .variant-default .vertical> 1178,0 10×240
                <scrollbar .horizontal .size-md .variant-default> 0,230 1188×10
            <tree-drop-indicator .hidden> 0,0 0×0
          <empty-state .size-md .variant-default> 6,254 1188×124
            <empty-title> 525,32 137×22 "No group selected"
            <empty-description> 316,62 555×22 "A .vxgroup is a policy: how its assets are packed, compressed and shipped."
            <empty-actions> 594,92 0×0
          <inspector .hidden .size-md .variant-default> 0,0 0×0
            <inspector-header> 0,0 0×0
              <search-box .empty .size-md .variant-default> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <field-placeholder> 0,0 0×0 "Search"
                <field-text> 0,0 0×0
                <icon-button .size-md .variant-subtle> 0,0 0×0
                  <icon .size-md .variant-default> 0,0 0×0
                  <label> 0,0 0×0 "Clear"
              <toggle-button .inspector-lock .size-sm .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Lock"
            <scroll-view .size-md .variant-default> 0,0 0×0
              <scroll-content> 0,0 0×0
                <inspector-body> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 0,0 0×0
              <scrollbar .horizontal .size-md .variant-default> 0,0 0×0
            <empty-state .hidden .size-md .variant-default> 0,0 0×0
              <empty-title> 0,0 0×0
              <empty-description> 0,0 0×0
              <empty-actions> 0,0 0×0
          <button .size-md .variant-default> 6,386 126×34
            <label> 13,6 100×22 "Analyse build"
          <analysis-list> 6,428 1188×22
            <analysis-row .warning> 0,0 1188×22
              <analysis-stage> 4,0 72×22 "build"
              <analysis-message> 84,0 1100×22 "Nothing here can run an analysis: the host has not supplied a planner."
        """;

    const string UnavailableFlags = """
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <tree-row .leaf .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Search"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <toggle-button .inspector-lock .size-sm .variant-subtle> Label="Lock"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <button .size-md .variant-default> State=Disabled Disabled=True Label="Analyse build"
        """;
}
