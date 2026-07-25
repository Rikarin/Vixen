using Vixen.Core.Syntax;

namespace Vixen.Raven.Syntax;

/// <summary>
///     Raven's view of the shared tree types.
/// </summary>
/// <remarks>
///     <see cref="SyntaxToken" /> comes from <c>Vixen.Core.Syntax</c>, which knows kinds
///     only as integers. Raven nodes get <c>Kind</c> from <see cref="RavenSyntaxNode" />;
///     this gives tokens the same spelling, so call sites do not have to care which side
///     of the boundary a token came from.
/// </remarks>
public static class RavenSyntaxExtensions {
    extension(SyntaxToken token) {
        /// <summary>The token's kind, projected from <see cref="SyntaxNode.RawKind" />.</summary>
        public SyntaxKind Kind => (SyntaxKind)token.RawKind;
    }
}
