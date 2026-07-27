#version 450

// The fullscreen triangle, in the arithmetic Library/PostFx/Fullscreen.rvn declares.
//
// Kept as GLSL rather than compiled from the .rvn because this suite predates the shader library
// being part of any build — and because what is under test here is the *host*: that
// FullScreenRenderer draws three vertices with no vertex buffer, at UVs the source is sampled by.
// If the two derivations of this triangle ever disagree, the picture is what says so.

layout(location = 0) out vec2 uv;

void main() {
    // Twice the viewport, so the visible region is its lower-left quarter and the UV mapping stays a
    // plain 0..1 over the screen.
    float x = float(gl_VertexIndex % 2) * 4.0 - 1.0;
    float y = float(gl_VertexIndex / 2) * 4.0 - 1.0;

    gl_Position = vec4(x, y, 0.0, 1.0);

    // Origin top-left, matching Transform.NdcToUv. A flipped V here turns the picture upside down,
    // which is the single most common way a post pass is wrong and the easiest to see.
    uv = vec2(x * 0.5 + 0.5, 0.5 - y * 0.5);
}
