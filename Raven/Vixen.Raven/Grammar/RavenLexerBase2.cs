using Antlr4.Runtime;

namespace Vixen.Raven.Grammar;

public abstract class RavenLexerBase2 : Lexer {
    protected int interpolatedStringLevel;
    protected Stack<bool> interpolatedVerbatiums = new();
    protected Stack<int> curlyLevels = new();
    protected bool verbatium;


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
