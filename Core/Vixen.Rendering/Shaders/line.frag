#version 450

layout(location = 0) in vec4 varying_colour;

layout(location = 0) out vec4 target;

void main() {
    // Premultiplied, which is what the alpha blend state expects — a grid whose distant lines fade
    // is the whole reason these carry an alpha at all.
    target = vec4(varying_colour.rgb * varying_colour.a, varying_colour.a);
}
