#version 450

// A composited group's surface, mixed with the picture already under it by the group's
// `mix-blend-mode` on the way into the frame — CSS Compositing 1 § 5.1 and § 5.3.
//
// The GLSL copy of `UiBlend` in `Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn`, which argues every line;
// what is here is the transcription and the few places GLSL spells a thing differently. It exists so
// that `UiRavenAgreementTests` can draw one blended frame through each and compare the two exactly —
// until it did, the sixteen modes had one implementation on the device and no second to hold it to
// (#783).
//
// ⚠ The backdrop is in set 1, texture 0 and sampler 1, for the reason the Raven gives: a second
// texture in set 0 would renumber the shared `ui atlas` layout. Set 0 is the image set every
// composite draw binds — the surface, its sampler, and the mask buffer at binding 2.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

// One entry of a `mask-image` list, sixty-four bytes. `ui-mask.frag` documents every lane.
struct MaskEntry {
    vec4 box;
    vec4 ramp;
    vec4 alphas;
    vec4 stops;
    vec4 area;
};

layout(std430, set = 0, binding = 2) readonly buffer Masks {
    MaskEntry entries[];
} masks;

// What the group composites into, captured before its composite draw — premultiplied, in the frame's
// units.
layout(set = 1, binding = 0) uniform texture2D backdrop;
layout(set = 1, binding = 1) uniform sampler backdrop_sampler;

// ⚠ At offset 16, past the vertex stage's projection, and in the Raven's declaration order: the
// operation, the filter flag, the three matrix rows and the mask list — 16 + 96 bytes.
layout(push_constant) uniform Blend {
    // `x` the mode as `UiBlendMode` numbers it, `y` the frame's white level, `zw` one over the
    // capture's size in texels for a transformed group and zero for any other.
    layout(offset = 16) vec4 operation;

    // `x` one when the group carries a `filter` colour matrix.
    vec4 filtering;

    vec4 red;
    vec4 green;
    vec4 blue;

    // `x` the first mask entry, `y` how many, `z` framebuffer pixels per document pixel.
    vec4 list;
} push;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

// `UiComposite.Filter`: the colour matrix on premultiplied colour, clamped to `[0, a·w]`.
vec3 composite_filter(vec4 sampled, float white) {
    float lit = sampled.a * (white > 0.0 ? white : 1.0);

    vec3 filtered = vec3(
        dot(push.red.rgb, sampled.rgb) + (push.red.w * lit),
        dot(push.green.rgb, sampled.rgb) + (push.green.w * lit),
        dot(push.blue.rgb, sampled.rgb) + (push.blue.w * lit)
    );

    return clamp(filtered, vec3(0.0), vec3(lit));
}

// `UiMaskList`, transcribed as `ui-mask.frag` transcribes it.
float mask_span(float t, float from, float to) {
    float width = to - from;

    return width > 1e-4 ? clamp((t - from) / width, 0.0, 1.0) : (t < from ? 0.0 : 1.0);
}

float mask_progress(vec2 offset, vec2 half_size, vec2 axis, int kind) {
    if (kind == 2) {
        vec2 normalised = offset / max(half_size, vec2(1e-4));

        return length(normalised) * 0.70710678;
    }

    if (kind == 3) {
        float angle = atan(offset.x, -offset.y) - atan(axis.x, -axis.y);
        float turns = (angle * 0.15915494309189535) + 1.0;

        return fract(turns);
    }

    vec2 direction = dot(axis, axis) > 1e-12 ? normalize(axis) : vec2(1.0, 0.0);
    float reach = abs(direction.x * half_size.x) + abs(direction.y * half_size.y);

    return ((dot(offset, direction) / max(reach, 1e-4)) * 0.5) + 0.5;
}

float mask_wrap(float value, float half_tile) {
    float period = half_tile * 2.0;
    float shifted = value + half_tile;

    return shifted - (period * floor(shifted / period)) - half_tile;
}

float mask_coverage(MaskEntry entry, vec2 point) {
    vec2 local = point;

    if (entry.area.z != 0.0 || entry.area.w != 0.0) {
        vec2 tile = abs(entry.area.zw);
        vec2 inside = point - entry.area.xy;

        if ((entry.area.z <= 0.0 && abs(inside.x) > tile.x)
            || (entry.area.w <= 0.0 && abs(inside.y) > tile.y)) {
            return 0.0;
        }

        local = entry.area.xy + vec2(
            entry.area.z > 0.0 ? mask_wrap(inside.x, tile.x) : inside.x,
            entry.area.w > 0.0 ? mask_wrap(inside.y, tile.y) : inside.y
        );
    }

    float progress = mask_progress(local - entry.box.xy, entry.box.zw, entry.ramp.xy, int(entry.ramp.z + 0.5));

    return entry.ramp.w > 0.5
        ? (progress < entry.stops.x
            ? mix(entry.alphas.x, entry.alphas.y, mask_span(progress, entry.alphas.w, entry.stops.x))
            : mix(entry.alphas.y, entry.alphas.z, mask_span(progress, entry.stops.x, entry.stops.y)))
        : mix(entry.alphas.x, entry.alphas.z, mask_span(progress, entry.alphas.w, entry.stops.y));
}

