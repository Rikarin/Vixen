// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;
using Vixen.Rendering;
using Vixen.Vfx;
using Xunit;
using RavenStage = Vixen.Raven.Symbols.ShaderStage;
using ShaderStage = Vixen.Graphics.ShaderStage;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>
///     One <see cref="VfxSystem" />'s particles, drawn to an offscreen target and read back.
/// </summary>
/// <remarks>
///     <para>
///         <b>The picture the two backends are going to be compared as.</b> The CPU expansion and the
///         device one produce the same thing — quads in world space — and the only comparison that
///         catches a disagreement neither counter nor buffer dump shows is a frame. So the expansion
///         is <see cref="VfxGeometryBuilder" /> for both, and this draws whatever it was handed with
///         the shipped <c>Raven/Library/Vfx/ParticleSprite.rvn</c>, additive, depth off, two-sided,
///         exactly as <c>ParticleSpriteDeviceTests</c> in the golden project does.
///     </para>
///     <para>
///         <b>Built out of the RHI rather than out of <c>RenderSystem</c>, and that is forced.</b> The
///         golden project reaches a frame through <c>EffectSystem</c>, whose only provider that
///         compiles anything lives in <c>Vixen.ShaderCompiler</c> — a tool this project does not
///         reference and should not, because the thing it exists to check is that
///         <c>Vixen.Raven</c> and <c>Vixen.Graphics.Vulkan</c> agree without a bundle in between. So
///         the pipeline layout is built here, from <see cref="ReflectionBuilder" />'s own description
///         of the shader. That is not a simplification of what the engine does: <c>EffectLoader</c>
///         builds the same handles from the same numbers, one translation later.
///     </para>
///     <para>
///         ⚠ <b>The clear is a blue no additive sprite can produce</b>, which is the whole reason it
///         is not black. A pass that ran and drew nothing leaves the clear; a pass that never ran
///         leaves black; a pass that drew leaves orange over blue. Three outcomes, one pixel.
///     </para>
/// </remarks>
static class ParticlePicture {
    /// <summary>The side of the square picture, unless a caller says otherwise.</summary>
    public const int DefaultSide = 128;

    /// <summary>What the pass clears to. See the remarks.</summary>
    public static Color4 Background => new(0f, 0f, 0.25f, 1f);

