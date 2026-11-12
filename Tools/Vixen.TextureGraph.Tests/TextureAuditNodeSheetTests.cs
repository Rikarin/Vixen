// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>A contact sheet of Patina #170, #174, #176 and #179's nodes, for a person to look at.</summary>
/// <remarks>
///     <c>VIXEN_NODE_PICTURES</c> names a folder; each subject is written there as its own PNG, all of them as
///     <c>audit-sheet.png</c>, and each tileable one again as <c>… tiled.png</c>, two by two, so a seam is a
///     line a person can see. Without the variable the case does nothing — the oracles are in the family
///     tests, and this is the picture those tests are not.
/// </remarks>
public class TextureAuditNodeSheetTests(ITestOutputHelper output) {
    const int Side = 256;

    /// <summary>Red runs across and green down, blue a slow diagonal: a stand-in position bake of a curved sheet.</summary>
    static readonly byte[] Position = Picture(
        Side,
        Side,
        static (x, y) => (x / (float)(Side - 1), y / (float)(Side - 1),
            0.5f + 0.25f * MathF.Sin((x + y) / (float)Side * MathF.PI), 1f)
    );

    /// <summary>Islands: a rectangle of its own proportions in each cell of an eight-by-eight grid.</summary>
    static readonly Wire Islands = new(
        "Mask",
        Grey(
            Side,
            Side,
            static (x, y) => {
                var cell = x / 32 * 7 + y / 32 * 13;
                var (w, h) = (8 + cell * 5 % 20, 8 + cell * 11 % 20);
                var (lx, ly) = (x % 32, y % 32);

                return lx >= 2 && lx < 2 + w && ly >= 2 && ly < 2 + h ? 1f : 0f;
            }
        ),
        true
    );

    /// <summary>Some round-ish blobs in a field, for a height or a mask.</summary>
    static readonly byte[] Blobs = Grey(
        Side,
        Side,
        static (x, y) => {
            return Math.Max(
                Math.Max(Bump(70, 80, 40), Bump(180, 70, 28)),
                Math.Max(Bump(120, 180, 50), Bump(220, 200, 20))
            );

            float Bump(float cx, float cy, float r) {
                var d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

                return Math.Clamp((r - d) / 6f, 0f, 1f);
            }
        }
    );

    static readonly Wire Disc = new(
        "Pattern",
        Grey(
            Side,
            Side,
            static (x, y) => Math.Clamp(
                Side * 0.45f
                - MathF.Sqrt((x - Side / 2f) * (x - Side / 2f) + (y - Side / 2f) * (y - Side / 2f)),
                0f,
                1f
            )
        )
    );

    static readonly Wire Square = new("Pattern", Solid(Side, Side, 1f, 1f, 1f));

    static readonly Wire Triangle = new(
        "Pattern",
        Grey(Side, Side, static (x, y) => Math.Abs(x - Side / 2f) < y * 0.5f ? 1f : 0f)
    );

    static readonly byte[] Rainbow = Picture(
        Side,
        Side,
        static (x, y) => (x / (float)Side, y / (float)Side, 1f - x / (float)Side, 1f)
    );

    static readonly byte[] TiltRight = Solid(Side, Side, 0.8f, 0.5f, 0.9f);

    static readonly byte[] TiltDown = Solid(Side, Side, 0.5f, 0.8f, 0.9f);

    static readonly byte[] Ramp = Grey(Side, Side, static (x, _) => x / (float)(Side - 1));

