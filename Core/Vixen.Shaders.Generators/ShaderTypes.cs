// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Shaders.Generators;

/// <summary>
///     Maps a shader data type onto the C# type the engine already writes it in.
/// </summary>
/// <remarks>
///     <para>
///         The mapping is to <c>Vixen.Core.Mathematics</c> rather than to a shader-shaped type of
///         this project's own, and that is the whole point of generating bindings: a render feature
///         holding a <see cref="T:Vixen.Core.Mathematics.Matrix4x4" /> passes it straight in. A
///         parallel type family would mean a conversion at every call site, which is where a
///         transpose or a swizzle gets introduced by someone trying to be helpful.
///     </para>
///     <para>
///         A type with no C# equivalent — a struct, or a matrix shape the engine has no type for —
///         maps to nothing, and the generator skips the field with a comment saying so rather than
///         emitting something that will not compile. A struct array is handled separately, through
///         its flattened leaves.
///     </para>
/// </remarks>
static class ShaderTypes {
    /// <summary>The C# type for a shader type, or null when the engine has none.</summary>
    public static string? Name(DataType type) {
        if (type.IsStruct) {
            return null;
        }

        if (type.IsMatrix) {
            return (type.Rows, type.Columns) switch {
                (4, 4) => "global::Vixen.Core.Mathematics.Matrix4x4",
                (3, 3) => "global::Vixen.Core.Mathematics.Matrix3x3",
                _ => null
            };
        }

        return type.Scalar switch {
            "Float" => Vector(type.Rows, "float", "Vector"),
            "Int" => Vector(type.Rows, "int", "Int"),
            "UInt" => type.Rows == 1 ? "uint" : null,
            "Bool" => type.Rows == 1 ? "bool" : null,
            "Double" => type.Rows == 1 ? "double" : null,
            _ => null
        };
    }

    /// <summary>Whether a value of this type is written with a matrix column stride.</summary>
    /// <remarks>
    ///     Only a <c>mat3</c>: its columns are twelve bytes of data in sixteen of space, so the host
    ///     bytes and the shader bytes genuinely differ. A <c>mat4</c> is a straight blit — see
    ///     <c>ShaderConstants.Write(Span&lt;byte&gt;, int, in Matrix4x4)</c> for why that is a
    ///     consequence of the convention rather than a coincidence.
    /// </remarks>
    public static bool NeedsColumnStride(DataType type) => type is { IsMatrix: true, Rows: 3, Columns: 3 };

    /// <summary>A C# identifier for a shader parameter's dotted, bracketed name.</summary>
    /// <remarks>
    ///     <c>lights[].color</c> becomes <c>LightsColor</c>. The original name stays on the key, so
    ///     the string a material asset or an editor uses is unchanged — this only decides what the
    ///     generated member is called.
    /// </remarks>
    public static string Identifier(string name) {
        var builder = new System.Text.StringBuilder(name.Length);
        var capitalise = true;

        foreach (var c in name) {
            if (char.IsLetterOrDigit(c)) {
                builder.Append(capitalise ? char.ToUpperInvariant(c) : c);
                capitalise = false;
                continue;
            }

            // A separator — '.', '[' or ']' — starts a new word rather than surviving into the name.
            capitalise = true;
        }

        if (builder.Length == 0) {
            return "Parameter";
        }

        // A leading digit is legal in a shader identifier only after a dot, but the joined form can
        // still start with one.
        return char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    static string? Vector(int rows, string scalar, string prefix) =>
        rows switch {
            1 => scalar,
            2 or 3 or 4 => $"global::Vixen.Core.Mathematics.{prefix}{rows}",
            _ => null
        };
}
