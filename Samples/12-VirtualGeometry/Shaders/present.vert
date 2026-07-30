#version 450

// The fullscreen triangle, with no vertex buffer — Samples/03's, verbatim, warning included.

layout(location = 0) out vec2 uv;

void main() {
    float x = float((gl_VertexIndex << 1) & 2) * 2.0 - 1.0;
    float y = float(gl_VertexIndex & 2) * 2.0 - 1.0;

    gl_Position = vec4(x, y, 0.0, 1.0);

    // ⚠ V is inverted, and it has to be. Clip +y is *up* — the engine's convention, which the Vulkan
    // backend gets with a negative-height viewport — while texel row zero is the *top* of the image.
    // See Samples/03's tonemap.vert, which learnt this the visible way.
    uv = vec2(x * 0.5 + 0.5, 0.5 - y * 0.5);
}
