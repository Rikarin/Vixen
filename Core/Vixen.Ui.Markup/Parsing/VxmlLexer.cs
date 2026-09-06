// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Diagnostics;

namespace Vixen.Ui.Markup.Parsing;

/// <summary>
///     The VXML lexer. Produces the full raw token list — trivia included — that
///     <see cref="VxmlParser" /> builds a tree from.
/// </summary>
/// <remarks>
///     <para>
///         A templating lexer cannot be a flat state machine, because the same character means
///         three things: <c>"</c> is a quote in a tag and a string delimiter in an expression, and
///         nothing at all in text. So there is a small mode stack — content, tag, attribute value —
///         and every transition is driven by a character the grammar already had to look at.
///     </para>
///     <para>
///         <b>VXML never parses C#.</b> Every expression, every <c>@code</c> body and every
///         <c>&lt;style&gt;</c> body is captured as one token by balancing delimiters, with C#
///         strings, characters and comments skipped so a brace inside a string cannot close a
///         block. Roslyn is the only thing that ever reads those characters, which is what keeps
///         this a markup language rather than a second, worse C# front end.
///     </para>
///     <para>
///         ⚠ <b>Whitespace that spans a line break is trivia; whitespace that does not is text.</b>
///         Indentation between two elements is formatting and disappears; the space you typed
///         between <c>@first</c> and <c>@last</c> on one line is content and survives. This is the
///         whole whitespace policy, it costs nothing because trivia already exists, and it is why
///         no later pass has to guess which spaces the author meant.
///     </para>
/// </remarks>
sealed class VxmlLexer {
    /// <summary>Where in the grammar the next character will be read.</summary>
    enum Mode {
        /// <summary>Markup content: text, tags, interpolations, directives.</summary>
        Content,

        /// <summary>Between <c>&lt;</c> and <c>&gt;</c>: a tag's name and attributes.</summary>
        Tag,

        /// <summary>Between an attribute value's quotes.</summary>
        AttributeValue
    }

    /// <summary>
    ///     An open directive body, and the element nesting depth it was opened at.
    /// </summary>
    /// <remarks>
    ///     <b>The element depth is what makes a literal brace harmless.</b> A <c>}</c> closes a
    ///     block only when the lexer is back at the depth the <c>{</c> was written at, so
    ///     <c>@if (x) { &lt;div&gt;a } b&lt;/div&gt; }</c> reads the first brace as text and the
    ///     second as the close. Without it, every <c>}</c> anywhere inside a body would end it.
    /// </remarks>
    readonly record struct Block(bool IsSwitch, int ElementDepth);

    readonly SlidingTextWindow window;
    readonly DiagnosticBag diagnostics;
    readonly SourceText text;
    readonly string filePath;

    readonly List<Block> blocks = [];

    Mode mode = Mode.Content;

    /// <summary>Open elements. Incremented by a start tag's <c>&gt;</c>, decremented by an end tag's.</summary>
    int elementDepth;

    /// <summary>Whether the tag being lexed is a closing one.</summary>
    bool inEndTag;

    /// <summary>The name of the tag being lexed, so <c>&lt;style&gt;</c> can switch to raw CSS.</summary>
    string tagName = string.Empty;

    /// <summary>The quote character an attribute value opened with, so the matching one closes it.</summary>
    char valueQuote;

    VxmlLexer(string source, DiagnosticBag diagnostics, SourceText text, string filePath) {
        window = new(source);
        this.diagnostics = diagnostics;
        this.text = text;
        this.filePath = filePath;
    }

    /// <summary>Lexes the whole source into raw tokens; the last entry is always end-of-file.</summary>
    /// <param name="source">The file's text.</param>
    /// <param name="diagnostics">Where lexical errors are reported.</param>
    /// <param name="text">The same text, for turning offsets into line positions.</param>
    /// <param name="filePath">The path diagnostics name.</param>
    /// <returns>Every token in order, trivia included, ending in end-of-file.</returns>
    public static List<LexedToken> Lex(string source, DiagnosticBag diagnostics, SourceText text, string filePath) {
        var lexer = new VxmlLexer(source, diagnostics, text, filePath);
        var tokens = new List<LexedToken>();

        while (!lexer.window.AtEnd) {
            var before = lexer.window.Position;
            lexer.Step(tokens);

            // A step that consumed nothing would spin. Nothing below can do that, and this is the
            // assertion that says so rather than the comment that claims it.
            if (lexer.window.Position == before) {
                throw new InvalidOperationException(
                    $"The lexer made no progress at offset {before}. This is a bug in {nameof(VxmlLexer)}."
                );
            }
        }

        tokens.Add(new((int)VxmlTokenKind.EndOfFile, string.Empty, lexer.window.Position, LexedTokenFlags.None));
        return tokens;
    }

