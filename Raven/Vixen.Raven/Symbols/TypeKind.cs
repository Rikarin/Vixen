// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     The shape of a <see cref="TypeSymbol" />. Shader languages care about the
///     scalar/vector/matrix distinction, so those are first-class here rather than
///     being folded into "struct".
/// </summary>
public enum TypeKind {
    /// <summary>A type that could not be resolved; absorbs further errors silently.</summary>
    Error,
    Void,
    Scalar,
    Vector,
    Matrix,

    /// <summary>A GPU resource: texture, sampler or buffer.</summary>
    Resource,
    Struct,
    Class,
    Shader,
    Protocol,
    Enum,
    Array,
    Nullable,
    Tuple,
    TypeParameter
}
