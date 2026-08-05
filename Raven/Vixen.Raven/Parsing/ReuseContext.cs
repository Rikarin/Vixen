// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.Parsing;

/// <summary>
///     The parse loops incremental reuse tells apart (see <c>ReuseCandidate.Context</c>). A node may
///     only be lent back to the loop that produced it, so every loop that builds a list of members
///     needs a value here, whether or not it asks the blender for anything yet.
/// </summary>
static class ReuseContext {
    /// <summary>
    ///     A run of <c>ParseMemberDeclaration</c> — the compilation unit's members and a shader,
    ///     struct or protocol body, which are the same grammar and so the same context.
    /// </summary>
    public const int MemberList = 0;

    /// <summary>
    ///     An enum's comma-separated members, which only <c>ParseEnumDeclaration</c> reads.
    ///     <see cref="EnumMemberDeclarationSyntax" /> is a <see cref="MemberDeclarationSyntax" /> and
    ///     is therefore collected as a candidate; this is what stops it being spliced into a
    ///     <see cref="MemberList" />, where nothing could have parsed it.
    /// </summary>
    public const int EnumBody = 1;
}