    /// <summary>
    ///     Emits the tokens of one construct. Usually one token; a directive header is several,
    ///     because <c>@if</c>, its parentheses, its condition and its opening brace are one
    ///     decision and splitting them across calls would mean carrying the decision in a field.
    /// </summary>
    void Step(List<LexedToken> tokens) {
        switch (mode) {
            case Mode.Content:
                StepContent(tokens);
                break;
            case Mode.Tag:
                StepTag(tokens);
                break;
            default:
                StepAttributeValue(tokens);
                break;
        }
    }

    // ================================================================== Content

    void StepContent(List<LexedToken> tokens) {
        var current = window.Current;

        if (current == '<') {
            if (window.Peek(1) == '!' && window.Peek(2) == '-' && window.Peek(3) == '-') {
                LexComment(tokens);
                return;
            }

            if (window.Peek(1) == '/') {
                Emit(tokens, VxmlTokenKind.LessThanSlash, 2);
                mode = Mode.Tag;
                inEndTag = true;
                tagName = string.Empty;
                return;
            }

            if (IsNameStart(window.Peek(1))) {
                Emit(tokens, VxmlTokenKind.LessThan, 1);
                mode = Mode.Tag;
                inEndTag = false;
                tagName = string.Empty;
                return;
            }
        }

        if (current == '}' && AtBlockClose()) {
            LexBlockClose(tokens);
            return;
        }

        if (current == '@') {
            LexAt(tokens);
            return;
        }

        // Directly inside a switch body, a whitespace run is always trivia — line break or not.
        // Between the brace and a label there is nothing else it could be, and requiring the
        // author to put `case` on its own line to make a one-liner parse would be a grammar rule
        // masquerading as a formatting preference.
        if (InSwitchBody() && IsWhitespace(current)) {
            SkipWhitespace(tokens);
            return;
        }

        if (AtSwitchLabel()) {
            LexSwitchLabel(tokens);
            return;
        }

        LexTextRun(tokens);
    }

    void LexComment(List<LexedToken> tokens) {
        var start = window.Position;
        window.Advance(4);

        while (!window.AtEnd && !(window.Current == '-' && window.Peek(1) == '-' && window.Peek(2) == '>')) {
            window.Advance();
        }

        if (window.AtEnd) {
            Report(MarkupDiagnostics.SyntaxError, start, window.Position, "unterminated comment");
        } else {
            window.Advance(3);
        }

        Add(tokens, VxmlTokenKind.Comment, start, LexedTokenFlags.Trivia);
    }

    /// <summary>
    ///     A run of literal text, ending where markup starts. Whitespace-only runs that cross a
    ///     line become trivia; everything else is a text token.
    /// </summary>
    void LexTextRun(List<LexedToken> tokens) {
        var start = window.Position;

        while (!window.AtEnd && !AtContentBreak()) {
            window.Advance();
        }

        var run = window.GetText(start, window.Position);
        var insignificant = run.AsSpan().IsWhiteSpace() && run.AsSpan().IndexOfAny('\r', '\n') >= 0;

        Add(
            tokens,
            insignificant ? VxmlTokenKind.Whitespace : VxmlTokenKind.Text,
            start,
            insignificant ? LexedTokenFlags.Trivia : LexedTokenFlags.None
        );
    }

    bool AtContentBreak() {
        var current = window.Current;

        if (current == '@') {
            return true;
        }

        if (current == '<' && (window.Peek(1) == '/' || IsNameStart(window.Peek(1))
                               || (window.Peek(1) == '!' && window.Peek(2) == '-' && window.Peek(3) == '-'))) {
            return true;
        }

        if (current == '}' && AtBlockClose()) {
            return true;
        }

        return current is 'c' or 'd' && AtSwitchLabel();
    }

    bool AtBlockClose() => blocks.Count > 0 && elementDepth == blocks[^1].ElementDepth;

    /// <summary>Whether a <c>case</c> or <c>default</c> label starts here.</summary>
    /// <remarks>
    ///     Only directly inside a switch body — <c>&lt;p&gt;case closed&lt;/p&gt;</c> inside a
    ///     section is at a deeper element depth and stays text.
    /// </remarks>
    bool AtSwitchLabel() => InSwitchBody() && (AtWord("case") || AtWord("default"));

    bool InSwitchBody() => blocks is [.., { IsSwitch: true } block] && elementDepth == block.ElementDepth;

