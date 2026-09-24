// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The behaviour tree's search popup (#89), held to what the hand-written control built.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Recorded before the port and not after it</b>, <c>MessageLogViewDumpTests</c>' shape:
///         every string below was produced by the C# <c>BehaviorSearchPopup</c>, so the markup is
///         held to it rather than to itself. Each state is reached the way a person reaches it —
///         Space over the canvas, the Add decorator button, letters typed into the field — because
///         until #1370 nothing opened this popup at all, and a dump taken by calling <c>Show</c>
///         would have recorded a state no person could reach.
///     </para>
///     <para>
///         ⚠ <b>Two dumps per state, because a tree dump is blind.</b> <c>UiTest.Tree</c> prints
///         tags, classes, rectangles and text; the field's value and a state bit live where only
///         <c>UiTest.Flags</c> looks.
///     </para>
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "UiTest.Flags reads nine properties by name; tests are not trimmed.")]
public sealed class BehaviorSearchPopupDumpTests {
    /// <summary>Where a run writes its dumps and pictures, when somebody asks it to.</summary>
    static readonly string? Record = Environment.GetEnvironmentVariable("VIXEN_BEHAVIOR_SEARCH_RECORD");

    /// <summary>Space over the tree: every composite and task, in declaration order.</summary>
    [Fact]
    public void Space_offers_every_composite_and_task() {
        using var harness = new Harness();

        harness.Space();

        Check(harness, "nodes", NodesTree, NodesFlags);
    }

    /// <summary>Three letters narrow it, best first.</summary>
    [Fact]
    public void Typing_ranks_what_matches() {
        using var harness = new Harness();

        harness.Space();
        harness.Type("sel");

        Check(harness, "typed", TypedTree, TypedFlags);
    }

    /// <summary>The attachment button offers decorators and nothing else.</summary>
    [Fact]
    public void Add_decorator_offers_only_decorators() {
        using var harness = new Harness();

        harness.Click(harness.View.AddDecorator);

        Check(harness, "decorators", DecoratorsTree, DecoratorsFlags);
    }

    /// <summary>A query nothing answers leaves an empty list and the field still holding the word.</summary>
    [Fact]
    public void A_query_nothing_answers_empties_the_list() {
        using var harness = new Harness();

        harness.Space();
        harness.Type("zzz");

        Check(harness, "nothing", NothingTree, NothingFlags);
    }

    /// <summary>Closed again by Escape: hidden, and with nothing left in the flags that says open.</summary>
    [Fact]
    public void Escape_closes_it() {
        using var harness = new Harness();

        harness.Space();
        harness.Ui.PressKey(InputKey.Escape);
        harness.Ui.Frame();

        Check(harness, "closed", ClosedTree, ClosedFlags);
    }

