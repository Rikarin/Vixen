// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Assimp;
using Vixen.Core.Mathematics;
using AssimpApi = Silk.NET.Assimp.Assimp;
using NumericsVector4 = System.Numerics.Vector4;

namespace Vixen.Interchange;

/// <summary>
///     Where an image lands on its texture coordinates, in <c>KHR_texture_transform</c>'s own terms: the coordinate
///     a texel is read at is the mesh's, scaled, then turned, then moved.
/// </summary>
/// <param name="Offset">How far the coordinate moves, in fractions of the image.</param>
/// <param name="Rotation">The turn, in radians, counter-clockwise in UV space.</param>
/// <param name="Scale">What the coordinate is multiplied by along each axis.</param>
public sealed record ModelTextureTransform(Vector2 Offset, float Rotation, Vector2 Scale) {
    /// <summary>The coordinate an image is read at for a mesh's <paramref name="uv" />, as glTF's spec writes it.</summary>
    /// <param name="uv">The mesh's coordinate.</param>
    /// <returns>Where in the image it reads.</returns>
    public Vector2 Apply(Vector2 uv) {
        var cos = MathF.Cos(Rotation);
        var sin = MathF.Sin(Rotation);
        var x = uv.X * Scale.X;
        var y = uv.Y * Scale.Y;
        return new((cos * x) + (sin * y) + Offset.X, (-sin * x) + (cos * y) + Offset.Y);
    }

    /// <summary>
    ///     glTF's transform back out of the one Assimp stores, which is moved to Assimp's own lower-left origin, turned
    ///     the other way, and then flipped back by the reader's <c>FlipUVs</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Assimp's glTF importer negates the rotation and rewrites the translation for its own convention, and
    ///     <c>FlipUVs</c> negates the rotation and the translation's Y again. This undoes exactly that arithmetic, so a
    ///     file's offset comes back as the file wrote it; a reader that took Assimp's numbers as glTF's would move every
    ///     transformed image.
    /// </remarks>
    internal static ModelTextureTransform FromAssimp(
        float translationX,
        float translationY,
        float scaleX,
        float scaleY,
        float rotation
    ) {
        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);
        var offsetX = translationX - (0.5f * scaleX * (-cos + sin + 1f));
        var offsetY = (0.5f * scaleY * (sin + cos - 1f)) + 1f - scaleY + translationY;
        return new(new(offsetX, offsetY), rotation, new(scaleX, scaleY));
    }
}

/// <summary>What a material says of its emission and its alpha, from the keys Assimp's glTF importer fills.</summary>
static class ModelMaterialSurface {
    /// <summary>The emissive colour, linear, or black for a material that names none.</summary>
    public static unsafe Vector3 EmissiveFactor(AssimpApi assimp, Material* material) {
        var colour = new NumericsVector4(0f, 0f, 0f, 1f);
        return assimp.GetMaterialColor(material, "$clr.emissive", 0, 0, ref colour) == Return.Success
            ? new(colour.X, colour.Y, colour.Z)
            : Vector3.Zero;
    }

    /// <summary><c>KHR_materials_emissive_strength</c>, or one.</summary>
    public static unsafe float EmissiveStrength(AssimpApi assimp, Material* material) =>
        Float(assimp, material, "$mat.emissiveIntensity", 1f);

    /// <summary>glTF's <c>alphaMode</c>, or opaque for a format that has none.</summary>
    public static unsafe ModelAlphaMode AlphaMode(AssimpApi assimp, Material* material) {
        AssimpString mode = default;
        if (assimp.GetMaterialString(material, "$mat.gltf.alphaMode", 0, 0, ref mode) != Return.Success) {
            return ModelAlphaMode.Opaque;
        }

        return mode.AsString switch {
            "MASK" => ModelAlphaMode.Mask,
            "BLEND" => ModelAlphaMode.Blend,
            _ => ModelAlphaMode.Opaque
        };
    }

    /// <summary>glTF's <c>alphaCutoff</c>, or its default of a half.</summary>
    public static unsafe float AlphaCutoff(AssimpApi assimp, Material* material) =>
        Float(assimp, material, "$mat.gltf.alphaCutoff", 0.5f);

    /// <summary>The texture transform of the first image of <paramref name="type" />, or null for none.</summary>
    /// <remarks>An identity transform is null too, so a reader asks one question: is the image placed?</remarks>
    public static unsafe ModelTextureTransform? Transform(AssimpApi assimp, Material* material, TextureType type) {
        Span<float> stored = stackalloc float[5];
        var count = 5u;
        if (assimp.GetMaterialFloatArray(material, "$tex.uvtrafo", (uint)type, 0, ref stored[0], ref count)
            != Return.Success
            || count < 5) {
            return null;
        }

        var transform = ModelTextureTransform.FromAssimp(stored[0], stored[1], stored[2], stored[3], stored[4]);
        const float near = 1e-6f;
        var identity = MathF.Abs(transform.Offset.X) < near
            && MathF.Abs(transform.Offset.Y) < near
            && MathF.Abs(transform.Rotation) < near
            && MathF.Abs(transform.Scale.X - 1f) < near
            && MathF.Abs(transform.Scale.Y - 1f) < near;
        return identity ? null : transform;
    }

    static unsafe float Float(AssimpApi assimp, Material* material, string key, float fallback) {
        var value = fallback;
        var one = 1u;
        return assimp.GetMaterialFloatArray(material, key, 0, 0, ref value, ref one) == Return.Success
            ? value
            : fallback;
    }
}
