#version 450

// The fullscreen triangle again, but with UVs scaled past 0..1 by a push constant.
//
// Which is what makes an address mode visible: at a scale of two, `Repeat` tiles the source four
// times and `ClampToEdge` stretches its edge texels across three quarters of the target.

layout(push_constant) uniform Push { vec2 scale; } push;
layout(location = 0) out vec2 uv;

void main() {
    float x = float(gl_VertexIndex % 2) * 4.0 - 1.0;
    float y = float(gl_VertexIndex / 2) * 4.0 - 1.0;

    gl_Position = vec4(x, y, 0.0, 1.0);
    uv = vec2(x * 0.5 + 0.5, 0.5 - y * 0.5) * push.scale;
}