    static void Check(Harness harness, string state, string tree, string flags) {
        var popup = harness.View.Search;
        var actualTree = harness.Ui.Tree(popup);
        var actualFlags = harness.Ui.Flags(popup);

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

    /// <summary>A tree view over a new tree, with every sheet the host loads.</summary>
    sealed class Harness : IDisposable {
        readonly ViewHarness harness = new();

        public Harness() {
            // The editor's own face, so a dump's text widths are the ones a person sees rather than
            // the zero a document with no font measures everything at.
            var face = FontFace.Load(File.ReadAllBytes(TypefacePath()), name: "OpenSans");

            harness.Ui.Document.Fonts.Register(face.Name, face);
            harness.Ui.Document.Fonts.Default = face;

            var path = harness.Project.Write("Assets/Guard.vxbt", string.Empty);
            var document = new BehaviorTreeDocument(harness.Project.Project, AssetId.Empty, path);

            View = harness.Ui.Document.Root.Add<BehaviorTreeView>();
            View.SetStyle("height", "760px");
            View.Show(document);
            harness.Ui.Frame();
        }

        public Vixen.Ui.Testing.UiTest Ui => harness.Ui;

        public BehaviorTreeView View { get; }

        /// <summary>Space with the focus in the canvas and the pointer over a fixed point of it.</summary>
        public void Space() {
            harness.Ui.Document.Focus(View.Canvas);
            harness.Ui.MovePointer(View.Canvas.AbsoluteLeft + 40f, View.Canvas.AbsoluteTop + 40f);
            harness.Ui.PressKey(InputKey.Space);
            harness.Ui.Frame();
        }

        /// <summary>Letters into whatever has the focus, then a frame so the rebuilt rows are laid out.</summary>
        public void Type(string text) {
            harness.Ui.TypeText(text);
            harness.Ui.Frame();
        }

        public void Click(UiElement element) {
            var x = element.AbsoluteLeft + (element.Width / 2f);
            var y = element.AbsoluteTop + (element.Height / 2f);

            harness.Ui.MovePointer(x, y);
            harness.Ui.PressPointer();
            harness.Ui.ReleasePointer();
            harness.Ui.Frame();
        }

        public void Dispose() => harness.Dispose();

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

    const string NodesTree = """
        <behavior-search .size-md .variant-default> 0,0 280×320
          <textbox .behavior-search-query .empty .size-md .variant-default> 5,5 270×31
            <field-placeholder> 9,4 114×22 "Search nodes…"
            <field-text> 9,6 0×19
          <scroll-view .behavior-search-list .size-md .variant-default> 5,38 270×277
            <scroll-content> 0,0 270×321
              <behavior-search-row> 0,0 260×22
                <search-label> 4,0 156×22 "Selector"
                <search-category> 168,0 88×22 "Composites"
              <behavior-search-row> 0,23 260×22
                <search-label> 4,0 156×22 "Sequence"
                <search-category> 168,0 88×22 "Composites"
              <behavior-search-row> 0,46 260×22
                <search-label> 4,0 156×22 "Priority"
                <search-category> 168,0 88×22 "Composites"
              <behavior-search-row> 0,69 260×22
                <search-label> 4,0 156×22 "Random selector"
                <search-category> 168,0 88×22 "Composites"
              <behavior-search-row> 0,92 260×22
                <search-label> 4,0 156×22 "Parallel"
                <search-category> 168,0 88×22 "Composites"
              <behavior-search-row> 0,115 260×22
                <search-label> 4,0 202×22 "Wait"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,138 260×22
                <search-label> 4,0 202×22 "Wait (from a key)"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,161 260×22
                <search-label> 4,0 202×22 "Finish with"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,184 260×22
                <search-label> 4,0 202×22 "Set blackboard value"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,207 260×22
                <search-label> 4,0 202×22 "Clear blackboard value"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,230 260×22
                <search-label> 4,0 202×22 "Log"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,253 260×22
                <search-label> 4,0 202×22 "Run subtree"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,276 260×22
                <search-label> 4,0 202×22 "Run subtree (from a key)"
                <search-category> 214,0 42×22 "Tasks"
              <behavior-search-row> 0,299 260×22
                <search-label> 4,0 202×22 "Run utility set"
                <search-category> 214,0 42×22 "Tasks"
            <scrollbar .size-md .variant-default .vertical> 260,0 10×277
            <scrollbar .horizontal .size-md .variant-default> 0,267 270×10
        """;

    const string NodesFlags = """
        <behavior-search .size-md .variant-default> State=FocusWithin
        <textbox .behavior-search-query .empty .size-md .variant-default> State=Focus, FocusVisible, FocusWithin, PlaceholderShown, Valid Placeholder="Search nodes…"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string TypedTree = """
        <behavior-search .size-md .variant-default> 0,0 280×91
          <textbox .behavior-search-query .size-md .variant-default> 5,5 270×34
            <field-placeholder> 0,0 0×0 "Search nodes…"
            <field-text> 9,6 21×22 "sel"
          <scroll-view .behavior-search-list .size-md .variant-default> 5,41 270×45
            <scroll-content> 0,0 270×45
              <behavior-search-row> 0,0 260×22
                <search-label> 4,0 156×22 "Selector"
                <search-category> 168,0 88×22 "Composites"
              <behavior-search-row> 0,23 260×22
                <search-label> 4,0 156×22 "Random selector"
                <search-category> 168,0 88×22 "Composites"
            <scrollbar .size-md .variant-default .vertical> 260,0 10×45
            <scrollbar .horizontal .size-md .variant-default> 0,35 270×10
        """;

    const string TypedFlags = """
        <behavior-search .size-md .variant-default> State=FocusWithin
        <textbox .behavior-search-query .size-md .variant-default> State=Focus, FocusVisible, FocusWithin, Valid Value="sel" Placeholder="Search nodes…"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string DecoratorsTree = """
        <behavior-search .size-md .variant-default> 0,0 280×320
          <textbox .behavior-search-query .empty .size-md .variant-default> 5,5 270×31
            <field-placeholder> 9,4 114×22 "Search nodes…"
            <field-text> 9,6 0×19
          <scroll-view .behavior-search-list .size-md .variant-default> 5,38 270×277
            <scroll-content> 0,0 270×344
              <behavior-search-row> 0,0 260×22
                <search-label> 4,0 163×22 "Blackboard"
                <search-category> 175,0 81×22 "Conditions"
              <behavior-search-row> 0,23 260×22
                <search-label> 4,0 163×22 "Compare entries"
                <search-category> 175,0 81×22 "Conditions"
              <behavior-search-row> 0,46 260×22
                <search-label> 4,0 163×22 "Is at location"
                <search-category> 175,0 81×22 "Conditions"
              <behavior-search-row> 0,69 260×22
                <search-label> 4,0 163×22 "Cone"
                <search-category> 175,0 81×22 "Conditions"
              <behavior-search-row> 0,92 260×22
                <search-label> 4,0 209×22 "Inverter"
                <search-category> 221,0 35×22 "Flow"
              <behavior-search-row> 0,115 260×22
                <search-label> 4,0 209×22 "Force success"
                <search-category> 221,0 35×22 "Flow"
              <behavior-search-row> 0,138 260×22
                <search-label> 4,0 209×22 "Force failure"
                <search-category> 221,0 35×22 "Flow"
              <behavior-search-row> 0,161 260×22
                <search-label> 4,0 209×22 "Random chance"
                <search-category> 221,0 35×22 "Flow"
              <behavior-search-row> 0,184 260×22
                <search-label> 4,0 193×22 "Cooldown"
                <search-category> 205,0 51×22 "Timing"
              <behavior-search-row> 0,207 260×22
                <search-label> 4,0 193×22 "Time limit"
                <search-category> 205,0 51×22 "Timing"
              <behavior-search-row> 0,230 260×22
                <search-label> 4,0 193×22 "Tag cooldown"
                <search-category> 205,0 51×22 "Timing"
              <behavior-search-row> 0,253 260×22
                <search-label> 4,0 193×22 "Set tag cooldown"
                <search-category> 205,0 51×22 "Timing"
              <behavior-search-row> 0,276 260×22
                <search-label> 4,0 209×22 "Loop"
                <search-category> 221,0 35×22 "Flow"
              <behavior-search-row> 0,299 260×22
                <search-label> 4,0 161×22 "Composite condition"
                <search-category> 173,0 83×22 "Decorators"
              <behavior-search-row> 0,322 260×22
                <search-label> 4,0 161×22 "Conditional loop"
                <search-category> 173,0 83×22 "Decorators"
            <scrollbar .size-md .variant-default .vertical> 260,0 10×277
            <scrollbar .horizontal .size-md .variant-default> 0,267 270×10
        """;

    const string DecoratorsFlags = """
        <behavior-search .size-md .variant-default> State=FocusWithin
        <textbox .behavior-search-query .empty .size-md .variant-default> State=Focus, FocusWithin, PlaceholderShown, Valid Placeholder="Search nodes…"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string NothingTree = """
        <behavior-search .size-md .variant-default> 0,0 280×46
          <textbox .behavior-search-query .size-md .variant-default> 5,5 270×34
            <field-placeholder> 0,0 0×0 "Search nodes…"
            <field-text> 9,6 23×22 "zzz"
          <scroll-view .behavior-search-list .size-md .variant-default> 5,41 270×0
            <scroll-content> 0,0 270×0
            <scrollbar .size-md .variant-default .vertical> 260,0 10×0
            <scrollbar .horizontal .size-md .variant-default> 0,-10 270×10
        """;

    const string NothingFlags = """
        <behavior-search .size-md .variant-default> State=FocusWithin
        <textbox .behavior-search-query .size-md .variant-default> State=Focus, FocusVisible, FocusWithin, Valid Value="zzz" Placeholder="Search nodes…"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ClosedTree = """
        <behavior-search .closed .size-md .variant-default> 0,0 0×0
          <textbox .behavior-search-query .empty .size-md .variant-default> 0,0 0×0
            <field-placeholder> 0,0 0×0 "Search nodes…"
            <field-text> 0,0 0×0
          <scroll-view .behavior-search-list .size-md .variant-default> 0,0 0×0
            <scroll-content> 0,0 0×0
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Selector"
                <search-category> 0,0 0×0 "Composites"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Sequence"
                <search-category> 0,0 0×0 "Composites"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Priority"
                <search-category> 0,0 0×0 "Composites"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Random selector"
                <search-category> 0,0 0×0 "Composites"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Parallel"
                <search-category> 0,0 0×0 "Composites"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Wait"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Wait (from a key)"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Finish with"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Set blackboard value"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Clear blackboard value"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Log"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Run subtree"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Run subtree (from a key)"
                <search-category> 0,0 0×0 "Tasks"
              <behavior-search-row> 0,0 0×0
                <search-label> 0,0 0×0 "Run utility set"
                <search-category> 0,0 0×0 "Tasks"
            <scrollbar .size-md .variant-default .vertical> 0,0 0×0
            <scrollbar .horizontal .size-md .variant-default> 0,0 0×0
        """;

    const string ClosedFlags = """
        <textbox .behavior-search-query .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Search nodes…"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;
}
