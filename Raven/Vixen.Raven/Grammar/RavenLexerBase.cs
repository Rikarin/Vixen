using System.Diagnostics.CodeAnalysis;
using Antlr4.Runtime;

namespace Vixen.Raven.Grammar;

/// <summary>
///     Base class for the generated lexer, supplying the action methods that
///     <c>RavenLexer.g4</c> invokes inline.
/// </summary>
/// <remarks>
///     Raven has no interpolated strings: the lexer grammar declares no
///     interpolation modes, so brace and colon tracking has nothing to track. The
///     three hooks below exist because the grammar's inline actions name them, and
///     they stay empty until there is a construct that needs them.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "ANTLR emits `this.OnOpenBrace()`; CS0176 rejects an instance-qualified call to a static member.")]
public abstract class RavenLexerBase(ICharStream input) : Lexer(input) {
    protected void OnOpenBrace() { }

    protected void OnCloseBrace() { }

    protected void OnColon() { }
}
