// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The chrome, as a picture, in both themes.</summary>
/// <remarks>
///     <para>
///         <b>A theme is a claim about what something looks like</b>, and the tests beside this one
///         cannot hold it to that: they ask which panel is open and which command ran, which stays
///         true through any palette at all. What broke the inspector — an element nothing styled
///         laying its children out sideways, a field the colour of the panel behind it — was
///         invisible to every assertion in the suite and obvious in the first screenshot.
///     </para>
///     <para>
///         ⚠ <b>A whole shell rather than a control at a time.</b> The thing being judged is a
///         relationship: whether the toolbar sits on the workspace, whether a panel reads as a
///         surface, whether the accent appears often enough to mean something. None of that is
///         visible one control at a time, which is exactly what <c>ControlVisualTests</c> is for
///         and why this is somewhere else.
///     </para>
/// </remarks>
public class EditorChromeVisualTests {
    [Theory]
    [InlineData(ThemeMode.Dark, "editor-chrome-dark")]
    [InlineData(ThemeMode.Light, "editor-chrome-light")]
    public void The_shell_in_both_themes(ThemeMode mode, string name) {
        using var fixture = new ChromeFixture(mode);
        fixture.Test.Screenshot(name);
    }

    /// <summary>
    ///     The one surface that is meant to look like it is in front of the window rather than in
    ///     it, which is a claim only a picture can check.
    /// </summary>
    [Fact]
    public void The_palette_over_the_shell() {
        using var fixture = new ChromeFixture(ThemeMode.Dark);

        fixture.Shell.Palette.OpenPalette();
        fixture.Shell.Palette.Field.Value = "layout";
        fixture.Shell.Palette.Refresh();
        fixture.Test.Frames(2);

        fixture.Test.Screenshot("editor-chrome-palette");
    }

    /// <summary>The transport hovered, which is the only way to check that a hover keeps its hue.</summary>
    /// <remarks>
    ///     ⚠ <b>The generic toolbar hover is a neutral grey.</b> Applied to a coloured button it reads
    ///     as the colour draining out of it at the moment the pointer arrives — the one instant it
    ///     most needs to look live — and no assertion about which command ran would ever notice.
    ///     Stop is hovered because it is the button that is <i>not</i> filled, so the picture carries
    ///     both cases: a green fill beside a red wash.
    /// </remarks>
    [Fact]
    public void The_transport_hovered() {
        using var fixture = new ChromeFixture(ThemeMode.Dark);

        fixture.Test.Get(".transport-stop").Hover();
        fixture.Test.Frames(2);

        fixture.Test.Screenshot("editor-chrome-transport-hover");
    }

    /// <summary>A shell with something in every panel, rendered through the visual harness.</summary>
    /// <remarks>
    ///     ⚠ <b>The shell owns its document and the harness has to be given that one.</b>
    ///     <c>UiTest.Create</c> makes its own, and a screenshot of it would be a picture of an empty
    ///     document while the shell laid itself out somewhere else entirely.
    /// </remarks>
    sealed class ChromeFixture : IDisposable {
        public ChromeFixture(ThemeMode mode, float width = 1100f, float height = 680f) {
            Shell = new EditorShell(width, height, mode);
            Font(Shell.Document);

            Shell.RegisterPanel("hierarchy", Title("Hierarchy"), Hierarchy);
            Shell.RegisterPanel("project", Title("Project"), Project);
            Shell.RegisterPanel("scene", Title("Scene"), Scene);
            Shell.RegisterPanel("inspector", Title("Inspector"), Inspector);
            Shell.RegisterPanel("console", Title("Console"), Console);

            Shell.RegisterLayout(
                "Default",
                Title("Default"),
                () => LayoutPresets.Standard(["hierarchy", "project"], ["scene"], ["inspector"], ["console"])
            );

            Shell.Workspace.Reset();
            Shell.Status = "SampleProject";

            // ⚠ The strip is described after the layout, because a section is built from ids and a
            // group whose commands do not exist yet comes out empty. The three modes are a
            // segmented control and the dropdown carries a chevron — neither is visible to an
            // assertion about which panel is open, which is what this file exists for.
            Modes();

            Transport();

            Shell.Toolbar.Show(
                new ToolbarButton("view.palette"),
                new ToolbarSeparator(),
                new ToolbarGroup("test.translate", "test.rotate", "test.scale"),
                new ToolbarButton("view.toggle-theme"),
                new ToolbarSeparator(),
                new ToolbarGroup("test.play", "test.pause", "test.step", "test.stop"),
                new ToolbarSeparator(),
                new ToolbarDropdown(Title("Layout"), "layout", "view.layout.Default", null, "view.reset-layout")
            );

            // The selection count and the frame time only exist once the shell has ticked, which is
            // also the only way the mean is anything but zero.
            Shell.SelectionCount = () => 1;

            Test = UiTest.Adopt(Shell.Document);

            Shell.Tick(TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));
            Test.Frames(2);
        }

