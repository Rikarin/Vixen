// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>The shaders and pipelines the tests build on.</summary>
/// <remarks>
///     Real GLSL rather than a placeholder string, because the translator runs over it on the way to
///     the fake driver and a source with no declarations would exercise none of it.
/// </remarks>
static class Pipelines {
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GlDevice, ShaderPair> Cache = [];

    /// <summary>A vertex shader with a per-draw transform and a vertex colour.</summary>
    public const string VertexSource = """
        #version 450 core
        layout(set = 3, binding = 0) uniform Draw { mat4 world; } draw;
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec4 colour;
        layout(location = 0) out vec4 varyingColour;
        void main() {
            varyingColour = colour;
            gl_Position = draw.world * vec4(position, 1.0);
        }
        """;

    /// <summary>A fragment shader sampling one texture.</summary>
    public const string FragmentSource = """
        #version 450 core
        layout(set = 2, binding = 0) uniform sampler2D albedo;
        layout(location = 0) in vec4 varyingColour;
        layout(location = 0) out vec4 target;
        void main() { target = varyingColour * texture(albedo, vec2(0.5)); }
        """;

    /// <summary>A compute shader over a storage buffer.</summary>
    public const string ComputeSource = """
        #version 450 core
        layout(local_size_x = 64) in;
        layout(set = 2, binding = 0) buffer Particles { vec4 items[]; } particles;
        void main() { particles.items[gl_GlobalInvocationID.x] = vec4(1.0); }
        """;

    /// <summary>The vertex layout <see cref="VertexSource" /> declares.</summary>
    public static VertexBufferLayout[] VertexLayout => [
        new(
            sizeof(float) * 7,
            [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 3)]
        )
    ];

    /// <summary>The per-draw and per-material sets the two shaders declare.</summary>
    public static PipelineLayoutHandle Layout(GlDevice device, int pushConstantBytes = 0) {
        var perDraw = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerDraw,
            [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)],
            "draw"
        ));

        var perMaterial = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)],
            "material"
        ));

        return device.CreatePipelineLayout(new(
            [perDraw, perMaterial],
            pushConstantBytes > 0 ? [new(ShaderStage.Vertex, 0, pushConstantBytes)] : null,
            "test layout"
        ));
    }

    /// <summary>The two shaders, created once per device.</summary>
    /// <remarks>
    ///     Once, deliberately. Creating them per pipeline would give every pipeline its own shader
    ///     handles, and the program cache keys on those — which is correct, and is exactly Vulkan's
    ///     behaviour for two <c>VkShaderModule</c>s over one source — so a helper that made fresh
    ///     ones would quietly defeat the sharing the cache exists for.
    /// </remarks>
    public static ShaderPair Shaders(GlDevice device) {
        if (Cache.TryGetValue(device, out var existing)) {
            return existing;
        }

        var created = new ShaderPair(
            device.CreateShader(ShaderStage.Vertex, Encoding.UTF8.GetBytes(VertexSource), "test.vert"),
            device.CreateShader(ShaderStage.Fragment, Encoding.UTF8.GetBytes(FragmentSource), "test.frag")
        );

        Cache.Add(device, created);
        return created;
    }

    /// <summary>The two shaders a test pipeline is built from.</summary>
    /// <param name="Vertex">The vertex shader.</param>
    /// <param name="Fragment">The fragment shader.</param>
    public sealed record ShaderPair(ShaderHandle Vertex, ShaderHandle Fragment);

    /// <summary>A graphics pipeline handle with the given state.</summary>
    public static PipelineHandle Handle(
        GlDevice device,
        BlendState blend,
        DepthStencilState depth,
        VertexBufferLayout[]? vertices = null,
        int pushConstantBytes = 0,
        PipelineLayoutHandle? layout = null
    ) {
        var (vertex, fragment) = Shaders(device);

        return device.CreateGraphicsPipeline(new(
            vertex,
            fragment,
            layout ?? Layout(device, pushConstantBytes),
            [new(PixelFormat.Rgba8UNorm, blend)],
            vertices,
            Rasterizer: RasterizerState.Default,
            DepthStencil: depth,
            DepthFormat: depth.DepthTest ? PixelFormat.Depth32Float : PixelFormat.Undefined,
            Name: "test"
        ));
    }

    /// <summary>The same, resolved to the backend object the state cache takes.</summary>
    public static GlPipeline Graphics(
        GlDevice device,
        RecordingGlApi gl,
        BlendState blend,
        DepthStencilState depth,
        VertexBufferLayout[]? vertices = null
    ) {
        var handle = Handle(device, blend, depth, vertices);
        gl.Clear();
        return device.Pipeline(handle);
    }
}
