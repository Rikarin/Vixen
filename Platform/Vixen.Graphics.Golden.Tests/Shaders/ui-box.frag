#version 450

// A rounded rectangle and its border, as a signed distance evaluated per pixel. One quad draws any
// size at any radius with an exact edge; tessellating the corner costs vertices in proportion to the
// radius and is still faceted.

layout(location = 0) in vec2 varying_texcoord;   // offset from the box's centre, in pixels
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;      // half width, half height, radius, thickness

layout(location = 0) out vec4 target;

// abs folds the box into one quadrant so one expression handles all four corners; the max with zero
// is what keeps the straight edges straight instead of bulging.
float box_distance(vec2 point, vec2 half_size, float radius) {
    float r = min(radius, min(half_size.x, half_size.y));
    vec2 q = abs(point) - half_size + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// Coverage across a one-pixel band, from the derivative of the distance itself. Taking the width
// from the geometry rather than from a constant is what makes the same shader right under any
// projection and any scale.
float coverage_of(float distance, float width) {
    return clamp(0.5 - (distance / width), 0.0, 1.0);
}

void main() {
    float distance = box_distance(varying_texcoord, varying_shape.xy, varying_shape.z);
    float width = max(fwidth(distance), 1e-4);
    float coverage = coverage_of(distance, width);

    float thickness = varying_shape.w;

    if (thickness > 0.0) {
        // The border is the band between the edge and `thickness` inside it. Taken as the difference
        // of two coverages rather than drawn as a second shape, so the two share one antialiased
        // outer edge and cannot disagree about where it is.
        coverage -= coverage_of(distance + thickness, width);
    }

    // Premultiplied, which is what the UI blend state expects. Straight alpha here would show as a
    // dark halo around every rounded corner.
    target = vec4(varying_colour.rgb * varying_colour.a * coverage, varying_colour.a * coverage);
}