    /// <summary>
    ///     Compiled once for the whole assembly, because it is the same shader every time.
    /// </summary>
    /// <remarks>
    ///     Parsing, binding, lowering and emitting the library takes appreciably longer than the draw
    ///     does, and none of it depends on the effect being drawn. The modules created from these
    ///     bytes are per-device and are <em>not</em> cached — a handle outliving its device is the
    ///     one thing a cache here could get wrong.
    /// </remarks>
    static readonly Lazy<Compiled> Sprite = new(CompileSprite, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Renders one effect's particles and hands back the RGBA8 pixels.</summary>
    /// <param name="device">The device to draw on.</param>
    /// <param name="effect">The system whose live particles are expanded. Not stepped here.</param>
    /// <param name="view">
    ///     The camera. Its <see cref="RenderCamera.ViewProjection" /> is what the vertices are
    ///     projected through, and the billboard basis is derived from the same record — see the
    ///     other overload for why those two must not be supplied separately.
    /// </param>
    /// <param name="side">The picture's width and height in pixels.</param>
    /// <returns><c>side × side</c> RGBA8 pixels, row-major from the top left.</returns>
    public static byte[] Render(VulkanDevice device, VfxSystem effect, RenderCamera view, int side = DefaultSide) =>
        Render(device, effect, view, out _, side);

    /// <summary>
    ///     The same, reporting how many particles the expansion actually produced.
    /// </summary>
    /// <param name="particles">
    ///     How many particles were expanded. ⚠ Worth asserting on: a clear-coloured picture is what
    ///     an effect with nothing alive in it produces <em>and</em> what a broken pipeline produces,
    ///     and this is the only thing that tells those two apart from outside.
    /// </param>
    /// <param name="edgeSharpness">
    ///     How fast the disc falls off. Low flattens the quad towards a hard-edged circle, which is
    ///     what a test asserting about the middle pixel wants — the shipped default of 1.6
    ///     concentrates the brightness into a point and makes the assertion about the sampling.
    /// </param>
    /// <param name="tint">Multiplied into every particle's colour. White when omitted.</param>
    /// <param name="emissive">
    ///     How much light the sprite emits. One means "as bright as the vertex colour said", which is
    ///     what an assertion in 0..1 wants; a photometric frame needs thousands.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>One camera, not two.</b> The expansion turns each quad to face a
    ///     <see cref="VfxCamera" /> and the vertex stage projects it through a matrix, and a harness
    ///     that took both separately would let them disagree — which draws a one-pixel sliver per
    ///     particle and looks exactly like an effect that emitted nothing. Both come off
    ///     <paramref name="view" /> here, the same way <c>ParticleRenderFeature.Camera</c> derives
    ///     its basis from the view it was given.
    /// </remarks>
    public static byte[] Render(
        VulkanDevice device,
        VfxSystem effect,
        RenderCamera view,
        out int particles,
        int side = DefaultSide,
        float edgeSharpness = 0.25f,
        Vector4? tint = null,
        float emissive = 1f
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentOutOfRangeException.ThrowIfLessThan(side, 1);

        var compiled = Sprite.Value;

        // The same call `ParticleRenderFeature.Quads` makes, against the same builder, from the same
        // basis — this is the code both backends are supposed to be measured against.
        var expanded = new ParticleVertex[Math.Max(effect.Count, 1) * VfxGeometryBuilder.VerticesPerParticle];
        var camera = VfxCamera.Looking(view.Position, view.Forward, view.Up);

        particles = new VfxGeometryBuilder().Build(effect, camera, expanded);

        return Draw(device, compiled, expanded.AsSpan(0, particles * VfxGeometryBuilder.VerticesPerParticle), new() {
            Side = side,
            ViewProjection = view.ViewProjection,
            Tint = tint ?? new Vector4(1f, 1f, 1f, 1f),
            Emissive = emissive,
            EdgeSharpness = edgeSharpness
        });
    }

    /// <summary>One pixel of a returned picture, as channels in 0..1.</summary>
    /// <param name="pixels">What <see cref="Render(VulkanDevice, VfxSystem, RenderCamera, int)" /> returned.</param>
    /// <param name="side">The side it was rendered at.</param>
    /// <param name="x">Column, clamped.</param>
    /// <param name="y">Row, clamped.</param>
    public static Vector3 Pixel(byte[] pixels, int side, int x, int y) {
        ArgumentNullException.ThrowIfNull(pixels);

        var offset = ((Math.Clamp(y, 0, side - 1) * side) + Math.Clamp(x, 0, side - 1)) * 4;

        return new(pixels[offset] / 255f, pixels[offset + 1] / 255f, pixels[offset + 2] / 255f);
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Everything about the draw that is not the geometry.</summary>
    sealed class Frame {
        public int Side { get; init; } = DefaultSide;

        public Matrix4x4 ViewProjection { get; init; } = Matrix4x4.Identity;

        public Vector4 Tint { get; init; } = new(1f, 1f, 1f, 1f);

        public float Emissive { get; init; } = 1f;

        public float EdgeSharpness { get; init; } = 0.25f;
    }

    /// <summary>Records and submits the one pass, and copies the target back.</summary>
    static byte[] Draw(VulkanDevice device, Compiled compiled, ReadOnlySpan<ParticleVertex> vertices, Frame frame) {
        var side = frame.Side;
        var quads = vertices.Length / VfxGeometryBuilder.VerticesPerParticle;
        var reflection = compiled.Reflection;

        var vertexModule = device.CreateShader(ShaderStage.Vertex, compiled.Vertex, "ParticleSprite.vert");
        var fragmentModule = device.CreateShader(ShaderStage.Fragment, compiled.Fragment, "ParticleSprite.frag");

        var setLayouts = SetLayouts(device, reflection);
        var pipelineLayout = device.CreatePipelineLayout(new(setLayouts, [], "ParticleSprite"));

        var pipeline = device.CreateGraphicsPipeline(new(
            vertexModule,
            fragmentModule,
            pipelineLayout,

            // Additive, and it goes on the target rather than on the pipeline — a wholly default
            // BlendState means opaque, so omitting this draws the sprite over the clear rather than
            // into it and the centre pixel comes back orange either way.
            [new(PixelFormat.Rgba8UNorm, BlendState.Additive)],
            VertexLayout(reflection),

            // No depth at all and nothing culled, which is what a billboard needs: its winding
            // follows the camera, so half of any effect is back-facing at any moment.
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "ParticleSprite"
        ));

        // Sized for at least one quad even when there is nothing to draw, because a zero-length
        // buffer is not a thing and the pass still has to clear.
        var vertexBytes = Math.Max(vertices.Length, VfxGeometryBuilder.VerticesPerParticle) * ParticleVertices.SizeInBytes;
        var indices = new uint[Math.Max(quads, 1) * VfxGeometryBuilder.IndicesPerParticle];

        VfxGeometryBuilder.WriteQuadIndices(indices, quads);

        var vertexBuffer = device.CreateBuffer(
            new(vertexBytes, BufferUsage.Vertex, MemoryAccess.HostUpload, "particle vertices")
        );

        var indexBuffer = device.CreateBuffer(
            new(indices.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "particle indices")
        );

        if (!vertices.IsEmpty) {
            device.Write(vertexBuffer, 0, MemoryMarshal.AsBytes(vertices));
        }

        device.Write(indexBuffer, 0, MemoryMarshal.AsBytes(indices.AsSpan()));

        // ⚠ Written by *name*, at the offsets the reflection reports. Set 2's members are a float4
        // and two floats, which std140 puts at 0, 16 and 20 — an arrangement nobody would guess and
        // that a host writing a packed struct gets wrong silently: `edgeSharpness` landing in
        // `emissive`'s slot is a sprite that is dimmer or brighter than it should be, and nothing
        // says so.
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            ["viewProjection"] = Bytes(frame.ViewProjection),
            ["tint"] = Bytes(frame.Tint),
            ["emissive"] = Bytes(frame.Emissive),
            ["edgeSharpness"] = Bytes(frame.EdgeSharpness)
        };

        var blocks = Blocks(device, reflection, values);

        var target = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            side,
            side,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "particle target"
        ));

