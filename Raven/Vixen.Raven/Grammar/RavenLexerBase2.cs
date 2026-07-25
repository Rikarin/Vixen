using Antlr4.Runtime;

namespace Vixen.Raven.Grammar;

public abstract class RavenLexerBase2 : Lexer {
    private protected int interpolatedStringLevel;
    private protected Stack<bool> interpolatedVerbatiums = new();
    private protected Stack<int> curlyLevels = new();
    private protected bool verbatium;


    public RavenLexerBase2(ICharStream input) : base(input) { }

    protected void OnInterpolatedRegularStringStart() {
        interpolatedStringLevel++;
        interpolatedVerbatiums.Push(false);
        verbatium = false;
    }

    protected void OnInterpolatedVerbatiumStringStart() {
        interpolatedStringLevel++;
        interpolatedVerbatiums.Push(true);
        verbatium = true;
    }

    protected void OnOpenBrace() {
        if (interpolatedStringLevel > 0) {
            curlyLevels.Push(curlyLevels.Pop() + 1);
        }
    }

    protected void OnCloseBrace() {
        if (interpolatedStringLevel > 0) {
            curlyLevels.Push(curlyLevels.Pop() - 1);
            if (curlyLevels.Peek() == 0) {
                curlyLevels.Pop();
                Skip();
                PopMode();
            }
        }
    }

    // Not static: ANTLR's generated lexer emits `this.OnColon()`, and CS0176
    // rejects an instance-qualified call to a static member.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "Called as an instance member by ANTLR-generated lexer code.")]
    protected void OnColon() {
        // TODO
        // if (interpolatedStringLevel > 0) {
        //     var ind = 1;
        //     var switchToFormatString = true;
        //     while ((char)_input.LA(ind) != '}') {
        //         if (_input.LA(ind) == ':' || _input.LA(ind) == ')') {
        //             switchToFormatString = false;
        //             break;
        //         }
        //
        //         ind++;
        //     }
        //
        //     if (switchToFormatString) {
        //         Mode(VixenLexer.INTERPOLATION_FORMAT);
        //     }
        // }
    }

    protected void OpenBraceInside() => curlyLevels.Push(1);

    protected void OnDoubleQuoteInside() {
        interpolatedStringLevel--;
        interpolatedVerbatiums.Pop();
        verbatium = interpolatedVerbatiums.Any() && interpolatedVerbatiums.Peek();
    }

    protected void OnCloseBraceInside() => curlyLevels.Pop();
    protected bool IsRegularCharInside() => !verbatium;
    protected bool IsVerbatiumDoubleQuoteInside() => verbatium;
}
