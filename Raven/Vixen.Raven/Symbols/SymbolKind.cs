// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>What kind of entity a <see cref="Symbol" /> denotes.</summary>
public enum SymbolKind {
    Namespace,
    NamedType,
    ArrayType,
    NullableType,
    TupleType,
    TypeParameter,
    ErrorType,
    Method,
    Field,
    Property,
    Parameter,
    Local
}
