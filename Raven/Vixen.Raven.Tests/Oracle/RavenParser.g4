// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

parser grammar RavenParser;

options { tokenVocab=RavenLexer; superClass=RavenParserBase; }


// Leading NL* lets a file open with comments or blank lines — an SPDX header
// (ADR-015) is comment lines whose terminators are NL tokens before `package`.
compilation_unit
    : NL* package_declaration import_directive* member_declaration* EOF
    ;

package_declaration
    : PACKAGE name NL+
    ;

import_directive
    : IMPORT STATIC? name NL+
    ;


// Attributes
// The trailing newline is NOT part of this rule: a declaration writes its
// attributes as `(attribute_list NL*)*` so they may sit on their own line(s),
// while a parameter writes `attribute_list*` and carries them inline —
// `func Fragment([Semantic("TEXCOORD0")] uv: float2)`.
attribute_list
    : '[' attribute (',' attribute)* ']'
    ;

attribute
    : name attribute_argument_list?
    ;

attribute_argument_list
    : '(' (attribute_argument (',' attribute_argument)*)? ')'
    ;

attribute_argument
    : name_colon? expression
    ;

parameter_list
    : '(' (parameter (',' parameter)*)? ')'
    ;

parameter
    : attribute_list* parameter_modifier* identifier_token (':' type)? equals_value_clause?
    ;

parameter_modifier
    : INOUT
    ;



// Names
name
    : name '.' simple_name              #QualifiedName
    | simple_name                       #SimpleName
    ;

simple_name
    : generic_name
    | identifier_name
    ;

generic_name
    : identifier_token type_argument_list
    ;

type_argument_list
    : '<' (type (',' type)*)? '>'
    ;

name_colon
    : identifier_name ':'
    ;

identifier_name
    : identifier_token;




// =====================================================================================================================
// ================================================= Declarations ======================================================
// =====================================================================================================================
member_declaration
    : field_declaration NL*
    | base_method_declaration NL*
    | base_property_declaration NL*
    | base_type_declaration NL*
    ;

base_property_declaration
    : property_declaration
    ;

field_declaration
    : (attribute_list NL*)* modifier* variable_declaration NL+
    ;

base_method_declaration
    : constructor_declaration
    | method_declaration
    | operator_declaration
    ;

constructor_declaration
    : (attribute_list NL*)* modifier* INIT parameter_list (block | (arrow_expression_clause NL))
    ;

// The body is optional: a `protocol` member (and an `abstract` method) declares a
// signature only — `func Draw()`. The trailing newline is left to the enclosing
// member_declaration, which already ends in `NL*`.
method_declaration
  : (attribute_list NL*)* modifier* FUNC identifier_token type_parameter_list? parameter_list type_parameter_constraint_clause* (':' type)? (block | (arrow_expression_clause NL))?
  ;

// Accessors are optional for the same reason a method body is: a protocol
// property declares `var Name: string` and nothing more.
property_declaration
    : (attribute_list NL*)* modifier* VAR identifier_token (':' type)? (accessor_list | ((arrow_expression_clause | equals_value_clause) NL))?
    ;

accessor_list
    : '{' NL* accessor_declaration* NL* '}'
    ;

accessor_declaration
    : (attribute_list NL*)* op=(GET | SET | WILL_SET | DID_SET) (block | (arrow_expression_clause NL)) NL*
    ;

operator_declaration
    : (attribute_list NL*)* modifier* type OPERATOR op=('+' | '-' | '!' | '~' | '++' | '--' | '*' | '/' | '%' | '<<' | '>>' | '>>>' | '|' | '&' | '^' | '==' | '!=' | '<' | '<=' | '>' | '>=') parameter_list (block | (arrow_expression_clause NL))
    ;

base_type_declaration
    : enum_declaration
    | type_declaration
    ;

type_declaration
    : shader_declaration
    | protocol_declaration
    | struct_declaration
    ;

shader_declaration
     : (attribute_list NL*)* modifier* SHADER identifier_token type_parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
     ;

struct_declaration
     : (attribute_list NL*)* modifier* STRUCT identifier_token type_parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
     ;

protocol_declaration
    : (attribute_list NL*)* modifier* PROTOCOL identifier_token type_parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
    ;

enum_declaration
    : (attribute_list NL*)* modifier* ENUM identifier_token base_list? '{' NL* (enum_member_declaration (',' NL* enum_member_declaration)*)? NL* '}' NL
    ;

enum_member_declaration
    : (attribute_list NL*)* identifier_token equals_value_clause?
    ;

type_parameter_list
    : '<' type_parameter (',' type_parameter)* '>'
    ;

