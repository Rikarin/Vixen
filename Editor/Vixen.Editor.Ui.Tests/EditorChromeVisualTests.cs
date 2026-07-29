// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
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

            Shell.Toolbar.Show("view.palette", "view.theme", null, "view.layout.Default");

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

            Test = UiTest.Adopt(Shell.Document);
            Test.Frames(2);
        }

        public EditorShell Shell { get; }

        public UiTest Test { get; }

        /// <summary>
        ///     ⚠ The shell and no more. The harness was handed the shell's document rather than one
        ///     of its own, and both types dispose the document they hold — see
        ///     <c>UiTest.Adopt</c>'s remarks, which say so for exactly this reason.
        /// </summary>
        public void Dispose() => Shell.Dispose();

        static StringId Title(string text) => new("test." + text, text);

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

        static void Console(DockPanel panel) {
            var log = panel.Add<UiElement>("panel");

            log.Add<TextBlock>().Text = "Loaded Main.vscene — 6 entities";
            log.Add<TextBlock>().Text = "Content build finished in 1.4 s";
        }

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
