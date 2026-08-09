#version 450

// A rounded rectangle and its border, as a signed distance evaluated per pixel. One quad draws any
// size at any radius with an exact edge; tessellating the corner costs vertices in proportion to the
// radius and is still faceted.

layout(location = 0) in vec2 varying_texcoord;   // offset from the box's centre, in pixels
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;
layout(location = 3) flat in int varying_index;

layout(location = 0) out vec4 target;

// ⚠ One record per box rather than twenty-odd more floats on every vertex. Four elliptical corners
// and a three-stop gradient would take the vertex from forty-eight bytes to well past a hundred, and
// every glyph in the frame would carry fields no shader reads on them.
//
// ⚠ **`Shape` is 112 bytes and there are five places that have to agree about that**, which is more
// than any one of them says on its own: `Vixen.Ui.Rendering.UiShape`, `UiRenderer`'s buffer stride,
// `SoftwareUiRasterizer`, the editor's `Ui.rvn`, and this file — of which there are three copies.
// `UiShapeLayoutTests` pins the first against the editor's reflection; the rest are pinned only by
// `Vixen.Graphics.Golden.Tests`, on a real device, which is how the 80-byte stride was caught.
struct Shape {
    vec4 size;       // half width, half height, border thickness, shape: 0 none 1 linear 2 radial 3 conic
    vec4 radiiX;     // clockwise from the top left
    vec4 radiiY;
    vec4 axis;       // gradient direction, a shadow's blur, then the space: 0 linear 1 sRGB 2 Oklab
    vec4 endColour;  // the last stop
    vec4 midColour;  // the middle stop, read only when stops.w is set
    vec4 stops;      // where the three stops sit, then whether the middle one exists
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

// A shadow's coverage: the same distance, faded over the blur radius instead of over a pixel.
//
// ⚠ `smoothstep` rather than a linear ramp, and it matters. A linear falloff has a corner in it at
// both ends, and a corner in the alpha of a large soft shadow is visible as a ring — the eye finds
// the second derivative of a gradient far more readily than the first. The cubic is not a Gaussian
// either, but it is C1 at both ends, which is the property that makes it look like light.
float shadow_coverage(float distance, float blur) {
    return 1.0 - smoothstep(-blur, blur, distance);
}

// ── Gradients ────────────────────────────────────────────────────────────────────────────────────
//
// ⚠ This is a transcription of `Editor/Vixen.Editor.Host/Shaders/Ui.rvn`'s `UiBox`, which is the
// version the editor draws with, and of `SoftwareUiRasterizer`, which is the version the UI test
// suite compares against. Three implementations of one shader is not a design anybody chose — see
// `Core/Vixen.Ui.Renderer/README.md` — but while they exist they have to agree, because a gradient
// that fades differently in a sample from the way it fades in the editor reads as a driver bug.

const float INV_ROOT_TWO = 0.7071067811865476;
const float INV_TWO_PI = 0.15915494309189535;

// Where this pixel sits along the gradient's ramp, before the stop positions are applied.
//
// ⚠ All three shapes take the same offset-from-centre and none needs a centre or a radius in the
// record. CSS's defaults for the two round shapes are both *at center* with an extent that is a
// function of the box, which is what let them cost no lanes at all.
float gradient_parameter(Shape shape, vec2 point, vec2 half_size) {
    int kind = int(shape.size.w + 0.5);

    if (kind == 2) {
        // Radial: CSS's `ellipse farthest-corner at center`. Dividing by the half size gives the
        // farthest-*side* ellipse, on which the corner sits at root two — so the reciprocal of root
        // two is the whole of `farthest-corner`. Get that scale wrong and the picture stays round
        // while the ramp finishes in the wrong place.
        return length(point / max(half_size, vec2(1e-4))) * INV_ROOT_TWO;
    }

    if (kind == 3) {
        // Conic: CSS sweeps clockwise from twelve o'clock, and screen space is y-down, so up is -y
        // and `atan(x, -y)` is already CSS's angle. The axis carries the `from` angle, written by
        // the host as (sin θ, -cos θ), which this same convention inverts exactly.
        //
        // ⚠ The difference of two `atan` lies in (-2π, 2π), so the turn of bias is what makes the
        // wrap land in [0, 1) rather than leaving half the disc negative and clamped flat.
        float angle = atan(point.x, -point.y) - atan(shape.axis.x, -shape.axis.y);
        return fract((angle * INV_TWO_PI) + 1.0);
    }

    // Linear: across the box's own extent along the axis, so one style suits boxes of any size.
    vec2 axis = normalize(shape.axis.xy);
    float reach = abs(axis.x * half_size.x) + abs(axis.y * half_size.y);

    return (dot(point, axis) / max(reach, 1e-4) * 0.5) + 0.5;
}

// Where `t` sits between two stops, flat outside them.
//
// ⚠ A zero-width span is a hard edge rather than a division by zero: `from-50% to-50%` is a legal
// declaration and a step is what it means.
float gradient_span(float t, float from, float to) {
    float width = to - from;
    return width > 1e-4 ? clamp((t - from) / width, 0.0, 1.0) : (t < from ? 0.0 : 1.0);
}

float srgb_from_linear1(float v) {
    return v <= 0.0031308 ? v * 12.92 : (1.055 * pow(max(v, 0.0), 1.0 / 2.4)) - 0.055;
}

float linear_from_srgb1(float v) {
    return v <= 0.04045 ? v / 12.92 : pow(max(v + 0.055, 0.0) / 1.055, 2.4);
}

// A *signed* cube root.
//
// ⚠ `pow` is NaN for a negative base, and a linear component here genuinely can be negative: the
// palette ships in `oklch` and a swatch outside the sRGB gamut reaches the draw list with a
// component below zero. Without the sign the midpoint of such a gradient is a NaN pixel, which
// blends into the target as a hole and reads as a compositing bug.
float cbrt1(float x) {
    return sign(x) * pow(abs(x), 1.0 / 3.0);
}

// Björn Ottosson's transform, matching the host's `Vixen.Core.Mathematics.Oklab`.
vec3 oklab_from_linear(vec3 c) {
    float l = (0.4122214708 * c.r) + (0.5363325363 * c.g) + (0.0514459929 * c.b);
    float m = (0.2119034982 * c.r) + (0.6806995451 * c.g) + (0.1073969566 * c.b);
    float s = (0.0883024619 * c.r) + (0.2817188376 * c.g) + (0.6299787005 * c.b);

    float lc = cbrt1(l);
    float mc = cbrt1(m);
    float sc = cbrt1(s);

    return vec3(
        (0.2104542553 * lc) + (0.7936177850 * mc) - (0.0040720468 * sc),
        (1.9779984951 * lc) - (2.4285922050 * mc) + (0.4505937099 * sc),
        (0.0259040371 * lc) + (0.7827717662 * mc) - (0.8086757660 * sc)
    );
}

vec3 linear_from_oklab(vec3 c) {
    float lc = c.x + (0.3963377774 * c.y) + (0.2158037573 * c.z);
    float mc = c.x - (0.1055613458 * c.y) - (0.0638541728 * c.z);
    float sc = c.x - (0.0894841775 * c.y) - (1.2914855480 * c.z);

    float l = lc * lc * lc;
    float m = mc * mc * mc;
    float s = sc * sc * sc;

    return vec3(
        (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
        (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
        (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s)
    );
}

// Mixes two stops in whichever space the record names: 0 linear RGB, 1 sRGB, 2 Oklab.
//
// ⚠ Zero is linear RGB because that is what this shader did before there was a choice, so a record
// written by code that predates the choice draws exactly what it drew. CSS's own default is sRGB and
// the host writes that explicitly.
vec4 gradient_mix(Shape shape, vec4 a, vec4 b, float u) {
    int space = int(shape.axis.w + 0.5);

    if (space == 2) {
        vec3 mixed = linear_from_oklab(mix(oklab_from_linear(a.rgb), oklab_from_linear(b.rgb), u));
        return vec4(mixed, mix(a.a, b.a, u));
    }

    if (space == 1) {
        vec3 ea = vec3(srgb_from_linear1(a.r), srgb_from_linear1(a.g), srgb_from_linear1(a.b));
        vec3 eb = vec3(srgb_from_linear1(b.r), srgb_from_linear1(b.g), srgb_from_linear1(b.b));
        vec3 m = mix(ea, eb, u);

        return vec4(linear_from_srgb1(m.r), linear_from_srgb1(m.g), linear_from_srgb1(m.b), mix(a.a, b.a, u));
    }

    return mix(a, b, u);
}

// The colour at `t`, through the stop positions and in the interpolation space that was asked for.
vec4 gradient_colour(Shape shape, vec4 near, float t) {
    if (shape.stops.w > 0.0) {
        // ⚠ Which side of the middle *stop*, not of one half. With `via-40%` the two halves of the
        // ramp are different lengths, and splitting at 0.5 draws the middle colour in the right
        // place with the wrong slope either side of it.
        if (t < shape.stops.y) {
            return gradient_mix(shape, near, shape.midColour, gradient_span(t, shape.stops.x, shape.stops.y));
        }

        return gradient_mix(shape, shape.midColour, shape.endColour, gradient_span(t, shape.stops.y, shape.stops.z));
    }

    return gradient_mix(shape, near, shape.endColour, gradient_span(t, shape.stops.x, shape.stops.z));
}

void main() {
    Shape shape = shapeBuffer.shapes[varying_index];

    vec2 half_size = shape.size.xy;
    float distance = box_distance(varying_texcoord, half_size, corner_radius(shape, varying_texcoord));
    float width = max(fwidth(distance), 1e-4);
    float blur = shape.axis.z;

    // A blurred box is a shadow, and its edge is the blur rather than a pixel. Branching here rather
    // than in a second shader because everything above this line — the corner selection, the
    // elliptical distance — is the same work, and a shadow that disagreed with its own box about
    // where the boundary is would sit visibly off it.
    float coverage = blur > 0.0 ? shadow_coverage(distance, blur) : coverage_of(distance, width);

    float thickness = shape.size.z;

    if (thickness > 0.0) {
        // The border is the band between the edge and `thickness` inside it. Taken as the difference
        // of two coverages rather than drawn as a second shape, so the two share one antialiased
        // outer edge and cannot disagree about where it is.
        coverage -= coverage_of(distance + thickness, width);
    }

    vec4 fill = varying_colour;

    if (shape.size.w > 0.0) {
        float t = gradient_parameter(shape, varying_texcoord, half_size);
        fill = gradient_colour(shape, varying_colour, t);
    }

    // Premultiplied, which is what the UI blend state expects. Straight alpha here would show as a
    // dark halo around every rounded corner.
    float alpha = fill.a * coverage;
    target = vec4(fill.rgb * alpha, alpha);
}
