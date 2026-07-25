using Antlr4.Runtime;
using Vixen.Raven.Diagnostics;
using Vixen.Core.Syntax.Text;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Syntax;

/// <summary>
///     Routes ANTLR lexer and parser errors into a <see cref="DiagnosticBag" /> with
///     real source spans instead of writing to the console. Implements both listener
///     interfaces: <c>int</c> for the lexer (offending char), <c>IToken</c> for the
///     parser (offending token).
/// </summary>
sealed class RavenSyntaxErrorListener(DiagnosticBag diagnostics, SourceText text, string filePath)
    : IAntlrErrorListener<int>, IAntlrErrorListener<IToken> {
    // Lexer: the offending symbol is a character code; position comes from line/column.
    public void SyntaxError(
        IRecognizer recognizer,
        int offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e
    ) {
        var span = SpanFromLineColumn(line, charPositionInLine);
        diagnostics.Add(SyntaxDiagnostics.InvalidCharacter, Location.Create(filePath, span, text), msg);
    }

    // Parser: the offending token carries absolute char offsets.
    public void SyntaxError(
        IRecognizer recognizer,
        IToken offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e
    ) {
        var span = offendingSymbol != null
            ? SpanFromToken(offendingSymbol)
            : SpanFromLineColumn(line, charPositionInLine);
        diagnostics.Add(SyntaxDiagnostics.SyntaxError, Location.Create(filePath, span, text), msg);
    }

    TextSpan SpanFromToken(IToken token) {
        var start = Clamp(token.StartIndex);
        // StopIndex is inclusive; an EOF/empty token reports StopIndex < StartIndex.
        var end = token.StopIndex >= token.StartIndex ? Clamp(token.StopIndex + 1) : start;
        return TextSpan.FromBounds(start, end);
    }

    TextSpan SpanFromLineColumn(int line, int charPositionInLine) {
        // ANTLR lines are 1-based; columns are 0-based.
        var lineIndex = line - 1;
        if (lineIndex < 0 || lineIndex >= text.LineCount) {
            return new(Clamp(0), 0);
        }

        var start = Clamp(text.GetLineStart(lineIndex) + charPositionInLine);
        var end = Math.Min(start + 1, text.Length);
        return TextSpan.FromBounds(start, end);
    }

    int Clamp(int position) => position < 0 ? 0 : position > text.Length ? text.Length : position;
}
