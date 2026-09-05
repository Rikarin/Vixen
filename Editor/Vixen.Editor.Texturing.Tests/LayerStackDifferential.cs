// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The stack doc 48 exit criterion 6 is measured on, and how two plans are compared.</summary>
/// <remarks>
///     <para>
///         <b>One stack, built here rather than in each suite</b>, because the device test and the
///         device-free test have to be measuring the same thing for the second to be worth anything
///         when the first skips.
///     </para>
///     <para>
///         ⚠ <b>One layer is a <em>texture</em> fill, and that is the difference between a
///         differential and a tautology.</b> Every other source a stack can express in this build is
///         a constant, and a blur, a levels and a blend of constants are all constants — so a
///         picture-level comparison of two flat images would be green whatever the two plans had
///         done with their op order. The checkerboard a device test uploads for
///         <see cref="CheckerAsset" /> is what puts spatial variation under the blur, the group and
///         the anchor, so "the bytes are equal" is a claim about pictures rather than about two
///         numbers. The device-free comparison does not need it and does not care.
///     </para>
/// </remarks>
static class LayerStackDifferential {
    /// <summary>The image reference the texture-fill layer names, which a device test supplies.</summary>
    public const string CheckerAsset = "Assets/Checker.png";

    /// <summary>Three channels, seven layers, a group, a mask and an anchor.</summary>
    /// <returns>The stack.</returns>
    /// <remarks>
    ///     ⚠ <b>Deliberately not the starter stack.</b> A one-layer stack explodes into three nodes
    ///     and every op index is its own, so an explosion that reordered or inserted anything would
    ///     still compile to the same plan — the differential would be green for the wrong reason.
    ///     What is here has a nested group, a filter that reads the composite beneath it, an anchor
    ///     that is an edge rather than a chain link, and channels that different layers write, which
    ///     is the shape where an ordering mistake shows.
    /// </remarks>
    public static LayerStackAsset Stack() =>
        new() {
            Name = "Differential",
            BaseWidth = 64,
            BaseHeight = 64,
            Seed = 0x5EEDu,
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0.25f, 0.5f, 0.75f, 1f] },
                        new() { Usage = "roughness", Default = [0.4f, 0.4f, 0.4f, 1f] },
                        new() { Usage = "height", Default = [0.5f, 0.5f, 0.5f, 1f] }
                    ],
                    Layers = [
                        new() {
                            Id = "base",
                            Name = "Base",
                            Kind = LayerKind.Fill,
                            Blend = LayerBlendMode.Copy,
                            Values = {
                                ["baseColor"] = [0.8f, 0.2f, 0.1f, 1f],
                                ["roughness"] = [0.7f, 0.7f, 0.7f, 1f],
                                ["height"] = [0.3f, 0.3f, 0.3f, 1f]
                            }
                        },
                        new() {
                            Id = "checker",
                            Name = "Checker plate",
                            Kind = LayerKind.Fill,
                            Fill = LayerFillSource.Texture,
                            Blend = LayerBlendMode.Multiply,
                            Opacity = 0.9f,
                            Channels = ["baseColor"],
                            Textures = { ["baseColor"] = CheckerAsset }
                        },
                        new() {
                            Id = "rough-only",
                            Name = "Rougher",
                            Kind = LayerKind.Fill,
                            Blend = LayerBlendMode.Multiply,
                            Opacity = 0.6f,
                            Channels = ["roughness"],
                            Values = { ["roughness"] = [0.5f, 0.5f, 0.5f, 1f] }
                        },
                        new() {
                            Id = "soften",
                            Name = "Soften",
                            Kind = LayerKind.Filter,
                            Filter = LayerFilterKind.Blur,
                            Blend = LayerBlendMode.Copy,
                            Opacity = 0.8f,
                            Settings = { ["Radius"] = [3f] },
                            Mask = new() { Source = LayerMaskSource.Constant, Value = 0.4f }
                        },
                        new() {
                            Id = "grime",
                            Name = "Grime",
                            Kind = LayerKind.Group,
                            Blend = LayerBlendMode.Screen,
                            Opacity = 0.5f,
                            Children = [
                                new() {
                                    Id = "grime-fill",
                                    Name = "Grime colour",
                                    Kind = LayerKind.Fill,
                                    Blend = LayerBlendMode.Overlay,
                                    Values = { ["baseColor"] = [0.1f, 0.09f, 0.08f, 0.75f] }
                                },
                                new() {
                                    Id = "grime-levels",
                                    Name = "Grime levels",
                                    Kind = LayerKind.Filter,
                                    Filter = LayerFilterKind.Levels,
                                    Blend = LayerBlendMode.Copy,
                                    Settings = { ["Gamma"] = [1.4f], ["Input White"] = [0.9f] }
                                }
                            ]
                        },
                        new() {
                            Id = "anchored",
                            Name = "Anchored to base",
                            Kind = LayerKind.Fill,
                            Blend = LayerBlendMode.Add,
                            Opacity = 0.35f,
                            Values = { ["baseColor"] = [0.2f, 0.2f, 0.2f, 1f] },
                            Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "base" }
                        },
                        new() {
                            Id = "hsl",
                            Name = "Warm it up",
                            Kind = LayerKind.Filter,
                            Filter = LayerFilterKind.Hsl,
                            Blend = LayerBlendMode.Copy,
                            Channels = ["baseColor"],
                            Settings = { ["Hue"] = [0.05f], ["Saturation"] = [1.2f] }
                        }
                    ]
                }
            ]
        };

    /// <summary>The compiled stack, and the compiled explosion of it, taken off a YAML round trip.</summary>
    /// <param name="stack">The stack.</param>
    /// <returns>The two compilations, in that order.</returns>
    /// <remarks>
    ///     ⚠ <b>The round trip is where the difference between the two paths lives.</b> Both halves
    ///     go through <c>LayerStackGraph.Build</c> and <c>TextureGraphCompiler</c> — doc 48 § D1's
    ///     whole point — so what is left to be wrong is the explosion's decoration and the file: a
    ///     setting the writer drops, a value the reader will not bind, a node order the loader does
    ///     not preserve, or a comment that somehow reached the compiler. Every one of those changes
    ///     an op, and <c>TexturePlan.SeedFor</c> mixes the op's <em>index</em> into its seed, so an
    ///     insertion anywhere moves every procedural op after it.
    /// </remarks>
    public static (LayerStackCompilation Stack, LayerStackCompilation Exploded) Both(LayerStackAsset stack) {
        var set = stack.Sets[0];
        var direct = LayerStackCompiler.Compile(stack, set);

        Assert.Empty(direct.Problems);
        Assert.Empty(direct.Diagnostics);
        Assert.NotNull(direct.Plan);

        var exploded = LayerStackExplode.Explode(stack, set);

        Assert.Empty(exploded.Problems);

        var yaml = LayerStackExplode.ToYaml(exploded);
        var reloaded = LayerStackExplode.Read(yaml, out var diagnostics);

        Assert.Empty(diagnostics);

        var second = LayerStackCompiler.Compile(stack, new LayerStackBuild(reloaded, [], []));

        Assert.Empty(second.Diagnostics);
        Assert.NotNull(second.Plan);

        return (direct, second);
    }

    /// <summary>Everything about two plans that would make them bake different pictures.</summary>
    /// <param name="expected">The stack's plan.</param>
    /// <param name="actual">The explosion's plan.</param>
    /// <remarks>
    ///     ⚠ <b>Field by field rather than <c>Assert.Equal</c>, because a <c>TextureOp</c> is a
    ///     <c>record</c> whose lists are <c>ImmutableArray</c>.</b> An <c>ImmutableArray</c>'s
    ///     equality is its <em>underlying array's reference</em>, so the compiler-generated
    ///     <c>Equals</c> on two structurally identical ops from two compilations returns false — a
    ///     comparison that is red for a reason that has nothing to do with the pictures, which is
    ///     worse than one that is green for the wrong reason.
    /// </remarks>
    public static void AssertSamePlan(TexturePlan expected, TexturePlan actual) {
        Assert.Equal(expected.BaseWidth, actual.BaseWidth);
        Assert.Equal(expected.BaseHeight, actual.BaseHeight);
        Assert.Equal(expected.BakeLevelOffset, actual.BakeLevelOffset);
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(Describe(expected), Describe(actual));
    }

    /// <summary>A plan as text, so a mismatch reads as a diff rather than as "false is not true".</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>One line per image, one per op, one for the outputs.</returns>
    public static string Describe(TexturePlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        StringBuilder text = new();

        for (var index = 0; index < plan.Images.Length; index++) {
            var image = plan.Images[index];

            text.Append(CultureInfo.InvariantCulture, $"image {index}: {image.Format} ");
            text.Append(CultureInfo.InvariantCulture, $"level {image.LevelOffset} external {image.External}\n");
        }

        for (var index = 0; index < plan.Ops.Length; index++) {
            var op = plan.Ops[index];

            text.Append(CultureInfo.InvariantCulture, $"op {index}: {op.Kernel} -> {op.Output}");
            text.Append(CultureInfo.InvariantCulture, $" reads [{string.Join(", ", op.Inputs)}]");
            text.Append(" with [");

            for (var parameter = 0; parameter < op.Parameters.Length; parameter++) {
                var value = op.Parameters[parameter];

                if (parameter > 0) {
                    text.Append(", ");
                }

                text.Append(CultureInfo.InvariantCulture, $"{value.Name}={value.Value:R}{value.Unit}");
            }

            text.Append(CultureInfo.InvariantCulture, $"] cpu {op.Cpu?.GetType().Name ?? "none"}");
            text.Append(CultureInfo.InvariantCulture, $" extent {op.EmittedForExtent?.ToString(CultureInfo.InvariantCulture) ?? "any"}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"outputs: [{string.Join(", ", plan.Outputs)}]\n");
        text.Append(CultureInfo.InvariantCulture, $"kernels: [{string.Join(", ", plan.Kernels.Keys.Order(StringComparer.Ordinal))}]\n");

        return text.ToString();
    }

    /// <summary>Which image carries a usage, in one compilation.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="usage">The map's usage.</param>
    /// <returns>The image index.</returns>
    public static int ImageOf(LayerStackCompilation compilation, string usage) {
        ArgumentNullException.ThrowIfNull(compilation);

        foreach (var output in compilation.Outputs) {
            if (string.Equals(output.Usage, usage, StringComparison.Ordinal)) {
                return output.Image;
            }
        }

        Assert.Fail($"nothing in this compilation produces '{usage}'");

        return -1;
    }
}
