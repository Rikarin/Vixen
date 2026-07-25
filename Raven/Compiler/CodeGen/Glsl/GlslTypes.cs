using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>Mapping the IR type model onto GLSL's spelling.</summary>
public static class GlslTypes {
    /// <summary>
    /// The GLSL name for a type, or null when GLSL cannot express it.
    /// </summary>
    /// <remarks>
    /// Matrices flip: Raven writes <c>matRxC</c> as R rows by C columns, GLSL
    /// writes <c>matCxR</c> as C columns by R rows. The flip is what keeps
    /// <c>m * v</c> meaning the same thing in both languages.
    /// </remarks>
    public static string? Name(IrType type) => type switch {
        IrScalarType scalar => scalar.Kind switch {
            IrTypeKind.Void => "void",
            IrTypeKind.Bool => "bool",
            IrTypeKind.Int => "int",
            IrTypeKind.UInt => "uint",
            IrTypeKind.Float => "float",
            IrTypeKind.Double => "double",
            _ => null
        },
        IrVectorType vector => VectorName(vector),
        IrMatrixType matrix => MatrixName(matrix),
        IrStructType structType => structType.Name,
        // GLSL has no separate texture and sampler objects outside Vulkan, so a
        // texture becomes the combined sampler and the sampler itself vanishes.
        IrTextureType texture => texture.Dimension switch {
            IrTextureDimension.Texture2D => "sampler2D",
            IrTextureDimension.Texture3D => "sampler3D",
            _ => "samplerCube"
        },
        _ => null
    };

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

    /// <summary>
    /// A declaration of <paramref name="name"/> at <paramref name="type"/>.
    /// Arrays put their extent after the name, as C-family languages do.
    /// </summary>
    public static string? Declare(IrType type, string name) {
        if (type is not IrArrayType array) {
            return Name(type) is { } simple ? $"{simple} {name}" : null;
        }

        // GLSL only allows a runtime-sized array as the last member of a storage
        // block, which the IR has no way to express yet.
        return array.Length is { } length && Name(array.Element) is { } element
            ? $"{element} {name}[{length}]"
            : null;
    }

    /// <summary>Words a generated identifier must not collide with.</summary>
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) {
        "attribute", "const", "uniform", "varying", "buffer", "shared", "coherent", "volatile", "restrict",
        "readonly", "writeonly", "layout", "centroid", "flat", "smooth", "noperspective", "patch", "sample",
        "break", "continue", "do", "for", "while", "switch", "case", "default", "if", "else", "subroutine",
        "in", "out", "inout", "true", "false", "invariant", "precise", "discard", "return", "struct",
        "void", "bool", "int", "uint", "float", "double", "lowp", "mediump", "highp", "precision",
        "main"
    };

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
}