        var targetView = device.CreateTextureView(target);

        var readback = device.CreateBuffer(new(
            side * side * 4,
            BufferUsage.CopyDestination,
            MemoryAccess.HostReadback,
            "particle readback"
        ));

        using var descriptors = new DescriptorAllocator(device, "Particles");

        device.BeginFrame();
        descriptors.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Graphics, "particles")) {
            List<BufferBarrier> buffers = [
                new(vertexBuffer, ResourceState.HostAccess, ResourceState.VertexInput),
                new(indexBuffer, ResourceState.HostAccess, ResourceState.VertexInput)
            ];

            buffers.AddRange(
                blocks.Select(block => new BufferBarrier(block.Buffer, ResourceState.HostAccess, ResourceState.UniformRead))
            );

            list.Barrier(new(
                [.. buffers],
                [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]
            ));

            list.BeginRenderPass(new(
                [new(targetView, LoadAction.Clear, StoreAction.Store, Background)],
                name: "Sparks"
            ));

            if (quads > 0) {
                list.BindPipeline(pipeline);

                foreach (var block in blocks) {
                    list.BindDescriptorSet(
                        (DescriptorSetSlot)block.Set,
                        descriptors.Allocate(
                            setLayouts[block.Set],
                            [DescriptorWrite.Uniform(block.Binding, block.Buffer, 0, block.Size)]
                        )
                    );
                }

                list.BindVertexBuffer(0, vertexBuffer);
                list.BindIndexBuffer(indexBuffer, IndexFormat.UInt32);
                list.DrawIndexed(quads * VfxGeometryBuilder.IndicesPerParticle);
            }

            list.EndRenderPass();

            list.Barrier(new([], [new(target, ResourceState.ColourTarget, ResourceState.CopySource)]));
            list.CopyTextureToBuffer(new(target), new(side, side, 1), readback, 0);
            list.Finish();

            device.GraphicsQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[side * side * 4];

        device.Read(readback, 0, pixels);

        device.Destroy(readback);
        device.Destroy(targetView);
        device.Destroy(target);

        foreach (var block in blocks) {
            device.Destroy(block.Buffer);
        }

        device.Destroy(indexBuffer);
        device.Destroy(vertexBuffer);
        device.Destroy(pipeline);
        device.Destroy(pipelineLayout);

        foreach (var layout in setLayouts) {
            device.Destroy(layout);
        }

        device.Destroy(fragmentModule);
        device.Destroy(vertexModule);

        return pixels;
    }

    // --- The layout ---------------------------------------------------------

    /// <summary>
    ///     One descriptor set layout per set the pipeline layout will have, including the empty ones.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The array index <em>is</em> the set number</b>, so a shader that declares sets 1 and 2
    ///     and no set 0 — which is exactly what <c>ParticleSprite.rvn</c> does, because the draw binds
    ///     a view block and a material block and nothing per frame — still needs three entries. Handing
    ///     over the two it declares would put the view block in set 0 and the material block in set 1,
    ///     and the driver refuses the draw naming the layout rather than the set.
    /// </remarks>
    static DescriptorSetLayoutHandle[] SetLayouts(VulkanDevice device, RavenReflection reflection) {
        var count = reflection.Sets.Length == 0 ? 0 : reflection.Sets.Max(set => set.Set) + 1;
        var layouts = new DescriptorSetLayoutHandle[count];

        for (var slot = 0; slot < count; slot++) {
            var declared = reflection.Sets.FirstOrDefault(set => set.Set == slot);

            DescriptorBinding[] bindings = declared is null
                ? []
                : [
                    .. declared.Bindings.Select(binding => new DescriptorBinding(
                        (uint)binding.Binding,
                        Kind(binding.Type),
                        Stages(binding.Stages),
                        Math.Max(1, binding.Count)
                    ))
                ];

            layouts[slot] = device.CreateDescriptorSetLayout(
                new((DescriptorSetSlot)slot, bindings, $"ParticleSprite.set{slot}")
            );
        }

        return layouts;
    }

    /// <summary>
    ///     The vertex buffer layout, joining <see cref="ParticleVertices.Schema" /> to the stage's
    ///     inputs by name.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>By name, never by location.</b> This shader's inputs are at locations 2, 3 and 4 —
    ///     Raven numbers a stage's parameters after its streams — so a layout that assumed 0, 1, 2
    ///     would describe attributes the module has no variables for, and the position would arrive
    ///     as whatever the driver leaves in an unwritten input. The schema's own
    ///     <see cref="VertexChannel.Format" /> decides how the bytes are read, matching
    ///     <see cref="VertexSchema.Layout" />.
    /// </remarks>
    static VertexBufferLayout[] VertexLayout(RavenReflection reflection) {
        var schema = ParticleVertices.Schema;
        var elements = new VertexElement[reflection.VertexInputs.Length];

        for (var index = 0; index < elements.Length; index++) {
            var input = reflection.VertexInputs[index];
            var channel = schema.Attributes.FirstOrDefault(a => string.Equals(a.Name, input.Name, StringComparison.Ordinal));

            if (channel.Name is null) {
                throw new InvalidOperationException(
                    $"ParticleSprite reads a vertex attribute called '{input.Name}' at location "
                    + $"{input.Location}, and ParticleVertices.Schema holds "
                    + $"{string.Join(", ", schema.Attributes.Select(a => a.Name))}."
                );
            }

            elements[index] = new((uint)input.Location, channel.Format, channel.Offset);
        }

        return [new(schema.Stride, elements)];
    }

    /// <summary>One host-visible uniform buffer per block the shader declares, filled by name.</summary>
    static List<Block> Blocks(VulkanDevice device, RavenReflection reflection, Dictionary<string, byte[]> values) {
        List<Block> blocks = [];

        foreach (var set in reflection.Sets) {
            foreach (var binding in set.Bindings) {
                if (binding.Type != DescriptorType.UniformBuffer || binding.Size <= 0) {
                    continue;
                }

                var bytes = new byte[binding.Size];

                foreach (var member in binding.Members) {
                    if (values.TryGetValue(member.Name, out var value)) {
                        value.AsSpan(0, Math.Min(value.Length, member.Size)).CopyTo(bytes.AsSpan(member.Offset));
                    }
                }

                var buffer = device.CreateBuffer(new(
                    bytes.Length,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    binding.Name
                ));

                device.Write(buffer, 0, bytes);

                blocks.Add(new(set.Set, (uint)binding.Binding, buffer, bytes.Length));
            }
        }

        return blocks;
    }

    /// <summary>One filled uniform block and where it belongs.</summary>
    readonly record struct Block(int Set, uint Binding, BufferHandle Buffer, int Size);

    static DescriptorKind Kind(DescriptorType type) =>
        type switch {
            DescriptorType.UniformBuffer => DescriptorKind.UniformBuffer,
            DescriptorType.StorageBuffer => DescriptorKind.StorageBuffer,
            DescriptorType.SampledTexture => DescriptorKind.SampledTexture,
            DescriptorType.StorageImage => DescriptorKind.StorageTexture,
            DescriptorType.Sampler => DescriptorKind.Sampler,
            _ => throw new NotSupportedException($"ParticlePicture cannot bind a {type}.")
        };

    static ShaderStage Stages(ShaderStages stages) {
        var result = ShaderStage.None;

        if (stages.HasFlag(ShaderStages.Vertex)) {
            result |= ShaderStage.Vertex;
        }

        if (stages.HasFlag(ShaderStages.Fragment)) {
            result |= ShaderStage.Fragment;
        }

        if (stages.HasFlag(ShaderStages.Compute)) {
            result |= ShaderStage.Compute;
        }

        return result;
    }

    static byte[] Bytes<T>(T value) where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();

    // --- The shader ---------------------------------------------------------

    /// <summary>The two modules and the description the layout above is built from.</summary>
    internal sealed record Compiled(byte[] Vertex, byte[] Fragment, RavenReflection Reflection);

    /// <summary>
    ///     Compiles the shipped <c>ParticleSprite.rvn</c>, from the library rather than from a copy.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same walk <c>RavenKernels</c> makes — parse, bind, lower, verify, generate — with
    ///         two differences it cannot share. It keeps the <em>graphics</em> stages rather than the
    ///         compute one, and it keeps the reflection, which is the half of the compiler's output
    ///         a draw needs and a dispatch against a hand-built layout does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Errors only, not an empty bag.</b> A binding with a declared default — which
    ///         <c>tint</c>, <c>emissive</c> and <c>edgeSharpness</c> all have — makes the SPIR-V
    ///         backend say out loud that a uniform cannot carry an initialiser and the default stays
    ///         host-side data (RVN4003, Info). That is correct and this is the host holding up its
    ///         end; asserting the bag is empty, as the compute path does, would fail on a shader with
    ///         nothing wrong with it.
    ///     </para>
    /// </remarks>
    static Compiled CompileSprite() {
        var root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Raven", "Library")
        );

        // Math.rvn is what declares `package Vixen.Shaders.Core`, which ParticleSprite.rvn imports.
        var trees = new[] { "Core/Math.rvn", "Vfx/ParticleSprite.rvn" }
            .Select(name => Path.Combine(root, name))
            .Select(path => SyntaxTree.ParseText(File.ReadAllText(path), path: Path.GetFileName(path)))
            .ToArray();

        foreach (var tree in trees) {
            Assert.True(tree.Diagnostics.Count == 0, Report("Parsing", tree.Diagnostics));
        }

        var compilation = Compilation.Create("ParticleSprite", trees);
        var semantic = compilation.GetDiagnostics();

        Assert.True(semantic.Count == 0, Report("Binding", semantic));

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);
        Assert.True(Errors(bag).Count == 0, Report("Lowering", Errors(bag)));

        var backend = TargetBackends.Create("spirv");

        Assert.NotNull(backend);

        var generated = backend!.Generate(module, bag);

        Assert.True(Errors(bag).Count == 0, Report("Generating", Errors(bag)));

        var shader = module.Shaders.FirstOrDefault(s => s.Name == "ParticleSprite");

        Assert.True(shader is not null, "The library produced no shader called 'ParticleSprite'.");

        return new(
            Unit(generated, RavenStage.Vertex),
            Unit(generated, RavenStage.Fragment),
            ReflectionBuilder.Describe(shader!, compilation.UsedPermutationKeys)
        );
    }

    static byte[] Unit(IReadOnlyList<GeneratedSource> generated, RavenStage stage) {
        foreach (var unit in generated) {
            if (unit.Stage == stage
                && unit.Name.StartsWith("ParticleSprite", StringComparison.Ordinal)
                && unit.Binary is { } binary) {
                return binary;
            }
        }

        Assert.Fail(
            $"No {stage} module for 'ParticleSprite'. Got: "
            + string.Join(", ", generated.Select(unit => $"{unit.Name} ({unit.Stage})"))
        );

        return [];
    }

    static List<Diagnostic> Errors(DiagnosticBag bag) =>
        [.. bag.ToArray().Where(d => d.Severity == DiagnosticSeverity.Error)];

    static string Report(string phase, IReadOnlyList<Diagnostic> diagnostics) =>
        $"{phase} ParticleSprite.rvn failed:\n{string.Join("\n", diagnostics.Select(d => d.ToString()))}";
}