    /// <summary>
    ///     Whether a keyword starts <paramref name="offset" /> characters ahead, ending at a word
    ///     boundary.
    /// </summary>
    /// <remarks>
    ///     ⚠ The boundary is an <i>identifier</i> boundary, not a name boundary. A name may contain
    ///     <c>:</c> so that <c>on:click</c> is one token — and using that rule here would mean
    ///     <c>default:</c> never ends, which is precisely the shape every switch label has.
    /// </remarks>
    bool AtWord(string word, int offset = 0) {
        for (var i = 0; i < word.Length; i++) {
            if (window.Peek(offset + i) != word[i]) {
                return false;
            }
        }

        return !IsIdentifierPart(window.Peek(offset + word.Length));
    }

    void LexSwitchLabel(List<LexedToken> tokens) {
        var isCase = AtWord("case");
        Emit(tokens, isCase ? VxmlTokenKind.CaseKeyword : VxmlTokenKind.DefaultKeyword, isCase ? 4 : 7);

        if (isCase) {
            SkipWhitespace(tokens);
            var start = window.Position;
            ScanPattern();

            if (window.Position > start) {
                Add(tokens, VxmlTokenKind.Expression, start);
            }
        }

        SkipWhitespace(tokens);
        if (window.Current == ':') {
            Emit(tokens, VxmlTokenKind.Colon, 1);
        }
    }

    /// <summary>
    ///     Closes a directive body, and takes an <c>else</c> or an <c>@empty</c> arm with it if one
    ///     follows.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Neither arm is looked for by the block it belongs to.</b> This runs for every
    ///     <c>}</c> whatever opened it, so <c>@if (…) { } @empty { }</c> lexes its <c>@empty</c> as
    ///     the keyword too — and the parser is what refuses it, with an id and a message. A lexer
    ///     that only took the arm after the right kind of block would leave the wrong one as an
    ///     interpolation of a variable called <c>empty</c>, which is a Roslyn error on generated
    ///     code about a name nobody wrote.
    /// </remarks>
    void LexBlockClose(List<LexedToken> tokens) {
        Emit(tokens, VxmlTokenKind.CloseBrace, 1);
        blocks.RemoveAt(blocks.Count - 1);

        // `else` is the one keyword written without an `@`, because a brace already put us in
        // directive position. Looking for it here rather than on the next step is what lets the
        // space in `} else {` stay ordinary whitespace instead of becoming a text node.
        var gap = 0;
        while (IsWhitespace(window.Peek(gap))) {
            gap++;
        }

        // ⚠ `@empty` keeps its `@` where `else` dropped one, and that is not an inconsistency: a
        // reader scanning a file for what draws sees every arm of an `@for` marked, and `empty` is
        // an ordinary identifier that a file may well interpolate elsewhere.
        if (window.Peek(gap) == '@' && AtWord("empty", gap + 1)) {
            SkipWhitespace(tokens);
            Emit(tokens, VxmlTokenKind.EmptyKeyword, 6);
            LexBlockOpen(tokens, isSwitch: false);
            return;
        }

        if (!AtWord("else", gap)) {
            return;
        }

        SkipWhitespace(tokens);
        Emit(tokens, VxmlTokenKind.ElseKeyword, 4);
        SkipWhitespace(tokens);

        if (AtWord("if")) {
            Emit(tokens, VxmlTokenKind.IfKeyword, 2);
            LexParenthesizedHeader(tokens, forLoop: false);
        }

        LexBlockOpen(tokens, isSwitch: false);
    }

    // ------------------------------------------------------------ @

