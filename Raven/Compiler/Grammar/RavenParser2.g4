parser grammar RavenParser2;

options { tokenVocab=RavenLexer2; superClass=RavenParserBase2; }


compilation_unit
    : package_declaration import_directive* member_declaration* EOF
    ;
    
package_declaration
    : PACKAGE name NL+
    ;
    
import_directive
    : GLOBAL? IMPORT STATIC? name NL+
    ;


// Attributes
// The trailing newline is NOT part of this rule: a declaration writes its
// attributes as `(attribute_list NL*)*` so they may sit on their own line(s),
// while a parameter writes `attribute_list*` and carries them inline —
// `func Pixel([Semantic("TEXCOORD0")] uv: float2)`.
attribute_list
    : '[' attribute_target_specifier? attribute (',' attribute)* ']'
    ;

attribute_target_specifier
    : (type? | identifier_token?) ':'
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
    : attribute_list* modifier* identifier_token (':' type)? equals_value_clause?
    ;



// Names
name
    : identifier_name '::' simple_name  #AliasQualifiedName
    | name '.' simple_name              #QualifiedName
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
    : GLOBAL
    | identifier_token;




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
    : indexer_declaration
    | property_declaration
    ;
    
field_declaration
    : (attribute_list NL*)* modifier* variable_declaration NL+
    ;
    
base_method_declaration
    : constructor_declaration
    | conversion_operator_declaration
    | destructor_declaration
    | method_declaration
    | operator_declaration
    ;

constructor_declaration
    : (attribute_list NL*)* modifier* INIT parameter_list constructor_initializer? (block | (arrow_expression_clause NL))
    ;
  
constructor_initializer
  : ':' init=(BASE | SELF) argument_list
  ;
  
destructor_declaration
    : (attribute_list NL*)* modifier* '~' INIT parameter_list (block | (arrow_expression_clause NL))
    ;
    
// The body is optional: a `protocol` member (and an `abstract` method) declares a
// signature only — `func Draw()`. The trailing newline is left to the enclosing
// member_declaration, which already ends in `NL*`.
method_declaration
  : (attribute_list NL*)* modifier* FUNC explicit_interface_specifier? identifier_token type_parameter_list? parameter_list type_parameter_constraint_clause* (':' type)? (block | (arrow_expression_clause NL))?
  ;
  
explicit_interface_specifier
    : name '.'
    ;
  
// Accessors are optional for the same reason a method body is: a protocol
// property declares `var Name: string` and nothing more.
property_declaration
    : (attribute_list NL*)* modifier* VAR explicit_interface_specifier? identifier_token (':' type)? (accessor_list | ((arrow_expression_clause | equals_value_clause) NL))?
    ;
    
accessor_list
    : '{' NL* accessor_declaration* NL* '}'
    ;

accessor_declaration
    : (attribute_list NL*)* modifier* op=(GET | SET | WILL_SET | DID_SET) (block | (arrow_expression_clause NL)) NL*
    ;

indexer_declaration
    : (attribute_list NL*)* modifier* type explicit_interface_specifier? SELF bracketed_parameter_list (accessor_list | (arrow_expression_clause NL))
    ;

bracketed_parameter_list
    : '[' parameter (',' parameter)* ']'
    ;
  
conversion_operator_declaration
    : (attribute_list NL*)* modifier* ct=(IMPLICIT | EXPLICIT) explicit_interface_specifier? OPERATOR type parameter_list (block | (arrow_expression_clause NL))
    ; 
    
operator_declaration
    : (attribute_list NL*)* modifier* type explicit_interface_specifier? OPERATOR op=('+' | '-' | '!' | '~' | '++' | '--' | '*' | '/' | '%' | '<<' | '>>' | '>>>' | '|' | '&' | '^' | '==' | '!=' | '<' | '<=' | '>' | '>=' | 'false' | 'true' | 'is') parameter_list (block | (arrow_expression_clause NL))
    ;
  
base_type_declaration
    : enum_declaration
    | type_declaration
    ;

