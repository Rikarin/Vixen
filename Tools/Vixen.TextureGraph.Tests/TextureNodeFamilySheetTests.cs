// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>A contact sheet of Patina #167, #168 and #172's nodes and compounds, for a person to look at.</summary>
/// <remarks>
///     <c>VIXEN_NODE_PICTURES</c> names a folder; each subject is written there as its own PNG and all of them
///     as <c>sheet.png</c>, in the order below. Without the variable the case does nothing — the oracles are in
///     the family tests, and this is the picture those tests are not.
/// </remarks>
public class TextureNodeFamilySheetTests {
    const int Side = 256;

    static readonly byte[] Ramp = Grey(Side, Side, (x, y) => (((y * Side) + x) / (float)(Side * Side)) * (((y * Side) + x) / (float)(Side * Side)));

    static readonly (string Name, string Path, Wire[] Wires, Action<GraphNode>? Numbers)[] Subjects = [
        ("noise gaussian", "Source/Noise", [], node => {
            node.SetText("Basis", "Gaussian");
            node.SetValue("Octaves", 5f);
        }),
        ("noise ridged", "Source/Noise", [], node => {
            node.SetText("Basis", "Gradient");
            node.SetText("Profile", "Ridged");
            node.SetValue("Octaves", 5f);
            node.SetValue("Scale", 4f);
        }),
        ("noise billow", "Source/Noise", [], node => {
            node.SetText("Basis", "Gradient");
            node.SetText("Profile", "Billow");
            node.SetValue("Octaves", 5f);
            node.SetValue("Scale", 4f);
        }),
        ("noise stretched", "Source/Noise", [], node => {
            node.SetText("Basis", "Value");
            node.SetValue("Scale", 4f);
            node.SetValue("Scale Y", 16f);
            node.SetValue("Octaves", 4f);
        }),
        ("noise min octave", "Source/Noise", [], node => {
            node.SetText("Basis", "Gradient");
            node.SetValue("Min Octave", 2f);
            node.SetValue("Octaves", 3f);
        }),
        ("worley octaves", "Source/Noise", [], node => {
            node.SetText("Basis", "Worley");
            node.SetValue("Scale", 4f);
            node.SetValue("Octaves", 4f);
        }),
        ("polygon 5", "Source/Shape", [], node => {
            node.SetText("Kind", "Polygon");
            node.SetValue("Sides", 5f);
            node.SetValue("Scale", 0.8f);
        }),
        ("polygon 8", "Source/Shape", [], node => {
            node.SetText("Kind", "Polygon");
            node.SetValue("Sides", 8f);
            node.SetValue("Scale", 0.8f);
            node.SetValue("Falloff", 0.3f);
        }),
        ("star", "Source/Shape", [], node => {
            node.SetText("Kind", "Star");
            node.SetValue("Sides", 5f);
            node.SetValue("Inner", 0.45f);
            node.SetValue("Scale", 0.9f);
        }),
        ("ring", "Source/Shape", [], node => {
            node.SetText("Kind", "Ring");
            node.SetValue("Inner", 0.6f);
            node.SetValue("Scale", 0.8f);
            node.SetValue("Falloff", 0.05f);
        }),
        ("capsule", "Source/Shape", [], node => {
            node.SetText("Kind", "Capsule");
            node.SetValue("Inner", 0.3f);
            node.SetValue("Scale", 0.8f);
            node.SetValue("Rotation", 0.5f);
        }),
        ("crescent", "Source/Shape", [], node => {
            node.SetText("Kind", "Crescent");
            node.SetValue("Inner", 0.3f);
            node.SetValue("Scale", 0.8f);
        }),
        ("pyramid", "Source/Shape", [], node => {
            node.SetText("Kind", "Pyramid");
            node.SetValue("Scale", 0.8f);
        }),
        ("hemisphere", "Source/Shape", [], node => {
            node.SetText("Kind", "Hemisphere");
            node.SetValue("Scale", 0.8f);
        }),
        ("ramp squared", "Colour/Levels", [new("Input", Ramp, Grey: true)], null),
        ("histogram", "Analysis/Histogram Render", [new("Input", Ramp, Grey: true)], null),
        ("equalised", "Colour/Histogram Equalise", [new("Input", Ramp, Grey: true)], null),
        ("clouds histogram", "Analysis/Histogram Render", [new("Input", [], Grey: true, Source: "Noise/Clouds")], null),
        ("summed area", "Analysis/Summed Area Table", [new("Input", Ramp, Grey: true)], null),
        ("clouds", "Noise/Clouds", [], null),
        ("cell walls", "Noise/Cell Walls", [], null),
        ("fractal cells", "Noise/Fractal Cells", [], null),
        ("streaks", "Noise/Streaks", [], null),
        ("spots", "Noise/Spots", [], null),
        ("liquid", "Noise/Liquid", [], null),
        ("stripes", "Patterns/Stripes", [], null),
        ("weave", "Patterns/Weave", [], null),
        ("hex cells", "Patterns/Hex Cells", [], null),
        ("triangle grid", "Patterns/Triangle Grid", [], null),
        ("brick varied", "Patterns/Brick", [], node => node.SetText("variation", "0.6"))
    ];

    [Fact]
    public void A_contact_sheet_for_a_person() {
        if (Environment.GetEnvironmentVariable("VIXEN_NODE_PICTURES") is not { } folder) {
            return;
        }

        Directory.CreateDirectory(folder);

        using var device = TextureKernelHarness.Open();

        const int Across = 6;
        var rows = (Subjects.Length + Across - 1) / Across;
        var sheet = new byte[Across * Side * rows * Side * 4];

        for (var at = 0; at < Subjects.Length; at++) {
            var (name, path, wires, numbers) = Subjects[at];
            var picture = Bake(device, path, Side, Side, wires, numbers);
            var bytes = new byte[Side * Side * 4];

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    for (var lane = 0; lane < 4; lane++) {
                        var value = (byte)Math.Clamp((int)MathF.Round(picture.At(x, y, lane) * 255f), 0, 255);
                        var row = ((at / Across) * Side) + y;
                        var column = ((at % Across) * Side) + x;

                        bytes[(((y * Side) + x) * 4) + lane] = value;
                        sheet[(((row * Across * Side) + column) * 4) + lane] = lane == 3 ? (byte)255 : value;
                    }
                }
            }

            PngCodec.Save(Path.Combine(folder, $"{at:D2} {name}.png"), new Bitmap(Side, Side, bytes));
        }

        PngCodec.Save(Path.Combine(folder, "sheet.png"), new Bitmap(Across * Side, rows * Side, sheet));
    }
}
