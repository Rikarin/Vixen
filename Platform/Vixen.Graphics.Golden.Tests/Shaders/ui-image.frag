#version 450

// A texture drawn into the interface: an image, a video frame, a viewport's render target.
//
// The same two bindings the text shader has, and deliberately so — one descriptor set layout serves
// both, so an image costs a set to allocate and nothing to design. What differs is what the sample
// *means*: text reads three distance channels and reconstructs a coverage from them, and this reads
// a colour and believes it.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;   // the tint, linear and straight-alpha
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

void main() {
    vec4 source_colour = texture(sampler2D(source, source_sampler), varying_texcoord);

    // ⚠ The sampled texture is straight alpha and the target is premultiplied, which is what the
    // other three pipelines write and therefore what the blend state expects. Multiplying the tint's
    // alpha in as well is what makes a faded-out image fade rather than turn into a dark rectangle.
    float alpha = source_colour.a * varying_colour.a;

    target = vec4(source_colour.rgb * varying_colour.rgb * alpha, alpha);
}
