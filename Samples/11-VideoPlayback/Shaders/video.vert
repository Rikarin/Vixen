#version 450

// A quad from the vertex index and four floats — no vertex buffer, no index buffer, no binding.
//
// ⚠ This drew a full-screen triangle until `VideoRenderer` existed, on the argument that a triangle
// covers every pixel once where a quad's diagonal makes the rasteriser evaluate a seam twice. That
// argument is correct and it is about the wrong shape: a renderer draws a video into a *rectangle* —
// a panel in a user interface, a picture-in-picture, a menu background with other things over it —
// and a full-screen triangle cannot express one. The saving was a strip of pixels along one
// diagonal, once per video.

layout(push_constant) uniform Constants {
    // xy: clip-space scale, zw: clip-space offset. Turns a 0..1 corner into a position. The y is
    // already negated on the CPU, because +y is up everywhere in this engine — see VideoConstants.
    vec4 placement;

    // xy: texture-coordinate scale, zw: offset. The crop, for a picture shown "cover".
    vec4 crop;

    // x: luma offset, y: luma scale, z: V's contribution to red, w: U's to blue.
    vec4 luma;

    // x: U's contribution to green, y: V's, z: the tint alpha, w: the sample mode.
    vec4 chroma;
} constants;

layout(location = 0) out vec2 varying_texcoord;

void main() {
    // Two triangles as 0,1,2, 2,3,0 over the corners (0,0) (1,0) (1,1) (0,1).
    int corner = int[6](0, 1, 2, 2, 3, 0)[gl_VertexIndex];
    vec2 uv = vec2(float(corner == 1 || corner == 2), float(corner >= 2));

    varying_texcoord = (uv * constants.crop.xy) + constants.crop.zw;
    gl_Position = vec4((uv * constants.placement.xy) + constants.placement.zw, 0.0, 1.0);
}
