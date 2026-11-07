// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A layer's children are effects on its own content, inside its mask.</summary>
public class LayerEffectTests {
    static LayerStackAsset Stack(params LayerAsset[] effects) =>
        new() {
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [new() { Usage = "baseColor", Default = [0.5f, 0.5f, 0.5f, 1f] }],
                    Layers = [
                        new() {
                            Id = "base",
                            Name = "Base",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [0f, 1f, 0f, 1f] },
                            Children = [..effects]
                        }
                    ]
                }
            ]
        };

    static int Ops(LayerStackAsset stack) => LayerStackCompiler.Compile(stack, stack.Sets[0]).Plan!.Ops.Length;

    static LayerAsset Red(bool enabled = true) =>
        new() {
            Id = "red",
            Name = "Red",
            Kind = LayerKind.Fill,
            Enabled = enabled,
            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] }
        };

    [Fact]
    public void An_effect_adds_to_its_layer() {
        Assert.True(Ops(Stack(Red())) > Ops(Stack()));
    }

    [Fact]
    public void A_hidden_effect_costs_nothing() {
        Assert.Equal(Ops(Stack()), Ops(Stack(Red(false))));
    }
}
