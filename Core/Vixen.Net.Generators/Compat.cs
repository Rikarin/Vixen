// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace System.Runtime.CompilerServices;

/// <summary>What an <c>init</c> accessor compiles against.</summary>
/// <remarks>
///     A compiler contract rather than a library one — the compiler looks the type up by name and
///     any assembly may declare it. A generator targets .NET Standard 2.1, which predates records,
///     and the models here are all records because an incremental generator's models have to be
///     value-equal. Same shim, and the same reason, as
///     <c>Vixen.Ui.Markup.Generators/Compat/Netstandard.cs</c>.
/// </remarks>
internal static class IsExternalInit;
