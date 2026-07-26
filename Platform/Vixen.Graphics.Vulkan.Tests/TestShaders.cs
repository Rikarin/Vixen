// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>Shaders, as SPIR-V, so that pipelines can be tested without a shader compiler.</summary>
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
static class TestShaders {
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

    /// <summary>
    ///     <c>glslc mesh.vert -o mesh.vert.spv</c>
    ///     <code>
    ///     #version 450
    ///
    ///     layout(location = 0) in vec2 position;
    ///     layout(location = 1) in vec4 colour;
    ///     layout(push_constant) uniform Push { vec2 offset; } push;
    ///     layout(location = 0) out vec4 varying_colour;
    ///
    ///     void main() {
    ///         gl_Position = vec4(position + push.offset, 0.0, 1.0);
    ///         varying_colour = colour;
    ///     }
    ///     </code>
    /// </summary>
    const string MeshVertexBase64 =
        "AwIjBwAAAQALAA0AJgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACQAAAAAA"
        + "BAAAAG1haW4AAAAADQAAABIAAAAiAAAAJAAAAAMAAwACAAAAwgEAAAQACgBHTF9HT09HTEVfY3BwX3N0eWxlX2xpbmVfZGly"
        + "ZWN0aXZlAAAEAAgAR0xfR09PR0xFX2luY2x1ZGVfZGlyZWN0aXZlAAUABAAEAAAAbWFpbgAAAAAFAAYACwAAAGdsX1BlclZl"
        + "cnRleAAAAAAGAAYACwAAAAAAAABnbF9Qb3NpdGlvbgAGAAcACwAAAAEAAABnbF9Qb2ludFNpemUAAAAABgAHAAsAAAACAAAA"
        + "Z2xfQ2xpcERpc3RhbmNlAAYABwALAAAAAwAAAGdsX0N1bGxEaXN0YW5jZQAFAAMADQAAAAAAAAAFAAUAEgAAAHBvc2l0aW9u"
        + "AAAAAAUABAAUAAAAUHVzaAAAAAAGAAUAFAAAAAAAAABvZmZzZXQAAAUABAAWAAAAcHVzaAAAAAAFAAYAIgAAAHZhcnlpbmdf"
        + "Y29sb3VyAAAFAAQAJAAAAGNvbG91cgAARwADAAsAAAACAAAASAAFAAsAAAAAAAAACwAAAAAAAABIAAUACwAAAAEAAAALAAAA"
        + "AQAAAEgABQALAAAAAgAAAAsAAAADAAAASAAFAAsAAAADAAAACwAAAAQAAABHAAQAEgAAAB4AAAAAAAAARwADABQAAAACAAAA"
        + "SAAFABQAAAAAAAAAIwAAAAAAAABHAAQAIgAAAB4AAAAAAAAARwAEACQAAAAeAAAAAQAAABMAAgACAAAAIQADAAMAAAACAAAA"
        + "FgADAAYAAAAgAAAAFwAEAAcAAAAGAAAABAAAABUABAAIAAAAIAAAAAAAAAArAAQACAAAAAkAAAABAAAAHAAEAAoAAAAGAAAA"
        + "CQAAAB4ABgALAAAABwAAAAYAAAAKAAAACgAAACAABAAMAAAAAwAAAAsAAAA7AAQADAAAAA0AAAADAAAAFQAEAA4AAAAgAAAA"
        + "AQAAACsABAAOAAAADwAAAAAAAAAXAAQAEAAAAAYAAAACAAAAIAAEABEAAAABAAAAEAAAADsABAARAAAAEgAAAAEAAAAeAAMA"
        + "FAAAABAAAAAgAAQAFQAAAAkAAAAUAAAAOwAEABUAAAAWAAAACQAAACAABAAXAAAACQAAABAAAAArAAQABgAAABsAAAAAAAAA"
        + "KwAEAAYAAAAcAAAAAACAPyAABAAgAAAAAwAAAAcAAAA7AAQAIAAAACIAAAADAAAAIAAEACMAAAABAAAABwAAADsABAAjAAAA"
        + "JAAAAAEAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAPQAEABAAAAATAAAAEgAAAEEABQAXAAAAGAAAABYAAAAPAAAA"
        + "PQAEABAAAAAZAAAAGAAAAIEABQAQAAAAGgAAABMAAAAZAAAAUQAFAAYAAAAdAAAAGgAAAAAAAABRAAUABgAAAB4AAAAaAAAA"
        + "AQAAAFAABwAHAAAAHwAAAB0AAAAeAAAAGwAAABwAAABBAAUAIAAAACEAAAANAAAADwAAAD4AAwAhAAAAHwAAAD0ABAAHAAAA"
        + "JQAAACQAAAA+AAMAIgAAACUAAAD9AAEAOAABAA==";

    /// <summary>
    ///     <c>glslc mesh.frag -o mesh.frag.spv</c>
    ///     <code>
    ///     #version 450
    ///
    ///     layout(location = 0) in vec4 varying_colour;
    ///     layout(location = 0) out vec4 target;
    ///
    ///     void main() { target = varying_colour; }
    ///     </code>
    /// </summary>
    const string MeshFragmentBase64 =
        "AwIjBwAAAQALAA0ADQAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAA"
        + "BAAAAG1haW4AAAAACQAAAAsAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAEAAoAR0xfR09PR0xFX2NwcF9zdHlsZV9saW5l"
        + "X2RpcmVjdGl2ZQAABAAIAEdMX0dPT0dMRV9pbmNsdWRlX2RpcmVjdGl2ZQAFAAQABAAAAG1haW4AAAAABQAEAAkAAAB0YXJn"
        + "ZXQAAAUABgALAAAAdmFyeWluZ19jb2xvdXIAAEcABAAJAAAAHgAAAAAAAABHAAQACwAAAB4AAAAAAAAAEwACAAIAAAAhAAMA"
        + "AwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAgAAQA"
        + "CgAAAAEAAAAHAAAAOwAEAAoAAAALAAAAAQAAADYABQACAAAABAAAAAAAAAADAAAA+AACAAUAAAA9AAQABwAAAAwAAAALAAAA"
        + "PgADAAkAAAAMAAAA/QABADgAAQA=";