    void LexAt(List<LexedToken> tokens) {
        // `@@` is a literal `@`. Its text is both characters, so the tree still reproduces the
        // file; the binder is what turns it back into one.
        if (window.Peek(1) == '@') {
            Emit(tokens, VxmlTokenKind.Text, 2);
            return;
        }

        if (AtDirective("component")) {
            Emit(tokens, VxmlTokenKind.ComponentKeyword, 10);
            SkipWhitespace(tokens);
            LexName(tokens);
            return;
        }

        if (AtDirective("using")) {
            Emit(tokens, VxmlTokenKind.UsingKeyword, 6);
            SkipWhitespace(tokens);
            LexUsingStatic(tokens);
            LexName(tokens);
            LexUsingAlias(tokens);
            return;
        }

        if (AtDirective("namespace")) {
            Emit(tokens, VxmlTokenKind.NamespaceKeyword, 10);
            SkipWhitespace(tokens);
            LexName(tokens);
            return;
        }

        // A name rather than an identifier, because an element name has hyphens in it — which is
        // the whole reason this directive exists rather than a rule about the type's name.
        if (AtDirective("tag")) {
            Emit(tokens, VxmlTokenKind.TagKeyword, 4);
            SkipWhitespace(tokens);
            LexName(tokens);
            return;
        }

        // A name and not an identifier, because a base type is dotted — and `IsNamePart` already
        // takes a `.` for `@using` and `@namespace`, which is the same shape of thing.
        if (AtDirective("inherits")) {
            Emit(tokens, VxmlTokenKind.InheritsKeyword, 9);
            SkipWhitespace(tokens);
            LexName(tokens);
            return;
        }

        // Two names: the ambient key, dotted for `@inherits`' reason, and the member the generated
        // property is called. Both are lexed as names — the parser is what says which kind each
        // becomes, because a name token that is a plain identifier is still a name to the lexer.
        if (AtDirective("inject")) {
            Emit(tokens, VxmlTokenKind.InjectKeyword, 7);
            SkipWhitespace(tokens);
            LexName(tokens);
            LexInjectName(tokens);
            return;
        }

        if (AtDirective("code")) {
            Emit(tokens, VxmlTokenKind.CodeKeyword, 5);
            LexCodeBody(tokens);
            return;
        }

        if (AtDirective("if")) {
            Emit(tokens, VxmlTokenKind.IfKeyword, 3);
            LexParenthesizedHeader(tokens, forLoop: false);
            LexBlockOpen(tokens, isSwitch: false);
            return;
        }

        if (AtDirective("for")) {
            Emit(tokens, VxmlTokenKind.ForKeyword, 4);
            LexParenthesizedHeader(tokens, forLoop: true);
            LexBlockOpen(tokens, isSwitch: false);
            return;
        }

        if (AtDirective("switch")) {
            Emit(tokens, VxmlTokenKind.SwitchKeyword, 7);
            LexParenthesizedHeader(tokens, forLoop: false);
            LexBlockOpen(tokens, isSwitch: true);
            return;
        }

        // An `@empty` that no `}` preceded — `LexBlockClose` has already taken the ones that follow
        // a loop. ⚠ It is a keyword only when a brace follows, because `empty` is a legal C#
        // identifier and every other directive here is spelled with a word that is not.
        if (AtDirective("empty") && AtBraceAhead(6)) {
            Emit(tokens, VxmlTokenKind.EmptyKeyword, 6);
            LexBlockOpen(tokens, isSwitch: false);
            return;
        }

        LexInterpolation(tokens);
    }

    /// <summary>
    ///     The <c>static</c> of <c>@using static System.Math</c>, if this using has one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The word is only a keyword when whitespace follows it.</b> <c>IsNamePart</c> takes a
    ///     <c>.</c>, so a bare <c>AtWord</c> would read the <c>static</c> of a hypothetical
    ///     <c>@using static.Thing</c> as the keyword and leave <c>.Thing</c> as no name at all.
    ///     Whitespace is also the only thing that separates the keyword from the name it qualifies,
    ///     which is why nothing else needs to be looked at.
    /// </remarks>
    void LexUsingStatic(List<LexedToken> tokens) {
        if (!AtWord("static") || !IsWhitespace(window.Peek(6))) {
            return;
        }

        Emit(tokens, VxmlTokenKind.StaticKeyword, 6);
        SkipWhitespace(tokens);
    }

    /// <summary>
    ///     The <c>= A.B.C</c> half of <c>@using Prefab = A.B.Prefab</c>, if this using has one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The <c>=</c> is looked for without consuming anything.</b> A directive hands the
    ///     lexer straight back to content, where a whitespace run that does not span a line break
    ///     is <i>text</i> — so skipping ahead to find out whether an <c>=</c> follows would turn
    ///     the space after every plain <c>@using X</c> into trivia. The scan also stops at a line
    ///     break, because an <c>=</c> on the next line belongs to whatever that line is.
    /// </remarks>
    void LexUsingAlias(List<LexedToken> tokens) {
        var gap = 0;
        while (IsWhitespace(window.Peek(gap)) && !IsNewLine(window.Peek(gap))) {
            gap++;
        }

        // `==` is not an alias, and reading it as one would turn a typo into two tokens the parser
        // then has to explain.
        if (window.Peek(gap) != '=' || window.Peek(gap + 1) == '=') {
            return;
        }

        SkipWhitespace(tokens);
        Emit(tokens, VxmlTokenKind.Equals, 1);
        SkipWhitespace(tokens);
        LexName(tokens);
    }

