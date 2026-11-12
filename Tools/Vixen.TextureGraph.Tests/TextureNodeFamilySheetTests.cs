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
public class TextureNodeFamilySheetTests(ITestOutputHelper output) {
    const int Side = 256;

    static readonly byte[] Ramp = Grey(
        Side,
        Side,
        static (x, y) => (y * Side + x) / (float)(Side * Side) * ((y * Side + x) / (float)(Side * Side))
    );

    /// <summary>Five discs of different sizes and a bar, the mask the distance and flood subjects measure.</summary>
    static readonly byte[] Discs = Grey(
        Side,
        Side,
        static (x, y) => {
            (float X, float Y, float R)[] discs =
                [(60, 60, 40), (180, 70, 28), (90, 180, 50), (200, 190, 20), (128, 128, 12)];

            foreach (var (cx, cy, r) in discs) {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < r * r) {
                    return 1f;
                }
            }

            return x is > 150 and < 240 && y is > 120 and < 140 ? 1f : 0f;
        }
    );

    /// <summary>A coloured board of eight by eight squares, for something to remap.</summary>
    static readonly byte[] Board = Picture(
        Side,
        Side,
        static (x, y) => {
            var odd = (x / 32 + y / 32) % 2 == 1;

            return odd ? (x / (float)Side, 0.3f, y / (float)Side, 1f) : (0.9f, 0.9f, 0.2f, 1f);
        }
    );

    /// <summary>A UV field turned about the centre, red across and green down.</summary>
    static readonly byte[] Swirled = Picture(
        Side,
        Side,
        static (x, y) => {
            var (u, v) = ((x + 0.5f) / Side - 0.5f, (y + 0.5f) / Side - 0.5f);
            var turn = 2f * MathF.Max(0f, 0.5f - MathF.Sqrt(u * u + v * v));
            var (c, s) = (MathF.Cos(turn), MathF.Sin(turn));

            return (u * c - v * s + 0.5f, u * s + v * c + 0.5f, 0.5f, 1f);
        }
    );

    /// <summary>A profile of three steps rising left to right.</summary>
    static readonly byte[] Steps = Grey(Side, Side, static (x, _) => MathF.Floor(x * 4f / Side) / 3f);

    /// <summary>A Tile Sampler's Index output at its default eight by eight grid.</summary>
    static readonly byte[] Numbered = Grey(Side, Side, static (x, y) => (x / 32 + y / 32 * 8 + 0.5f) / 64f);

    /// <summary>A mid grey to glow over.</summary>
    static readonly byte[] Dim = Grey(Side, Side, static (_, _) => 0.15f);

    /// <summary>A light grey to cast a shadow on.</summary>
    static readonly byte[] Light = Grey(Side, Side, static (_, _) => 0.85f);

    static readonly (string Name, string Path, Wire[] Wires, Action<GraphNode>? Numbers)[] Subjects = [
        ("noise gaussian", "Source/Noise", [], static node => {
            node.SetText("Basis", "Gaussian");
            node.SetValue("Octaves", 5f);
        }),
        ("noise ridged", "Source/Noise", [], static node => {
            node.SetText("Basis", "Gradient");
            node.SetText("Profile", "Ridged");
            node.SetValue("Octaves", 5f);
            node.SetValue("Scale", 4f);
        }),
        ("noise billow", "Source/Noise", [], static node => {
            node.SetText("Basis", "Gradient");
            node.SetText("Profile", "Billow");
            node.SetValue("Octaves", 5f);
            node.SetValue("Scale", 4f);
        }),
        ("noise stretched", "Source/Noise", [], static node => {
            node.SetText("Basis", "Value");
            node.SetValue("Scale", 4f);
            node.SetValue("Scale Y", 16f);
            node.SetValue("Octaves", 4f);
        }),
        ("noise min octave", "Source/Noise", [], static node => {
            node.SetText("Basis", "Gradient");
            node.SetValue("Min Octave", 2f);
            node.SetValue("Octaves", 3f);
        }),
        ("worley octaves", "Source/Noise", [], static node => {
            node.SetText("Basis", "Worley");
            node.SetValue("Scale", 4f);
            node.SetValue("Octaves", 4f);
        }),
        ("polygon 5", "Source/Shape", [], static node => {
            node.SetText("Kind", "Polygon");
            node.SetValue("Sides", 5f);
            node.SetValue("Scale", 0.8f);
        }),
        ("polygon 8", "Source/Shape", [], static node => {
            node.SetText("Kind", "Polygon");
            node.SetValue("Sides", 8f);
            node.SetValue("Scale", 0.8f);
            node.SetValue("Falloff", 0.3f);
        }),
        ("star", "Source/Shape", [], static node => {
            node.SetText("Kind", "Star");
            node.SetValue("Sides", 5f);
            node.SetValue("Inner", 0.45f);
            node.SetValue("Scale", 0.9f);
        }),
        ("ring", "Source/Shape", [], static node => {
            node.SetText("Kind", "Ring");
            node.SetValue("Inner", 0.6f);
            node.SetValue("Scale", 0.8f);
            node.SetValue("Falloff", 0.05f);
        }),
        ("capsule", "Source/Shape", [], static node => {
            node.SetText("Kind", "Capsule");
            node.SetValue("Inner", 0.3f);
            node.SetValue("Scale", 0.8f);
            node.SetValue("Rotation", 0.5f);
        }),
        ("crescent", "Source/Shape", [], static node => {
            node.SetText("Kind", "Crescent");
            node.SetValue("Inner", 0.3f);
            node.SetValue("Scale", 0.8f);
        }),
        ("pyramid", "Source/Shape", [], static node => {
            node.SetText("Kind", "Pyramid");
            node.SetValue("Scale", 0.8f);
        }),
        ("hemisphere", "Source/Shape", [], static node => {
            node.SetText("Kind", "Hemisphere");
            node.SetValue("Scale", 0.8f);
        }),
        ("ramp squared", "Colour/Levels", [new("Input", Ramp, true)], null),
        ("histogram", "Analysis/Histogram Render", [new("Input", Ramp, true)], null),
        ("equalised", "Colour/Histogram Equalise", [new("Input", Ramp, true)], null),
        ("clouds histogram", "Analysis/Histogram Render", [new("Input", [], true, "Noise/Clouds")], null),
        ("summed area", "Analysis/Summed Area Table", [new("Input", Ramp, true)], null),
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
        ("varied brick", "Patterns/Varied Brick", [], null),

        // Patina wave 8: #167's blue noise and the rest of the noise row, #168's patterns, #170's maps and
        // #174's distance and flood nodes.
        ("blue noise", "Source/Blue Noise", [], static node => node.SetValue("Scale", 4f)),
        ("blue noise cut", "Colour/Threshold", [new("Input", [], true, "Source/Blue Noise")],
            static node => node.SetValue("Level", 0.9f)),
        ("directional noise", "Noise/Directional Noise", [], static node => node.SetValue("Angle", 0.5f)),
        ("gaussian blobs", "Noise/Gaussian Blobs", [], null),
        ("plasma", "Noise/Plasma", [], null),
        ("caustics", "Noise/Caustics", [], null),
        ("moisture", "Noise/Moisture", [], null),
        ("fur", "Noise/Fur", [], null),
        ("fibres", "Noise/Fibres", [], null),
        ("waveform bars", "Noise/Waveform Bars", [], null),
        ("creased cloth", "Noise/Creased Cloth", [], null),
        ("microscope view", "Noise/Microscope View", [], null),
        ("mesh grid", "Patterns/Mesh Grid", [], null),
        ("fan pavement", "Patterns/Fan Pavement", [], null),
        ("arc pavement", "Patterns/Arc Pavement", [], null),
        ("triangulated", "Patterns/Triangulated Surface", [], null),
        ("directional distance", "Analysis/Directional Distance", [new("Mask", Discs, true)],
            static node => node.SetValue("Angle", 0.6f)),
        ("height extrude", "Surface/Height Extrude", [new("Height", Discs, true)], static node => {
            node.SetValue("Length", 0.15f);
            node.SetValue("Falloff", 0.6f);
        }),
        ("uv remap", "Space/UV Remap", [new("Input", Board, false), new("UV", Swirled)], null),
        ("outline", "Adjust/Outline", [new("Mask", Discs, true)], null),
        ("outer glow", "Adjust/Outer Glow", [new("Input", Dim), new("Mask", Discs, true)], null),
        ("cast shadow", "Adjust/Cast Shadow", [new("Input", Light), new("Shape", Discs, true)], null),
        ("bevel profile", "Surface/Bevel Profile", [new("Mask", Discs, true), new("Profile", Steps)], null),
        ("island remap", "Utility/Island Remap", [new("Mask", Discs, true), new("Pattern", Board)], null),
        ("fill from edges", "Utility/Fill from Edges", [new("Mask", Discs, true)], null),
        ("instance range", "Utility/Instance Range", [new("Index", Numbered, true)], null),
        ("atlas pack", "Utility/Atlas Pack",
            [new("First", Discs), new("Second", Board), new("Third", Swirled), new("Fourth", Steps)], null),
        ("sampler maps", "Placement/Tile Sampler", [
            new("Pattern", Discs), new("Pattern 2", Board), new("Vector Map", Swirled), new("Colour Map", Swirled),
            new("Displacement Map", [], Source: "Source/Noise")
        ], static node => node.SetValue("Scale", 0.8f)),
        ("sampler index map", "Placement/Tile Sampler", [
            new("Pattern", Discs), new("Pattern 2", Board), new("Pattern 3", Steps), new("Index Map", Ramp, true)
        ], static node => node.SetValue("Scale", 0.8f))
    ];

    [Fact]
    public void A_contact_sheet_for_a_person() {
        if (Environment.GetEnvironmentVariable("VIXEN_NODE_PICTURES") is not { } folder) {
            return;
        }

        Directory.CreateDirectory(folder);

        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

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
                        var row = at / Across * Side + y;
                        var column = at % Across * Side + x;

                        bytes[(y * Side + x) * 4 + lane] = value;
                        sheet[(row * Across * Side + column) * 4 + lane] = lane == 3 ? (byte)255 : value;
                    }
                }
            }

            PngCodec.Save(Path.Combine(folder, $"{at:D2} {name}.png"), new(Side, Side, bytes));
        }

        PngCodec.Save(Path.Combine(folder, "sheet.png"), new(Across * Side, rows * Side, sheet));
    }
}
