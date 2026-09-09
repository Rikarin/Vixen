// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>What the host writes, against what the shaders say they read.</summary>
/// <remarks>
///     <para>
///         <b>Every one of these is a number that appears in two places and is checked by nothing
///         else.</b> The modules come from <c>Shaders/Ui.rvn</c> through Raven, and where Raven puts
///         a vertex attribute or a push-constant member is Raven's decision — <c>StreamPlan</c>
///         locates a stage's parameters after its streams, and <c>ReflectionBuilder</c> lays a push
///         block out std430 from offset zero. <c>UiRenderer</c> writes bytes at fixed offsets and
///         binds attributes at fixed locations. Nothing joins the two but this.
///     </para>
///     <para>
///         ⚠ <b>Neither kind of mismatch is a validation error, which is why they are worth a test.</b>
///         A wrong attribute location leaves that attribute bound to nothing and the stage reading
///         whatever the driver left there — an interface drawn from uninitialised memory, on one
///         driver. A wrong push offset is a blur whose sigma is the viewport scale.
///     </para>
///     <para>
///         ⚠ <b>The offsets caught a real one on the way in.</b> Ported from GLSL, the three
///         compositing stages had said <c>layout(offset = 16)</c>; Raven cannot, so each carries
///         sixteen bytes of <c>reserved</c> instead. That is what these assert — and it is the sort
///         of thing that is obvious for a week and then is not.
///     </para>
///     <para>
///         Read out of the committed <c>.reflect.json</c> rather than out of the generated
///         <c>*Keys</c> constants, deliberately: the constants are generated <i>from</i> these files,
///         so asserting against them would be asserting that a generator is a generator.
///     </para>
/// </remarks>
public class ShaderReflectionTests {
    /// <summary>Where the modules and their reflection live, relative to the repository root.</summary>
    const string Shaders = "Platform/Vixen.Ui.Desktop/Shaders";

    /// <summary>The four vertex attributes, in the order <c>UiVertex</c> declares them.</summary>
    /// <remarks>
    ///     3 to 6 rather than 0 to 3, because <c>Ui.rvn</c> declares three streams and a stage's own
    ///     parameters come after them. A stream added to it moves all four, which is exactly why
    ///     `UiShaderLibrary` reads them rather than writing them down — and why this test asserts the
    ///     *relationship* by naming the numbers that are live today.
    /// </remarks>
    [Theory]
    [InlineData("position", 3)]
    [InlineData("texcoord", 4)]
    [InlineData("vertexColour", 5)]
    [InlineData("vertexShape", 6)]
    public void TheVertexAttributesAreWhereTheHostBindsThem(string name, int location) {
        var inputs = Reflection("UiVertex").GetProperty("VertexInputs");

        foreach (var input in inputs.EnumerateArray()) {
            if (input.GetProperty("Name").GetString() == name) {
                Assert.Equal(location, input.GetProperty("Location").GetInt32());
                return;
            }
        }

        Assert.Fail($"UiVertex declares no attribute called '{name}'.");
    }

    /// <summary>The fragment stages' push constants start at 16, where <c>UiRenderer</c> writes them.</summary>
    /// <remarks>
    ///     ⚠ <b>The sixteen bytes below each of these is the vertex stage's projection.</b> A Vulkan
    ///     push-constant block is shared by every stage of a pipeline and <c>UiRenderer</c> writes the
    ///     fragment half at offset 16 for all three of these — so a stage whose first real member sat
    ///     at zero would read the projection as its own data. Raven emits a block from offset zero and
    ///     has no <c>layout(offset =)</c>, so each of the three declares a <c>reserved: float4</c>
    ///     first and this is what says the trick still works.
    /// </remarks>
    [Theory]
    [InlineData("UiBlur", "kernel", 16)]
    [InlineData("UiColour", "red", 16)]
    [InlineData("UiColour", "green", 32)]
    [InlineData("UiColour", "blue", 48)]
    [InlineData("UiMask", "red", 16)]
    [InlineData("UiMask", "green", 32)]
    [InlineData("UiMask", "blue", 48)]
    [InlineData("UiMask", "list", 64)]
    public void ThePushConstantsAreWhereTheHostWritesThem(string shader, string member, int offset) {
        foreach (var block in Reflection(shader).GetProperty("PushConstants").EnumerateArray()) {
            foreach (var declared in block.GetProperty("Members").EnumerateArray()) {
                if (declared.GetProperty("Name").GetString() == member) {
                    Assert.Equal(offset, declared.GetProperty("Offset").GetInt32());
                    return;
                }
            }
        }

        Assert.Fail($"{shader} declares no push constant called '{member}'.");
    }

