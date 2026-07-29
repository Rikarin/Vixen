// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace System.Runtime.CompilerServices;

/// <summary>
///     The marker the compiler needs for <c>init</c> accessors, which is what a positional record
///     compiles into.
/// </summary>
/// <remarks>
///     A compiler contract rather than a library one — the compiler looks the type up by name and
///     any assembly may declare it. Present in .NET 5 and later and absent from netstandard2.1,
///     which is the target a Roslyn generator has to use, and a generator's models are records all
///     the way down because an incremental pipeline caches on value equality. Declaring it here is
///     the documented workaround; it is a compile-time marker and nothing references it at run time.
/// </remarks>
internal static class IsExternalInit;