    /// <summary>The <c>Theme</c> of <c>@inject ITheme Theme</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The gap is measured before anything is consumed</b>, for <see cref="LexUsingAlias" />'s
    ///     reason. A directive hands the lexer straight back to content, where a whitespace run is
    ///     <i>text</i> — so skipping ahead unconditionally would swallow the line break after a
    ///     half-written <c>@inject ITheme</c> and read the next line's tag name as the member name.
    ///     Stopping at the break is what makes the missing half a diagnostic on the directive rather
    ///     than a silently accepted file that generates the wrong property.
    /// </remarks>
    void LexInjectName(List<LexedToken> tokens) {
        var gap = 0;
        while (IsWhitespace(window.Peek(gap)) && !IsNewLine(window.Peek(gap))) {
            gap++;
        }

        if (!IsNameStart(window.Peek(gap))) {
            return;
        }

        SkipWhitespace(tokens);
        LexName(tokens);
    }

    /// <summary>Whether a <c>{</c> starts <paramref name="offset" /> characters ahead, past spaces.</summary>
    bool AtBraceAhead(int offset) {
        while (IsWhitespace(window.Peek(offset))) {
            offset++;
        }

        return window.Peek(offset) == '{';
    }

    bool AtDirective(string name) {
        if (window.Current != '@') {
            return false;
        }

        return AtWord(name, 1);
    }

    /// <summary>
    ///     <c>@</c> and the expression it interpolates.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The scan is speculative, and a failed one has to be undone.</b> An <c>@(</c> the
    ///     file never closes runs to the end looking for its <c>)</c> — and dropping the characters
    ///     it walked over is a tree that no longer reproduces its file, which is exactly what
    ///     <c>@(abc</c> used to do to everything after the <c>@</c>. Rewinding hands them back to
    ///     content, so an unclosed paren costs the author the interpolation and nothing else: the
    ///     <c>&lt;/div&gt;</c> after it is still a close tag.
    /// </remarks>
    void LexInterpolation(List<LexedToken> tokens) {
        var at = window.Position;
        Emit(tokens, VxmlTokenKind.At, 1);

        var start = window.Position;

        if (ScanExpression()) {
            Add(tokens, VxmlTokenKind.Expression, start);
            return;
        }

        if (window.Position > start) {
            Report(MarkupDiagnostics.UnbalancedDelimiter, start, window.Position, "(");
            window.Rewind(start);
            return;
        }

        Report(MarkupDiagnostics.ExpectedExpression, at, at + 1);
    }

    /// <summary><c>(</c> condition <c>)</c>, with <c>var</c> <c>i</c> <c>in</c> in front for an <c>@for</c>.</summary>
    void LexParenthesizedHeader(List<LexedToken> tokens, bool forLoop) {
        SkipWhitespace(tokens);

        if (window.Current != '(') {
            return;
        }

        var open = window.Position;
        Emit(tokens, VxmlTokenKind.OpenParen, 1);
        SkipWhitespace(tokens);

        if (forLoop) {
            if (AtWord("var")) {
                Emit(tokens, VxmlTokenKind.VarKeyword, 3);
                SkipWhitespace(tokens);
            }

            LexName(tokens);
            SkipWhitespace(tokens);

            // ⚠ `@for (var row, i in Rows)` — the index is a second name, and it is the only comma
            // this grammar has. Lexed rather than left inside the sequence expression because the
            // scan for the sequence starts after `in`, and `row, i` before it would otherwise be one
            // `Name` token followed by characters nothing claims.
            if (window.Current == ',') {
                Emit(tokens, VxmlTokenKind.Comma, 1);
                SkipWhitespace(tokens);
                LexName(tokens);
                SkipWhitespace(tokens);
            }

            if (AtWord("in")) {
                Emit(tokens, VxmlTokenKind.InKeyword, 2);
                SkipWhitespace(tokens);
            }
        }

        var start = window.Position;
        var balanced = ScanToCloseParen();

        if (window.Position > start) {
            Add(tokens, VxmlTokenKind.Expression, start);
        }

        if (balanced) {
            Emit(tokens, VxmlTokenKind.CloseParen, 1);
        } else {
            Report(MarkupDiagnostics.UnbalancedDelimiter, open, window.Position, "(");
        }
    }

    void LexBlockOpen(List<LexedToken> tokens, bool isSwitch) {
        SkipWhitespace(tokens);

        if (window.Current != '{') {
            return;
        }

        Emit(tokens, VxmlTokenKind.OpenBrace, 1);
        blocks.Add(new(isSwitch, elementDepth));
    }

