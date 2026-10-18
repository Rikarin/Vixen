// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>
///     A state box near the state map's right edge is as wide as the same box at its left (#1447).
/// </summary>
/// <remarks>
///     <see cref="StateMapView" /> places each box by <c>left</c> and <c>top</c> alone. Since #1447 an
///     auto-width absolute box shrinks to the room right of its <c>left</c>, as CSS 2 §10.3.7 says, so
///     a box near the edge folded its name onto several lines — a node on a canvas changing shape with
///     where it was dragged, and its arrows with it. <c>state-box</c> says <c>white-space: nowrap</c>:
///     a box is as wide as its name wherever it is, and the map's <c>overflow: hidden</c> clips it.
/// </remarks>
public class StateBoxWidthTests {
    const string Name = "Locomotion blend while crouched";

    [Fact]
    public void A_box_at_the_right_edge_is_as_wide_and_as_tall_as_one_at_the_left() {
        using var harness = new ViewHarness();

        // A real face: with none, every name measures zero wide and nothing can wrap.
        var face = FontFace.Load(File.ReadAllBytes(TypefacePath()), name: "OpenSans");
        harness.Ui.Document.Fonts.Register(face.Name, face);
        harness.Ui.Document.Fonts.Default = face;

        var document = new AnimationGraphDocument(
            harness.Project.Project,
            AssetId.Empty,
            harness.Project.Write("Assets/Hero.vxanimgraph", string.Empty)
        );

        var layer = document.Layer(0)!;
        document.AddState(layer, Name, 10f, 40f);
        document.AddState(layer, Name + " ", 330f, 120f);

        var map = harness.Ui.Document.Root.Add<StateMapView>();
        map.SetStyle("width", "400px");
        map.SetStyle("flex-grow", "0");
        map.SetStyle("height", "300px");
        map.Graph = document;
        map.Refresh();
        harness.Ui.Frame();

        var boxes = map.Children.Where(static child => child.Tag == "state-box" && !child.HasClass("parked")).ToList();
        var left = boxes.Single(box => box.Children[0].Text == Name).Bounds;
        var right = boxes.Single(box => box.Children[0].Text == Name + " ").Bounds;

        Assert.True(right.X - map.Bounds.X + left.Width > 400f, $"the premise: the box at {right.X - map.Bounds.X} has room for {left.Width}");
        Assert.Equal(left.Width, right.Width, 1f);
        Assert.Equal(left.Height, right.Height, 0.5f);
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
