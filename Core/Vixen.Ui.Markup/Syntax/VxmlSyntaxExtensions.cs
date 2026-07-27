// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;

namespace Vixen.Ui.Markup.Syntax;

/// <summary>VXML's view of the shared tree types.</summary>
/// <remarks>
///     <see cref="SyntaxToken" /> comes from <c>Vixen.Core.Syntax</c>, which knows kinds only as
///     integers. VXML nodes get <c>Kind</c> from <see cref="VxmlSyntaxNode" />; this gives tokens
///     the same spelling, so call sites do not have to care which side of the boundary a token
///     came from.
/// </remarks>
public static class VxmlSyntaxExtensions {
    extension(SyntaxToken token) {
        /// <summary>The token's kind, projected from <see cref="SyntaxNode.RawKind" />.</summary>
        public SyntaxKind Kind => (SyntaxKind)token.RawKind;
    }
}
