// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;
using Xunit;

// Two assemblies name a compute stage, which is the shape of this whole project: the compiler's
// enum describes a shader it emitted and the RHI's describes a stage a pipeline is created for.
// They mean the same thing and are not the same type, and aliasing is better here than picking one
// and qualifying the other at every use.
using DeviceStage = Vixen.Graphics.ShaderStage;
using RavenStage = Vixen.Raven.Symbols.ShaderStage;

namespace Vixen.Raven.Gpu.Tests;

/// <summary>Compiles a compute kernel against the shipped library and runs it on a device.</summary>
/// <remarks>
///     <para>
///         <b>Against the shipped library, not a copy of it.</b> The point of a numeric gate is that
///         it is a claim about the code that ships; a kernel that pasted the BRDF in would be a gate
///         on the paste. So <c>Raven/Library/Core</c> and the one <c>Shading</c> file under test are
///         parsed off disk and compiled with the kernel — which is also how a real effect reaches
///         them.
///     </para>
///     <para>
///         ⚠ <b>Only the files the kernel needs</b>, and not the whole of <c>Shading</c>. Most of
///         that package declares <c>compose</c> slots, and every declared slot in a compilation has
///         to be bound whether or not anything reaches it — so pulling the package in wholesale
///         would mean filling ten bindings to ask a question about three pure functions.
///     </para>
/// </remarks>
static class ShaderRun {
    /// <summary>Where the shipped library is, relative to the test binary.</summary>
    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Raven", "Library"));

    /// <summary>What one dispatch produced, and the reflection of the shader that produced it.</summary>
    /// <param name="Values">The floats the kernel wrote.</param>
    /// <param name="Reflection">What the compiler said the module's interface is.</param>
    public sealed record Result(float[] Values, RavenReflection Reflection);