    /// <summary><c>{</c> C# <c>}</c> after <c>@code</c>, the body captured whole.</summary>
    void LexCodeBody(List<LexedToken> tokens) {
        SkipWhitespace(tokens);

        if (window.Current != '{') {
            return;
        }

        var open = window.Position;
        Emit(tokens, VxmlTokenKind.OpenBrace, 1);

        var start = window.Position;
        var balanced = ScanToCloseBrace();

        if (window.Position > start) {
            Add(tokens, VxmlTokenKind.Code, start);
        }

        if (balanced) {
            Emit(tokens, VxmlTokenKind.CloseBrace, 1);
        } else {
            Report(MarkupDiagnostics.UnbalancedDelimiter, open, window.Position, "{");
        }
    }

    // ================================================================== Tag

    void StepTag(List<LexedToken> tokens) {
        var current = window.Current;

        if (IsWhitespace(current)) {
            SkipWhitespace(tokens);
            return;
        }

        switch (current) {
            case '/' when window.Peek(1) == '>':
                Emit(tokens, VxmlTokenKind.SlashGreaterThan, 2);
                mode = Mode.Content;
                return;

            case '>':
                Emit(tokens, VxmlTokenKind.GreaterThan, 1);
                mode = Mode.Content;

                if (inEndTag) {
                    elementDepth = Math.Max(0, elementDepth - 1);
                } else {
                    elementDepth++;
                    if (string.Equals(tagName, "style", StringComparison.Ordinal)) {
                        LexCss(tokens);
                    }
                }

                return;

            case '=':
                Emit(tokens, VxmlTokenKind.Equals, 1);
                return;

            case '"' or '\'':
                valueQuote = current;
                Emit(tokens, VxmlTokenKind.Quote, 1);
                mode = Mode.AttributeValue;
                return;

            case '@':
                LexInterpolation(tokens);
                return;
        }

        if (IsNameStart(current)) {
            var start = window.Position;
            LexName(tokens);

            if (tagName.Length == 0) {
                tagName = window.GetText(start, window.Position);
            }

            return;
        }

        // Nothing else can appear in a tag. Report it and carry it as trivia, so the tree still
        // reproduces the file byte for byte.
        var offending = window.Position;
        window.Advance();
        Report(MarkupDiagnostics.SyntaxError, offending, window.Position, $"'{current}' cannot appear in a tag");
        Add(tokens, VxmlTokenKind.Whitespace, offending, LexedTokenFlags.Trivia);
    }

    void LexCss(List<LexedToken> tokens) {
        var start = window.Position;

        while (!window.AtEnd && !AtCloseStyleTag()) {
            window.Advance();
        }

        if (window.AtEnd) {
            Report(MarkupDiagnostics.UnclosedStyleBlock, start, window.Position);
        }

        // A `<style></style>` with nothing between it has no body token rather than an empty one:
        // a zero-width token would have nowhere to hang its trivia and nothing to say.
        if (window.Position > start) {
            Add(tokens, VxmlTokenKind.Css, start);
        }
    }

    bool AtCloseStyleTag() {
        if (window.Current != '<' || window.Peek(1) != '/') {
            return false;
        }

        const string Name = "style";
        for (var i = 0; i < Name.Length; i++) {
            if (char.ToLowerInvariant(window.Peek(i + 2)) != Name[i]) {
                return false;
            }
        }

        return !IsNamePart(window.Peek(Name.Length + 2));
    }

    // ================================================================== Attribute value

    void StepAttributeValue(List<LexedToken> tokens) {
        var current = window.Current;

        if (current == valueQuote) {
            Emit(tokens, VxmlTokenKind.Quote, 1);
            mode = Mode.Tag;
            return;
        }

        if (current == '@') {
            if (window.Peek(1) == '@') {
                Emit(tokens, VxmlTokenKind.Text, 2);
                return;
            }

            LexInterpolation(tokens);
            return;
        }

        // The two characters that could stop this run on its first character were both handled
        // above, so it always consumes at least one and the caller's progress check holds.
        var start = window.Position;
        while (!window.AtEnd && window.Current != valueQuote && window.Current != '@') {
            window.Advance();
        }

        // Whitespace inside a value is always content: nobody indents the middle of a class list
        // by accident, and `class=" "` means a space.
        Add(tokens, VxmlTokenKind.Text, start);
    }

    // ================================================================== Scanning

