#version 450

// Cook-Torrance with GGX, Smith height-correlated visibility and Schlick's Fresnel — the
// microfacet model docs/plan/06 specifies, written out rather than pulled from Raven's library
// because Raven is not yet wired into the build (docs/plan/07).
//
// The ambient term is analytic and deliberately labelled as a stand-in. Real image-based lighting
// needs a prefiltered radiance cube and a BRDF integration LUT, which are an importer's output and
// not a sample's; what is here is the constant-radiance environment those two integrate to, which
// is enough to show a metal's rim behaving differently from a dielectric's and not enough to show
// a reflection.

layout(set = 0, binding = 0) uniform View {
    mat4 viewProjection;
    vec4 eye;
    vec4 lightDirection;
    vec4 lightColour;
    vec4 ambient;
} view;

layout(push_constant) uniform Push {
    mat4 model;
    vec4 material;
} push;

layout(location = 0) in vec3 worldPosition;
layout(location = 1) in vec3 worldNormal;
layout(location = 0) out vec4 target;

const float Pi = 3.14159265359;

// GGX / Trowbridge-Reitz normal distribution.
float distribution(float nDotH, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float d = (nDotH * nDotH * (a2 - 1.0)) + 1.0;
    return a2 / max(Pi * d * d, 1e-7);
}

// Smith height-correlated visibility, which already carries the 1 / (4 nDotL nDotV) denominator.
float visibility(float nDotV, float nDotL, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float v = nDotL * sqrt((nDotV * nDotV * (1.0 - a2)) + a2);
    float l = nDotV * sqrt((nDotL * nDotL * (1.0 - a2)) + a2);
    return 0.5 / max(v + l, 1e-7);
}

vec3 fresnel(vec3 f0, float vDotH) {
    return f0 + ((1.0 - f0) * pow(1.0 - vDotH, 5.0));
}

void main() {
    float metallic = push.material.x;

    // Clamped away from zero. A roughness of exactly 0 puts the GGX denominator at zero over a
    // measure-zero set of directions, and what comes out is a single infinitely bright texel — which
    // survives tone mapping as a white dot and reads as a dead pixel.
    float roughness = clamp(push.material.y, 0.045, 1.0);

    // One base colour for the whole grid, deliberately. Varying albedo across it as well would
    // confound the two axes the grid exists to separate: a sphere would be darker and it would not
    // be clear whether that was the metalness, the roughness or the colour.
    vec3 albedo = push.material.zzz * vec3(0.86, 0.62, 0.35);

    vec3 n = normalize(worldNormal);
    vec3 v = normalize(view.eye.xyz - worldPosition);
    vec3 l = normalize(view.lightDirection.xyz);
    vec3 h = normalize(v + l);

    float nDotV = max(dot(n, v), 1e-4);
    float nDotL = max(dot(n, l), 0.0);
    float nDotH = max(dot(n, h), 0.0);
    float vDotH = max(dot(v, h), 0.0);

    // A metal has no diffuse and takes its reflectance from its albedo; a dielectric reflects a
    // flat 4% and keeps its albedo for the diffuse lobe. That one line is most of what "metallic"
    // means.
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 diffuseColour = albedo * (1.0 - metallic);

    vec3 f = fresnel(f0, vDotH);
    float d = distribution(nDotH, roughness);
    float vis = visibility(nDotV, nDotL, roughness);

    vec3 specular = f * d * vis;
    vec3 diffuse = (vec3(1.0) - f) * diffuseColour / Pi;

    vec3 direct = (diffuse + specular) * view.lightColour.rgb * nDotL;

    // The stand-in environment: a constant-radiance hemisphere, which integrates to the albedo for
    // the diffuse lobe and to f0 for the specular one at grazing-free incidence. Enough to keep the
    // unlit side from being black, and honest about being no more than that.
    vec3 ambient = view.ambient.rgb * ((diffuseColour * 0.9) + (f0 * (1.0 - (roughness * 0.7))));

    target = vec4(direct + ambient, 1.0);
}
