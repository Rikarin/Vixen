// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace System.Diagnostics.CodeAnalysis;

/// <summary>
///     The nullable-analysis marker for a <c>Try</c> method's <c>out</c> parameter, for the source a
///     generator links from a net11.0 project.
/// </summary>
/// <remarks>
///     A compiler contract like <c>IsExternalInit</c>: the compiler looks it up by name. netstandard2.0,
///     the target a Roslyn generator has to use, does not declare it, and a referenced assembly's
///     internal copy is CS0122 rather than a fallback, so the generator that links the source
///     declares its own.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute {
    /// <summary>The return value for which the parameter is not null.</summary>
    public bool ReturnValue { get; } = returnValue;
}