    /// <summary>
    ///     An expression after <c>@</c>: either a parenthesised one, or Razor's implicit form — a
    ///     name followed by member accesses, calls and indexers, stopping at the first character
    ///     that cannot continue one.
    /// </summary>
    /// <remarks>
    ///     The implicit form is what makes <c>Clicked @count times</c> read the way it looks. It is
    ///     also why <c>@a + b</c> interpolates only <c>a</c>: an operator ends the expression, and
    ///     the author who meant otherwise writes <c>@(a + b)</c>.
    /// </remarks>
    /// <returns>
    ///     Whether one was found. ⚠ A <c>false</c> does not mean nothing was consumed — an
    ///     unbalanced <c>(</c> is only known to be one at the end of the file — so the caller has to
    ///     rewind rather than assume the window stayed put.
    /// </returns>
    bool ScanExpression() {
        if (window.Current == '(') {
            return ScanBalanced('(', ')');
        }

        if (!IsNameStart(window.Current)) {
            return false;
        }

        while (IsNamePart(window.Current)) {
            window.Advance();
        }

        while (true) {
            var current = window.Current;

            if (current == '.' && IsNameStart(window.Peek(1))) {
                window.Advance();
            } else if (current == '?' && window.Peek(1) == '.' && IsNameStart(window.Peek(2))) {
                window.Advance(2);
            } else if (current == '(') {
                if (!ScanBalanced('(', ')')) {
                    return true;
                }

                continue;
            } else if (current == '[') {
                if (!ScanBalanced('[', ']')) {
                    return true;
                }

                continue;
            } else if (current == '!' && window.Peek(1) != '=') {
                window.Advance();
                continue;
            } else {
                return true;
            }

            while (IsNamePart(window.Current)) {
                window.Advance();
            }
        }
    }

    /// <summary>Consumes a delimited run, leaving the position after the closing delimiter.</summary>
    bool ScanBalanced(char open, char close) {
        var depth = 0;

        while (!window.AtEnd) {
            var current = window.Current;

            if (current == open) {
                depth++;
                window.Advance();
            } else if (current == close) {
                depth--;
                window.Advance();

                if (depth == 0) {
                    return true;
                }
            } else {
                SkipCSharp();
            }
        }

        return false;
    }

    /// <summary>Consumes up to, but not including, the <c>)</c> that closes an already-open paren.</summary>
    bool ScanToCloseParen() {
        var depth = 1;

        while (!window.AtEnd) {
            switch (window.Current) {
                case '(':
                    depth++;
                    window.Advance();
                    continue;
                case ')' when depth == 1:
                    return true;
                case ')':
                    depth--;
                    window.Advance();
                    continue;
                default:
                    SkipCSharp();
                    continue;
            }
        }

        return false;
    }

    /// <summary>Consumes up to, but not including, the <c>}</c> that closes an already-open brace.</summary>
    bool ScanToCloseBrace() {
        var depth = 1;

        while (!window.AtEnd) {
            switch (window.Current) {
                case '{':
                    depth++;
                    window.Advance();
                    continue;
                case '}' when depth == 1:
                    return true;
                case '}':
                    depth--;
                    window.Advance();
                    continue;
                default:
                    SkipCSharp();
                    continue;
            }
        }

        return false;
    }

    /// <summary>
    ///     Consumes a <c>case</c> label's pattern, stopping at the colon that ends it.
    /// </summary>
    /// <remarks>
    ///     A conditional expression is the only construct that puts a colon inside a pattern
    ///     position, so the scan tracks <c>?</c> depth — while taking care that <c>?.</c>,
    ///     <c>??</c> and a trailing <c>?</c> on a nullable type are not conditionals.
    /// </remarks>
    void ScanPattern() {
        var conditionals = 0;

        while (!window.AtEnd) {
            var current = window.Current;

            if (current == '?') {
                if (window.Peek(1) is '.' or '?' or ')' or ',' or ' ' or ':') {
                    window.Advance();
                    continue;
                }

                conditionals++;
                window.Advance();
                continue;
            }

            if (current == ':') {
                if (conditionals == 0) {
                    return;
                }

                conditionals--;
                window.Advance();
                continue;
            }

            if (current is '(' or '[' or '{') {
                var close = current == '(' ? ')' : current == '[' ? ']' : '}';
                if (!ScanBalanced(current, close)) {
                    return;
                }

                continue;
            }

            if (IsNewLine(current)) {
                return;
            }

            SkipCSharp();
        }
    }

