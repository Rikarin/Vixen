// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>A triangle, as SPIR-V, so that pipelines can be tested without a shader compiler.</summary>
/// <remarks>
///     <para>
///         Committed as bytes rather than compiled by the test, deliberately. The RHI never parses
///         shader source and has no compiler dependency, and adding one to the test suite would mean
///         every machine and every CI leg needed <c>glslc</c> installed before a single pipeline test
///         could run. Raven is the engine's shader front end
///         ([07](../../docs/plan/07-raven-shader-pipeline.md)); this is a fixture, not a pipeline
///         stage.
///     </para>
///     <para>
///         The GLSL each was compiled from is below verbatim, with the exact command, so that
///         regenerating them is a copy-and-paste rather than an archaeology exercise.
///     </para>
/// </remarks>
static class TriangleShaders {
    /// <summary>
    ///     <c>glslc -O tri.vert -o tri.vert.spv</c>
    ///     <code>
    ///     #version 450
    ///
    ///     layout(location = 0) out vec3 colour;
    ///
    ///     vec2 positions[3] = vec2[](vec2(0.0, -0.6), vec2(0.6, 0.6), vec2(-0.6, 0.6));
    ///     vec3 colours[3] = vec3[](vec3(1, 0, 0), vec3(0, 1, 0), vec3(0, 0, 1));
    ///
    ///     void main() {
    ///         gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
    ///         colour = colours[gl_VertexIndex];
    ///     }
    ///     </code>
    /// </summary>
    const string VertexBase64 =
        "AwIjBwAAAQALAA0AOgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAA"
        + "A8ACAAAAAAABAAAAG1haW4AAAAAIgAAACYAAAAxAAAARwADACAAAAACAAAASAAFACAAAAAAAAAACwAAAAAAAA"
        + "BIAAUAIAAAAAEAAAALAAAAAQAAAEgABQAgAAAAAgAAAAsAAAADAAAASAAFACAAAAADAAAACwAAAAQAAABHAAQ"
        + "AJgAAAAsAAAAqAAAARwAEADEAAAAeAAAAAAAAABMAAgACAAAAIQADAAMAAAACAAAAFgADAAYAAAAgAAAAFwAE"
        + "AAcAAAAGAAAAAgAAABUABAAIAAAAIAAAAAAAAAArAAQACAAAAAkAAAADAAAAHAAEAAoAAAAHAAAACQAAACsAB"
        + "AAGAAAADQAAAAAAAAArAAQABgAAAA4AAACamRm/LAAFAAcAAAAPAAAADQAAAA4AAAArAAQABgAAABAAAACamR"
        + "k/LAAFAAcAAAARAAAAEAAAABAAAAAsAAUABwAAABIAAAAOAAAAEAAAACwABgAKAAAAEwAAAA8AAAARAAAAEgA"
        + "AABcABAAUAAAABgAAAAMAAAAcAAQAFQAAABQAAAAJAAAAKwAEAAYAAAAYAAAAAACAPywABgAUAAAAGQAAABgA"
        + "AAANAAAADQAAACwABgAUAAAAGgAAAA0AAAAYAAAADQAAACwABgAUAAAAGwAAAA0AAAANAAAAGAAAACwABgAVA"
        + "AAAHAAAABkAAAAaAAAAGwAAABcABAAdAAAABgAAAAQAAAArAAQACAAAAB4AAAABAAAAHAAEAB8AAAAGAAAAHg"
        + "AAAB4ABgAgAAAAHQAAAAYAAAAfAAAAHwAAACAABAAhAAAAAwAAACAAAAA7AAQAIQAAACIAAAADAAAAFQAEACM"
        + "AAAAgAAAAAQAAACsABAAjAAAAJAAAAAAAAAAgAAQAJQAAAAEAAAAjAAAAOwAEACUAAAAmAAAAAQAAACAABAAu"
        + "AAAAAwAAAB0AAAAgAAQAMAAAAAMAAAAUAAAAOwAEADAAAAAxAAAAAwAAACAABAA2AAAABwAAAAoAAAAgAAQAN"
        + "wAAAAcAAAAHAAAAIAAEADgAAAAHAAAAFQAAACAABAA5AAAABwAAABQAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAP"
        + "gAAgAFAAAAOwAEADgAAAAXAAAABwAAADsABAA2AAAADAAAAAcAAAA+AAMADAAAABMAAAA+AAMAFwAAABwAAAA"
        + "9AAQAIwAAACcAAAAmAAAAQQAFADcAAAApAAAADAAAACcAAAA9AAQABwAAACoAAAApAAAAUQAFAAYAAAArAAAA"
        + "KgAAAAAAAABRAAUABgAAACwAAAAqAAAAAQAAAFAABwAdAAAALQAAACsAAAAsAAAADQAAABgAAABBAAUALgAAA"
        + "C8AAAAiAAAAJAAAAD4AAwAvAAAALQAAAEEABQA5AAAANAAAABcAAAAnAAAAPQAEABQAAAA1AAAANAAAAD4AAw"
        + "AxAAAANQAAAP0AAQA4AAEA";

    /// <summary>
    ///     <c>glslc -O tri.frag -o tri.frag.spv</c>
    ///     <code>
    ///     #version 450
    ///
    ///     layout(location = 0) in vec3 colour;
    ///     layout(location = 0) out vec4 target;
    ///
    ///     void main() {
    ///         target = vec4(colour, 1.0);
    ///     }
    ///     </code>
    /// </summary>
    const string FragmentBase64 =
        "AwIjBwAAAQALAA0AEwAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAA"
        + "A8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAABHAAQACQAAAB4AAAAAAAAARwAEAAwAAA"
        + "AeAAAAAAAAABMAAgACAAAAIQADAAMAAAACAAAAFgADAAYAAAAgAAAAFwAEAAcAAAAGAAAABAAAACAABAAIAAA"
        + "AAwAAAAcAAAA7AAQACAAAAAkAAAADAAAAFwAEAAoAAAAGAAAAAwAAACAABAALAAAAAQAAAAoAAAA7AAQACwAA"
        + "AAwAAAABAAAAKwAEAAYAAAAOAAAAAACAPzYABQACAAAABAAAAAAAAAADAAAA+AACAAUAAAA9AAQACgAAAA0AA"
        + "AAMAAAAUQAFAAYAAAAPAAAADQAAAAAAAABRAAUABgAAABAAAAANAAAAAQAAAFEABQAGAAAAEQAAAA0AAAACAA"
        + "AAUAAHAAcAAAASAAAADwAAABAAAAARAAAADgAAAD4AAwAJAAAAEgAAAP0AAQA4AAEA";

    /// <summary>The vertex module.</summary>
    public static byte[] Vertex => Convert.FromBase64String(VertexBase64);

    /// <summary>The fragment module.</summary>
    public static byte[] Fragment => Convert.FromBase64String(FragmentBase64);
}