    /// <summary>
    ///     <c>glslc mul.comp -o mul.comp.spv</c>
    ///     <code>
    ///     #version 450
    ///
    ///     layout(local_size_x = 64) in;
    ///     layout(set = 0, binding = 0) buffer Data { uint values[]; };
    ///     layout(push_constant) uniform Push { uint multiplier; } push;
    ///
    ///     void main() {
    ///         uint i = gl_GlobalInvocationID.x;
    ///         values[i] = values[i] * push.multiplier + i;
    ///     }
    ///     </code>
    /// </summary>
    const string ComputeBase64 =
        "AwIjBwAAAQALAA0AKAAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABgAFAAAA"
        + "BAAAAG1haW4AAAAACwAAABAABgAEAAAAEQAAAEAAAAABAAAAAQAAAAMAAwACAAAAwgEAAAQACgBHTF9HT09HTEVfY3BwX3N0"
        + "eWxlX2xpbmVfZGlyZWN0aXZlAAAEAAgAR0xfR09PR0xFX2luY2x1ZGVfZGlyZWN0aXZlAAUABAAEAAAAbWFpbgAAAAAFAAMA"
        + "CAAAAGkAAAAFAAgACwAAAGdsX0dsb2JhbEludm9jYXRpb25JRAAAAAUABAARAAAARGF0YQAAAAAGAAUAEQAAAAAAAAB2YWx1"
        + "ZXMAAAUAAwATAAAAAAAAAAUABAAbAAAAUHVzaAAAAAAGAAYAGwAAAAAAAABtdWx0aXBsaWVyAAAFAAQAHQAAAHB1c2gAAAAA"
        + "RwAEAAsAAAALAAAAHAAAAEcABAAQAAAABgAAAAQAAABHAAMAEQAAAAMAAABIAAUAEQAAAAAAAAAjAAAAAAAAAEcABAATAAAA"
        + "IQAAAAAAAABHAAQAEwAAACIAAAAAAAAARwADABsAAAACAAAASAAFABsAAAAAAAAAIwAAAAAAAABHAAQAJwAAAAsAAAAZAAAA"
        + "EwACAAIAAAAhAAMAAwAAAAIAAAAVAAQABgAAACAAAAAAAAAAIAAEAAcAAAAHAAAABgAAABcABAAJAAAABgAAAAMAAAAgAAQA"
        + "CgAAAAEAAAAJAAAAOwAEAAoAAAALAAAAAQAAACsABAAGAAAADAAAAAAAAAAgAAQADQAAAAEAAAAGAAAAHQADABAAAAAGAAAA"
        + "HgADABEAAAAQAAAAIAAEABIAAAACAAAAEQAAADsABAASAAAAEwAAAAIAAAAVAAQAFAAAACAAAAABAAAAKwAEABQAAAAVAAAA"
        + "AAAAACAABAAYAAAAAgAAAAYAAAAeAAMAGwAAAAYAAAAgAAQAHAAAAAkAAAAbAAAAOwAEABwAAAAdAAAACQAAACAABAAeAAAA"
        + "CQAAAAYAAAArAAQABgAAACUAAABAAAAAKwAEAAYAAAAmAAAAAQAAACwABgAJAAAAJwAAACUAAAAmAAAAJgAAADYABQACAAAA"
        + "BAAAAAAAAAADAAAA+AACAAUAAAA7AAQABwAAAAgAAAAHAAAAQQAFAA0AAAAOAAAACwAAAAwAAAA9AAQABgAAAA8AAAAOAAAA"
        + "PgADAAgAAAAPAAAAPQAEAAYAAAAWAAAACAAAAD0ABAAGAAAAFwAAAAgAAABBAAYAGAAAABkAAAATAAAAFQAAABcAAAA9AAQA"
        + "BgAAABoAAAAZAAAAQQAFAB4AAAAfAAAAHQAAABUAAAA9AAQABgAAACAAAAAfAAAAhAAFAAYAAAAhAAAAGgAAACAAAAA9AAQA"
        + "BgAAACIAAAAIAAAAgAAFAAYAAAAjAAAAIQAAACIAAABBAAYAGAAAACQAAAATAAAAFQAAABYAAAA+AAMAJAAAACMAAAD9AAEA"
        + "OAABAA==";

    /// <summary>The vertex module.</summary>
    public static byte[] Vertex => Convert.FromBase64String(VertexBase64);

    /// <summary>The fragment module.</summary>
    public static byte[] Fragment => Convert.FromBase64String(FragmentBase64);

    /// <summary>A vertex module that reads a vertex buffer and a push constant.</summary>
    public static byte[] MeshVertex => Convert.FromBase64String(MeshVertexBase64);

    /// <summary>The fragment module that goes with it.</summary>
    public static byte[] MeshFragment => Convert.FromBase64String(MeshFragmentBase64);

    /// <summary>A compute module that reads and writes a storage buffer.</summary>
    public static byte[] Compute => Convert.FromBase64String(ComputeBase64);
}