float mask_compose(int operation, float source, float backdrop) {
    if (operation == 1) {
        return source * (1.0 - backdrop);
    }

    if (operation == 2) {
        return source * backdrop;
    }

    if (operation == 3) {
        return (source * (1.0 - backdrop)) + (backdrop * (1.0 - source));
    }

    return source + (backdrop * (1.0 - source));
}

float mask_list(vec2 point, int first, int count) {
    if (count <= 0) {
        return 1.0;
    }

    float result = mask_coverage(masks.entries[first + count - 1], point);

    // ⚠ Counted up and indexed down, as `UiBlend.List` walks it, rather than `ui-mask.frag`'s
    // descending loop: the same entries in the same order, and the arithmetic census holds this copy
    // to the integer additions its shader performs.
    for (int index = 0; index <= count - 2; index++) {
        int at = count - 2 - index;
        MaskEntry entry = masks.entries[first + at];

        result = mask_compose(int(entry.stops.z + 0.5), mask_coverage(entry, point), result);
    }

    return clamp(result, 0.0, 1.0);
}

// `hard-light`, and `overlay` with its operands swapped.
float blend_hard(float cb, float cs) {
    if (cs <= 0.5) {
        return cb * (2.0 * cs);
    }

    float screen = (2.0 * cs) - 1.0;

    return cb + screen - (cb * screen);
}

// ⚠ The specification's order of the three cases: a backdrop of zero stays zero under any source.
float blend_dodge(float cb, float cs) {
    if (cb <= 0.0) {
        return 0.0;
    }

    if (cs >= 1.0) {
        return 1.0;
    }

    return min(1.0, cb / (1.0 - cs));
}

float blend_burn(float cb, float cs) {
    if (cb >= 1.0) {
        return 1.0;
    }

    if (cs <= 0.0) {
        return 0.0;
    }

    return 1.0 - min(1.0, (1.0 - cb) / cs);
}

// ⚠ `D(Cb)` is a cubic up to a quarter and a square root above it.
float blend_soft(float cb, float cs) {
    float d = cb <= 0.25 ? ((((16.0 * cb) - 12.0) * cb) + 4.0) * cb : sqrt(cb);

    if (cs <= 0.5) {
        return cb - ((1.0 - (2.0 * cs)) * cb * (1.0 - cb));
    }

    return cb + (((2.0 * cs) - 1.0) * (d - cb));
}

// § 5.3's luminosity, with its three weights.
float blend_lum(vec3 c) {
    return (0.3 * c.x) + (0.59 * c.y) + (0.11 * c.z);
}

// ⚠ The luma and both extremes are taken once, before either adjustment.
vec3 blend_clip(vec3 colour) {
    vec3 c = colour;
    float l = blend_lum(colour);
    float low = min(colour.x, min(colour.y, colour.z));
    float high = max(colour.x, max(colour.y, colour.z));

    if (low < 0.0 && l - low > 0.000001) {
        c = vec3(l) + ((c - vec3(l)) * (l / (l - low)));
    }

    if (high > 1.0 && high - l > 0.000001) {
        c = vec3(l) + ((c - vec3(l)) * ((1.0 - l) / (high - l)));
    }

    return c;
}

vec3 blend_set_lum(vec3 c, float lum) {
    return blend_clip(c + vec3(lum - blend_lum(c)));
}

float blend_sat(vec3 c) {
    return max(c.x, max(c.y, c.z)) - min(c.x, min(c.y, c.z));
}

float blend_channel(vec3 c, int index) {
    if (index == 1) {
        return c.y;
    }

    if (index == 2) {
        return c.z;
    }

    return c.x;
}

float blend_place(int index, int low, int middle, float stretched, float sat) {
    if (index == low) {
        return 0.0;
    }

    if (index == middle) {
        return stretched;
    }

    return sat;
}

