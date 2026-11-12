// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #111: anchor points are named, survive the file, and say who reads them.</summary>
public class AnchorReferenceTests {
    /// <summary>Every place a reference can be written comes back from the file as it went in.</summary>
    [Fact]
    public void Every_reference_survives_the_file() {
        var read = LayerStackYaml.Read(LayerStackYaml.Write(Stack())).Sets[0];

        Assert.Equal("Wear", read.Layers[0].AnchorPoint);
        Assert.Equal(new() { Anchor = "Wear" }, read.Layers[1].Anchor);
        Assert.Equal(LayerFillSource.Anchor, read.Layers[1].Fill);
        Assert.Equal(new() { Anchor = "Wear", Channel = "height" }, read.Layers[1].Mask.Anchor);
        Assert.Equal(
            new() { Anchor = "Wear", Channel = "roughness" },
            read.Layers[2].Inputs["Foreground"].Anchor
        );
        Assert.Equal(
            new() { Anchor = "Wear", Channel = "height" },
            read.Layers[2].Mask.Layers[0].GeneratorInputs["Input"]
        );
        Assert.Equal(
            new() { Anchor = "Wear", Channel = "height" },
            read.Layers[2].Mask.Effects[0].Inputs["Foreground"].Anchor
        );
        Assert.True(read.Layers[3].Flattened);
        Assert.Equal("stack.Body.g.vxpaint", read.Layers[3].Paint);
    }

    /// <summary>The list of who reads what names every reference the compiler reads, and no other.</summary>
    [Fact]
    public void The_uses_of_an_anchor_point_are_every_reference_to_it() {
        var uses = LayerStackEdit.AnchorUses(Stack().Sets[0]);

        Assert.Equal(
            [
                ("fill", "fill", string.Empty), ("fill", "mask", "height"), ("filter", "input Foreground", "roughness"),
                ("filter", "mask entry 1 generator Input", "height"),
                ("filter", "mask effect Colour/Blend Foreground", "height")
            ],
            uses.Select(static use => (use.Layer, use.Where, use.Reference.Channel)).ToArray()
        );
        Assert.Equal("a", LayerStackEdit.AnchorPoints(Stack().Sets[0])["Wear"]);
    }

    /// <summary>Renaming rewrites every reference, and leaves the layers it was handed as they were.</summary>
    [Fact]
    public void Renaming_an_anchor_point_rewrites_its_references_and_nothing_else() {
        var set = Stack().Sets[0];
        var before = LayerStackYaml.Write(Stack());

        var moved = LayerStackEdit.Reanchored(set.Layers, "Wear", "Edge");

        Assert.All(
            LayerStackEdit.AnchorUses(set with { Layers = moved }),
            static use => Assert.Equal("Edge", use.Reference.Anchor)
        );
        Assert.Equal(before, LayerStackYaml.Write(Stack() with { Sets = [set] }));
        Assert.Equal("Wear 2", LayerStackEdit.FreeAnchor(set, "Wear"));
        Assert.Equal("Edge", LayerStackEdit.FreeAnchor(set, "Edge"));
    }

    static LayerStackAsset Stack() =>
        new() {
            Name = "stack",
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [
                        new() { Usage = "baseColor" }, new() { Usage = "roughness" }, new() { Usage = "height" }
                    ],
                    Layers = [
                        new() { Id = "a", AnchorPoint = "Wear", Values = { ["baseColor"] = [0.2f, 0.2f, 0.2f, 1f] } },
                        new() {
                            Id = "fill",
                            Fill = LayerFillSource.Anchor,
                            Anchor = new() { Anchor = "Wear" },
                            Mask = new() {
                                Source = LayerMaskSource.Anchor, Anchor = new() { Anchor = "Wear", Channel = "height" }
                            }
                        },
                        new() {
                            Id = "filter",
                            Kind = LayerKind.Filter,
                            FilterNode = "Colour/Blend",
                            Inputs = {
                                ["Foreground"] = LayerInputAsset.Of(new() { Anchor = "Wear", Channel = "roughness" })
                            },
                            Mask = new() {
                                Layers = [
                                    new() {
                                        Source = LayerMaskSource.Generator,
                                        Generator = "Filters/Blur",
                                        GeneratorInputs = { ["Input"] = new() { Anchor = "Wear", Channel = "height" } }
                                    }
                                ],
                                Effects = [
                                    new() {
                                        Node = "Colour/Blend",
                                        Inputs = {
                                            ["Foreground"] = LayerInputAsset.Of(
                                                new() { Anchor = "Wear", Channel = "height" }
                                            )
                                        }
                                    }
                                ]
                            }
                        },
                        new() {
                            Id = "g",
                            Kind = LayerKind.Group,
                            Paint = "stack.Body.g.vxpaint",
                            Children = [new() { Id = "c" }]
                        }
                    ]
                }
            ]
        };
}
