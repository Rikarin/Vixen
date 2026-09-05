// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>Every kernel in <c>Shaders/</c>, through the real Raven front end, with no device.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is what replaces <c>CheckShaders</c>' editor half for this assembly, and it is a
///         stronger instrument than the thing it replaces.</b> That gate proves a committed
///         <c>.spv</c> matches the <c>.rvn</c> beside it; there is no committed module here, because a
///         storage image's format is part of its type and a kernel is compiled once per format a plan
///         can ask it to write. So what is asserted instead is the thing the gate was a proxy for:
///         every kernel compiles, in every variant, every time this suite runs — on a machine with no
///         GPU and no Vulkan loader, where a device test skips.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day it does not run.</b> If
///         <see cref="TextureKernels.Names" /> came back empty — the embedded-resource item removed
///         from the csproj, the prefix renamed, the folder moved — every <c>[Theory]</c> below would
///         have no cases and the suite would pass having compiled nothing.
///         <see cref="Every_kernel_the_folder_holds_is_embedded" /> is the guard: it counts the
///         <c>.rvn</c> files on disk and requires the embedded set to match them exactly.
///     </para>
/// </remarks>
public class TextureKernelTests {
    /// <summary>The <c>Shaders/</c> directory in the source tree, found from the test binary.</summary>
    static string ShaderRoot =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "Vixen.Editor.TextureGraph",
                "Shaders"
            )
        );

    public static TheoryData<string, TextureFormat> Variants {
        get {
            TheoryData<string, TextureFormat> data = [];

            foreach (var kernel in TextureKernels.Names) {
                foreach (var format in TextureFormats.Storable) {
                    data.Add(kernel, format);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> Kernels => [.. TextureKernels.Names];

    /// <summary>The embedded set is the folder's set — the check that keeps every theory below honest.</summary>
    [Fact]
    public void Every_kernel_the_folder_holds_is_embedded() {
        Assert.True(Directory.Exists(ShaderRoot), $"The kernel sources are not where this test looks: {ShaderRoot}");

        var onDisk = Directory
            .EnumerateFiles(ShaderRoot, "*.rvn")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(onDisk);
        Assert.Equal(onDisk, TextureKernels.Names.ToArray());
    }

    /// <summary>A kernel's file name is the shader it declares, because an op names the shader.</summary>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_kernel_declares_the_shader_its_file_is_named_after(string kernel) {
        var declared = Regex
            .Matches(TextureKernels.Source(kernel), @"^shader\s+(\w+)", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal([kernel], declared);
    }

    /// <summary>Every kernel compiles, in every format a plan can make it write.</summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void A_kernel_compiles_in_every_storable_format(string kernel, TextureFormat format) {
        var data = Compile(kernel, format);

        Assert.Equal(kernel, data.ShaderName);

        var compute = Assert.Single(data.Stages, stage => stage.Stage == ShaderStage.Compute);

        Assert.NotEmpty(compute.Bytecode);

        // A texture-graph kernel is one compute entry point. A vertex or fragment stage in one would
        // mean the dispatcher had a pipeline it could not create and no reason to know why.
        Assert.Single(data.Stages);
    }

    /// <summary>
    ///     The variant actually carries the format asked for, which is the whole of the rewrite.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without this, <see cref="A_kernel_compiles_in_every_storable_format" /> would pass on a
    ///     rewrite that did nothing.</b> Every variant would be the committed <c>rgba16f</c> source,
    ///     which compiles perfectly — and the failure would surface as an image written through the
    ///     wrong format decoration on a device, which is undefined behaviour and not an error.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Variants))]
    public void A_variant_declares_the_format_it_was_built_for(string kernel, TextureFormat format) {
        var source = TextureKernels.Variant(kernel, format);

        Assert.Contains($"[Format(\"{TextureFormats.RavenName(format)}\")]", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"\[Format\("));
    }

    /// <summary>
    ///     The bindings a kernel declares are the shape the evaluator binds: a block, its textures in
    ///     declaration order, and exactly one storage image.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The evaluator binds an op's inputs positionally</b>, over the sampled textures sorted
    ///     by binding number — <c>BindingPlan</c> puts the uniform block at 0 and then the textures in
    ///     declaration order. Nothing in the C# would notice if a kernel declared its foreground before
    ///     its background; the picture would simply be composited the wrong way round, which is a
    ///     plausible picture. So the order is written down here.
    /// </remarks>
    [Theory]
    [InlineData("Blend", "background", "foreground")]
    [InlineData("Blur", "source")]
    [InlineData("Levels", "source")]
    public void A_kernel_declares_its_inputs_in_the_order_the_evaluator_binds_them(string kernel, params string[] inputs) {
        var data = Compile(kernel, TextureFormat.Rgba8);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);

        Assert.Single(
            data.Bindings,
            binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.StorageTexture }
        );
    }

    /// <summary>
    ///     Every kernel dispatches at the size the evaluator assumes, which is the one number the
    ///     reflection does not carry.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A kernel declaring <c>[ComputeShader(16, 16, 1)]</c> against a host dispatching in
    ///     eights leaves three quarters of every image unwritten</b> — and an unwritten storage image
    ///     is whatever the allocator left, which on a fresh device is often zero and therefore looks
    ///     like a kernel that ran and produced black.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_kernel_declares_the_workgroup_size_the_evaluator_dispatches(string kernel) {
        Assert.Contains(
            $"[ComputeShader({TexturePlanEvaluator.GroupSize}, {TexturePlanEvaluator.GroupSize}, 1)]",
            TextureKernels.Source(kernel),
            StringComparison.Ordinal
        );
    }

    /// <summary>A name no kernel has is an argument failure rather than a null reference later.</summary>
    /// <remarks>
    ///     ⚠ <b>This used to name <c>Warp</c>, and doc 48 § 4.4 then shipped a kernel called that</b>
    ///     — so the test went red for the best possible reason and the name had to become one no
    ///     catalogue entry can ever take. A node name is not a safe stand-in for "does not exist"
    ///     when forty-four of them are still to be written.
    /// </remarks>
    [Fact]
    public void An_unknown_kernel_is_refused_by_name() {
        var failure = Assert.Throws<ArgumentException>(
            () => TextureKernels.Variant("NoKernelIsCalledThis", TextureFormat.Rgba8)
        );

        Assert.Contains("NoKernelIsCalledThis", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A format no kernel can write is refused where the variant is asked for.</summary>
    [Theory]
    [InlineData(TextureFormat.R8)]
    [InlineData(TextureFormat.Rg8)]
    public void A_format_no_storage_image_has_is_refused(TextureFormat format) {
        Assert.False(TextureFormats.IsStorable(format));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureKernels.Variant("Blur", format));
    }

    static EffectData Compile(string kernel, TextureFormat format) {
        var name = TextureKernels.VariantName(kernel, format);
        var source = TextureKernels.Variant(kernel, format);
        var data = RavenEffectCompiler.FromSources([(name, source)]).TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