    /// <summary>Compiles a kernel, dispatches it, and reads back the floats it wrote.</summary>
    /// <param name="kernel">The Raven source. Must declare one compute shader called <c>Gate</c>.</param>
    /// <param name="imports">Library files to compile with it, as paths under <c>Raven/Library</c>.</param>
    /// <param name="count">How many floats the output buffer holds.</param>
    /// <param name="groups">How many workgroups to dispatch.</param>
    /// <param name="uniforms">Bytes to put in the uniform buffer, or empty for a kernel with none.</param>
    /// <returns>What came back, or <see langword="null" /> when there is no device to run it on.</returns>
    public static Result? Run(
        string kernel,
        string[] imports,
        int count,
        int groups,
        ReadOnlySpan<byte> uniforms = default
    ) {
        VulkanRequirement.Available(VulkanDevice.TryCreate(new(), out var device, out var reason), reason);

        using var owned = device!;
        VulkanDiagnostics.Reset();

        var (binary, reflection) = Compile(kernel, imports);

        var output = owned.CreateBuffer(new(
            count * sizeof(float),
            BufferUsage.Storage | BufferUsage.CopySource,
            MemoryAccess.DeviceLocal,
            "gate.output"
        ));

        var readback = owned.CreateBuffer(new(
            count * sizeof(float),
            BufferUsage.CopyDestination,
            MemoryAccess.HostReadback,
            "gate.readback"
        ));

        var hasUniforms = !uniforms.IsEmpty;

        var constants = hasUniforms
            ? owned.CreateBuffer(new(uniforms.Length, BufferUsage.Uniform, MemoryAccess.HostUpload, "gate.uniforms"))
            : default;

        if (hasUniforms) {
            owned.Write(constants, 0, uniforms);
        }

        // ⚠ Built from the reflection rather than from what this file assumes. That is the whole of
        // the layout gate's method, and it costs nothing to apply it everywhere: a set built by hand
        // would agree with the shader by luck, and the one case where it did not would look like
        // arithmetic going wrong rather than a binding.
        var bindings = reflection.Sets
            .SelectMany(set => set.Bindings)
            .OrderBy(binding => binding.Binding)
            .ToArray();

        var entries = bindings
            .Select(binding => new DescriptorBinding(
                (uint)binding.Binding,
                binding.Type == DescriptorType.UniformBuffer
                    ? DescriptorKind.UniformBuffer
                    : DescriptorKind.StorageBuffer,
                DeviceStage.Compute
            ))
            .ToArray();

        var setLayout = owned.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, entries, "gate.set"));
        var layout = owned.CreatePipelineLayout(new([setLayout], [], "gate.layout"));
        var descriptors = owned.CreateDescriptorSet(setLayout, "gate.set");

        var writes = bindings
            .Select(binding => binding.Type == DescriptorType.UniformBuffer
                ? DescriptorWrite.Uniform((uint)binding.Binding, constants)
                : DescriptorWrite.Storage((uint)binding.Binding, output))
            .ToArray();

        owned.UpdateDescriptorSet(descriptors, writes);

        var module = owned.CreateShader(DeviceStage.Compute, binary, "Gate");
        var pipeline = owned.CreateComputePipeline(new(module, layout, "Gate"));

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Compute, "gate")) {
            list.Barrier(new([new(output, ResourceState.Undefined, ResourceState.ShaderWrite)], []));
            list.BindPipeline(pipeline);
            list.BindDescriptorSet(DescriptorSetSlot.PerFrame, descriptors);
            list.Dispatch(groups);
            list.Barrier(new([new(output, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            list.CopyBuffer(output, 0, readback, 0, count * sizeof(float));
            list.Finish();
            owned.ComputeQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        var bytes = new byte[count * sizeof(float)];

        owned.Read(readback, 0, bytes);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "The dispatch produced validation errors: " + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        var values = new float[count];

        for (var index = 0; index < count; index++) {
            values[index] = BitConverter.ToSingle(bytes, index * sizeof(float));
        }

        owned.Destroy(pipeline);
        owned.Destroy(module);
        owned.Destroy(descriptors);
        owned.Destroy(layout);
        owned.Destroy(setLayout);

        if (hasUniforms) {
            owned.Destroy(constants);
        }

        owned.Destroy(readback);
        owned.Destroy(output);

        return new(values, reflection);
    }

    /// <summary>The whole front half, with every phase's complaints carried into the assertion.</summary>
    /// <remarks>
    ///     Failures are reported with the source attached, because a <c>KeyNotFoundException</c> on
    ///     the entry point's name is a much worse way to learn that binding failed.
    /// </remarks>
    public static (byte[] Binary, RavenReflection Reflection) Compile(string kernel, string[] imports) {
        var trees = imports
            .Select(name => Path.Combine(LibraryRoot, name))
            .Select(path => SyntaxTree.ParseText(File.ReadAllText(path), path: Path.GetFileName(path)))
            .Append(SyntaxTree.ParseText(kernel, path: "Gate.rvn"))
            .ToArray();

        foreach (var tree in trees) {
            Assert.True(tree.Diagnostics.Count == 0, Report("Parsing", tree.Diagnostics, kernel));
        }

        var compilation = Compilation.Create("Gate", trees);
        var semantic = compilation.GetDiagnostics();

        Assert.True(semantic.Count == 0, Report("Binding", semantic, kernel));

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, Report("Lowering", bag.ToArray(), kernel));

        var backend = TargetBackends.Create("spirv");

        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);

        Assert.True(bag.IsEmpty, Report("Generating", bag.ToArray(), kernel));

        var unit = Assert.Single(
            generated,
            candidate => candidate.Stage == RavenStage.Compute && candidate.Name.StartsWith("Gate", StringComparison.Ordinal)
        );

        Assert.NotNull(unit.Binary);

        // ⚠ The reflection is built from the same lowered shader the backend just emitted, not from
        // a second compilation. That is what makes the layout gate a gate at all: if the two came
        // from different runs, a disagreement between them would be an artefact of the test rather
        // than the fault it is looking for.
        var shader = Assert.Single(
            module.Shaders,
            candidate => candidate.Name.StartsWith("Gate", StringComparison.Ordinal)
        );

        return (unit.Binary, ReflectionBuilder.Describe(shader, compilation.UsedPermutationKeys));
    }

    static string Report(string phase, IReadOnlyList<Diagnostic> diagnostics, string source) =>
        $"{phase} the gate's shader failed:\n{string.Join("\n", diagnostics.Select(d => d.ToString()))}\n\n{source}";
}