        public EditorShell Shell { get; }

        /// <summary>The ring the console draws from.</summary>
        /// <remarks>
        ///     ⚠ <b>On a fixed clock, because the console draws the timestamp.</b> A golden
        ///     screenshot of a wall clock differs from itself every run, which is a suite that fails
        ///     at random and is therefore the suite nobody trusts.
        /// </remarks>
        readonly RingBufferSink logs = new(256) {
            TimeProvider = new FixedClock(new DateTimeOffset(2026, 3, 14, 9, 26, 53, TimeSpan.Zero))
        };

        public UiTest Test { get; }

        /// <summary>
        ///     ⚠ The shell and no more. The harness was handed the shell's document rather than one
        ///     of its own, and both types dispose the document they hold — see
        ///     <c>UiTest.Adopt</c>'s remarks, which say so for exactly this reason.
        /// </summary>
        public void Dispose() {
            Shell.Dispose();
            logs.Dispose();
        }

        /// <summary>A clock that does not move.</summary>
        sealed class FixedClock(DateTimeOffset now) : TimeProvider {
            public override DateTimeOffset GetUtcNow() => now;
        }

        /// <summary>The transport, mid-play, which is the state the colour is for.</summary>
        /// <remarks>
        ///     ⚠ <b>Playing, deliberately.</b> The picture worth checking is the filled one: whether
        ///     a white glyph on a saturated green reads in both themes, and whether the two buttons
        ///     that are merely coloured still read beside one that is filled. A stopped transport is
        ///     four muted glyphs and says nothing about any of that.
        /// </remarks>
        void Transport() {
            Play("test.play", "Play", EditorIcons.Play, "transport-play", on: true);
            Play("test.pause", "Pause", EditorIcons.Pause, "transport-pause", on: false);
            Play("test.step", "Step Frame", EditorIcons.Step, "transport-step", on: null);
            Play("test.stop", "Stop", EditorIcons.Stop, "transport-stop", on: null);
        }

        void Play(string id, string title, PathBuilder icon, string className, bool? on) =>
            Shell.Commands.Add(
                new EditorCommand(id, Title(title), () => { }) {
                    Icon = icon,
                    ClassName = className,
                    Checked = on is { } state ? () => state : null
                }
            );

        static StringId Title(string text) => new("test." + text, text);

        /// <summary>Three commands that are one choice, so the segmented control has members.</summary>
        void Modes() {
            var current = "translate";

            foreach (var mode in new[] { "translate", "rotate", "scale" }) {
                var chosen = mode;

                Shell.Commands.Add(
                    new EditorCommand("test." + mode, Title(char.ToUpperInvariant(mode[0]) + mode[1..]), () => current = chosen) {
                        Checked = () => current == chosen,
                        RadioGroup = "gizmo"
                    }
                );
            }
        }

        static void Hierarchy(DockPanel panel) {
            var tree = panel.Add<TreeView>();

            var root = tree.Root.Add("Scene Root", "root");
            var ground = root.Add("Ground", "ground");

            ground.Add("Crate", "crate");
            ground.Add("Barrel", "barrel");
            root.Add("Directional Light", "light");
            root.Add("Main Camera", "camera");

            tree.Refresh();
            tree.Expand(root);
            tree.Expand(ground);
            tree.Select(ground.Children[0]);
        }