type_declaration
    : shader_declaration
    | protocol_declaration
    | struct_declaration
    | class_declaration
    ;

shader_declaration
     : (attribute_list NL*)* modifier* SHADER identifier_token type_parameter_list? parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
     ;

struct_declaration
     : (attribute_list NL*)* modifier* STRUCT identifier_token type_parameter_list? parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
     ;

class_declaration
     : (attribute_list NL*)* modifier* CLASS identifier_token type_parameter_list? parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
     ;

protocol_declaration
    : (attribute_list NL*)* modifier* PROTOCOL identifier_token type_parameter_list? parameter_list? base_list? type_parameter_constraint_clause* ('{' NL* member_declaration* NL* '}')? NL
    ;

enum_declaration
    : (attribute_list NL*)* modifier* ENUM identifier_token base_list? '{' NL* (enum_member_declaration (',' NL* enum_member_declaration)*)? NL* '}' NL
    ;
    
enum_member_declaration
    : (attribute_list NL*)* modifier* identifier_token equals_value_clause?
    ;

type_parameter_list
    : '<' type_parameter (',' type_parameter)* '>'
    ;

type_parameter
    : (attribute_list NL*)* variance=(IN | OUT)? identifier_token
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
    : primary_constructor_base_type
    | simple_base_type
    ;

primary_constructor_base_type
    : type argument_list
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
    : name_colon? kind=(REF | OUT | IN)? expression
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
    | repeat_statement
    | empty_statement
    | expression_statement
    | for_statement
    | if_statement
    | local_declaration_statement
    | local_function_statement
    | return_statement
    | switch_statement
    | using_statement
    | while_statement
    ;

break_statement
    : (attribute_list NL*)* BREAK NL+
    ;

continue_statement
    : (attribute_list NL*)* CONTINUE NL+
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
    : ELSE block
    ;

return_statement
    : (attribute_list NL*)* RETURN expression? NL
    ;

local_function_statement
    : (attribute_list NL*)* modifier* FUNC identifier_token type_parameter_list? parameter_list type_parameter_constraint_clause* (':' type)? (block | (arrow_expression_clause NL))
    ;
    
local_declaration_statement
    : (attribute_list NL*)* USING? modifier* variable_declaration NL
    ;
    
while_statement
    : (attribute_list NL*)* WHILE '(' expression ')' statement
    ;

using_statement
    : (attribute_list NL*)* USING '(' (variable_declaration | expression) ')' statement
    ;

switch_statement
  : (attribute_list NL*)* SWITCH '('? expression ')'? '{' switch_section* '}'
  ;

switch_section
  : switch_label+ statement+
  ;

switch_label
  : case_pattern_switch_label
  | case_switch_label
  | default_switch_label
  ;

case_pattern_switch_label
  : CASE pattern when_clause? ':'
  ;
  
case_switch_label
    : CASE expression ':'
    ;

default_switch_label
    : DEFAULT ':'
    ;


