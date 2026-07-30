// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>Mapping the IR type model onto GLSL's spelling.</summary>
public static class GlslTypes {
    /// <summary>Words a generated identifier must not collide with.</summary>
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) {
        "attribute",
        "const",
        "uniform",
        "varying",
        "buffer",
        "shared",
        "coherent",
        "volatile",
        "restrict",
        "readonly",
        "writeonly",
        "layout",
        "centroid",
        "flat",
        "smooth",
        "noperspective",
        "patch",
        "sample",
        "break",
        "continue",
        "do",
        "for",
        "while",
        "switch",
        "case",
        "default",
        "if",
        "else",
        "subroutine",
        "in",
        "out",
        "inout",
        "true",
        "false",
        "invariant",
        "precise",
        "discard",
        "return",
        "struct",
        "void",
        "bool",
        "int",
        "uint",
        "float",
        "double",
        "lowp",
        "mediump",
        "highp",
        "precision",
        "main",

        // GLSL's "reserved for future use" list, which is reserved just as hard as the rest of the
        // grammar and is the half that gets forgotten. `Library/Ui/RoundedRect.rvn` found it: a local
        // called `half` is perfectly good Raven and emitted GLSL that `glslc` rejects outright, with
        // nothing in Raven having said a word. Every one of these is an ordinary identifier in Raven.
        "common",
        "partition",
        "active",
        "asm",
        "class",
        "union",
        "enum",
        "typedef",
        "template",
        "this",
        "resource",
        "goto",
        "inline",
        "noinline",
        "public",
        "static",
        "extern",
        "external",
        "interface",
        "long",
        "short",
        "half",
        "fixed",
        "unsigned",
        "superp",
        "input",
        "output",
        "filter",
        "sizeof",
        "cast",
        "namespace",
        "using",
        "hvec2",
        "hvec3",
        "hvec4",
        "fvec2",
        "fvec3",
        "fvec4",
        "sampler3DRect"
    };

    /// <summary>
    ///     The GLSL name for a type, or null when GLSL cannot express it.
    /// </summary>
    /// <remarks>
    ///     Matrices flip: Raven writes <c>matRxC</c> as R rows by C columns, GLSL
    ///     writes <c>matCxR</c> as C columns by R rows. The flip is what keeps
    ///     <c>m * v</c> meaning the same thing in both languages.
    /// </remarks>
    public static string? Name(IrType type) =>
        type switch {
            IrScalarType scalar => scalar.Kind switch {
                IrTypeKind.Void => "void",
                IrTypeKind.Bool => "bool",
                IrTypeKind.Int => "int",
                IrTypeKind.UInt => "uint",
                IrTypeKind.Float => "float",
                IrTypeKind.Double => "double",
                // The explicit-width names, which is what the extension that provides them calls
                // them; GLSL's own `int64_t` has no other spelling.
                IrTypeKind.Int64 => "int64_t",
                IrTypeKind.UInt64 => "uint64_t",
                _ => null
            },
            IrVectorType vector => VectorName(vector),
            IrMatrixType matrix => MatrixName(matrix),
            IrStructType structType => structType.Name,
            // Vulkan GLSL has separate texture and sampler objects, so both survive as
            // themselves and pair up at the sample site — the same shape as SPIR-V, and
            // the reason the two backends agree about binding indices.
            IrTextureType texture => texture.Dimension switch {
                IrTextureDimension.Texture2D => "texture2D",
                IrTextureDimension.Texture3D => "texture3D",
                _ => "textureCube"
            },
            IrSamplerType => "sampler",

            // A storage image is its own GLSL type, prefixed by the component class it reads as:
            // `image2D` for float, `iimage2D` for int, `uimage2D` for uint. Nothing pairs with it —
            // there is no sampler — which is why a load is `imageLoad` rather than `texelFetch`.
            IrStorageImageType image =>
                image.TexelType.ComponentType.Kind switch {
                    IrTypeKind.Int => "i",
                    IrTypeKind.UInt => "u",
                    _ => string.Empty
                }
                + (image.Dimension == IrTextureDimension.Texture3D ? "image3D" : "image2D"),

            // GLSL's prefix array type: `float[4]`, which is what an array constructor spells.
            // A declaration puts the extents after the *name* instead — see `Declare` — and both
            // forms are legal GLSL for the same type, which is why the two are separate methods.
            IrArrayType array => Extents(array) is { } extents ? extents.Element + extents.Suffix : null,

            _ => null
        };

    /// <summary>
    ///     An array's element type and its <c>[a][b]</c> extents, outermost first, or null when
    ///     any part of it has no GLSL spelling. An array of arrays is one declaration with two
    ///     extents in both targets, never a nested type name.
    /// </summary>
    static (string Element, string Suffix)? Extents(IrArrayType array, bool allowUnsizedOuter = false) {
        var suffix = new StringBuilder();
        IrType type = array;
        var outermost = true;

        while (type is IrArrayType inner) {
            // Only the outermost extent may be omitted, and only as a storage block's last member —
            // which is exactly the one position the caller sets the flag for.
            if (inner.Length is not { } length) {
                if (!(outermost && allowUnsizedOuter)) {
                    return null;
                }

                suffix.Append("[]");
                type = inner.Element;
                outermost = false;
                continue;
            }

            suffix.Append('[').Append(length).Append(']');
            type = inner.Element;
            outermost = false;
        }

        return Name(type) is { } element ? (element, suffix.ToString()) : null;
    }

    /// <summary>
    ///     The combined sampler type a texture pairs into — what <c>sampler2D(t, s)</c>
    ///     constructs at the sample site.
    /// </summary>
    public static string Combined(IrTextureType texture) {
        ArgumentNullException.ThrowIfNull(texture);

        return texture.Dimension switch {
            IrTextureDimension.Texture2D => "sampler2D",
            IrTextureDimension.Texture3D => "sampler3D",
            _ => "samplerCube"
        };
    }

    /// <summary>
    ///     A declaration of <paramref name="name" /> at <paramref name="type" />.
    ///     Arrays put their extents after the name, as C-family languages do.
    /// </summary>
    /// <param name="allowUnsizedOuter">
    ///     Whether the outermost extent may be omitted — <c>Particle data[]</c>. True only for a
    ///     storage block's last member, which is the one place GLSL allows it.
    /// </param>
    public static string? Declare(IrType type, string name, bool allowUnsizedOuter = false) {
        if (type is not IrArrayType array) {
            return Name(type) is { } simple ? $"{simple} {name}" : null;
        }

        return Extents(array, allowUnsizedOuter) is { } extents
            ? $"{extents.Element} {name}{extents.Suffix}"
            : null;
    }

    /// <summary>Makes an identifier safe to emit, mangling only when it must.</summary>
    public static string Identifier(string name) {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

        if (cleaned.Length == 0 || char.IsDigit(cleaned[0])) {
            cleaned = "_" + cleaned;
        }

        // `gl_` is reserved for the implementation.
        return Reserved.Contains(cleaned) || cleaned.StartsWith("gl_", StringComparison.Ordinal)
            ? cleaned + "_"
            : cleaned;
    }

    static string? VectorName(IrVectorType vector) {
        var prefix = vector.Component.Kind switch {
            IrTypeKind.Bool => "b",
            IrTypeKind.Int => "i",
            IrTypeKind.UInt => "u",
            IrTypeKind.Float => string.Empty,
            IrTypeKind.Double => "d",
            _ => null
        };

        return prefix is null || vector.Size is < 2 or > 4 ? null : $"{prefix}vec{vector.Size}";
    }

    static string? MatrixName(IrMatrixType matrix) {
        var prefix = matrix.Component.Kind switch {
            IrTypeKind.Float => string.Empty,
            IrTypeKind.Double => "d",
            _ => null
        };

        if (prefix is null || matrix.Rows is < 2 or > 4 || matrix.Columns is < 2 or > 4) {
            return null;
        }

        // GLSL orders the suffix columns-first.
        return matrix.Rows == matrix.Columns
            ? $"{prefix}mat{matrix.Rows}"
            : $"{prefix}mat{matrix.Columns}x{matrix.Rows}";
    }
}