    /// <summary>
    ///     Advances past one C# atom: a string, a character, a comment, or a single character.
    /// </summary>
    /// <remarks>
    ///     This is the whole reason a brace inside a string cannot close an <c>@code</c> block, and
    ///     it is the only C# knowledge in the assembly.
    /// </remarks>
    void SkipCSharp() {
        var current = window.Current;

        if (current == '/' && window.Peek(1) == '/') {
            while (!window.AtEnd && !IsNewLine(window.Current)) {
                window.Advance();
            }

            return;
        }

        if (current == '/' && window.Peek(1) == '*') {
            window.Advance(2);
            while (!window.AtEnd && !(window.Current == '*' && window.Peek(1) == '/')) {
                window.Advance();
            }

            if (!window.AtEnd) {
                window.Advance(2);
            }

            return;
        }

        if (current == '\'') {
            window.Advance();
            while (!window.AtEnd && window.Current != '\'') {
                window.Advance(window.Current == '\\' ? 2 : 1);
            }

            window.TryAdvance('\'');
            return;
        }

        if (AtStringStart(out var prefix)) {
            SkipString(prefix);
            return;
        }

        window.Advance();
    }

    bool AtStringStart(out int prefix) {
        prefix = 0;
        while (window.Peek(prefix) is '@' or '$') {
            prefix++;
        }

        return window.Peek(prefix) == '"';
    }

    void SkipString(int prefix) {
        var verbatim = false;
        for (var i = 0; i < prefix; i++) {
            verbatim |= window.Peek(i) == '@';
        }

        window.Advance(prefix);

        var quotes = 0;
        while (window.Peek(quotes) == '"') {
            quotes++;
        }

        if (quotes >= 3) {
            // A raw string literal: no escapes at all, and it ends at the same run of quotes.
            window.Advance(quotes);
            while (!window.AtEnd && !AtQuoteRun(quotes)) {
                window.Advance();
            }

            if (!window.AtEnd) {
                window.Advance(quotes);
            }

            return;
        }

        if (quotes == 2) {
            window.Advance(2);
            return;
        }

        window.Advance();

        while (!window.AtEnd) {
            if (window.Current == '"') {
                // In a verbatim string a doubled quote is an escaped quote, not the end.
                if (verbatim && window.Peek(1) == '"') {
                    window.Advance(2);
                    continue;
                }

                window.Advance();
                return;
            }

            window.Advance(!verbatim && window.Current == '\\' ? 2 : 1);
        }
    }

    bool AtQuoteRun(int count) {
        for (var i = 0; i < count; i++) {
            if (window.Peek(i) != '"') {
                return false;
            }
        }

        return true;
    }

    // ================================================================== Emitting

    void LexName(List<LexedToken> tokens) {
        var start = window.Position;

        if (!IsNameStart(window.Current)) {
            return;
        }

        window.Advance();
        while (IsNamePart(window.Current)) {
            window.Advance();
        }

        Add(tokens, VxmlTokenKind.Name, start);
    }

    void SkipWhitespace(List<LexedToken> tokens) {
        var start = window.Position;

        while (IsWhitespace(window.Current)) {
            window.Advance();
        }

        if (window.Position > start) {
            Add(tokens, VxmlTokenKind.Whitespace, start, LexedTokenFlags.Trivia);
        }
    }

    void Emit(List<LexedToken> tokens, VxmlTokenKind kind, int width) {
        var start = window.Position;
        window.Advance(width);
        Add(tokens, kind, start);
    }

    void Add(List<LexedToken> tokens, VxmlTokenKind kind, int start, LexedTokenFlags flags = LexedTokenFlags.None) =>
        tokens.Add(new((int)kind, window.GetText(start, window.Position), start, flags));

    void Report(DiagnosticDescriptor descriptor, int start, int end, params object[] arguments) =>
        diagnostics.Add(descriptor, Location.Create(filePath, TextSpan.FromBounds(start, end), text), arguments);

    // ================================================================== Character classes

    static bool IsNewLine(char c) => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    static bool IsWhitespace(char c) => c != SlidingTextWindow.InvalidCharacter && char.IsWhiteSpace(c);

    static bool IsNameStart(char c) => c == '_' || (c != SlidingTextWindow.InvalidCharacter && char.IsLetter(c));

    /// <summary>What can continue an identifier — a keyword, an <c>@for</c> variable.</summary>
    static bool IsIdentifierPart(char c) =>
        c == '_' || (c != SlidingTextWindow.InvalidCharacter && char.IsLetterOrDigit(c));

    /// <summary>
    ///     What can continue a name. Dashes, colons and dots are included, so <c>on:click.stop</c>
    ///     and <c>Vixen.Ui.Controls</c> are each one token — splitting them is a decision about
    ///     meaning, and the lexer has no meaning to hand.
    /// </summary>
    static bool IsNamePart(char c) =>
        c is '_' or '-' or ':' or '.' || (c != SlidingTextWindow.InvalidCharacter && char.IsLetterOrDigit(c));
}