        static void Project(DockPanel panel) {
            var list = panel.Add<UiElement>("panel");

            foreach (var entry in new[] { "Materials", "Meshes", "Scenes", "Textures" }) {
                list.Add<TextBlock>().Text = entry;
            }
        }

        static void Scene(DockPanel panel) => panel.Add<ViewportControl>();

        /// <summary>
        ///     The rows a property grid builds, built by hand.
        /// </summary>
        /// <remarks>
        ///     ⚠ <c>PropertyGrid.Inspect</c> reads a generated descriptor, and a fixture type in a
        ///     test assembly has none — so a real grid here draws its search box and no rows at all,
        ///     which is a picture of nothing. The tags are the grid's own, so the styling under test
        ///     is the styling a grid gets.
        /// </remarks>
        static void Inspector(DockPanel panel) {
            var body = panel.Add<UiElement>("property-grid");
            body.Add<SearchBox>().Placeholder = "Search";

            var rows = body.Add<UiElement>("property-body");

            Row(rows, "Name").Add<TextBox>().Value = "Crate";
            Row(rows, "Mass").Add<NumericInput>().Value = "12.5";
            Row(rows, "Material").Add<Select>().Placeholder = "Wood";
            Row(rows, "Casts Shadow").Add<CheckBox>().IsChecked = true;
        }

        static UiElement Row(UiElement into, string label) {
            var row = into.Add<UiElement>("property-row");
            row.Add<UiElement>("property-label").Text = label;

            return row.Add<UiElement>("property-editor");
        }

        /// <summary>The real console over a sink with a plausible session in it.</summary>
        /// <remarks>
        ///     ⚠ <b>The actual panel, not two lines of text pretending to be one.</b> The console is
        ///     where doc 20's "nothing they find is a toy" bar is most easily failed — a level
        ///     badge that is unreadable against the strip, a message column the category has pushed
        ///     off the panel — and none of that is visible to an assertion about how many rows there
        ///     are.
        /// </remarks>
        void Console(DockPanel panel) {
            var view = panel.Add<ConsoleView>();

            // ⚠ The model first, then the lines. It starts at the sink's current end — a console
            // opened an hour into a session does not replay the hour — so logging before it exists
            // is logging into a console that will never show it.
            view.Show(new ConsoleModel(logs));

            Say(LogLevel.Information, "Vixen.Editor", "Loaded Main.vxscene — 6 entities");
            Say(LogLevel.Information, "Vixen.Editor.Assets.Content.ContentPipeline", "Imported 24, 3 unchanged");
            Say(LogLevel.Warning, "Vixen.Editor.Assets", "crate_albedo.png has no mipmaps");
            Say(LogLevel.Error, "Vixen.Editor", "Could not save the scene — the disk is full");
            Say(LogLevel.Information, "Vixen.Editor", "Content build finished in 1.4 s");

            view.Tick();
        }

        void Say(LogLevel level, string category, string message) =>
            logs.CreateLogger(category).Log(level, default, message, null, static (state, _) => state);

        static void Font(UiDocument document) {
            foreach (var path in Candidates()) {
                if (!File.Exists(path)) {
                    continue;
                }

                try {
                    var face = FontFace.Load(File.ReadAllBytes(path), name: Path.GetFileNameWithoutExtension(path));

                    document.Fonts.Register(face.Name, face);
                    document.Fonts.Default = face;

                    return;
                } catch (InvalidDataException) {
                    // The next one. The list exists because no single path is reliable.
                }
            }
        }

        /// <summary>
        ///     ⚠ <b>Borrowed from the operating system, for the reason <c>Vixen.Editor.App</c>'s own
        ///     font loader gives</b>: the repository has no Latin UI font to commit, and the faces
        ///     under <c>Vixen.Ui.Text.Tests/Fonts</c> are the Unicode Consortium's shaping fixtures.
        ///     A machine with none of these renders every label at zero width — the boxes are still
        ///     the boxes, so the picture is still worth comparing, which is why this is a search
        ///     rather than a requirement.
        /// </summary>
        static IEnumerable<string> Candidates() => [
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/Library/Fonts/Arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\arial.ttf"
        ];
    }

}