// =====================================================================================================================
// ================================================= Expressions =======================================================
// =====================================================================================================================
expression
    : expression op=('=' | '+=' | '-=' | '*=' | '/=' | '%=' | '&=' | '^=' | '|=' | '<<=' | '>>=' | '>>>=' | '??=') expression #AssignmentExpression
    | expression op=('+' | '-' | '*' | '/' | '%' | '<<' | '>>' | '>>>' | '||' | '&&' | '|' | '&' | '^' | '==' | '!=' | '<' | '<=' | '>' | '>=' | 'is' | 'as' | '??') expression #BinaryExpression
    | '(' type ')' expression                   #CastExpression
    | identifier_token '=>' expression          #SimpleLambdaExpression
    | parameter_list (':' type)? '=>' expression #ParenthesizedLambdaExpression
    | '{' NL* (anonymous_member_declarator (',' NL* anonymous_member_declarator)*)? NL* '}' #AnonymousObjectCreationExpression
    | '[' NL* (collection_element (',' NL* collection_element)*)? NL* ']' #CollectionExpression
    | expression '??' expression                #ConditionalAccessExpression
    | expression '?' expression ':' expression  #ConditionalExpression
    | expression argument_list                  #InvocationExpression
    | DEFAULT '(' type ')'                      #DefaultExpression
    | expression bracketed_argument_list        #ElementAccessExpression
    | bracketed_argument_list                   #ImplicitElementAccess
    | op=(BASE | SELF)                          #InstanceExpression
    | expression IS pattern                     #IsPatternExpression
    | literal_expression                        #LiteralExpression
    | expression '.' simple_name                #MemberAccessExpression
    | '.' simple_name                           #MemberBindingExpression
    | '(' expression ')'                        #ParenthesizedExpression
    | expression op=('++' | '--' | '!')         #PostfixUnaryExpression
    | op=('!' | '+' | '++' | '-' | '--' | '^' | '~') expression #PrefixUnaryExpression
    | expression DOUBLE_DOT expression          #RangeExpression
    | REF expression                            #RefExpression
    | SIZEOF '(' type ')'                       #SizeofExpression
    | expression SWITCH '{' NL* (switch_expression_arm (',' NL* switch_expression_arm)*)? NL* '}' #SwitchExpression
    | '(' argument (',' argument)+ ')'?         #TupleExpression
    | type                                      #TypeExpression
    | type variable_designation                 #DeclarationExpression
  ;
    
literal_expression
    : DEFAULT
    | FALSE
    | TRUE
    | NULL_
    | numeric_literal_token
    | STRING_LITERAL
    | CHARACTER_LITERAL
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

anonymous_member_declarator
    : expression
    ;
    
switch_expression_arm
    : pattern when_clause? '=>' expression
    ;
    
pattern
    : pattern op=(OR | AND) pattern                             #BinaryPattern
    | expression                                                #ConstantPattern
//    | type variable_designation                                 #DeclarationPattern
    | DISCARD                                                   #DiscardPattern
    | '[' (pattern (',' pattern)*)? ']' variable_designation?   #ListPattern
    | '(' pattern ')'                                           #ParenthesizedPattern
    | op=('!=' | '<' | '<=' | '==' | '>' | '>=') expression     #RelationalPattern
    | '..' pattern?                                             #SlicePattern
//    | type                                                      #TypePattern
    | NOT pattern                                               #UnaryPattern
    | op=(VAL | VAR) variable_designation                       #VarPattern
  ;

variable_designation
    : DISCARD                                                       #DiscardDesignation
    | '(' (variable_designation (',' variable_designation)*)? ')'   #ParenthesizedVariableDesignation
    | identifier_token                                              #SimpleVariableDesignation
    ;

when_clause
    : WHEN expression
    ;

// =====================================================================================================================
// ================================================= TYPES =============================================================
// =====================================================================================================================
type
    : type array_rank_specifier+    #ArrayType
    | type '?'                      #NullableType
    | name                          #NameType
    | pType=(BOOL | BOOL2 | BOOL3 | BOOL4 | INT | INT2 | INT3 | INT4 | UINT | UINT2 | UINT3 | UINT4 | FLOAT | FLOAT2 | FLOAT3 | FLOAT4 | DOUBLE | DOUBLE2 | DOUBLE3 | DOUBLE4 | MAT2 | MAT2X3 | MAT2X4 | MAT3 | MAT3X2 | MAT3X4 | MAT4 | MAT4X2 | MAT4X3 | MAT4X4) #PredefinedType
    | '(' tuple_element (',' tuple_element)+ ')' #TupleType
    ;
    
tuple_element
  : type identifier_token?
  ;

array_rank_specifier
  : '[' ','* ']'
  ;

identifier_token
    : AT? IDENTIFIER
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
    : ABSTRACT
    | CONST
    | OVERRIDE
    | PARTIAL
    | PRIVATE
    | PROTECTED
    | PUBLIC
    | READONLY
    | RECORD
    | STATIC
    ;
    