    /// <summary>And the whole block fits the 128 bytes every Vulkan implementation guarantees.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>UiMask</c> is the widest: sixteen reserved, a colour matrix at forty-eight, a mask
    ///         reference at sixteen. The number is a floor that was reached rather than a budget that
    ///         was chosen — see <c>UiRenderer</c>'s constructor — so the next thing to want a push
    ///         constant here fails this rather than one device somewhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That last sentence read as "the block is at the ceiling" in four audits of
    ///         <c>Rikarin/Vixen#229</c>, and it does not say that.</b> The widest block is eighty of
    ///         the guaranteed hundred and twenty-eight. What is at the ceiling is a <i>mask list</i>,
    ///         which is what <c>MaskEntry</c>'s remark in <c>Ui.rvn</c> is about: an entry is
    ///         sixty-four bytes, so one of them plus the matrix plus the reserved sixteen is exactly
    ///         128 and a second will not fit. <see cref="ThereIsRoomForARoundedBackdropBox" /> is the
    ///         half that says the other thing out loud.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("UiVertex")]
    [InlineData("UiBlur")]
    [InlineData("UiColour")]
    [InlineData("UiMask")]
    public void ThePushConstantBlockFitsTheGuaranteedSize(string shader) {
        foreach (var block in Reflection(shader).GetProperty("PushConstants").EnumerateArray()) {
            var size = block.GetProperty("Offset").GetInt32() + block.GetProperty("Size").GetInt32();

            Assert.True(size <= 128, $"{shader}'s push block ends at {size}, past the guaranteed 128.");
        }
    }

    /// <summary>The composite stages leave room for a rounded backdrop's box, which is #229's channel.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A pin on HEADROOM rather than on a size, and it exists because the absence of one
    ///         cost four audits the wrong answer.</b> <c>Rikarin/Vixen#229</c>'s remaining divergence
    ///         is that <c>UiRenderer</c> draws a rounded group's backdrop with square corners, and
    ///         closing it means telling a composite fragment where the rounded box is. Four audits
    ///         priced that as a fourth <c>MaskEntry</c> shape — whose real cost is routing every
    ///         rounded backdrop through the mask pipeline — on the sentence "the push constants are
    ///         full". They are not: the widest composite block is <c>UiMask</c>'s eighty bytes of the
    ///         guaranteed hundred and twenty-eight, and a box as a centre and a half plus a
    ///         uniform-or-zero radius is two <c>float4</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is a claim about the future, so it is asserted rather than written down.</b>
    ///         The day something spends that headroom, #229's cheapest channel really does become the
    ///         mask-pipeline one and every note pointing at this paragraph is wrong — and nothing else
    ///         in the tree could see it happen, because a fuller block still passes
    ///         <see cref="ThePushConstantBlockFitsTheGuaranteedSize" />.
    ///     </para>
    ///     <para>
    ///         ⚠ The count is asserted with the size. <c>UiImage</c> declares no push block at all, so
    ///         three of the four composite stages produce one — and a run that resolved none would
    ///         report a widest of zero, which is infinite headroom and a green test saying nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ThereIsRoomForARoundedBackdropBox() {
        // One `float4` for the box as a centre and a half, one for the radius — uniform or zero by
        // `DrawCommand.Radius`'s rule, so three of its four lanes are spare.
        const int BackdropBox = 32;
        const int Guaranteed = 128;

        var widest = 0;
        var blocks = 0;

        foreach (var shader in new[] { "UiImage", "UiBlur", "UiColour", "UiMask" }) {
            foreach (var block in Reflection(shader).GetProperty("PushConstants").EnumerateArray()) {
                widest = Math.Max(widest, block.GetProperty("Offset").GetInt32() + block.GetProperty("Size").GetInt32());
                blocks++;
            }
        }

        Assert.Equal(3, blocks);

        Assert.True(
            Guaranteed - widest >= BackdropBox,
            $"the widest composite push block is {widest} of {Guaranteed}, leaving {Guaranteed - widest} "
            + $"bytes where a rounded backdrop's box needs {BackdropBox}. Two readings, and they want "
            + "opposite things. If Rikarin/Vixen#229 has LANDED, the box is what is in there and this "
            + "assertion should be rewritten to name it — the headroom was spent on the thing it was "
            + "being held for. If it has not, something else took the channel and the expensive answer "
            + "those audits gave (a fourth MaskEntry shape, routing every rounded backdrop through the "
            + "mask pipeline) is the right one again: re-read UiLayer.BackdropRadius, "
            + "docs/guide/ui/compositing.md and docs/plan/43, all three of which now say it is not."
        );
    }

    /// <summary>Every stage the host loads is committed, which a glob cannot say on its own.</summary>
    /// <remarks>
    ///     ⚠ <c>UiShaderLibrary</c> finds its modules by suffix over the assembly's manifest and
    ///     throws for one that is missing — at run time, on the first frame that draws. A stage added
    ///     to <c>Ui.rvn</c> and never committed, or one renamed on one side only, fails here instead.
    /// </remarks>
    [Theory]
    [InlineData("UiVertex.vert.spv")]
    [InlineData("UiBox.frag.spv")]
    [InlineData("UiText.frag.spv")]
    [InlineData("UiSolid.frag.spv")]
    [InlineData("UiImage.frag.spv")]
    [InlineData("UiBlur.frag.spv")]
    [InlineData("UiColour.frag.spv")]
    [InlineData("UiMask.frag.spv")]
    public void EveryStageTheHostLoadsIsEmbedded(string module) {
        var assembly = typeof(UiShaderLibrary).Assembly;

        Assert.Contains(
            assembly.GetManifestResourceNames(),
            entry => entry.EndsWith(module, StringComparison.Ordinal)
        );
    }

    static JsonElement Reflection(string shader) {
        var path = Path.Combine(RepositoryRoot(), Shaders, $"{shader}.reflect.json");

        Assert.True(File.Exists(path), $"{Shaders}/{shader}.reflect.json is missing; run ./build.sh CheckShaders --update-shaders.");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
