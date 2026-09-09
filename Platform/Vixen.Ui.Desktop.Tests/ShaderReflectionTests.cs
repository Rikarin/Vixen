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
    ///         reference at sixteen, a backdrop box at thirty-two. The number is a floor that was
    ///         reached rather than a budget that was chosen — see <c>UiRenderer</c>'s constructor — so
    ///         the next thing to want a push constant here fails this rather than one device somewhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That last sentence read as "the block is at the ceiling" in four audits of
    ///         <c>Rikarin/Vixen#229</c>, and it does not say that.</b> The widest block is a hundred
    ///         and twelve of the guaranteed hundred and twenty-eight, and was eighty before the box
    ///         those audits said there was no room for landed in it. What is at the ceiling is a
    ///         <i>mask list</i>, which is what <c>MaskEntry</c>'s remark in <c>Ui.rvn</c> is about: an
    ///         entry is sixty-four bytes, so one of them plus the matrix plus the reserved sixteen is
    ///         exactly 128 and a second will not fit.
    ///         <see cref="TheBackdropBoxIsWhereTheHostPushesIt" /> is the half that says where the
    ///         sixteen bytes that are left begin.
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

    /// <summary>The backdrop box is at the offset the host writes it to, in both stages that read it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This was <c>ThereIsRoomForARoundedBackdropBox</c>, a pin on HEADROOM, and the
    ///         headroom has been spent on exactly the thing it was held for — which is what its own
    ///         failure message asked the next reader to do.</b> Four audits of <c>Rikarin/Vixen#229</c>
    ///         priced a rounded backdrop's channel as a fourth <c>MaskEntry</c> shape, on the sentence
    ///         "the push constants are full". They were not, and the box is now two <c>float4</c> at
    ///         the end of <c>UiColour</c>'s block and of <c>UiMask</c>'s.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An offset and not a size, because a size cannot be wrong in a way that draws
    ///         anything.</b> <c>UiRenderer.SubmitDraw</c> lays these bytes out by hand — one
    ///         <c>Span&lt;float&gt;</c> per branch, pushed at 16 — so the wire is an agreement between
    ///         a literal in C# and a declaration order in Raven, and nothing but this compares them. A
    ///         box written where the mask list is read is not a validation error and not a blank
    ///         frame: it is a group that fades out around a border box built from an entry index.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>UiImage</c> declaring no block is part of the claim.</b> It draws every
    ///         viewport, thumbnail and video frame in the interface, and the whole reason a rounded
    ///         backdrop composites through <c>colourPipeline</c> is to keep it that way — so a block
    ///         appearing there means the cost this design refuses has been taken silently.
    ///     </para>
    /// </remarks>
    [Theory]
    // `reserved` 16, then the colour matrix's three rows — the box is straight after them.
    [InlineData("UiColour", 64, 80, 96)]
    // The same, plus the mask list's own `float4` at 64.
    [InlineData("UiMask", 80, 96, 112)]
    public void TheBackdropBoxIsWhereTheHostPushesIt(string shader, int box, int corner, int size) {
        var block = Assert.Single(Reflection(shader).GetProperty("PushConstants").EnumerateArray().ToArray());

        Assert.Equal(0, block.GetProperty("Offset").GetInt32());
        Assert.Equal(size, block.GetProperty("Size").GetInt32());

        var members = block.GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(member => member.GetProperty("Name").GetString()!, member => member.GetProperty("Offset").GetInt32());

        // ⚠ The census, and it is not decoration: `ToDictionary` over an empty array succeeds, and
        // every assertion below is a lookup that would then fail for the wrong reason — "the box
        // moved" where the truth is "the reflection resolved nothing".
        Assert.Equal(size / 16, members.Count);

        Assert.Equal(box, members["box"]);
        Assert.Equal(corner, members["corner"]);
    }

    /// <summary>And the stage that must not have one still does not.</summary>
    /// <remarks>
    ///     ⚠ The other half of <see cref="TheBackdropBoxIsWhereTheHostPushesIt" />'s third paragraph,
    ///     asserted separately because a <c>[Theory]</c> row that expects nothing has no offsets to
    ///     name. A block here would mean every image draw in the interface had started writing a push
    ///     range, which is a cost paid once per viewport per frame and visible in nothing.
    /// </remarks>
    [Fact]
    public void TheImageStageStillDeclaresNoPushBlock() =>
        Assert.Empty(Reflection("UiImage").GetProperty("PushConstants").EnumerateArray().ToArray());

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
