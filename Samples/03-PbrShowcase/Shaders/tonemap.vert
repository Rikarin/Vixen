#version 450

// The fullscreen triangle, with no vertex buffer. Three vertices covering twice the viewport, so
// the visible region is one triangle rather than two and there is no seam down the diagonal.

layout(location = 0) out vec2 uv;

void main() {
    float x = float((gl_VertexIndex << 1) & 2) * 2.0 - 1.0;
    float y = float(gl_VertexIndex & 2) * 2.0 - 1.0;

    gl_Position = vec4(x, y, 0.0, 1.0);

    // ⚠ V is inverted, and it has to be. Clip +y is *up* — the engine's convention, which the Vulkan
    // backend gets with a negative-height viewport — while texel row zero is the *top* of the image.
    // So the bottom of the triangle samples the first row, and the obvious `y * 0.5 + 0.5` presents
    // the scene upside down.
    //
    // Which is exactly what the first version of this sample did, and exactly what
    // `Vixen.Graphics.Golden.Tests/Shaders/fullscreen.vert` warns about in its own comment: "a
    // flipped V here turns the picture upside down, which is the single most common way a post pass
    // is wrong and the easiest to see." Easiest to see, and invisible to everything that is not a
    // picture — the sample ran five frames with no validation error at all.
    uv = vec2(x * 0.5 + 0.5, 0.5 - y * 0.5);
}