    static readonly (string Name, string Path, Wire[] Wires, Action<GraphNode>? Numbers, bool Tiles)[] Subjects = [
        ("distance euclidean", "Analysis/Distance", [new("Mask", Blobs, true)],
            static node => node.SetValue("Max Distance", 0.15f), true),
        ("distance manhattan", "Analysis/Distance", [new("Mask", Blobs, true)], static node => {
            node.SetText("Metric", "Manhattan");
            node.SetValue("Max Distance", 0.15f);
        }, true),
        ("distance chebyshev", "Analysis/Distance", [new("Mask", Blobs, true)], static node => {
            node.SetText("Metric", "Chebyshev");
            node.SetValue("Max Distance", 0.15f);
        }, true),
        ("distance colour", "Analysis/Distance Colour", [new("Mask", Blobs, true), new("Source", Rainbow)],
            static node => node.SetValue("Max Distance", 0.1f), true),
        ("distance colour faded", "Analysis/Distance Colour", [new("Mask", Blobs, true), new("Source", Rainbow)],
            static node => {
                node.SetValue("Max Distance", 0.1f);
                node.SetValue("Fade", 1f);
            }, true),
        ("flood colour", "Analysis/Flood Fill", [Islands], static node => node.SetText("Kind", "Colour"), true),
        ("flood gradient", "Analysis/Flood Fill", [Islands], static node => node.SetText("Kind", "Gradient"), true),
        ("flood gradient varied", "Analysis/Flood Fill", [Islands], static node => {
            node.SetText("Kind", "Gradient");
            node.SetValue("Angle Variation", MathF.Tau);
        }, true),
        ("height shadow", "Surface/Height Shadow", [new("Height", Blobs, true)], null, true),
        ("height shadow soft low", "Surface/Height Shadow", [new("Height", Blobs, true)], static node => {
            node.SetValue("Elevation", 0.35f);
            node.SetValue("Softness", 0.15f);
        }, true),
        ("height shadow noise", "Surface/Height Shadow", [new("Height", [], true, "Noise/Clouds")],
            static node => {
                node.SetValue("Height Scale", 0.4f);
                node.SetValue("Elevation", 0.6f);
            }, true),
        ("normal blend ramp", "Surface/Normal Blend",
            [new("Background", TiltRight), new("Foreground", TiltDown), new("Mask", Ramp, true)], null, false),
        ("tile sampler random", "Placement/Tile Sampler", [Disc], static node => {
            node.SetText("Output", "Random");
            node.SetValue("Scale", 0.8f);
            node.SetValue("Scale Jitter", 0.4f);
        }, true),
        ("tile sampler colour", "Placement/Tile Sampler", [Disc], static node => {
            node.SetText("Output", "Colour");
            node.SetValue("Scale", 0.9f);
        }, true),
        ("tile sampler uv", "Placement/Tile Sampler", [Square], static node => {
            node.SetText("Output", "Uv");
            node.SetValue("Scale", 0.9f);
            node.SetValue("Rotation Jitter", 0.6f);
        }, true),
        ("tile sampler running bond", "Placement/Tile Sampler", [Square], static node => {
            node.SetText("Output", "Index");
            node.SetValue("Grid X", 4f);
            node.SetValue("Grid Y", 8f);
            node.SetValue("Scale", 0.92f);
            node.SetValue("Row Offset", 0.5f);
        }, true),
        ("splatter ring follow", "Placement/Splatter", [Triangle], static node => {
            node.SetText("Distribution", "Ring");
            node.SetValue("Count", 12f);
            node.SetValue("Scale", 0.1f);
            node.SetValue("Follow Ring", 1f);
        }, true),
        ("splatter 2000 colour", "Placement/Splatter", [Disc], static node => {
            node.SetText("Output", "Colour");
            node.SetValue("Count", 2000f);
            node.SetValue("Scale", 0.04f);
            node.SetValue("Scale Jitter", 0.5f);
        }, true),
        ("volume gradient noise", "Source/Volume Noise", [new("Position", Position)], null, false),
        ("volume simplex ridged", "Source/Volume Noise", [new("Position", Position)], static node => {
            node.SetText("Basis", "Simplex");
            node.SetText("Profile", "Ridged");
            node.SetValue("Octaves", 5f);
        }, false),
        ("volume worley", "Source/Volume Noise", [new("Position", Position)], static node => {
            node.SetText("Basis", "Worley");
            node.SetValue("Scale", 8f);
            node.SetValue("Octaves", 1f);
        }, false),
        ("volume value", "Source/Volume Noise", [new("Position", Position)], static node => {
            node.SetText("Basis", "Value");
            node.SetValue("Scale", 6f);
        }, false),
        ("volume mask box", "Source/Volume Mask", [new("Position", Position)], null, false),
        ("volume mask ellipsoid", "Source/Volume Mask", [new("Position", Position)], static node => {
            node.SetText("Shape", "Ellipsoid");
            node.SetValue("Size X", 0.7f);
        }, false),
        ("volume gradient", "Source/Volume Gradient", [new("Position", Position)], static node => {
            node.SetValue("Start X", 0f);
            node.SetValue("End X", 1f);
        }, false)
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
            var (name, path, wires, numbers, tiles) = Subjects[at];
            var picture = Bake(device, path, Side, Side, wires, numbers, tiling: tiles);
            var bytes = new byte[Side * Side * 4];
            var tiled = new byte[Side * Side * 4 * 4];

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    for (var lane = 0; lane < 4; lane++) {
                        var value = (byte)Math.Clamp((int)MathF.Round(picture.At(x, y, lane) * 255f), 0, 255);
                        var opaque = lane == 3 ? (byte)255 : value;
                        var row = at / Across * Side + y;
                        var column = at % Across * Side + x;

                        bytes[(y * Side + x) * 4 + lane] = value;
                        sheet[(row * Across * Side + column) * 4 + lane] = opaque;

                        foreach (var (ox, oy) in ((int, int)[])[(0, 0), (Side, 0), (0, Side), (Side, Side)]) {
                            tiled[((y + oy) * 2 * Side + x + ox) * 4 + lane] = opaque;
                        }
                    }
                }
            }

            PngCodec.Save(Path.Combine(folder, $"{at:D2} {name}.png"), new(Side, Side, bytes));

            if (tiles) {
                PngCodec.Save(Path.Combine(folder, $"{at:D2} {name} tiled.png"), new(2 * Side, 2 * Side, tiled));
            }
        }

        PngCodec.Save(Path.Combine(folder, "audit-sheet.png"), new(Across * Side, rows * Side, sheet));
    }
}
