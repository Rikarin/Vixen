// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace System.Runtime.CompilerServices;

/// <summary>What <c>[CallerArgumentExpression]</c> compiles against.</summary>
/// <remarks>
///     Declared for the same reason as <see cref="IsExternalInit" /> and read the same way: the
///     compiler matches on the name, so the throw helpers in <c>ArgumentNullException.cs</c> report
///     the caller's own expression rather than a hard-coded name.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute {
    public string ParameterName { get; } = parameterName;
}
