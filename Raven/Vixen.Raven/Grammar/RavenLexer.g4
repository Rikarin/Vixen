// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

lexer grammar RavenLexer;
import UnicodeClasses;

channels { COMMENTS_CHANNEL }
options { superClass = RavenLexerBase; }

SINGLE_LINE_DOC_COMMENT:     '///' InputCharacter* -> channel(COMMENTS_CHANNEL);
EMPTY_DELIMITED_DOC_COMMENT: '/***/'               -> channel(COMMENTS_CHANNEL);
DELIMITED_DOC_COMMENT:       '/**' ~'/' .*? '*/'   -> channel(COMMENTS_CHANNEL);
SINGLE_LINE_COMMENT:         '//' InputCharacter*  -> channel(COMMENTS_CHANNEL);
DELIMITED_COMMENT:           '/*' .*? '*/'         -> channel(COMMENTS_CHANNEL);
WHITESPACES:                 Whitespace+           -> channel(HIDDEN);
NL:                          NewLine;


// Accessors
GET: 'get';
SET: 'set';
WILL_SET: 'willSet';
DID_SET: 'didSet';




// Our keywords
FUNC:           'func';
PROTOCOL:       'protocol';     // Replaceing 'interface'
SELF:           'self';         // Replacing 'this'
SHADER:         'shader';
STRUCT:         'struct';
VAR:            'var';
VAL:            'val';
REPEAT:         'repeat';       // Replacing 'do' in do-while
IMPORT:         'import';       // Replacing 'using'
PACKAGE:        'package';
INIT:           'init';         // Replacing ctor()



// Data Types
BOOL:           'bool';
BOOL2:          'bool2';
BOOL3:          'bool3';
BOOL4:          'bool4';
INT:            'int';
INT2:           'int2';
INT3:           'int3';
INT4:           'int4';
UINT:           'uint';
UINT2:          'uint2';
UINT3:          'uint3';
UINT4:          'uint4';
FLOAT:          'float';
FLOAT2:         'float2';
FLOAT3:         'float3';
FLOAT4:         'float4';
DOUBLE:         'double';
DOUBLE2:        'double2';
DOUBLE3:        'double3';
DOUBLE4:        'double4';
MAT2:           'mat2';
MAT2X3:         'mat2x3';
MAT2X4:         'mat2x4';
MAT3:           'mat3';
MAT3X2:         'mat3x2';
MAT3X4:         'mat3x4';
MAT4:           'mat4';
MAT4X2:         'mat4x2';
MAT4X3:         'mat4x3';



// Keywords
BASE:           'base';
BREAK:          'break';
CASE:           'case';
CONTINUE:       'continue';
DEFAULT:        'default';
ELSE:           'else';
ENUM:           'enum';
FALSE:          'false';
FOR:            'for';
IF:             'if';
IN:             'in';
OPERATOR:       'operator';
RETURN:         'return';
SWITCH:         'switch';
TRUE:           'true';
WHILE:          'while';
WHERE:          'where';


// ===== Modifiers =====
COMPOSE:        'compose';      // Shader-typed member, bound to a concrete shader at compile time
CONST:          'const';
OVERRIDE:       'override';
READONLY:       'readonly';
STATIC:         'static';




// Identifiers
IDENTIFIER: IdentifierOrKeyword;


// Literals
//LITERAL_ACCESS:      [0-9] ('_'* [0-9])* IntegerTypeSuffix? DOT IdentifierOrKeyword;
INTEGER_LITERAL:     [0-9] ('_'* [0-9])* IntegerTypeSuffix?;
HEX_INTEGER_LITERAL: '0' [xX] ('_'* HexDigit)+ IntegerTypeSuffix?;
BIN_INTEGER_LITERAL: '0' [bB] ('_'* [01])+ IntegerTypeSuffix?;
REAL_LITERAL:        ([0-9] ('_'* [0-9])*)? '.' [0-9] ('_'* [0-9])* ExponentPart? [FfDdMm]? | [0-9] ('_'* [0-9])* ([FfDdMm] | ExponentPart [FfDdMm]?);

