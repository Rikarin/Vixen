#version 450

// A rounded rectangle and its border, as a signed distance evaluated per pixel. One quad draws any
// size at any radius with an exact edge; tessellating the corner costs vertices in proportion to the
// radius and is still faceted.

layout(location = 0) in vec2 varying_texcoord;   // offset from the box's centre, in pixels
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;
layout(location = 3) flat in int varying_index;

layout(location = 0) out vec4 target;

// ⚠ One record per box rather than fourteen more floats on every vertex. Four elliptical corners and
// a gradient would take the vertex from forty-eight bytes to a hundred and four, and every glyph in
// the frame would carry fields no shader reads on them.
struct Shape {
    vec4 size;     // half width, half height, border thickness, whether there is a gradient
    vec4 radiiX;   // clockwise from the top left
    vec4 radiiY;
    vec4 axis;     // which way the gradient runs, in the box's own space
    vec4 endColour;
};

layout(std430, set = 0, binding = 2) readonly buffer Shapes {
    Shape shapes[];
} shapeBuffer;

// The radius of the corner this pixel is nearest, from the four authored pairs.
vec2 corner_radius(Shape shape, vec2 point) {
    bool top = point.y < 0.0;
    bool left = point.x < 0.0;

    // Clockwise from the top left: TL, TR, BR, BL.
    int index = top ? (left ? 0 : 1) : (left ? 3 : 2);
    return vec2(shape.radiiX[index], shape.radiiY[index]);
}

// The signed distance to a box with an elliptical corner, negative inside.
//
// `q` is the offset from the corner ellipse's *centre*, which sits a radius in from each edge — so
// `q <= 0` on an axis means this pixel is in the band where the boundary is that straight edge, and
// only the quadrant where both are positive is the ellipse at all. Getting that wrong is not a
// rounding error: measuring from the ellipse's centre where the edge is straight reports every pixel
// down the flat part of the side as being a radius further out than it is, and the box comes back
// with its whole left edge eaten away.
//
// ⚠ In the corner quadrant the ellipse is turned into a circle by scaling, and the distance scaled
// back by the *smaller* semi-axis. The exact distance to an ellipse has no closed form and is solved
// iteratively; this is exact on the axes and within a fraction of a pixel between them, which is all
// a one-pixel antialiasing band can tell apart. Scaling back by the larger axis instead leaves the
// edge soft on the flat side of a wide corner.
float box_distance(vec2 point, vec2 half_size, vec2 radius) {
    vec2 r = min(max(radius, vec2(0.0)), half_size);
    vec2 q = abs(point) - half_size + r;

    if (r.x <= 0.0 || r.y <= 0.0) {
        vec2 square = abs(point) - half_size;
        return length(max(square, 0.0)) + min(max(square.x, square.y), 0.0);
    }

    if (q.x <= 0.0 && q.y <= 0.0) {
        // Inside the inner rectangle, where the nearest boundary is whichever straight edge is closer.
        return max(q.x - r.x, q.y - r.y);
    }

    if (q.x <= 0.0) {
        return q.y - r.y;
    }

    if (q.y <= 0.0) {
        return q.x - r.x;
    }

    return (length(q / r) - 1.0) * min(r.x, r.y);
}

// Coverage across a one-pixel band, from the derivative of the distance itself. Taking the width
// from the geometry rather than from a constant is what makes the same shader right under any
// projection and any scale.
float coverage_of(float distance, float width) {
    return clamp(0.5 - (distance / width), 0.0, 1.0);
}

void main() {
    Shape shape = shapeBuffer.shapes[varying_index];

    vec2 half_size = shape.size.xy;
    float distance = box_distance(varying_texcoord, half_size, corner_radius(shape, varying_texcoord));
    float width = max(fwidth(distance), 1e-4);
    float coverage = coverage_of(distance, width);

    float thickness = shape.size.z;

    if (thickness > 0.0) {
        // The border is the band between the edge and `thickness` inside it. Taken as the difference
        // of two coverages rather than drawn as a second shape, so the two share one antialiased
        // outer edge and cannot disagree about where it is.
        coverage -= coverage_of(distance + thickness, width);
    }

    vec4 fill = varying_colour;

    if (shape.size.w > 0.0) {
        // ⚠ The gradient runs across the box's own extent along the axis, so the same axis means the
        // same picture whatever the box's size — which is what lets one style be shared by a column
        // of buttons of different heights. Normalised by the projection of the half size onto the
        // axis, because that is how far the axis reaches before it leaves the box.
        vec2 axis = normalize(shape.axis.xy);
        float reach = abs(axis.x * half_size.x) + abs(axis.y * half_size.y);
        float t = clamp((dot(varying_texcoord, axis) / max(reach, 1e-4) * 0.5) + 0.5, 0.0, 1.0);

        fill = mix(varying_colour, shape.endColour, t);
    }

    // Premultiplied, which is what the UI blend state expects. Straight alpha here would show as a
    // dark halo around every rounded corner.
    float alpha = fill.a * coverage;
    target = vec4(fill.rgb * alpha, alpha);
}