// ⚠ Indices, not a sort: the first strictly smallest and the first strictly largest, and a grey —
// no saturation to stretch — comes back black, where § 5.3 would divide by zero.
vec3 blend_set_sat(vec3 c, float sat) {
    int low = 0;
    int high = 0;

    if (c.y < c.x) {
        low = 1;
    }

    if (c.z < blend_channel(c, low)) {
        low = 2;
    }

    if (c.y > c.x) {
        high = 1;
    }

    if (c.z > blend_channel(c, high)) {
        high = 2;
    }

    if (low == high) {
        return vec3(0.0);
    }

    int middle = 3 - low - high;
    float span = blend_channel(c, high) - blend_channel(c, low);
    float stretched = ((blend_channel(c, middle) - blend_channel(c, low)) * sat) / span;

    return vec3(
        blend_place(0, low, middle, stretched, sat),
        blend_place(1, low, middle, stretched, sat),
        blend_place(2, low, middle, stretched, sat)
    );
}

// § 5.1's `B(Cb, Cs)` for the sixteen modes, numbered as `UiBlendMode` numbers them.
vec3 blend_mix(int mode, vec3 cb, vec3 cs) {
    if (mode == 1) {
        return cb * cs;
    }

    if (mode == 2) {
        return cb + cs - (cb * cs);
    }

    if (mode == 3) {
        return vec3(blend_hard(cs.x, cb.x), blend_hard(cs.y, cb.y), blend_hard(cs.z, cb.z));
    }

    if (mode == 4) {
        return min(cb, cs);
    }

    if (mode == 5) {
        return max(cb, cs);
    }

    if (mode == 6) {
        return vec3(blend_dodge(cb.x, cs.x), blend_dodge(cb.y, cs.y), blend_dodge(cb.z, cs.z));
    }

    if (mode == 7) {
        return vec3(blend_burn(cb.x, cs.x), blend_burn(cb.y, cs.y), blend_burn(cb.z, cs.z));
    }

    if (mode == 8) {
        return vec3(blend_hard(cb.x, cs.x), blend_hard(cb.y, cs.y), blend_hard(cb.z, cs.z));
    }

    if (mode == 9) {
        return vec3(blend_soft(cb.x, cs.x), blend_soft(cb.y, cs.y), blend_soft(cb.z, cs.z));
    }

    if (mode == 10) {
        return abs(cb - cs);
    }

    if (mode == 11) {
        return cb + cs - (2.0 * cb * cs);
    }

    if (mode == 12) {
        return blend_set_lum(blend_set_sat(cs, blend_sat(cb)), blend_lum(cb));
    }

    if (mode == 13) {
        return blend_set_lum(blend_set_sat(cb, blend_sat(cs)), blend_lum(cb));
    }

    if (mode == 14) {
        return blend_set_lum(cs, blend_lum(cb));
    }

    if (mode == 15) {
        return blend_set_lum(cb, blend_lum(cs));
    }

    return cs;
}

// `UiBlend.Apply`: § 5.1's weighting of the mixed colour, on un-premultiplied operands normalised by
// the frame's white, re-premultiplied and re-lit.
vec4 blend_apply(int mode, vec4 group, vec4 under, float white) {
    if (mode == 0 || under.a <= 0.0 || group.a <= 0.0) {
        return group;
    }

    float scale = white > 0.0 ? white : 1.0;
    vec3 cs = clamp(group.rgb / (group.a * scale), vec3(0.0), vec3(1.0));
    vec3 cb = clamp(under.rgb / (under.a * scale), vec3(0.0), vec3(1.0));
    vec3 mixed = ((1.0 - under.a) * cs) + (under.a * blend_mix(mode, cb, cs));

    return vec4(mixed * (group.a * scale), group.a);
}

void main() {
    // The group as it will be composited: filtered, then masked, then faded by the quad's alpha.
    vec4 sampled = texture(sampler2D(source, source_sampler), varying_texcoord);
    vec3 paint = push.filtering.x > 0.5 ? composite_filter(sampled, push.operation.y) : sampled.rgb;

    ivec2 size = textureSize(sampler2D(source, source_sampler), 0);
    vec2 point = (varying_texcoord * vec2(float(size.x), float(size.y))) / max(push.list.z, 0.0001);
    float coverage = push.list.y > 0.5 ? mask_list(point, int(push.list.x + 0.5), int(push.list.y + 0.5)) : 1.0;
    vec4 group = vec4(paint * varying_colour.rgb * varying_colour.a * coverage, sampled.a * varying_colour.a * coverage);

    // The window position for a transformed group, the surface coordinate otherwise.
    vec2 at = push.operation.z > 0.0 ? gl_FragCoord.xy * push.operation.zw : varying_texcoord;
    vec4 under = texture(sampler2D(backdrop, backdrop_sampler), at);

    target = blend_apply(int(push.operation.x + 0.5), group, under, push.operation.y);
}