STRING_LITERAL:      '"'  (~["\\\r\n\u0085\u2028\u2029] | CommonCharacter)* '"';


// Punctuations
OPEN_BRACE:               '{' { this.OnOpenBrace(); };
CLOSE_BRACE:              '}' { this.OnCloseBrace(); };
OPEN_BRACKET:             '[';
CLOSE_BRACKET:            ']';
OPEN_PARENS:              '(';
CLOSE_PARENS:             ')';
DOT:                      '.';
DOUBLE_DOT:               '..';
COMMA:                    ',';
COLON:                    ':' { this.OnColon(); };
INTERR:                   '?';
LAMBDA:                   '=>';


// Operators
PLUS:                     '+';
MINUS:                    '-';
STAR:                     '*';
DIV:                      '/';
PERCENT:                  '%';
AMP:                      '&';
BITWISE_OR:               '|';
CARET:                    '^';
BANG:                     '!';
TILDE:                    '~';
ASSIGNMENT:               '=';
LT:                       '<';
GT:                       '>';
OP_INC:                   '++';
OP_DEC:                   '--';
OP_AND:                   '&&';
OP_OR:                    '||';
OP_EQ:                    '==';
OP_NE:                    '!=';
OP_LE:                    '<=';
OP_GE:                    '>=';
OP_ADD_ASSIGNMENT:        '+=';
OP_SUB_ASSIGNMENT:        '-=';
OP_MULT_ASSIGNMENT:       '*=';
OP_DIV_ASSIGNMENT:        '/=';
OP_MOD_ASSIGNMENT:        '%=';
OP_AND_ASSIGNMENT:        '&=';
OP_OR_ASSIGNMENT:         '|=';
OP_XOR_ASSIGNMENT:        '^=';
OP_LEFT_SHIFT:            '<<';
OP_LEFT_SHIFT_ASSIGNMENT: '<<=';
OP_RIGHT_SHIFT:           '>>';
OP_RIGHT_SHIFT_ASSIGNMENT: '>>=';
OP_UNSIGNED_RIGHT_SHIFT:   '>>>';
OP_UNSIGNED_RIGHT_SHIFT_ASSIGNMENT: '>>>=';


// Fragments
// Everything except new line character. Used for comments
fragment InputCharacter:     ~[\r\n\u0085\u2028\u2029];
fragment IntegerTypeSuffix:  [uU];
fragment ExponentPart:       [eE] ('+' | '-')? [0-9] ('_'* [0-9])*;

fragment CommonCharacter
	: SimpleEscapeSequence
	| HexEscapeSequence
	| UnicodeEscapeSequence
	;

fragment SimpleEscapeSequence
	: '\\\''
	| '\\"'
	| '\\\\'
	| '\\0'
	| '\\a'
	| '\\b'
	| '\\f'
	| '\\n'
	| '\\r'
	| '\\t'
	| '\\v'
	;

fragment HexEscapeSequence
	: '\\x' HexDigit
	| '\\x' HexDigit HexDigit
	| '\\x' HexDigit HexDigit HexDigit
	| '\\x' HexDigit HexDigit HexDigit HexDigit
	;

fragment NewLine
	: '\r\n' | '\r' | '\n'
	| '\u0085' // <Next Line CHARACTER (U+0085)>'
	| '\u2028' // '<Line Separator CHARACTER (U+2028)>'
	| '\u2029' // '<Paragraph Separator CHARACTER (U+2029)>'
	;

fragment Whitespace
	: UNICODE_CLASS_ZS // '<Any Character With Unicode Class Zs>'
	| '\u0009' // '<Horizontal Tab Character (U+0009)>'
	| '\u000B' // '<Vertical Tab Character (U+000B)>'
	| '\u000C' // '<Form Feed Character (U+000C)>'
	;

fragment IdentifierOrKeyword
	: IdentifierStartCharacter IdentifierPartCharacter*
	;

fragment IdentifierStartCharacter
	: LetterCharacter
	| '_'
	;

fragment IdentifierPartCharacter
	: LetterCharacter
	| DecimalDigitCharacter
	| ConnectingCharacter
	| CombiningCharacter
	| FormattingCharacter
	;

// '<A Unicode Character Of Classes Lu, Ll, Lt, Lm, Lo, Or Nl>'
// WARNING: ignores UnicodeEscapeSequence
fragment LetterCharacter
	: UNICODE_CLASS_LU
	| UNICODE_CLASS_LL
	| UNICODE_CLASS_LT
	| UNICODE_CLASS_LM
	| UNICODE_CLASS_LO
	| UNICODE_CLASS_NL
	| UnicodeEscapeSequence
	;

// '<A Unicode Character Of The Class Nd>'
// WARNING: ignores UnicodeEscapeSequence
fragment DecimalDigitCharacter
	: UNICODE_CLASS_ND
	| UnicodeEscapeSequence
	;

//'<A Unicode Character Of The Class Pc>'
// WARNING: ignores UnicodeEscapeSequence
fragment ConnectingCharacter
	: UNICODE_CLASS_PC
	| UnicodeEscapeSequence
	;

// '<A Unicode Character Of Classes Mn Or Mc>'
// WARNING: ignores UnicodeEscapeSequence
fragment CombiningCharacter
	: UNICODE_CLASS_MN
	| UNICODE_CLASS_MC
	| UnicodeEscapeSequence
	;

// '<A Unicode Character Of The Class Cf>'
// WARNING: ignores UnicodeEscapeSequence
fragment FormattingCharacter
	: UNICODE_CLASS_CF
	| UnicodeEscapeSequence
	;

//B.1.5 Unicode Character Escape Sequences
fragment UnicodeEscapeSequence
	: '\\u' HexDigit HexDigit HexDigit HexDigit
	| '\\U' HexDigit HexDigit HexDigit HexDigit HexDigit HexDigit HexDigit HexDigit
	;

fragment HexDigit : [0-9] | [A-F] | [a-f];