// A `val` parameter parameterises by a compile-time constant rather than by a type:
// `shader Blur<val TapCount: int>`.
type_parameter
    : (attribute_list NL*)* identifier_token
    | (attribute_list NL*)* VAL identifier_token ':' type
    ;

type_parameter_constraint_clause
    : WHERE identifier_name ':' type_parameter_constraint (',' type_parameter_constraint)*
    ;

type_parameter_constraint
    : DEFAULT          #DefaultConstraint
    | type             #TypeContraint
    ;

base_list
    : ':' base_type (',' base_type)*
    ;

base_type
    : simple_base_type
    ;

simple_base_type
    : type
    ;

variable_declaration
    : (VAR | VAL) identifier_token (':' type)? equals_value_clause?
    ;

argument_list
    : '(' (argument (',' argument)*)? ')'
    ;

argument
    : name_colon? expression
    ;

bracketed_argument_list
    : '[' argument (',' argument)* ']'
    ;


// =====================================================================================================================
// ================================================= Statements ========================================================
// =====================================================================================================================
block
    : '{' NL* statement* NL* '}'
    ;

statement
    : block
    | break_statement
    | continue_statement
    | discard_statement
    | repeat_statement
    | empty_statement
    | expression_statement
    | for_statement
    | if_statement
    | local_declaration_statement
    | return_statement
    | switch_statement
    | while_statement
    ;

break_statement
    : (attribute_list NL*)* BREAK NL+
    ;

continue_statement
    : (attribute_list NL*)* CONTINUE NL+
    ;

discard_statement
    : (attribute_list NL*)* DISCARD NL+
    ;

repeat_statement
    : (attribute_list NL*)* REPEAT statement WHILE '(' expression ')' NL+
    ;

empty_statement
    : (attribute_list NL*)* NL+
    ;

expression_statement
    : (attribute_list NL*)* expression NL+
    ;

for_statement
    : (attribute_list NL*)* FOR '(' identifier_token IN expression ')' block
    ;

if_statement
    : (attribute_list NL*)* IF '(' expression ')' block else_clause?
    ;

else_clause
    : ELSE (block | if_statement)
    ;

return_statement
    : (attribute_list NL*)* RETURN expression? NL
    ;

local_declaration_statement
    : (attribute_list NL*)* variable_declaration NL
    ;

while_statement
    : (attribute_list NL*)* WHILE '(' expression ')' statement
    ;

switch_statement
  : (attribute_list NL*)* SWITCH '(' expression ')' '{' NL* switch_section* NL* '}' NL*
  ;

switch_section
  : switch_label+ statement+
  ;

// A label is followed by a newline in practice, and the statement after it brings its own;
// the body's braces need the same NL* every other block body has.

switch_label
  : case_switch_label
  | default_switch_label
  ;

case_switch_label
    : CASE expression ':' NL*
    ;

default_switch_label
    : DEFAULT ':' NL*
    ;


// =====================================================================================================================
// ================================================= Expressions =======================================================
// =====================================================================================================================
// ANTLR gives a left-recursive rule's alternatives DECREASING precedence in the
// order they are written, so this list runs from tightest binding to loosest:
// postfix, prefix, the arithmetic/relational/logical ladder, conditional,
// assignment, and finally the primaries (whose order among themselves
// only resolves ambiguity — e.g. a collection literal must outrank an implicit
// element access, and a cast must outrank a parenthesized expression).
expression
    // --- postfix ---
    : expression argument_list                  #InvocationExpression
    | expression bracketed_argument_list        #ElementAccessExpression
    | expression '.' simple_name                #MemberAccessExpression
    | expression op=('++' | '--')               #PostfixUnaryExpression
    // --- prefix ---
    | op=('!' | '+' | '++' | '-' | '--' | '~') expression #PrefixUnaryExpression
    | '(' unsized_type ')' expression           #CastExpression
    // --- binary operators, tightest first ---
    | expression op=('*' | '/' | '%') expression #BinaryExpression
    | expression op=('+' | '-') expression      #BinaryExpression
    | expression op=('<<' | '>>' | '>>>') expression #BinaryExpression
    | expression DOUBLE_DOT expression          #RangeExpression
    | expression op=('<' | '<=' | '>' | '>=') expression #BinaryExpression
    | expression op=('==' | '!=') expression    #BinaryExpression
    | expression op='&' expression              #BinaryExpression
    | expression op='^' expression              #BinaryExpression
    | expression op='|' expression              #BinaryExpression
    | expression op='&&' expression             #BinaryExpression
    | expression op='||' expression             #BinaryExpression
    | <assoc=right> expression '?' expression ':' expression #ConditionalExpression
    // --- assignment binds loosest, so its right-hand side swallows the rest ---
    | <assoc=right> expression op=('=' | '+=' | '-=' | '*=' | '/=' | '%=' | '&=' | '^=' | '|=' | '<<=' | '>>=' | '>>>=') expression #AssignmentExpression
    // --- primaries ---
    | '[' NL* (collection_element (',' NL* collection_element)*)? NL* ']' #CollectionExpression
    | DEFAULT '(' type ')'                      #DefaultExpression
    | op=(BASE | SELF)                          #InstanceExpression
    | literal_expression                        #LiteralExpression
    | '(' expression ')'                        #ParenthesizedExpression
    | '(' argument (',' argument)+ ')'          #TupleExpression
    | unsized_type                              #TypeExpression
  ;

