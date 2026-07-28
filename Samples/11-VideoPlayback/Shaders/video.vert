#version 450

// One triangle that covers the screen, built from the vertex index alone — no vertex buffer, no
// index buffer, no binding. A quad would be two triangles and a seam down the diagonal where the
// rasteriser evaluates the same pixels twice.

layout(location = 0) out vec2 varying_texcoord;

void main() {
    // (0,0) (2,0) (0,2) in texture space, which is (-1,-1) (3,-1) (-1,3) in clip space: a triangle
    // whose hypotenuse is off screen and whose interior covers every pixel exactly once.
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);

    varying_texcoord = uv;
    gl_Position = vec4((uv * 2.0) - 1.0, 0.0, 1.0);
}
