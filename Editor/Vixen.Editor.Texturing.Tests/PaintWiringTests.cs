// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The half of doc 48 § M9 that had no callers: a paint layer and a painted mask reach the plan.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>, and it is this
///         repository's commonest defect rather than a missing feature.</b> Batch 9 built the brush,
///         the stroke, the composite, the seam dilation, the undo command and the <c>.vxpaint</c>
///         with fifty-two tests behind them, and <c>LayerStackGraph</c> refused both layer kinds by
///         name — so every one of those tests exercised a model nothing in the editor could reach.
///     </para>
///     <para>
///         ⚠ <b>What is asserted here is the <em>reference</em> and not the picture.</b> A compilation
///         is pure: it carries the string a host has to resolve, and whether the file behind it reads
///         is <c>PaintPreviewDeviceTests</c>' question. Splitting them that way is what lets this
///         suite run with no device and no file system at all.
///     </para>
/// </remarks>
public class PaintWiringTests {
    /// <summary>A reference survives being written and read back, path and channel both.</summary>
    /// <remarks>
    ///     ⚠ <b>The path holds the awkward characters and the channel does not, which is why the
    ///     channel is first.</b> A round trip over a name with spaces, dots and a folder in it is the
    ///     case a split-from-the-back parser gets wrong.
    /// </remarks>
    [Fact]
    public void A_painted_reference_round_trips_a_path_a_file_system_allows() {
        var reference = PaintReference.Reference("Paint/Hero.Body.rust layer.vxpaint", "baseColor");

        Assert.True(PaintReference.Claims(reference));
        Assert.True(PaintReference.TryParse(reference, out var path, out var usage));
        Assert.Equal("Paint/Hero.Body.rust layer.vxpaint", path);
        Assert.Equal("baseColor", usage);
    }

    /// <summary>An ordinary asset path is not claimed, so it still goes to the asset database.</summary>
    [Fact]
    public void An_imported_pictures_path_is_not_a_painted_reference() {
        Assert.False(PaintReference.Claims("Assets/Rust.png"));
        Assert.False(PaintReference.TryParse("Assets/Rust.png", out _, out _));
    }

    /// <summary>A malformed painted reference is claimed and not parsed, which are two answers.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves matter and only together.</b> A host that asked only
    ///     <see cref="PaintReference.TryParse" /> would fall through to the asset database and tell
    ///     an artist that a file called <c>vxpaint:baseColor</c> is missing — a sentence naming
    ///     nothing they wrote.
    /// </remarks>
    [Fact]
    public void A_painted_reference_with_no_channel_is_claimed_and_refused() {
        Assert.True(PaintReference.Claims("vxpaint:Body.vxpaint"));
        Assert.False(PaintReference.TryParse("vxpaint:Body.vxpaint", out var path, out var usage));
        Assert.Equal("", path);
        Assert.Equal("", usage);
    }

    /// <summary>A paint layer emits one bitmap per channel it writes, each naming its own.</summary>
    /// <remarks>
    ///     ⚠ <b>Per channel, because a <c>.vxpaint</c> is one image per channel.</b> A single
    ///     reference for the layer would make a stack that paints base colour and roughness read the
    ///     same picture into both, which is a plausible-looking wrong answer rather than a failure.
    /// </remarks>
    [Fact]
    public void A_paint_layer_writing_two_channels_names_both_of_them() {
        var stack = Two(new() { Id = "paint", Kind = LayerKind.Paint, Paint = "Hull.paint.vxpaint" });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Empty(compilation.Problems);

        var named = compilation.Externals.Select(external => external.Asset).OrderBy(name => name).ToArray();

        Assert.Equal(
            ["vxpaint:baseColor|Hull.paint.vxpaint", "vxpaint:roughness|Hull.paint.vxpaint"],
            named
        );
    }

    /// <summary>A paint layer restricted to one channel names one, and it is that one.</summary>
    [Fact]
    public void A_paint_layer_restricted_to_a_channel_names_only_it() {
        var stack = Two(new() {
            Id = "paint",
            Kind = LayerKind.Paint,
            Paint = "Hull.paint.vxpaint",
            Channels = ["roughness"]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);
        var external = Assert.Single(compilation.Externals);

        Assert.Equal("vxpaint:roughness|Hull.paint.vxpaint", external.Asset);
    }

    /// <summary>A paint layer nobody has painted on warns and does not stop the stack.</summary>
    /// <remarks>
    ///     ⚠ <b>The distinction a refusal would have destroyed.</b>
    ///     <c>LayerStackCompiler.Compile</c> throws the plan away on <c>HasErrors</c>, so a paint
    ///     layer that refused until its first stroke would black out the preview of every other layer
    ///     in the stack the moment a panel created one — and creating one before painting on it is
    ///     the only order in which it can happen.
    /// </remarks>
    [Fact]
    public void A_paint_layer_with_no_canvas_yet_warns_and_the_rest_of_the_stack_still_compiles() {
        var stack = Two(new() { Id = "paint", Kind = LayerKind.Paint });

        // A fill under it, so a green run means "the others compiled" rather than "nothing did".
        stack.Sets[0].Layers.Insert(0, new() {
            Id = "base",
            Kind = LayerKind.Fill,
            Fill = LayerFillSource.Constant,
            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Empty(compilation.Externals);
        Assert.All(compilation.Problems, problem => Assert.Equal(NodeSeverity.Warning, problem.Severity));
        Assert.Contains(compilation.Problems, problem => problem.Layer == "paint");
    }

    /// <summary>A painted mask compiles to a bitmap over the mask channel of its canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>The usage is <c>mask</c> and not the layer's channel, and that is the one asymmetry
    ///     between the two paint sources.</b> A mask is one image whatever the layer under it writes
    ///     — <c>PaintCanvas</c>' degenerate case — so a mask compiled per channel would name seven
    ///     images in a file that holds one and refuse six of them.
    /// </remarks>
    [Fact]
    public void A_painted_mask_names_the_mask_channel_once_however_many_channels_the_layer_writes() {
        var stack = Two(new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Fill = LayerFillSource.Constant,
            Values = { ["baseColor"] = [1f, 0f, 0f, 1f], ["roughness"] = [0.5f, 0.5f, 0.5f, 1f] },
            Mask = new() { Source = LayerMaskSource.Paint, Paint = "Hull.l.mask.vxpaint" }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Empty(compilation.Problems);

        // Two channels, so two bitmap nodes — and both of them name the same one image in the file.
        Assert.Equal(2, compilation.Externals.Length);
        Assert.All(
            compilation.Externals,
            external => Assert.Equal("vxpaint:mask|Hull.l.mask.vxpaint", external.Asset)
        );
    }

    /// <summary>A painted mask with no canvas warns and leaves the layer unmasked.</summary>
    [Fact]
    public void A_painted_mask_with_no_canvas_yet_warns_rather_than_refusing() {
        var stack = Two(new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Fill = LayerFillSource.Constant,
            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] },
            Mask = new() { Source = LayerMaskSource.Paint }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Empty(compilation.Externals);
        Assert.All(compilation.Problems, problem => Assert.Equal(NodeSeverity.Warning, problem.Severity));
    }

    /// <summary>A stack whose set writes base colour and roughness, over the given layers.</summary>
    static LayerStackAsset Two(LayerAsset layer) =>
        new() {
            Name = "Test",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                        new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] }
                    ],
                    Layers = [layer]
                }
            ]
        };
}