literal_expression
    : DEFAULT
    | FALSE
    | TRUE
    | numeric_literal_token
    // A string literal is metadata only: it may appear in an attribute
    // argument, never as a runtime value. The binder enforces that.
    | STRING_LITERAL
    ;

equals_value_clause
    : '=' expression
    ;

arrow_expression_clause
    : '=>' expression
    ;

collection_element
    : expression        #ExpressionElement
    | '..' expression   #SpreadElement
    ;


// =====================================================================================================================
// ================================================= TYPES =============================================================
// =====================================================================================================================
// A type where a type is *certain*: a declaration's annotation, a return type, a type
// argument, a tuple element, a base type, `default(…)`. Nothing else can own a `[` here,
// so `[4]` is free to size the array.
type
    : type array_rank_specifier+    #ArrayType
    | name                          #NameType
    | pType=(BOOL | BOOL2 | BOOL3 | BOOL4 | INT | INT2 | INT3 | INT4 | UINT | UINT2 | UINT3 | UINT4 | FLOAT | FLOAT2 | FLOAT3 | FLOAT4 | DOUBLE | DOUBLE2 | DOUBLE3 | DOUBLE4 | MAT2 | MAT2X3 | MAT2X4 | MAT3 | MAT3X2 | MAT3X4 | MAT4 | MAT4X2 | MAT4X3) #PredefinedType
    | '(' tuple_element (',' tuple_element)+ ')' #TupleType
    ;

// The same grammar without sizes, for the two positions where a type competes with an
// expression reading: a cast's type, and a bare type used as a value (which is also how
// every plain identifier expression arrives). A `[…]` there is an element access — `a[4]`
// indexes and `(a[4]) - 1` is arithmetic, not a cast of `-1`.
//
// It has to be a second rule rather than a flag on the first, because ANTLR's subrules are
// greedy: reaching the sized alternative from here would let `type array_rank_specifier+`
// swallow the `[4]` before `#ElementAccessExpression` was ever offered it. The hand-written
// parser draws the same line with `ScanArrayRank(allowSizes:)` and `TryScanType(false)`.
unsized_type
    : unsized_type unsized_rank_specifier+  #UnsizedArrayType
    | name                                  #UnsizedNameType
    | pType=(BOOL | BOOL2 | BOOL3 | BOOL4 | INT | INT2 | INT3 | INT4 | UINT | UINT2 | UINT3 | UINT4 | FLOAT | FLOAT2 | FLOAT3 | FLOAT4 | DOUBLE | DOUBLE2 | DOUBLE3 | DOUBLE4 | MAT2 | MAT2X3 | MAT2X4 | MAT3 | MAT3X2 | MAT3X4 | MAT4 | MAT4X2 | MAT4X3) #UnsizedPredefinedType
    | '(' tuple_element (',' tuple_element)+ ')' #UnsizedTupleType
    ;

// `(rgb: float3, a: float)` — the name leads, as it does for a field, a parameter and a
// `val`. This was the one place in the language where a name followed its type.
tuple_element
  : (identifier_token ':')? type
  ;

// `[4]` sizes, `[]` does not, `[,]` adds a dimension. A size and commas are alternatives
// rather than a sequence, because a multi-dimensional array is never sized.
array_rank_specifier
  : '[' ','* ']'
  | '[' expression ']'
  ;

unsized_rank_specifier
  : '[' ','* ']'
  ;

identifier_token
    : IDENTIFIER
    ;

numeric_literal_token
    : integer_literal_token
    | real_literal_token
    ;

real_literal_token
    : REAL_LITERAL
    ;

integer_literal_token
    : INTEGER_LITERAL
    | HEX_INTEGER_LITERAL
    | BIN_INTEGER_LITERAL
    ;

modifier
    : COMPOSE
    | CONST
    | OVERRIDE
    | READONLY
    | STATIC
    | STREAM
    | GROUPSHARED
    ;
