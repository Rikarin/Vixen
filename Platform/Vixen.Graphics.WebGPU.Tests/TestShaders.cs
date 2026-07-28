// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>Shaders, as WGSL, so that pipelines can be tested without a shader compiler.</summary>
/// <remarks>
///     <para>
///         Source rather than bytecode, which is the opposite of what <c>Vixen.Graphics.Vulkan</c>'s
///         fixtures do and is right for the same reason theirs are: <b>WGSL is what this backend's
///         implementations compile.</b> A browser accepts nothing else, and committing SPIR-V here
///         would test a path only the native surface has. Raven is the engine's shader front end
///         ([07](../../docs/plan/07-raven-shader-pipeline.md)) and reaches WGSL through SPIRV-Cross;
///         these are fixtures, not a pipeline stage.
///     </para>
///     <para>
///         Every entry point is called <c>main</c>, which is what
///         <see cref="WebGpuDevice.EntryPoint" /> says the backend asks for.
///     </para>
/// </remarks>
static class TestShaders {
    /// <summary>A triangle covering the centre of the target and none of its corners.</summary>
    /// <remarks>
    ///     Counter-clockwise in clip space, which is the engine's front face. Whether that survives
    ///     back-face culling depends on the viewport's Y flip as well, which is what
    ///     <c>CullingRemovesExactlyOneWinding</c> is for — this shader states the winding and does
    ///     not assume the answer.
    /// </remarks>
    public static ReadOnlySpan<byte> Vertex => """
        @vertex
        fn main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
            var points = array<vec2<f32>, 3>(
                vec2<f32>(0.0, 0.6),
                vec2<f32>(-0.6, -0.6),
                vec2<f32>(0.6, -0.6)
            );

            return vec4<f32>(points[index], 0.0, 1.0);
        }
        """u8;

    /// <summary>The same triangle, moved along X by an emulated push constant.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Group 0 is not arbitrary.</b> The pipeline layout it is built with declares no
    ///         descriptor sets of its own, so the backend puts the emulated push-constant block at
    ///         group <c>Sets.Length</c>, which is zero. That is the convention
    ///         <see cref="PushConstantRing" /> documents, and this shader is the only thing in the
    ///         repository that has to agree with it.
    ///     </para>
    ///     <para>
    ///         A <c>vec4&lt;f32&gt;</c> rather than a bare <c>f32</c> because the uniform address
    ///         space requires a sixteen-byte alignment, and X rather than Y because clip-space X
    ///         reaches the framebuffer without a flip on any API — so a test built on it asserts the
    ///         push constant arrived rather than which way up the viewport is.
    ///     </para>
    /// </remarks>
    public static ReadOnlySpan<byte> VertexPushed => """
        @group(0) @binding(0) var<uniform> push: vec4<f32>;

        @vertex
        fn main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
            var points = array<vec2<f32>, 3>(
                vec2<f32>(0.0, 0.3),
                vec2<f32>(-0.3, -0.3),
                vec2<f32>(0.3, -0.3)
            );

            return vec4<f32>(points[index] + vec2<f32>(push.x, 0.0), 0.0, 1.0);
        }
        """u8;

    /// <summary>Four attributes, one per vertex format family the engine ships.</summary>
    /// <remarks>
    ///     Every input is used, because an implementation is free to ignore a declared attribute
    ///     nothing reads — and an unread attribute is not validated against the buffer layout, which
    ///     is exactly the check this fixture exists to provoke.
    /// </remarks>
    public static ReadOnlySpan<byte> VertexWithAttributes => """
        @vertex
        fn main(
            @location(0) position: vec3<f32>,
            @location(1) uv: vec2<f32>,
            @location(2) colour: vec4<f32>,
            @location(3) normal: vec4<f32>
        ) -> @builtin(position) vec4<f32> {
            return vec4<f32>(position, 1.0)
                + vec4<f32>(uv, 0.0, 0.0)
                + colour
                + normal;
        }
        """u8;

    /// <summary>Opaque green, so a covered pixel is distinguishable from a cleared one.</summary>
    public static ReadOnlySpan<byte> Fragment => """
        @fragment
        fn main() -> @location(0) vec4<f32> {
            return vec4<f32>(0.0, 1.0, 0.0, 1.0);
        }
        """u8;

    /// <summary>Writes each invocation's index times a uniform, into a storage buffer.</summary>
    /// <remarks>
    ///     Multiplied rather than merely written so that the answer is not something a zeroed buffer
    ///     or an unbound uniform could produce by accident — both of which would pass a test that
    ///     only checked the dispatch happened.
    /// </remarks>
    public static ReadOnlySpan<byte> Compute => """
        @group(0) @binding(0) var<uniform> constants: vec4<u32>;
        @group(0) @binding(1) var<storage, read_write> result: array<u32>;

        @compute @workgroup_size(1)
        fn main(@builtin(global_invocation_id) id: vec3<u32>) {
            result[id.x] = id.x * constants.x;
        }
        """u8;

    /// <summary>The source of a module, for a failure that needs to print it.</summary>
    /// <param name="module">The WGSL.</param>
    public static string Text(ReadOnlySpan<byte> module) => Encoding.UTF8.GetString(module);
}
