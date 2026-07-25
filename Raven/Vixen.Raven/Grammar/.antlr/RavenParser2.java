// Generated from /Users/jiu/Projects/Vixen/Raven/Compiler/Grammar/RavenParser2.g4 by ANTLR 4.13.1
import org.antlr.v4.runtime.atn.*;
import org.antlr.v4.runtime.dfa.DFA;
import org.antlr.v4.runtime.*;
import org.antlr.v4.runtime.misc.*;
import org.antlr.v4.runtime.tree.*;
import java.util.List;
import java.util.Iterator;
import java.util.ArrayList;

@SuppressWarnings({"all", "warnings", "unchecked", "unused", "cast", "CheckReturnValue"})
public class RavenParser2 extends RavenParserBase2 {
	static { RuntimeMetaData.checkVersion("4.13.1", RuntimeMetaData.VERSION); }

	protected static final DFA[] _decisionToDFA;
	protected static final PredictionContextCache _sharedContextCache =
		new PredictionContextCache();
	public static final int
		SINGLE_LINE_DOC_COMMENT=1, EMPTY_DELIMITED_DOC_COMMENT=2, DELIMITED_DOC_COMMENT=3, 
		SINGLE_LINE_COMMENT=4, DELIMITED_COMMENT=5, WHITESPACES=6, SHARP=7, NL=8, 
		GLOBAL=9, OR=10, AND=11, NOT=12, DISCARD=13, GET=14, SET=15, WILL_SET=16, 
		DID_SET=17, USING=18, FUNC=19, PROTOCOL=20, SELF=21, SHADER=22, VAR=23, 
		VAL=24, REPEAT=25, IMPORT=26, PACKAGE=27, INIT=28, BOOL=29, BOOL2=30, 
		BOOL3=31, BOOL4=32, INT=33, INT2=34, INT3=35, INT4=36, UINT=37, UINT2=38, 
		UINT3=39, UINT4=40, FLOAT=41, FLOAT2=42, FLOAT3=43, FLOAT4=44, DOUBLE=45, 
		DOUBLE2=46, DOUBLE3=47, DOUBLE4=48, MAT2=49, MAT2X3=50, MAT2X4=51, MAT3=52, 
		MAT3X2=53, MAT3X4=54, MAT4=55, MAT4X2=56, MAT4X3=57, AS=58, BASE=59, BREAK=60, 
		CASE=61, CONTINUE=62, DEFAULT=63, ELSE=64, ENUM=65, EXPLICIT=66, FALSE=67, 
		FOR=68, IF=69, IMPLICIT=70, IN=71, IS=72, NULL_=73, OPERATOR=74, OUT=75, 
		REF=76, RETURN=77, SIZEOF=78, STRUCT=79, SWITCH=80, TRUE=81, WHILE=82, 
		WHEN=83, WHERE=84, ABSTRACT=85, CONST=86, OVERRIDE=87, PARTIAL=88, PRIVATE=89, 
		PROTECTED=90, PUBLIC=91, READONLY=92, STATIC=93, IDENTIFIER=94, AT=95, 
		INTEGER_LITERAL=96, HEX_INTEGER_LITERAL=97, BIN_INTEGER_LITERAL=98, REAL_LITERAL=99, 
		OPEN_BRACE=100, CLOSE_BRACE=101, OPEN_BRACKET=102, CLOSE_BRACKET=103, 
		OPEN_PARENS=104, CLOSE_PARENS=105, DOT=106, DOUBLE_DOT=107, COMMA=108, 
		COLON=109, DOUBLE_COLON=110, SEMICOLON=111, INTERR=112, LAMBDA=113, PLUS=114, 
		MINUS=115, STAR=116, DIV=117, PERCENT=118, AMP=119, BITWISE_OR=120, CARET=121, 
		BANG=122, TILDE=123, ASSIGNMENT=124, LT=125, GT=126, OP_COALESCING=127, 
		OP_INC=128, OP_DEC=129, OP_AND=130, OP_OR=131, OP_EQ=132, OP_NE=133, OP_LE=134, 
		OP_GE=135, OP_ADD_ASSIGNMENT=136, OP_SUB_ASSIGNMENT=137, OP_MULT_ASSIGNMENT=138, 
		OP_DIV_ASSIGNMENT=139, OP_MOD_ASSIGNMENT=140, OP_AND_ASSIGNMENT=141, OP_OR_ASSIGNMENT=142, 
		OP_XOR_ASSIGNMENT=143, OP_LEFT_SHIFT=144, OP_LEFT_SHIFT_ASSIGNMENT=145, 
		OP_COALESCING_ASSIGNMENT=146, OP_RIGHT_SHIFT=147, OP_RIGHT_SHIFT_ASSIGNMENT=148, 
		OP_UNSIGNED_RIGHT_SHIFT=149, OP_UNSIGNED_RIGHT_SHIFT_ASSIGNMENT=150, UNICODE_CLASS_CC=151, 
		UNICODE_CLASS_CF=152, UNICODE_CLASS_CO=153, UNICODE_CLASS_CS=154, UNICODE_CLASS_LL=155, 
		UNICODE_CLASS_LM=156, UNICODE_CLASS_LO=157, UNICODE_CLASS_LT=158, UNICODE_CLASS_LU=159, 
		UNICODE_CLASS_MC=160, UNICODE_CLASS_ME=161, UNICODE_CLASS_MN=162, UNICODE_CLASS_ND=163, 
		UNICODE_CLASS_NL=164, UNICODE_CLASS_NO=165, UNICODE_CLASS_PC=166, UNICODE_CLASS_PD=167, 
		UNICODE_CLASS_PE=168, UNICODE_CLASS_PF=169, UNICODE_CLASS_PI=170, UNICODE_CLASS_PO=171, 
		UNICODE_CLASS_PS=172, UNICODE_CLASS_SC=173, UNICODE_CLASS_SK=174, UNICODE_CLASS_SM=175, 
		UNICODE_CLASS_SO=176, UNICODE_CLASS_ZL=177, UNICODE_CLASS_ZP=178, UNICODE_CLASS_ZS=179, 
		DIRECTIVE_WHITESPACES=180, DIGITS=181, DEFINE=182, UNDEF=183, ELIF=184, 
		ENDIF=185, LINE=186, ERROR=187, WARNING=188, PRAGMA=189, DIRECTIVE_HIDDEN=190, 
		CONDITIONAL_SYMBOL=191, DIRECTIVE_NEW_LINE=192, TEXT=193, MAT4X4=194;
	public static final int
		RULE_compilation_unit = 0, RULE_package_declaration = 1, RULE_import_directive = 2, 
		RULE_attribute_list = 3, RULE_attribute_target_specifier = 4, RULE_attribute = 5, 
		RULE_attribute_argument_list = 6, RULE_attribute_argument = 7, RULE_parameter_list = 8, 
		RULE_parameter = 9, RULE_name = 10, RULE_simple_name = 11, RULE_generic_name = 12, 
		RULE_type_argument_list = 13, RULE_name_colon = 14, RULE_identifier_name = 15, 
		RULE_member_declaration = 16, RULE_base_property_declaration = 17, RULE_field_declaration = 18, 
		RULE_base_method_declaration = 19, RULE_constructor_declaration = 20, 
		RULE_constructor_initializer = 21, RULE_destructor_declaration = 22, RULE_method_declaration = 23, 
		RULE_explicit_interface_specifier = 24, RULE_property_declaration = 25, 
		RULE_accessor_list = 26, RULE_accessor_declaration = 27, RULE_indexer_declaration = 28, 
		RULE_bracketed_parameter_list = 29, RULE_conversion_operator_declaration = 30, 
		RULE_operator_declaration = 31, RULE_base_type_declaration = 32, RULE_type_declaration = 33, 
		RULE_shader_declaration = 34, RULE_protocol_declaration = 35, RULE_enum_declaration = 36, 
		RULE_enum_member_declaration = 37, RULE_type_parameter_list = 38, RULE_type_parameter = 39, 
		RULE_type_parameter_constraint_clause = 40, RULE_type_parameter_constraint = 41, 
		RULE_base_list = 42, RULE_base_type = 43, RULE_primary_constructor_base_type = 44, 
		RULE_simple_base_type = 45, RULE_variable_declaration = 46, RULE_argument_list = 47, 
		RULE_argument = 48, RULE_bracketed_argument_list = 49, RULE_block = 50, 
		RULE_statement = 51, RULE_break_statement = 52, RULE_continue_statement = 53, 
		RULE_repeat_statement = 54, RULE_empty_statement = 55, RULE_expression_statement = 56, 
		RULE_for_statement = 57, RULE_if_statement = 58, RULE_else_clause = 59, 
		RULE_return_statement = 60, RULE_local_function_statement = 61, RULE_local_declaration_statement = 62, 
		RULE_while_statement = 63, RULE_using_statement = 64, RULE_switch_statement = 65, 
		RULE_switch_section = 66, RULE_switch_label = 67, RULE_case_pattern_switch_label = 68, 
		RULE_case_switch_label = 69, RULE_default_switch_label = 70, RULE_expression = 71, 
		RULE_literal_expression = 72, RULE_equals_value_clause = 73, RULE_arrow_expression_clause = 74, 
		RULE_collection_element = 75, RULE_switch_expression_arm = 76, RULE_pattern = 77, 
		RULE_variable_designation = 78, RULE_when_clause = 79, RULE_type = 80, 
		RULE_tuple_element = 81, RULE_array_rank_specifier = 82, RULE_identifier_token = 83, 
		RULE_numeric_literal_token = 84, RULE_real_literal_token = 85, RULE_integer_literal_token = 86, 
		RULE_modifier = 87;
	private static String[] makeRuleNames() {
		return new String[] {
			"compilation_unit", "package_declaration", "import_directive", "attribute_list", 
			"attribute_target_specifier", "attribute", "attribute_argument_list", 
			"attribute_argument", "parameter_list", "parameter", "name", "simple_name", 
			"generic_name", "type_argument_list", "name_colon", "identifier_name", 
			"member_declaration", "base_property_declaration", "field_declaration", 
			"base_method_declaration", "constructor_declaration", "constructor_initializer", 
			"destructor_declaration", "method_declaration", "explicit_interface_specifier", 
			"property_declaration", "accessor_list", "accessor_declaration", "indexer_declaration", 
			"bracketed_parameter_list", "conversion_operator_declaration", "operator_declaration", 
			"base_type_declaration", "type_declaration", "shader_declaration", "protocol_declaration", 
			"enum_declaration", "enum_member_declaration", "type_parameter_list", 
			"type_parameter", "type_parameter_constraint_clause", "type_parameter_constraint", 
			"base_list", "base_type", "primary_constructor_base_type", "simple_base_type", 
			"variable_declaration", "argument_list", "argument", "bracketed_argument_list", 
			"block", "statement", "break_statement", "continue_statement", "repeat_statement", 
			"empty_statement", "expression_statement", "for_statement", "if_statement", 
			"else_clause", "return_statement", "local_function_statement", "local_declaration_statement", 
			"while_statement", "using_statement", "switch_statement", "switch_section", 
			"switch_label", "case_pattern_switch_label", "case_switch_label", "default_switch_label", 
			"expression", "literal_expression", "equals_value_clause", "arrow_expression_clause", 
			"collection_element", "switch_expression_arm", "pattern", "variable_designation", 
			"when_clause", "type", "tuple_element", "array_rank_specifier", "identifier_token", 
			"numeric_literal_token", "real_literal_token", "integer_literal_token", 
			"modifier"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, "'/***/'", null, null, null, null, "'#'", null, "'global'", 
			"'or'", "'and'", "'not'", "'_'", "'get'", "'set'", "'willSet'", "'didSet'", 
			"'using'", "'func'", "'protocol'", "'self'", "'shader'", "'var'", "'val'", 
			"'repeat'", "'import'", "'package'", "'init'", "'bool'", "'bool2'", "'bool3'", 
			"'bool4'", "'int'", "'int2'", "'int3'", "'int4'", "'uint'", "'uint2'", 
			"'uint3'", "'uint4'", "'float'", "'float2'", "'float3'", "'float4'", 
			"'double'", "'double2'", "'double3'", "'double4'", "'mat2'", "'mat2x3'", 
			"'mat2x4'", "'mat3'", "'mat3x2'", "'mat3x4'", "'mat4'", "'mat4x2'", "'mat4x3'", 
			"'as'", "'base'", "'break'", "'case'", "'continue'", "'default'", "'else'", 
			"'enum'", "'explicit'", "'false'", "'for'", "'if'", "'implicit'", "'in'", 
			"'is'", "'null'", "'operator'", "'out'", "'ref'", "'return'", "'sizeof'", 
			"'struct'", "'switch'", "'true'", "'while'", "'when'", "'where'", "'abstract'", 
			"'const'", "'override'", "'partial'", "'private'", "'protected'", "'public'", 
			"'readonly'", "'static'", null, "'@'", null, null, null, null, "'{'", 
			"'}'", "'['", "']'", "'('", "')'", "'.'", "'..'", "','", "':'", "'::'", 
			"';'", "'?'", "'=>'", "'+'", "'-'", "'*'", "'/'", "'%'", "'&'", "'|'", 
			"'^'", "'!'", "'~'", "'='", "'<'", "'>'", "'??'", "'++'", "'--'", "'&&'", 
			"'||'", "'=='", "'!='", "'<='", "'>='", "'+='", "'-='", "'*='", "'/='", 
			"'%='", "'&='", "'|='", "'^='", "'<<'", "'<<='", "'??='", "'>>'", "'>>='", 
			"'>>>'", "'>>>='", null, null, null, null, null, null, null, null, null, 
			null, null, null, null, null, null, null, null, null, null, null, null, 
			null, null, null, null, null, "'\\u2028'", "'\\u2029'", null, null, null, 
			"'define'", "'undef'", "'elif'", "'endif'", "'line'", null, null, null, 
			"'hidden'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "SINGLE_LINE_DOC_COMMENT", "EMPTY_DELIMITED_DOC_COMMENT", "DELIMITED_DOC_COMMENT", 
			"SINGLE_LINE_COMMENT", "DELIMITED_COMMENT", "WHITESPACES", "SHARP", "NL", 
			"GLOBAL", "OR", "AND", "NOT", "DISCARD", "GET", "SET", "WILL_SET", "DID_SET", 
			"USING", "FUNC", "PROTOCOL", "SELF", "SHADER", "VAR", "VAL", "REPEAT", 
			"IMPORT", "PACKAGE", "INIT", "BOOL", "BOOL2", "BOOL3", "BOOL4", "INT", 
			"INT2", "INT3", "INT4", "UINT", "UINT2", "UINT3", "UINT4", "FLOAT", "FLOAT2", 
			"FLOAT3", "FLOAT4", "DOUBLE", "DOUBLE2", "DOUBLE3", "DOUBLE4", "MAT2", 
			"MAT2X3", "MAT2X4", "MAT3", "MAT3X2", "MAT3X4", "MAT4", "MAT4X2", "MAT4X3", 
			"AS", "BASE", "BREAK", "CASE", "CONTINUE", "DEFAULT", "ELSE", "ENUM", 
			"EXPLICIT", "FALSE", "FOR", "IF", "IMPLICIT", "IN", "IS", "NULL_", "OPERATOR", 
			"OUT", "REF", "RETURN", "SIZEOF", "STRUCT", "SWITCH", "TRUE", "WHILE", 
			"WHEN", "WHERE", "ABSTRACT", "CONST", "OVERRIDE", "PARTIAL", "PRIVATE", 
			"PROTECTED", "PUBLIC", "READONLY", "STATIC", "IDENTIFIER", "AT", "INTEGER_LITERAL", 
			"HEX_INTEGER_LITERAL", "BIN_INTEGER_LITERAL", "REAL_LITERAL", "OPEN_BRACE", 
			"CLOSE_BRACE", "OPEN_BRACKET", "CLOSE_BRACKET", "OPEN_PARENS", "CLOSE_PARENS", 
			"DOT", "DOUBLE_DOT", "COMMA", "COLON", "DOUBLE_COLON", "SEMICOLON", "INTERR", 
			"LAMBDA", "PLUS", "MINUS", "STAR", "DIV", "PERCENT", "AMP", "BITWISE_OR", 
			"CARET", "BANG", "TILDE", "ASSIGNMENT", "LT", "GT", "OP_COALESCING", 
			"OP_INC", "OP_DEC", "OP_AND", "OP_OR", "OP_EQ", "OP_NE", "OP_LE", "OP_GE", 
			"OP_ADD_ASSIGNMENT", "OP_SUB_ASSIGNMENT", "OP_MULT_ASSIGNMENT", "OP_DIV_ASSIGNMENT", 
			"OP_MOD_ASSIGNMENT", "OP_AND_ASSIGNMENT", "OP_OR_ASSIGNMENT", "OP_XOR_ASSIGNMENT", 
			"OP_LEFT_SHIFT", "OP_LEFT_SHIFT_ASSIGNMENT", "OP_COALESCING_ASSIGNMENT", 
			"OP_RIGHT_SHIFT", "OP_RIGHT_SHIFT_ASSIGNMENT", "OP_UNSIGNED_RIGHT_SHIFT", 
			"OP_UNSIGNED_RIGHT_SHIFT_ASSIGNMENT", "UNICODE_CLASS_CC", "UNICODE_CLASS_CF", 
			"UNICODE_CLASS_CO", "UNICODE_CLASS_CS", "UNICODE_CLASS_LL", "UNICODE_CLASS_LM", 
			"UNICODE_CLASS_LO", "UNICODE_CLASS_LT", "UNICODE_CLASS_LU", "UNICODE_CLASS_MC", 
			"UNICODE_CLASS_ME", "UNICODE_CLASS_MN", "UNICODE_CLASS_ND", "UNICODE_CLASS_NL", 
			"UNICODE_CLASS_NO", "UNICODE_CLASS_PC", "UNICODE_CLASS_PD", "UNICODE_CLASS_PE", 
			"UNICODE_CLASS_PF", "UNICODE_CLASS_PI", "UNICODE_CLASS_PO", "UNICODE_CLASS_PS", 
			"UNICODE_CLASS_SC", "UNICODE_CLASS_SK", "UNICODE_CLASS_SM", "UNICODE_CLASS_SO", 
			"UNICODE_CLASS_ZL", "UNICODE_CLASS_ZP", "UNICODE_CLASS_ZS", "DIRECTIVE_WHITESPACES", 
			"DIGITS", "DEFINE", "UNDEF", "ELIF", "ENDIF", "LINE", "ERROR", "WARNING", 
			"PRAGMA", "DIRECTIVE_HIDDEN", "CONDITIONAL_SYMBOL", "DIRECTIVE_NEW_LINE", 
			"TEXT", "MAT4X4"
		};
	}
	private static final String[] _SYMBOLIC_NAMES = makeSymbolicNames();
	public static final Vocabulary VOCABULARY = new VocabularyImpl(_LITERAL_NAMES, _SYMBOLIC_NAMES);

	/**
	 * @deprecated Use {@link #VOCABULARY} instead.
	 */
	@Deprecated
	public static final String[] tokenNames;
	static {
		tokenNames = new String[_SYMBOLIC_NAMES.length];
		for (int i = 0; i < tokenNames.length; i++) {
			tokenNames[i] = VOCABULARY.getLiteralName(i);
			if (tokenNames[i] == null) {
				tokenNames[i] = VOCABULARY.getSymbolicName(i);
			}

			if (tokenNames[i] == null) {
				tokenNames[i] = "<INVALID>";
			}
		}
	}

	@Override
	@Deprecated
	public String[] getTokenNames() {
		return tokenNames;
	}

	@Override

	public Vocabulary getVocabulary() {
		return VOCABULARY;
	}

	@Override
	public String getGrammarFileName() { return "RavenParser2.g4"; }

	@Override
	public String[] getRuleNames() { return ruleNames; }

	@Override
	public String getSerializedATN() { return _serializedATN; }

	@Override
	public ATN getATN() { return _ATN; }

	public RavenParser2(TokenStream input) {
		super(input);
		_interp = new ParserATNSimulator(this,_ATN,_decisionToDFA,_sharedContextCache);
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Compilation_unitContext extends ParserRuleContext {
		public Package_declarationContext package_declaration() {
			return getRuleContext(Package_declarationContext.class,0);
		}
		public TerminalNode EOF() { return getToken(RavenParser2.EOF, 0); }
		public List<Import_directiveContext> import_directive() {
			return getRuleContexts(Import_directiveContext.class);
		}
		public Import_directiveContext import_directive(int i) {
			return getRuleContext(Import_directiveContext.class,i);
		}
		public List<Member_declarationContext> member_declaration() {
			return getRuleContexts(Member_declarationContext.class);
		}
		public Member_declarationContext member_declaration(int i) {
			return getRuleContext(Member_declarationContext.class,i);
		}
		public Compilation_unitContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_compilation_unit; }
	}

	public final Compilation_unitContext compilation_unit() throws RecognitionException {
		Compilation_unitContext _localctx = new Compilation_unitContext(_ctx, getState());
		enterRule(_localctx, 0, RULE_compilation_unit);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(176);
			package_declaration();
			setState(180);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,0,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					{
					{
					setState(177);
					import_directive();
					}
					} 
				}
				setState(182);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,0,_ctx);
			}
			setState(186);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while ((((_la) & ~0x3f) == 0 && ((1L << _la) & 288230375914209792L) != 0) || ((((_la - 65)) & ~0x3f) == 0 && ((1L << (_la - 65)) & 288231065492914211L) != 0) || _la==MAT4X4) {
				{
				{
				setState(183);
				member_declaration();
				}
				}
				setState(188);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(189);
			match(EOF);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Package_declarationContext extends ParserRuleContext {
		public TerminalNode PACKAGE() { return getToken(RavenParser2.PACKAGE, 0); }
		public NameContext name() {
			return getRuleContext(NameContext.class,0);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Package_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_package_declaration; }
	}

	public final Package_declarationContext package_declaration() throws RecognitionException {
		Package_declarationContext _localctx = new Package_declarationContext(_ctx, getState());
		enterRule(_localctx, 2, RULE_package_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(191);
			match(PACKAGE);
			setState(192);
			name(0);
			setState(194); 
			_errHandler.sync(this);
			_la = _input.LA(1);
			do {
				{
				{
				setState(193);
				match(NL);
				}
				}
				setState(196); 
				_errHandler.sync(this);
				_la = _input.LA(1);
			} while ( _la==NL );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Import_directiveContext extends ParserRuleContext {
		public TerminalNode IMPORT() { return getToken(RavenParser2.IMPORT, 0); }
		public NameContext name() {
			return getRuleContext(NameContext.class,0);
		}
		public TerminalNode GLOBAL() { return getToken(RavenParser2.GLOBAL, 0); }
		public TerminalNode STATIC() { return getToken(RavenParser2.STATIC, 0); }
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Import_directiveContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_import_directive; }
	}

	public final Import_directiveContext import_directive() throws RecognitionException {
		Import_directiveContext _localctx = new Import_directiveContext(_ctx, getState());
		enterRule(_localctx, 4, RULE_import_directive);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(199);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==GLOBAL) {
				{
				setState(198);
				match(GLOBAL);
				}
			}

			setState(201);
			match(IMPORT);
			setState(203);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==STATIC) {
				{
				setState(202);
				match(STATIC);
				}
			}

			setState(205);
			name(0);
			setState(207); 
			_errHandler.sync(this);
			_la = _input.LA(1);
			do {
				{
				{
				setState(206);
				match(NL);
				}
				}
				setState(209); 
				_errHandler.sync(this);
				_la = _input.LA(1);
			} while ( _la==NL );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Attribute_listContext extends ParserRuleContext {
		public TerminalNode OPEN_BRACKET() { return getToken(RavenParser2.OPEN_BRACKET, 0); }
		public List<AttributeContext> attribute() {
			return getRuleContexts(AttributeContext.class);
		}
		public AttributeContext attribute(int i) {
			return getRuleContext(AttributeContext.class,i);
		}
		public TerminalNode CLOSE_BRACKET() { return getToken(RavenParser2.CLOSE_BRACKET, 0); }
		public Attribute_target_specifierContext attribute_target_specifier() {
			return getRuleContext(Attribute_target_specifierContext.class,0);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Attribute_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_attribute_list; }
	}

	public final Attribute_listContext attribute_list() throws RecognitionException {
		Attribute_listContext _localctx = new Attribute_listContext(_ctx, getState());
		enterRule(_localctx, 6, RULE_attribute_list);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(211);
			match(OPEN_BRACKET);
			setState(213);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,6,_ctx) ) {
			case 1:
				{
				setState(212);
				attribute_target_specifier();
				}
				break;
			}
			setState(215);
			attribute();
			setState(220);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==COMMA) {
				{
				{
				setState(216);
				match(COMMA);
				setState(217);
				attribute();
				}
				}
				setState(222);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(223);
			match(CLOSE_BRACKET);
			setState(225); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(224);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(227); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,8,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Attribute_target_specifierContext extends ParserRuleContext {
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Attribute_target_specifierContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_attribute_target_specifier; }
	}

	public final Attribute_target_specifierContext attribute_target_specifier() throws RecognitionException {
		Attribute_target_specifierContext _localctx = new Attribute_target_specifierContext(_ctx, getState());
		enterRule(_localctx, 8, RULE_attribute_target_specifier);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(235);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,11,_ctx) ) {
			case 1:
				{
				setState(230);
				_errHandler.sync(this);
				_la = _input.LA(1);
				if ((((_la) & ~0x3f) == 0 && ((1L << _la) & 288230375614841344L) != 0) || ((((_la - 94)) & ~0x3f) == 0 && ((1L << (_la - 94)) & 1027L) != 0) || _la==MAT4X4) {
					{
					setState(229);
					type(0);
					}
				}

				}
				break;
			case 2:
				{
				setState(233);
				_errHandler.sync(this);
				_la = _input.LA(1);
				if (_la==IDENTIFIER || _la==AT) {
					{
					setState(232);
					identifier_token();
					}
				}

				}
				break;
			}
			setState(237);
			match(COLON);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class AttributeContext extends ParserRuleContext {
		public NameContext name() {
			return getRuleContext(NameContext.class,0);
		}
		public Attribute_argument_listContext attribute_argument_list() {
			return getRuleContext(Attribute_argument_listContext.class,0);
		}
		public AttributeContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_attribute; }
	}

	public final AttributeContext attribute() throws RecognitionException {
		AttributeContext _localctx = new AttributeContext(_ctx, getState());
		enterRule(_localctx, 10, RULE_attribute);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(239);
			name(0);
			setState(241);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==OPEN_PARENS) {
				{
				setState(240);
				attribute_argument_list();
				}
			}

			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Attribute_argument_listContext extends ParserRuleContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<Attribute_argumentContext> attribute_argument() {
			return getRuleContexts(Attribute_argumentContext.class);
		}
		public Attribute_argumentContext attribute_argument(int i) {
			return getRuleContext(Attribute_argumentContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Attribute_argument_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_attribute_argument_list; }
	}

	public final Attribute_argument_listContext attribute_argument_list() throws RecognitionException {
		Attribute_argument_listContext _localctx = new Attribute_argument_listContext(_ctx, getState());
		enterRule(_localctx, 12, RULE_attribute_argument_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(243);
			match(OPEN_PARENS);
			setState(252);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908934413824L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7044052759682763329L) != 0) || _la==MAT4X4) {
				{
				setState(244);
				attribute_argument();
				setState(249);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==COMMA) {
					{
					{
					setState(245);
					match(COMMA);
					setState(246);
					attribute_argument();
					}
					}
					setState(251);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				}
			}

			setState(254);
			match(CLOSE_PARENS);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Attribute_argumentContext extends ParserRuleContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Name_colonContext name_colon() {
			return getRuleContext(Name_colonContext.class,0);
		}
		public Attribute_argumentContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_attribute_argument; }
	}

	public final Attribute_argumentContext attribute_argument() throws RecognitionException {
		Attribute_argumentContext _localctx = new Attribute_argumentContext(_ctx, getState());
		enterRule(_localctx, 14, RULE_attribute_argument);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(257);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,15,_ctx) ) {
			case 1:
				{
				setState(256);
				name_colon();
				}
				break;
			}
			setState(259);
			expression(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Parameter_listContext extends ParserRuleContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<ParameterContext> parameter() {
			return getRuleContexts(ParameterContext.class);
		}
		public ParameterContext parameter(int i) {
			return getRuleContext(ParameterContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Parameter_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_parameter_list; }
	}

	public final Parameter_listContext parameter_list() throws RecognitionException {
		Parameter_listContext _localctx = new Parameter_listContext(_ctx, getState());
		enterRule(_localctx, 16, RULE_parameter_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(261);
			match(OPEN_PARENS);
			setState(270);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 133119L) != 0)) {
				{
				setState(262);
				parameter();
				setState(267);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==COMMA) {
					{
					{
					setState(263);
					match(COMMA);
					setState(264);
					parameter();
					}
					}
					setState(269);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				}
			}

			setState(272);
			match(CLOSE_PARENS);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class ParameterContext extends ParserRuleContext {
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Equals_value_clauseContext equals_value_clause() {
			return getRuleContext(Equals_value_clauseContext.class,0);
		}
		public ParameterContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_parameter; }
	}

	public final ParameterContext parameter() throws RecognitionException {
		ParameterContext _localctx = new ParameterContext(_ctx, getState());
		enterRule(_localctx, 18, RULE_parameter);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(277);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(274);
				attribute_list();
				}
				}
				setState(279);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(283);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(280);
				modifier();
				}
				}
				setState(285);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(286);
			identifier_token();
			setState(289);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(287);
				match(COLON);
				setState(288);
				type(0);
				}
			}

			setState(292);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==ASSIGNMENT) {
				{
				setState(291);
				equals_value_clause();
				}
			}

			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class NameContext extends ParserRuleContext {
		public NameContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_name; }
	 
		public NameContext() { }
		public void copyFrom(NameContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class SimpleNameContext extends NameContext {
		public Simple_nameContext simple_name() {
			return getRuleContext(Simple_nameContext.class,0);
		}
		public SimpleNameContext(NameContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class QualifiedNameContext extends NameContext {
		public NameContext name() {
			return getRuleContext(NameContext.class,0);
		}
		public TerminalNode DOT() { return getToken(RavenParser2.DOT, 0); }
		public Simple_nameContext simple_name() {
			return getRuleContext(Simple_nameContext.class,0);
		}
		public QualifiedNameContext(NameContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class AliasQualifiedNameContext extends NameContext {
		public Identifier_nameContext identifier_name() {
			return getRuleContext(Identifier_nameContext.class,0);
		}
		public TerminalNode DOUBLE_COLON() { return getToken(RavenParser2.DOUBLE_COLON, 0); }
		public Simple_nameContext simple_name() {
			return getRuleContext(Simple_nameContext.class,0);
		}
		public AliasQualifiedNameContext(NameContext ctx) { copyFrom(ctx); }
	}

	public final NameContext name() throws RecognitionException {
		return name(0);
	}

	private NameContext name(int _p) throws RecognitionException {
		ParserRuleContext _parentctx = _ctx;
		int _parentState = getState();
		NameContext _localctx = new NameContext(_ctx, _parentState);
		NameContext _prevctx = _localctx;
		int _startState = 20;
		enterRecursionRule(_localctx, 20, RULE_name, _p);
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(300);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,22,_ctx) ) {
			case 1:
				{
				_localctx = new AliasQualifiedNameContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;

				setState(295);
				identifier_name();
				setState(296);
				match(DOUBLE_COLON);
				setState(297);
				simple_name();
				}
				break;
			case 2:
				{
				_localctx = new SimpleNameContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(299);
				simple_name();
				}
				break;
			}
			_ctx.stop = _input.LT(-1);
			setState(307);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,23,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					if ( _parseListeners!=null ) triggerExitRuleEvent();
					_prevctx = _localctx;
					{
					{
					_localctx = new QualifiedNameContext(new NameContext(_parentctx, _parentState));
					pushNewRecursionContext(_localctx, _startState, RULE_name);
					setState(302);
					if (!(precpred(_ctx, 2))) throw new FailedPredicateException(this, "precpred(_ctx, 2)");
					setState(303);
					match(DOT);
					setState(304);
					simple_name();
					}
					} 
				}
				setState(309);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,23,_ctx);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			unrollRecursionContexts(_parentctx);
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Simple_nameContext extends ParserRuleContext {
		public Generic_nameContext generic_name() {
			return getRuleContext(Generic_nameContext.class,0);
		}
		public Identifier_nameContext identifier_name() {
			return getRuleContext(Identifier_nameContext.class,0);
		}
		public Simple_nameContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_simple_name; }
	}

	public final Simple_nameContext simple_name() throws RecognitionException {
		Simple_nameContext _localctx = new Simple_nameContext(_ctx, getState());
		enterRule(_localctx, 22, RULE_simple_name);
		try {
			setState(312);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,24,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(310);
				generic_name();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(311);
				identifier_name();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Generic_nameContext extends ParserRuleContext {
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Type_argument_listContext type_argument_list() {
			return getRuleContext(Type_argument_listContext.class,0);
		}
		public Generic_nameContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_generic_name; }
	}

	public final Generic_nameContext generic_name() throws RecognitionException {
		Generic_nameContext _localctx = new Generic_nameContext(_ctx, getState());
		enterRule(_localctx, 24, RULE_generic_name);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(314);
			identifier_token();
			setState(315);
			type_argument_list();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Type_argument_listContext extends ParserRuleContext {
		public TerminalNode LT() { return getToken(RavenParser2.LT, 0); }
		public TerminalNode GT() { return getToken(RavenParser2.GT, 0); }
		public List<TypeContext> type() {
			return getRuleContexts(TypeContext.class);
		}
		public TypeContext type(int i) {
			return getRuleContext(TypeContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Type_argument_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type_argument_list; }
	}

	public final Type_argument_listContext type_argument_list() throws RecognitionException {
		Type_argument_listContext _localctx = new Type_argument_listContext(_ctx, getState());
		enterRule(_localctx, 26, RULE_type_argument_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(317);
			match(LT);
			setState(326);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if ((((_la) & ~0x3f) == 0 && ((1L << _la) & 288230375614841344L) != 0) || ((((_la - 94)) & ~0x3f) == 0 && ((1L << (_la - 94)) & 1027L) != 0) || _la==MAT4X4) {
				{
				setState(318);
				type(0);
				setState(323);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==COMMA) {
					{
					{
					setState(319);
					match(COMMA);
					setState(320);
					type(0);
					}
					}
					setState(325);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				}
			}

			setState(328);
			match(GT);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Name_colonContext extends ParserRuleContext {
		public Identifier_nameContext identifier_name() {
			return getRuleContext(Identifier_nameContext.class,0);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public Name_colonContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_name_colon; }
	}

	public final Name_colonContext name_colon() throws RecognitionException {
		Name_colonContext _localctx = new Name_colonContext(_ctx, getState());
		enterRule(_localctx, 28, RULE_name_colon);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(330);
			identifier_name();
			setState(331);
			match(COLON);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Identifier_nameContext extends ParserRuleContext {
		public TerminalNode GLOBAL() { return getToken(RavenParser2.GLOBAL, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Identifier_nameContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_identifier_name; }
	}

	public final Identifier_nameContext identifier_name() throws RecognitionException {
		Identifier_nameContext _localctx = new Identifier_nameContext(_ctx, getState());
		enterRule(_localctx, 30, RULE_identifier_name);
		try {
			setState(335);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case GLOBAL:
				enterOuterAlt(_localctx, 1);
				{
				setState(333);
				match(GLOBAL);
				}
				break;
			case IDENTIFIER:
			case AT:
				enterOuterAlt(_localctx, 2);
				{
				setState(334);
				identifier_token();
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Member_declarationContext extends ParserRuleContext {
		public Field_declarationContext field_declaration() {
			return getRuleContext(Field_declarationContext.class,0);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Base_method_declarationContext base_method_declaration() {
			return getRuleContext(Base_method_declarationContext.class,0);
		}
		public Base_property_declarationContext base_property_declaration() {
			return getRuleContext(Base_property_declarationContext.class,0);
		}
		public Base_type_declarationContext base_type_declaration() {
			return getRuleContext(Base_type_declarationContext.class,0);
		}
		public Member_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_member_declaration; }
	}

	public final Member_declarationContext member_declaration() throws RecognitionException {
		Member_declarationContext _localctx = new Member_declarationContext(_ctx, getState());
		enterRule(_localctx, 32, RULE_member_declaration);
		try {
			int _alt;
			setState(365);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,32,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(337);
				field_declaration();
				setState(341);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,28,_ctx);
				while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
					if ( _alt==1 ) {
						{
						{
						setState(338);
						match(NL);
						}
						} 
					}
					setState(343);
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,28,_ctx);
				}
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(344);
				base_method_declaration();
				setState(348);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,29,_ctx);
				while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
					if ( _alt==1 ) {
						{
						{
						setState(345);
						match(NL);
						}
						} 
					}
					setState(350);
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,29,_ctx);
				}
				}
				break;
			case 3:
				enterOuterAlt(_localctx, 3);
				{
				setState(351);
				base_property_declaration();
				setState(355);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,30,_ctx);
				while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
					if ( _alt==1 ) {
						{
						{
						setState(352);
						match(NL);
						}
						} 
					}
					setState(357);
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,30,_ctx);
				}
				}
				break;
			case 4:
				enterOuterAlt(_localctx, 4);
				{
				setState(358);
				base_type_declaration();
				setState(362);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,31,_ctx);
				while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
					if ( _alt==1 ) {
						{
						{
						setState(359);
						match(NL);
						}
						} 
					}
					setState(364);
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,31,_ctx);
				}
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Base_property_declarationContext extends ParserRuleContext {
		public Indexer_declarationContext indexer_declaration() {
			return getRuleContext(Indexer_declarationContext.class,0);
		}
		public Property_declarationContext property_declaration() {
			return getRuleContext(Property_declarationContext.class,0);
		}
		public Base_property_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_base_property_declaration; }
	}

	public final Base_property_declarationContext base_property_declaration() throws RecognitionException {
		Base_property_declarationContext _localctx = new Base_property_declarationContext(_ctx, getState());
		enterRule(_localctx, 34, RULE_base_property_declaration);
		try {
			setState(369);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,33,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(367);
				indexer_declaration();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(368);
				property_declaration();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Field_declarationContext extends ParserRuleContext {
		public Variable_declarationContext variable_declaration() {
			return getRuleContext(Variable_declarationContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Field_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_field_declaration; }
	}

	public final Field_declarationContext field_declaration() throws RecognitionException {
		Field_declarationContext _localctx = new Field_declarationContext(_ctx, getState());
		enterRule(_localctx, 36, RULE_field_declaration);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(374);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(371);
				attribute_list();
				}
				}
				setState(376);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(380);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(377);
				modifier();
				}
				}
				setState(382);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(383);
			variable_declaration();
			setState(385); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(384);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(387); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,36,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Base_method_declarationContext extends ParserRuleContext {
		public Constructor_declarationContext constructor_declaration() {
			return getRuleContext(Constructor_declarationContext.class,0);
		}
		public Conversion_operator_declarationContext conversion_operator_declaration() {
			return getRuleContext(Conversion_operator_declarationContext.class,0);
		}
		public Destructor_declarationContext destructor_declaration() {
			return getRuleContext(Destructor_declarationContext.class,0);
		}
		public Method_declarationContext method_declaration() {
			return getRuleContext(Method_declarationContext.class,0);
		}
		public Operator_declarationContext operator_declaration() {
			return getRuleContext(Operator_declarationContext.class,0);
		}
		public Base_method_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_base_method_declaration; }
	}

	public final Base_method_declarationContext base_method_declaration() throws RecognitionException {
		Base_method_declarationContext _localctx = new Base_method_declarationContext(_ctx, getState());
		enterRule(_localctx, 38, RULE_base_method_declaration);
		try {
			setState(394);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,37,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(389);
				constructor_declaration();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(390);
				conversion_operator_declaration();
				}
				break;
			case 3:
				enterOuterAlt(_localctx, 3);
				{
				setState(391);
				destructor_declaration();
				}
				break;
			case 4:
				enterOuterAlt(_localctx, 4);
				{
				setState(392);
				method_declaration();
				}
				break;
			case 5:
				enterOuterAlt(_localctx, 5);
				{
				setState(393);
				operator_declaration();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Constructor_declarationContext extends ParserRuleContext {
		public TerminalNode INIT() { return getToken(RavenParser2.INIT, 0); }
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Constructor_initializerContext constructor_initializer() {
			return getRuleContext(Constructor_initializerContext.class,0);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Constructor_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_constructor_declaration; }
	}

	public final Constructor_declarationContext constructor_declaration() throws RecognitionException {
		Constructor_declarationContext _localctx = new Constructor_declarationContext(_ctx, getState());
		enterRule(_localctx, 40, RULE_constructor_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(399);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(396);
				attribute_list();
				}
				}
				setState(401);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(405);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(402);
				modifier();
				}
				}
				setState(407);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(408);
			match(INIT);
			setState(409);
			parameter_list();
			setState(411);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(410);
				constructor_initializer();
				}
			}

			setState(417);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(413);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(414);
				arrow_expression_clause();
				setState(415);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Constructor_initializerContext extends ParserRuleContext {
		public Token init;
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public Argument_listContext argument_list() {
			return getRuleContext(Argument_listContext.class,0);
		}
		public TerminalNode BASE() { return getToken(RavenParser2.BASE, 0); }
		public TerminalNode SELF() { return getToken(RavenParser2.SELF, 0); }
		public Constructor_initializerContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_constructor_initializer; }
	}

	public final Constructor_initializerContext constructor_initializer() throws RecognitionException {
		Constructor_initializerContext _localctx = new Constructor_initializerContext(_ctx, getState());
		enterRule(_localctx, 42, RULE_constructor_initializer);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(419);
			match(COLON);
			setState(420);
			((Constructor_initializerContext)_localctx).init = _input.LT(1);
			_la = _input.LA(1);
			if ( !(_la==SELF || _la==BASE) ) {
				((Constructor_initializerContext)_localctx).init = (Token)_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			setState(421);
			argument_list();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Destructor_declarationContext extends ParserRuleContext {
		public TerminalNode TILDE() { return getToken(RavenParser2.TILDE, 0); }
		public TerminalNode INIT() { return getToken(RavenParser2.INIT, 0); }
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Destructor_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_destructor_declaration; }
	}

	public final Destructor_declarationContext destructor_declaration() throws RecognitionException {
		Destructor_declarationContext _localctx = new Destructor_declarationContext(_ctx, getState());
		enterRule(_localctx, 44, RULE_destructor_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(426);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(423);
				attribute_list();
				}
				}
				setState(428);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(432);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(429);
				modifier();
				}
				}
				setState(434);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(435);
			match(TILDE);
			setState(436);
			match(INIT);
			setState(437);
			parameter_list();
			setState(442);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(438);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(439);
				arrow_expression_clause();
				setState(440);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Method_declarationContext extends ParserRuleContext {
		public TerminalNode FUNC() { return getToken(RavenParser2.FUNC, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Explicit_interface_specifierContext explicit_interface_specifier() {
			return getRuleContext(Explicit_interface_specifierContext.class,0);
		}
		public Type_parameter_listContext type_parameter_list() {
			return getRuleContext(Type_parameter_listContext.class,0);
		}
		public List<Type_parameter_constraint_clauseContext> type_parameter_constraint_clause() {
			return getRuleContexts(Type_parameter_constraint_clauseContext.class);
		}
		public Type_parameter_constraint_clauseContext type_parameter_constraint_clause(int i) {
			return getRuleContext(Type_parameter_constraint_clauseContext.class,i);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Method_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_method_declaration; }
	}

	public final Method_declarationContext method_declaration() throws RecognitionException {
		Method_declarationContext _localctx = new Method_declarationContext(_ctx, getState());
		enterRule(_localctx, 46, RULE_method_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(447);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(444);
				attribute_list();
				}
				}
				setState(449);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(453);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(450);
				modifier();
				}
				}
				setState(455);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(456);
			match(FUNC);
			setState(458);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,47,_ctx) ) {
			case 1:
				{
				setState(457);
				explicit_interface_specifier();
				}
				break;
			}
			setState(460);
			identifier_token();
			setState(462);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==LT) {
				{
				setState(461);
				type_parameter_list();
				}
			}

			setState(464);
			parameter_list();
			setState(468);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==WHERE) {
				{
				{
				setState(465);
				type_parameter_constraint_clause();
				}
				}
				setState(470);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(473);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(471);
				match(COLON);
				setState(472);
				type(0);
				}
			}

			setState(479);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(475);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(476);
				arrow_expression_clause();
				setState(477);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Explicit_interface_specifierContext extends ParserRuleContext {
		public NameContext name() {
			return getRuleContext(NameContext.class,0);
		}
		public TerminalNode DOT() { return getToken(RavenParser2.DOT, 0); }
		public Explicit_interface_specifierContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_explicit_interface_specifier; }
	}

	public final Explicit_interface_specifierContext explicit_interface_specifier() throws RecognitionException {
		Explicit_interface_specifierContext _localctx = new Explicit_interface_specifierContext(_ctx, getState());
		enterRule(_localctx, 48, RULE_explicit_interface_specifier);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(481);
			name(0);
			setState(482);
			match(DOT);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Property_declarationContext extends ParserRuleContext {
		public TerminalNode VAR() { return getToken(RavenParser2.VAR, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Accessor_listContext accessor_list() {
			return getRuleContext(Accessor_listContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Explicit_interface_specifierContext explicit_interface_specifier() {
			return getRuleContext(Explicit_interface_specifierContext.class,0);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public Equals_value_clauseContext equals_value_clause() {
			return getRuleContext(Equals_value_clauseContext.class,0);
		}
		public Property_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_property_declaration; }
	}

	public final Property_declarationContext property_declaration() throws RecognitionException {
		Property_declarationContext _localctx = new Property_declarationContext(_ctx, getState());
		enterRule(_localctx, 50, RULE_property_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(487);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(484);
				attribute_list();
				}
				}
				setState(489);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(493);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(490);
				modifier();
				}
				}
				setState(495);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(496);
			match(VAR);
			setState(498);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,54,_ctx) ) {
			case 1:
				{
				setState(497);
				explicit_interface_specifier();
				}
				break;
			}
			setState(500);
			identifier_token();
			setState(503);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(501);
				match(COLON);
				setState(502);
				type(0);
				}
			}

			setState(512);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(505);
				accessor_list();
				}
				break;
			case LAMBDA:
			case ASSIGNMENT:
				{
				{
				setState(508);
				_errHandler.sync(this);
				switch (_input.LA(1)) {
				case LAMBDA:
					{
					setState(506);
					arrow_expression_clause();
					}
					break;
				case ASSIGNMENT:
					{
					setState(507);
					equals_value_clause();
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(510);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Accessor_listContext extends ParserRuleContext {
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<Accessor_declarationContext> accessor_declaration() {
			return getRuleContexts(Accessor_declarationContext.class);
		}
		public Accessor_declarationContext accessor_declaration(int i) {
			return getRuleContext(Accessor_declarationContext.class,i);
		}
		public Accessor_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_accessor_list; }
	}

	public final Accessor_listContext accessor_list() throws RecognitionException {
		Accessor_listContext _localctx = new Accessor_listContext(_ctx, getState());
		enterRule(_localctx, 52, RULE_accessor_list);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(514);
			match(OPEN_BRACE);
			setState(518);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,58,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					{
					{
					setState(515);
					match(NL);
					}
					} 
				}
				setState(520);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,58,_ctx);
			}
			setState(524);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while ((((_la) & ~0x3f) == 0 && ((1L << _la) & 245760L) != 0) || ((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 131583L) != 0)) {
				{
				{
				setState(521);
				accessor_declaration();
				}
				}
				setState(526);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(530);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==NL) {
				{
				{
				setState(527);
				match(NL);
				}
				}
				setState(532);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(533);
			match(CLOSE_BRACE);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Accessor_declarationContext extends ParserRuleContext {
		public Token op;
		public TerminalNode GET() { return getToken(RavenParser2.GET, 0); }
		public TerminalNode SET() { return getToken(RavenParser2.SET, 0); }
		public TerminalNode WILL_SET() { return getToken(RavenParser2.WILL_SET, 0); }
		public TerminalNode DID_SET() { return getToken(RavenParser2.DID_SET, 0); }
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public Accessor_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_accessor_declaration; }
	}

	public final Accessor_declarationContext accessor_declaration() throws RecognitionException {
		Accessor_declarationContext _localctx = new Accessor_declarationContext(_ctx, getState());
		enterRule(_localctx, 54, RULE_accessor_declaration);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(538);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(535);
				attribute_list();
				}
				}
				setState(540);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(544);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(541);
				modifier();
				}
				}
				setState(546);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(547);
			((Accessor_declarationContext)_localctx).op = _input.LT(1);
			_la = _input.LA(1);
			if ( !((((_la) & ~0x3f) == 0 && ((1L << _la) & 245760L) != 0)) ) {
				((Accessor_declarationContext)_localctx).op = (Token)_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			setState(552);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(548);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(549);
				arrow_expression_clause();
				setState(550);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			setState(557);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,64,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					{
					{
					setState(554);
					match(NL);
					}
					} 
				}
				setState(559);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,64,_ctx);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Indexer_declarationContext extends ParserRuleContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TerminalNode SELF() { return getToken(RavenParser2.SELF, 0); }
		public Bracketed_parameter_listContext bracketed_parameter_list() {
			return getRuleContext(Bracketed_parameter_listContext.class,0);
		}
		public Accessor_listContext accessor_list() {
			return getRuleContext(Accessor_listContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Explicit_interface_specifierContext explicit_interface_specifier() {
			return getRuleContext(Explicit_interface_specifierContext.class,0);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Indexer_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_indexer_declaration; }
	}

	public final Indexer_declarationContext indexer_declaration() throws RecognitionException {
		Indexer_declarationContext _localctx = new Indexer_declarationContext(_ctx, getState());
		enterRule(_localctx, 56, RULE_indexer_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(563);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(560);
				attribute_list();
				}
				}
				setState(565);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(569);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(566);
				modifier();
				}
				}
				setState(571);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(572);
			type(0);
			setState(574);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==GLOBAL || _la==IDENTIFIER || _la==AT) {
				{
				setState(573);
				explicit_interface_specifier();
				}
			}

			setState(576);
			match(SELF);
			setState(577);
			bracketed_parameter_list();
			setState(582);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(578);
				accessor_list();
				}
				break;
			case LAMBDA:
				{
				{
				setState(579);
				arrow_expression_clause();
				setState(580);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Bracketed_parameter_listContext extends ParserRuleContext {
		public TerminalNode OPEN_BRACKET() { return getToken(RavenParser2.OPEN_BRACKET, 0); }
		public List<ParameterContext> parameter() {
			return getRuleContexts(ParameterContext.class);
		}
		public ParameterContext parameter(int i) {
			return getRuleContext(ParameterContext.class,i);
		}
		public TerminalNode CLOSE_BRACKET() { return getToken(RavenParser2.CLOSE_BRACKET, 0); }
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Bracketed_parameter_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_bracketed_parameter_list; }
	}

	public final Bracketed_parameter_listContext bracketed_parameter_list() throws RecognitionException {
		Bracketed_parameter_listContext _localctx = new Bracketed_parameter_listContext(_ctx, getState());
		enterRule(_localctx, 58, RULE_bracketed_parameter_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(584);
			match(OPEN_BRACKET);
			setState(585);
			parameter();
			setState(590);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==COMMA) {
				{
				{
				setState(586);
				match(COMMA);
				setState(587);
				parameter();
				}
				}
				setState(592);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(593);
			match(CLOSE_BRACKET);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Conversion_operator_declarationContext extends ParserRuleContext {
		public Token ct;
		public TerminalNode OPERATOR() { return getToken(RavenParser2.OPERATOR, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public TerminalNode IMPLICIT() { return getToken(RavenParser2.IMPLICIT, 0); }
		public TerminalNode EXPLICIT() { return getToken(RavenParser2.EXPLICIT, 0); }
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Explicit_interface_specifierContext explicit_interface_specifier() {
			return getRuleContext(Explicit_interface_specifierContext.class,0);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Conversion_operator_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_conversion_operator_declaration; }
	}

	public final Conversion_operator_declarationContext conversion_operator_declaration() throws RecognitionException {
		Conversion_operator_declarationContext _localctx = new Conversion_operator_declarationContext(_ctx, getState());
		enterRule(_localctx, 60, RULE_conversion_operator_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(598);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(595);
				attribute_list();
				}
				}
				setState(600);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(604);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(601);
				modifier();
				}
				}
				setState(606);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(607);
			((Conversion_operator_declarationContext)_localctx).ct = _input.LT(1);
			_la = _input.LA(1);
			if ( !(_la==EXPLICIT || _la==IMPLICIT) ) {
				((Conversion_operator_declarationContext)_localctx).ct = (Token)_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			setState(609);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==GLOBAL || _la==IDENTIFIER || _la==AT) {
				{
				setState(608);
				explicit_interface_specifier();
				}
			}

			setState(611);
			match(OPERATOR);
			setState(612);
			type(0);
			setState(613);
			parameter_list();
			setState(618);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(614);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(615);
				arrow_expression_clause();
				setState(616);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Operator_declarationContext extends ParserRuleContext {
		public Token op;
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TerminalNode OPERATOR() { return getToken(RavenParser2.OPERATOR, 0); }
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public TerminalNode PLUS() { return getToken(RavenParser2.PLUS, 0); }
		public TerminalNode MINUS() { return getToken(RavenParser2.MINUS, 0); }
		public TerminalNode BANG() { return getToken(RavenParser2.BANG, 0); }
		public TerminalNode TILDE() { return getToken(RavenParser2.TILDE, 0); }
		public TerminalNode OP_INC() { return getToken(RavenParser2.OP_INC, 0); }
		public TerminalNode OP_DEC() { return getToken(RavenParser2.OP_DEC, 0); }
		public TerminalNode STAR() { return getToken(RavenParser2.STAR, 0); }
		public TerminalNode DIV() { return getToken(RavenParser2.DIV, 0); }
		public TerminalNode PERCENT() { return getToken(RavenParser2.PERCENT, 0); }
		public TerminalNode OP_LEFT_SHIFT() { return getToken(RavenParser2.OP_LEFT_SHIFT, 0); }
		public TerminalNode OP_RIGHT_SHIFT() { return getToken(RavenParser2.OP_RIGHT_SHIFT, 0); }
		public TerminalNode OP_UNSIGNED_RIGHT_SHIFT() { return getToken(RavenParser2.OP_UNSIGNED_RIGHT_SHIFT, 0); }
		public TerminalNode BITWISE_OR() { return getToken(RavenParser2.BITWISE_OR, 0); }
		public TerminalNode AMP() { return getToken(RavenParser2.AMP, 0); }
		public TerminalNode CARET() { return getToken(RavenParser2.CARET, 0); }
		public TerminalNode OP_EQ() { return getToken(RavenParser2.OP_EQ, 0); }
		public TerminalNode OP_NE() { return getToken(RavenParser2.OP_NE, 0); }
		public TerminalNode LT() { return getToken(RavenParser2.LT, 0); }
		public TerminalNode OP_LE() { return getToken(RavenParser2.OP_LE, 0); }
		public TerminalNode GT() { return getToken(RavenParser2.GT, 0); }
		public TerminalNode OP_GE() { return getToken(RavenParser2.OP_GE, 0); }
		public TerminalNode FALSE() { return getToken(RavenParser2.FALSE, 0); }
		public TerminalNode TRUE() { return getToken(RavenParser2.TRUE, 0); }
		public TerminalNode IS() { return getToken(RavenParser2.IS, 0); }
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Explicit_interface_specifierContext explicit_interface_specifier() {
			return getRuleContext(Explicit_interface_specifierContext.class,0);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Operator_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_operator_declaration; }
	}

	public final Operator_declarationContext operator_declaration() throws RecognitionException {
		Operator_declarationContext _localctx = new Operator_declarationContext(_ctx, getState());
		enterRule(_localctx, 62, RULE_operator_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(623);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(620);
				attribute_list();
				}
				}
				setState(625);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(629);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(626);
				modifier();
				}
				}
				setState(631);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(632);
			type(0);
			setState(634);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==GLOBAL || _la==IDENTIFIER || _la==AT) {
				{
				setState(633);
				explicit_interface_specifier();
				}
			}

			setState(636);
			match(OPERATOR);
			setState(637);
			((Operator_declarationContext)_localctx).op = _input.LT(1);
			_la = _input.LA(1);
			if ( !(((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7926194606683734049L) != 0) || ((((_la - 132)) & ~0x3f) == 0 && ((1L << (_la - 132)) & 167951L) != 0)) ) {
				((Operator_declarationContext)_localctx).op = (Token)_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			setState(638);
			parameter_list();
			setState(643);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(639);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(640);
				arrow_expression_clause();
				setState(641);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Base_type_declarationContext extends ParserRuleContext {
		public Enum_declarationContext enum_declaration() {
			return getRuleContext(Enum_declarationContext.class,0);
		}
		public Type_declarationContext type_declaration() {
			return getRuleContext(Type_declarationContext.class,0);
		}
		public Base_type_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_base_type_declaration; }
	}

	public final Base_type_declarationContext base_type_declaration() throws RecognitionException {
		Base_type_declarationContext _localctx = new Base_type_declarationContext(_ctx, getState());
		enterRule(_localctx, 64, RULE_base_type_declaration);
		try {
			setState(647);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,78,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(645);
				enum_declaration();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(646);
				type_declaration();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Type_declarationContext extends ParserRuleContext {
		public Shader_declarationContext shader_declaration() {
			return getRuleContext(Shader_declarationContext.class,0);
		}
		public Protocol_declarationContext protocol_declaration() {
			return getRuleContext(Protocol_declarationContext.class,0);
		}
		public Type_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type_declaration; }
	}

	public final Type_declarationContext type_declaration() throws RecognitionException {
		Type_declarationContext _localctx = new Type_declarationContext(_ctx, getState());
		enterRule(_localctx, 66, RULE_type_declaration);
		try {
			setState(651);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,79,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(649);
				shader_declaration();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(650);
				protocol_declaration();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Shader_declarationContext extends ParserRuleContext {
		public TerminalNode SHADER() { return getToken(RavenParser2.SHADER, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Type_parameter_listContext type_parameter_list() {
			return getRuleContext(Type_parameter_listContext.class,0);
		}
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public Base_listContext base_list() {
			return getRuleContext(Base_listContext.class,0);
		}
		public List<Type_parameter_constraint_clauseContext> type_parameter_constraint_clause() {
			return getRuleContexts(Type_parameter_constraint_clauseContext.class);
		}
		public Type_parameter_constraint_clauseContext type_parameter_constraint_clause(int i) {
			return getRuleContext(Type_parameter_constraint_clauseContext.class,i);
		}
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<Member_declarationContext> member_declaration() {
			return getRuleContexts(Member_declarationContext.class);
		}
		public Member_declarationContext member_declaration(int i) {
			return getRuleContext(Member_declarationContext.class,i);
		}
		public Shader_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_shader_declaration; }
	}

	public final Shader_declarationContext shader_declaration() throws RecognitionException {
		Shader_declarationContext _localctx = new Shader_declarationContext(_ctx, getState());
		enterRule(_localctx, 68, RULE_shader_declaration);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(656);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(653);
				attribute_list();
				}
				}
				setState(658);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(662);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(659);
				modifier();
				}
				}
				setState(664);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(665);
			match(SHADER);
			setState(666);
			identifier_token();
			setState(668);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==LT) {
				{
				setState(667);
				type_parameter_list();
				}
			}

			setState(671);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==OPEN_PARENS) {
				{
				setState(670);
				parameter_list();
				}
			}

			setState(674);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(673);
				base_list();
				}
			}

			setState(679);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==WHERE) {
				{
				{
				setState(676);
				type_parameter_constraint_clause();
				}
				}
				setState(681);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(700);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==OPEN_BRACE) {
				{
				setState(682);
				match(OPEN_BRACE);
				setState(684); 
				_errHandler.sync(this);
				_alt = 1;
				do {
					switch (_alt) {
					case 1:
						{
						{
						setState(683);
						match(NL);
						}
						}
						break;
					default:
						throw new NoViableAltException(this);
					}
					setState(686); 
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,86,_ctx);
				} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
				setState(691);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while ((((_la) & ~0x3f) == 0 && ((1L << _la) & 288230375914209792L) != 0) || ((((_la - 65)) & ~0x3f) == 0 && ((1L << (_la - 65)) & 288231065492914211L) != 0) || _la==MAT4X4) {
					{
					{
					setState(688);
					member_declaration();
					}
					}
					setState(693);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				setState(695); 
				_errHandler.sync(this);
				_la = _input.LA(1);
				do {
					{
					{
					setState(694);
					match(NL);
					}
					}
					setState(697); 
					_errHandler.sync(this);
					_la = _input.LA(1);
				} while ( _la==NL );
				setState(699);
				match(CLOSE_BRACE);
				}
			}

			setState(702);
			match(NL);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Protocol_declarationContext extends ParserRuleContext {
		public TerminalNode PROTOCOL() { return getToken(RavenParser2.PROTOCOL, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Type_parameter_listContext type_parameter_list() {
			return getRuleContext(Type_parameter_listContext.class,0);
		}
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public Base_listContext base_list() {
			return getRuleContext(Base_listContext.class,0);
		}
		public List<Type_parameter_constraint_clauseContext> type_parameter_constraint_clause() {
			return getRuleContexts(Type_parameter_constraint_clauseContext.class);
		}
		public Type_parameter_constraint_clauseContext type_parameter_constraint_clause(int i) {
			return getRuleContext(Type_parameter_constraint_clauseContext.class,i);
		}
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<Member_declarationContext> member_declaration() {
			return getRuleContexts(Member_declarationContext.class);
		}
		public Member_declarationContext member_declaration(int i) {
			return getRuleContext(Member_declarationContext.class,i);
		}
		public Protocol_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_protocol_declaration; }
	}

	public final Protocol_declarationContext protocol_declaration() throws RecognitionException {
		Protocol_declarationContext _localctx = new Protocol_declarationContext(_ctx, getState());
		enterRule(_localctx, 70, RULE_protocol_declaration);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(707);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(704);
				attribute_list();
				}
				}
				setState(709);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(713);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(710);
				modifier();
				}
				}
				setState(715);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(716);
			match(PROTOCOL);
			setState(717);
			identifier_token();
			setState(719);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==LT) {
				{
				setState(718);
				type_parameter_list();
				}
			}

			setState(722);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==OPEN_PARENS) {
				{
				setState(721);
				parameter_list();
				}
			}

			setState(725);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(724);
				base_list();
				}
			}

			setState(730);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==WHERE) {
				{
				{
				setState(727);
				type_parameter_constraint_clause();
				}
				}
				setState(732);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(751);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==OPEN_BRACE) {
				{
				setState(733);
				match(OPEN_BRACE);
				setState(735); 
				_errHandler.sync(this);
				_alt = 1;
				do {
					switch (_alt) {
					case 1:
						{
						{
						setState(734);
						match(NL);
						}
						}
						break;
					default:
						throw new NoViableAltException(this);
					}
					setState(737); 
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,96,_ctx);
				} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
				setState(742);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while ((((_la) & ~0x3f) == 0 && ((1L << _la) & 288230375914209792L) != 0) || ((((_la - 65)) & ~0x3f) == 0 && ((1L << (_la - 65)) & 288231065492914211L) != 0) || _la==MAT4X4) {
					{
					{
					setState(739);
					member_declaration();
					}
					}
					setState(744);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				setState(746); 
				_errHandler.sync(this);
				_la = _input.LA(1);
				do {
					{
					{
					setState(745);
					match(NL);
					}
					}
					setState(748); 
					_errHandler.sync(this);
					_la = _input.LA(1);
				} while ( _la==NL );
				setState(750);
				match(CLOSE_BRACE);
				}
			}

			setState(753);
			match(NL);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Enum_declarationContext extends ParserRuleContext {
		public TerminalNode ENUM() { return getToken(RavenParser2.ENUM, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Base_listContext base_list() {
			return getRuleContext(Base_listContext.class,0);
		}
		public List<Enum_member_declarationContext> enum_member_declaration() {
			return getRuleContexts(Enum_member_declarationContext.class);
		}
		public Enum_member_declarationContext enum_member_declaration(int i) {
			return getRuleContext(Enum_member_declarationContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Enum_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_enum_declaration; }
	}

	public final Enum_declarationContext enum_declaration() throws RecognitionException {
		Enum_declarationContext _localctx = new Enum_declarationContext(_ctx, getState());
		enterRule(_localctx, 72, RULE_enum_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(758);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(755);
				attribute_list();
				}
				}
				setState(760);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(764);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(761);
				modifier();
				}
				}
				setState(766);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(767);
			match(ENUM);
			setState(768);
			identifier_token();
			setState(770);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(769);
				base_list();
				}
			}

			setState(772);
			match(OPEN_BRACE);
			setState(774); 
			_errHandler.sync(this);
			_la = _input.LA(1);
			do {
				{
				{
				setState(773);
				match(NL);
				}
				}
				setState(776); 
				_errHandler.sync(this);
				_la = _input.LA(1);
			} while ( _la==NL );
			setState(786);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 133119L) != 0)) {
				{
				setState(778);
				enum_member_declaration();
				setState(783);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==COMMA) {
					{
					{
					setState(779);
					match(COMMA);
					setState(780);
					enum_member_declaration();
					}
					}
					setState(785);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				}
			}

			setState(788);
			match(CLOSE_BRACE);
			setState(789);
			match(NL);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Enum_member_declarationContext extends ParserRuleContext {
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Equals_value_clauseContext equals_value_clause() {
			return getRuleContext(Equals_value_clauseContext.class,0);
		}
		public Enum_member_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_enum_member_declaration; }
	}

	public final Enum_member_declarationContext enum_member_declaration() throws RecognitionException {
		Enum_member_declarationContext _localctx = new Enum_member_declarationContext(_ctx, getState());
		enterRule(_localctx, 74, RULE_enum_member_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(794);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(791);
				attribute_list();
				}
				}
				setState(796);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(800);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(797);
				modifier();
				}
				}
				setState(802);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(803);
			identifier_token();
			setState(805);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==ASSIGNMENT) {
				{
				setState(804);
				equals_value_clause();
				}
			}

			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Type_parameter_listContext extends ParserRuleContext {
		public TerminalNode LT() { return getToken(RavenParser2.LT, 0); }
		public List<Type_parameterContext> type_parameter() {
			return getRuleContexts(Type_parameterContext.class);
		}
		public Type_parameterContext type_parameter(int i) {
			return getRuleContext(Type_parameterContext.class,i);
		}
		public TerminalNode GT() { return getToken(RavenParser2.GT, 0); }
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Type_parameter_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type_parameter_list; }
	}

	public final Type_parameter_listContext type_parameter_list() throws RecognitionException {
		Type_parameter_listContext _localctx = new Type_parameter_listContext(_ctx, getState());
		enterRule(_localctx, 76, RULE_type_parameter_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(807);
			match(LT);
			setState(808);
			type_parameter();
			setState(813);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==COMMA) {
				{
				{
				setState(809);
				match(COMMA);
				setState(810);
				type_parameter();
				}
				}
				setState(815);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(816);
			match(GT);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Type_parameterContext extends ParserRuleContext {
		public Token variance;
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public TerminalNode IN() { return getToken(RavenParser2.IN, 0); }
		public TerminalNode OUT() { return getToken(RavenParser2.OUT, 0); }
		public Type_parameterContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type_parameter; }
	}

	public final Type_parameterContext type_parameter() throws RecognitionException {
		Type_parameterContext _localctx = new Type_parameterContext(_ctx, getState());
		enterRule(_localctx, 78, RULE_type_parameter);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(821);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(818);
				attribute_list();
				}
				}
				setState(823);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(825);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==IN || _la==OUT) {
				{
				setState(824);
				((Type_parameterContext)_localctx).variance = _input.LT(1);
				_la = _input.LA(1);
				if ( !(_la==IN || _la==OUT) ) {
					((Type_parameterContext)_localctx).variance = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				}
			}

			setState(827);
			identifier_token();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Type_parameter_constraint_clauseContext extends ParserRuleContext {
		public TerminalNode WHERE() { return getToken(RavenParser2.WHERE, 0); }
		public Identifier_nameContext identifier_name() {
			return getRuleContext(Identifier_nameContext.class,0);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public List<Type_parameter_constraintContext> type_parameter_constraint() {
			return getRuleContexts(Type_parameter_constraintContext.class);
		}
		public Type_parameter_constraintContext type_parameter_constraint(int i) {
			return getRuleContext(Type_parameter_constraintContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Type_parameter_constraint_clauseContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type_parameter_constraint_clause; }
	}

	public final Type_parameter_constraint_clauseContext type_parameter_constraint_clause() throws RecognitionException {
		Type_parameter_constraint_clauseContext _localctx = new Type_parameter_constraint_clauseContext(_ctx, getState());
		enterRule(_localctx, 80, RULE_type_parameter_constraint_clause);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(829);
			match(WHERE);
			setState(830);
			identifier_name();
			setState(831);
			match(COLON);
			setState(832);
			type_parameter_constraint();
			setState(837);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==COMMA) {
				{
				{
				setState(833);
				match(COMMA);
				setState(834);
				type_parameter_constraint();
				}
				}
				setState(839);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Type_parameter_constraintContext extends ParserRuleContext {
		public Type_parameter_constraintContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type_parameter_constraint; }
	 
		public Type_parameter_constraintContext() { }
		public void copyFrom(Type_parameter_constraintContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class DefaultConstraintContext extends Type_parameter_constraintContext {
		public TerminalNode DEFAULT() { return getToken(RavenParser2.DEFAULT, 0); }
		public DefaultConstraintContext(Type_parameter_constraintContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class TypeContraintContext extends Type_parameter_constraintContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TypeContraintContext(Type_parameter_constraintContext ctx) { copyFrom(ctx); }
	}

	public final Type_parameter_constraintContext type_parameter_constraint() throws RecognitionException {
		Type_parameter_constraintContext _localctx = new Type_parameter_constraintContext(_ctx, getState());
		enterRule(_localctx, 82, RULE_type_parameter_constraint);
		try {
			setState(842);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case DEFAULT:
				_localctx = new DefaultConstraintContext(_localctx);
				enterOuterAlt(_localctx, 1);
				{
				setState(840);
				match(DEFAULT);
				}
				break;
			case GLOBAL:
			case BOOL:
			case BOOL2:
			case BOOL3:
			case BOOL4:
			case INT:
			case INT2:
			case INT3:
			case INT4:
			case UINT:
			case UINT2:
			case UINT3:
			case UINT4:
			case FLOAT:
			case FLOAT2:
			case FLOAT3:
			case FLOAT4:
			case DOUBLE:
			case DOUBLE2:
			case DOUBLE3:
			case DOUBLE4:
			case MAT2:
			case MAT2X3:
			case MAT2X4:
			case MAT3:
			case MAT3X2:
			case MAT3X4:
			case MAT4:
			case MAT4X2:
			case MAT4X3:
			case IDENTIFIER:
			case AT:
			case OPEN_PARENS:
			case MAT4X4:
				_localctx = new TypeContraintContext(_localctx);
				enterOuterAlt(_localctx, 2);
				{
				setState(841);
				type(0);
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Base_listContext extends ParserRuleContext {
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public List<Base_typeContext> base_type() {
			return getRuleContexts(Base_typeContext.class);
		}
		public Base_typeContext base_type(int i) {
			return getRuleContext(Base_typeContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Base_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_base_list; }
	}

	public final Base_listContext base_list() throws RecognitionException {
		Base_listContext _localctx = new Base_listContext(_ctx, getState());
		enterRule(_localctx, 84, RULE_base_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(844);
			match(COLON);
			setState(845);
			base_type();
			setState(850);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==COMMA) {
				{
				{
				setState(846);
				match(COMMA);
				setState(847);
				base_type();
				}
				}
				setState(852);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Base_typeContext extends ParserRuleContext {
		public Primary_constructor_base_typeContext primary_constructor_base_type() {
			return getRuleContext(Primary_constructor_base_typeContext.class,0);
		}
		public Simple_base_typeContext simple_base_type() {
			return getRuleContext(Simple_base_typeContext.class,0);
		}
		public Base_typeContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_base_type; }
	}

	public final Base_typeContext base_type() throws RecognitionException {
		Base_typeContext _localctx = new Base_typeContext(_ctx, getState());
		enterRule(_localctx, 86, RULE_base_type);
		try {
			setState(855);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,115,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(853);
				primary_constructor_base_type();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(854);
				simple_base_type();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Primary_constructor_base_typeContext extends ParserRuleContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Argument_listContext argument_list() {
			return getRuleContext(Argument_listContext.class,0);
		}
		public Primary_constructor_base_typeContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_primary_constructor_base_type; }
	}

	public final Primary_constructor_base_typeContext primary_constructor_base_type() throws RecognitionException {
		Primary_constructor_base_typeContext _localctx = new Primary_constructor_base_typeContext(_ctx, getState());
		enterRule(_localctx, 88, RULE_primary_constructor_base_type);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(857);
			type(0);
			setState(858);
			argument_list();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Simple_base_typeContext extends ParserRuleContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Simple_base_typeContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_simple_base_type; }
	}

	public final Simple_base_typeContext simple_base_type() throws RecognitionException {
		Simple_base_typeContext _localctx = new Simple_base_typeContext(_ctx, getState());
		enterRule(_localctx, 90, RULE_simple_base_type);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(860);
			type(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Variable_declarationContext extends ParserRuleContext {
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public TerminalNode VAR() { return getToken(RavenParser2.VAR, 0); }
		public TerminalNode VAL() { return getToken(RavenParser2.VAL, 0); }
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Equals_value_clauseContext equals_value_clause() {
			return getRuleContext(Equals_value_clauseContext.class,0);
		}
		public Variable_declarationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_variable_declaration; }
	}

	public final Variable_declarationContext variable_declaration() throws RecognitionException {
		Variable_declarationContext _localctx = new Variable_declarationContext(_ctx, getState());
		enterRule(_localctx, 92, RULE_variable_declaration);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(862);
			_la = _input.LA(1);
			if ( !(_la==VAR || _la==VAL) ) {
			_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			setState(863);
			identifier_token();
			setState(866);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(864);
				match(COLON);
				setState(865);
				type(0);
				}
			}

			setState(869);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==ASSIGNMENT) {
				{
				setState(868);
				equals_value_clause();
				}
			}

			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Argument_listContext extends ParserRuleContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<ArgumentContext> argument() {
			return getRuleContexts(ArgumentContext.class);
		}
		public ArgumentContext argument(int i) {
			return getRuleContext(ArgumentContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Argument_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_argument_list; }
	}

	public final Argument_listContext argument_list() throws RecognitionException {
		Argument_listContext _localctx = new Argument_listContext(_ctx, getState());
		enterRule(_localctx, 94, RULE_argument_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(871);
			match(OPEN_PARENS);
			setState(880);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908934413824L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7044052759682763601L) != 0) || _la==MAT4X4) {
				{
				setState(872);
				argument();
				setState(877);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==COMMA) {
					{
					{
					setState(873);
					match(COMMA);
					setState(874);
					argument();
					}
					}
					setState(879);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				}
			}

			setState(882);
			match(CLOSE_PARENS);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class ArgumentContext extends ParserRuleContext {
		public Token kind;
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Name_colonContext name_colon() {
			return getRuleContext(Name_colonContext.class,0);
		}
		public TerminalNode REF() { return getToken(RavenParser2.REF, 0); }
		public TerminalNode OUT() { return getToken(RavenParser2.OUT, 0); }
		public TerminalNode IN() { return getToken(RavenParser2.IN, 0); }
		public ArgumentContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_argument; }
	}

	public final ArgumentContext argument() throws RecognitionException {
		ArgumentContext _localctx = new ArgumentContext(_ctx, getState());
		enterRule(_localctx, 96, RULE_argument);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(885);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,120,_ctx) ) {
			case 1:
				{
				setState(884);
				name_colon();
				}
				break;
			}
			setState(888);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,121,_ctx) ) {
			case 1:
				{
				setState(887);
				((ArgumentContext)_localctx).kind = _input.LT(1);
				_la = _input.LA(1);
				if ( !(((((_la - 71)) & ~0x3f) == 0 && ((1L << (_la - 71)) & 49L) != 0)) ) {
					((ArgumentContext)_localctx).kind = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				}
				break;
			}
			setState(890);
			expression(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Bracketed_argument_listContext extends ParserRuleContext {
		public TerminalNode OPEN_BRACKET() { return getToken(RavenParser2.OPEN_BRACKET, 0); }
		public List<ArgumentContext> argument() {
			return getRuleContexts(ArgumentContext.class);
		}
		public ArgumentContext argument(int i) {
			return getRuleContext(ArgumentContext.class,i);
		}
		public TerminalNode CLOSE_BRACKET() { return getToken(RavenParser2.CLOSE_BRACKET, 0); }
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Bracketed_argument_listContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_bracketed_argument_list; }
	}

	public final Bracketed_argument_listContext bracketed_argument_list() throws RecognitionException {
		Bracketed_argument_listContext _localctx = new Bracketed_argument_listContext(_ctx, getState());
		enterRule(_localctx, 98, RULE_bracketed_argument_list);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(892);
			match(OPEN_BRACKET);
			setState(893);
			argument();
			setState(898);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==COMMA) {
				{
				{
				setState(894);
				match(COMMA);
				setState(895);
				argument();
				}
				}
				setState(900);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(901);
			match(CLOSE_BRACKET);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class BlockContext extends ParserRuleContext {
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<StatementContext> statement() {
			return getRuleContexts(StatementContext.class);
		}
		public StatementContext statement(int i) {
			return getRuleContext(StatementContext.class,i);
		}
		public BlockContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_block; }
	}

	public final BlockContext block() throws RecognitionException {
		BlockContext _localctx = new BlockContext(_ctx, getState());
		enterRule(_localctx, 100, RULE_block);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(903);
			match(OPEN_BRACE);
			setState(907);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,123,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					{
					{
					setState(904);
					match(NL);
					}
					} 
				}
				setState(909);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,123,_ctx);
			}
			setState(913);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,124,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					{
					{
					setState(910);
					statement();
					}
					} 
				}
				setState(915);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,124,_ctx);
			}
			setState(919);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==NL) {
				{
				{
				setState(916);
				match(NL);
				}
				}
				setState(921);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(922);
			match(CLOSE_BRACE);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class StatementContext extends ParserRuleContext {
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public Break_statementContext break_statement() {
			return getRuleContext(Break_statementContext.class,0);
		}
		public Continue_statementContext continue_statement() {
			return getRuleContext(Continue_statementContext.class,0);
		}
		public Repeat_statementContext repeat_statement() {
			return getRuleContext(Repeat_statementContext.class,0);
		}
		public Empty_statementContext empty_statement() {
			return getRuleContext(Empty_statementContext.class,0);
		}
		public Expression_statementContext expression_statement() {
			return getRuleContext(Expression_statementContext.class,0);
		}
		public For_statementContext for_statement() {
			return getRuleContext(For_statementContext.class,0);
		}
		public If_statementContext if_statement() {
			return getRuleContext(If_statementContext.class,0);
		}
		public Local_declaration_statementContext local_declaration_statement() {
			return getRuleContext(Local_declaration_statementContext.class,0);
		}
		public Local_function_statementContext local_function_statement() {
			return getRuleContext(Local_function_statementContext.class,0);
		}
		public Return_statementContext return_statement() {
			return getRuleContext(Return_statementContext.class,0);
		}
		public Switch_statementContext switch_statement() {
			return getRuleContext(Switch_statementContext.class,0);
		}
		public Using_statementContext using_statement() {
			return getRuleContext(Using_statementContext.class,0);
		}
		public While_statementContext while_statement() {
			return getRuleContext(While_statementContext.class,0);
		}
		public StatementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_statement; }
	}

	public final StatementContext statement() throws RecognitionException {
		StatementContext _localctx = new StatementContext(_ctx, getState());
		enterRule(_localctx, 102, RULE_statement);
		try {
			setState(938);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,126,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(924);
				block();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(925);
				break_statement();
				}
				break;
			case 3:
				enterOuterAlt(_localctx, 3);
				{
				setState(926);
				continue_statement();
				}
				break;
			case 4:
				enterOuterAlt(_localctx, 4);
				{
				setState(927);
				repeat_statement();
				}
				break;
			case 5:
				enterOuterAlt(_localctx, 5);
				{
				setState(928);
				empty_statement();
				}
				break;
			case 6:
				enterOuterAlt(_localctx, 6);
				{
				setState(929);
				expression_statement();
				}
				break;
			case 7:
				enterOuterAlt(_localctx, 7);
				{
				setState(930);
				for_statement();
				}
				break;
			case 8:
				enterOuterAlt(_localctx, 8);
				{
				setState(931);
				if_statement();
				}
				break;
			case 9:
				enterOuterAlt(_localctx, 9);
				{
				setState(932);
				local_declaration_statement();
				}
				break;
			case 10:
				enterOuterAlt(_localctx, 10);
				{
				setState(933);
				local_function_statement();
				}
				break;
			case 11:
				enterOuterAlt(_localctx, 11);
				{
				setState(934);
				return_statement();
				}
				break;
			case 12:
				enterOuterAlt(_localctx, 12);
				{
				setState(935);
				switch_statement();
				}
				break;
			case 13:
				enterOuterAlt(_localctx, 13);
				{
				setState(936);
				using_statement();
				}
				break;
			case 14:
				enterOuterAlt(_localctx, 14);
				{
				setState(937);
				while_statement();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Break_statementContext extends ParserRuleContext {
		public TerminalNode BREAK() { return getToken(RavenParser2.BREAK, 0); }
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Break_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_break_statement; }
	}

	public final Break_statementContext break_statement() throws RecognitionException {
		Break_statementContext _localctx = new Break_statementContext(_ctx, getState());
		enterRule(_localctx, 104, RULE_break_statement);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(943);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(940);
				attribute_list();
				}
				}
				setState(945);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(946);
			match(BREAK);
			setState(948); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(947);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(950); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,128,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Continue_statementContext extends ParserRuleContext {
		public TerminalNode CONTINUE() { return getToken(RavenParser2.CONTINUE, 0); }
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Continue_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_continue_statement; }
	}

	public final Continue_statementContext continue_statement() throws RecognitionException {
		Continue_statementContext _localctx = new Continue_statementContext(_ctx, getState());
		enterRule(_localctx, 106, RULE_continue_statement);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(955);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(952);
				attribute_list();
				}
				}
				setState(957);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(958);
			match(CONTINUE);
			setState(960); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(959);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(962); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,130,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Repeat_statementContext extends ParserRuleContext {
		public TerminalNode REPEAT() { return getToken(RavenParser2.REPEAT, 0); }
		public StatementContext statement() {
			return getRuleContext(StatementContext.class,0);
		}
		public TerminalNode WHILE() { return getToken(RavenParser2.WHILE, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Repeat_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_repeat_statement; }
	}

	public final Repeat_statementContext repeat_statement() throws RecognitionException {
		Repeat_statementContext _localctx = new Repeat_statementContext(_ctx, getState());
		enterRule(_localctx, 108, RULE_repeat_statement);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(967);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(964);
				attribute_list();
				}
				}
				setState(969);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(970);
			match(REPEAT);
			setState(971);
			statement();
			setState(972);
			match(WHILE);
			setState(973);
			match(OPEN_PARENS);
			setState(974);
			expression(0);
			setState(975);
			match(CLOSE_PARENS);
			setState(977); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(976);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(979); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,132,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Empty_statementContext extends ParserRuleContext {
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Empty_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_empty_statement; }
	}

	public final Empty_statementContext empty_statement() throws RecognitionException {
		Empty_statementContext _localctx = new Empty_statementContext(_ctx, getState());
		enterRule(_localctx, 110, RULE_empty_statement);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(984);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(981);
				attribute_list();
				}
				}
				setState(986);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(988); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(987);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(990); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,134,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Expression_statementContext extends ParserRuleContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public Expression_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_expression_statement; }
	}

	public final Expression_statementContext expression_statement() throws RecognitionException {
		Expression_statementContext _localctx = new Expression_statementContext(_ctx, getState());
		enterRule(_localctx, 112, RULE_expression_statement);
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(995);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,135,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					{
					{
					setState(992);
					attribute_list();
					}
					} 
				}
				setState(997);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,135,_ctx);
			}
			setState(998);
			expression(0);
			setState(1000); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(999);
					match(NL);
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(1002); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,136,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class For_statementContext extends ParserRuleContext {
		public TerminalNode FOR() { return getToken(RavenParser2.FOR, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public TerminalNode IN() { return getToken(RavenParser2.IN, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public For_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_for_statement; }
	}

	public final For_statementContext for_statement() throws RecognitionException {
		For_statementContext _localctx = new For_statementContext(_ctx, getState());
		enterRule(_localctx, 114, RULE_for_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1007);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1004);
				attribute_list();
				}
				}
				setState(1009);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1010);
			match(FOR);
			setState(1011);
			match(OPEN_PARENS);
			setState(1012);
			identifier_token();
			setState(1013);
			match(IN);
			setState(1014);
			expression(0);
			setState(1015);
			match(CLOSE_PARENS);
			setState(1016);
			block();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class If_statementContext extends ParserRuleContext {
		public TerminalNode IF() { return getToken(RavenParser2.IF, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public Else_clauseContext else_clause() {
			return getRuleContext(Else_clauseContext.class,0);
		}
		public If_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_if_statement; }
	}

	public final If_statementContext if_statement() throws RecognitionException {
		If_statementContext _localctx = new If_statementContext(_ctx, getState());
		enterRule(_localctx, 116, RULE_if_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1021);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1018);
				attribute_list();
				}
				}
				setState(1023);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1024);
			match(IF);
			setState(1025);
			match(OPEN_PARENS);
			setState(1026);
			expression(0);
			setState(1027);
			match(CLOSE_PARENS);
			setState(1028);
			block();
			setState(1030);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==ELSE) {
				{
				setState(1029);
				else_clause();
				}
			}

			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Else_clauseContext extends ParserRuleContext {
		public TerminalNode ELSE() { return getToken(RavenParser2.ELSE, 0); }
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public Else_clauseContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_else_clause; }
	}

	public final Else_clauseContext else_clause() throws RecognitionException {
		Else_clauseContext _localctx = new Else_clauseContext(_ctx, getState());
		enterRule(_localctx, 118, RULE_else_clause);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1032);
			match(ELSE);
			setState(1033);
			block();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Return_statementContext extends ParserRuleContext {
		public TerminalNode RETURN() { return getToken(RavenParser2.RETURN, 0); }
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Return_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_return_statement; }
	}

	public final Return_statementContext return_statement() throws RecognitionException {
		Return_statementContext _localctx = new Return_statementContext(_ctx, getState());
		enterRule(_localctx, 120, RULE_return_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1038);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1035);
				attribute_list();
				}
				}
				setState(1040);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1041);
			match(RETURN);
			setState(1043);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908934413824L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7044052759682763329L) != 0) || _la==MAT4X4) {
				{
				setState(1042);
				expression(0);
				}
			}

			setState(1045);
			match(NL);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Local_function_statementContext extends ParserRuleContext {
		public TerminalNode FUNC() { return getToken(RavenParser2.FUNC, 0); }
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Parameter_listContext parameter_list() {
			return getRuleContext(Parameter_listContext.class,0);
		}
		public BlockContext block() {
			return getRuleContext(BlockContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Type_parameter_listContext type_parameter_list() {
			return getRuleContext(Type_parameter_listContext.class,0);
		}
		public List<Type_parameter_constraint_clauseContext> type_parameter_constraint_clause() {
			return getRuleContexts(Type_parameter_constraint_clauseContext.class);
		}
		public Type_parameter_constraint_clauseContext type_parameter_constraint_clause(int i) {
			return getRuleContext(Type_parameter_constraint_clauseContext.class,i);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Arrow_expression_clauseContext arrow_expression_clause() {
			return getRuleContext(Arrow_expression_clauseContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public Local_function_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_local_function_statement; }
	}

	public final Local_function_statementContext local_function_statement() throws RecognitionException {
		Local_function_statementContext _localctx = new Local_function_statementContext(_ctx, getState());
		enterRule(_localctx, 122, RULE_local_function_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1050);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1047);
				attribute_list();
				}
				}
				setState(1052);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1056);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(1053);
				modifier();
				}
				}
				setState(1058);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1059);
			match(FUNC);
			setState(1060);
			identifier_token();
			setState(1062);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==LT) {
				{
				setState(1061);
				type_parameter_list();
				}
			}

			setState(1064);
			parameter_list();
			setState(1068);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==WHERE) {
				{
				{
				setState(1065);
				type_parameter_constraint_clause();
				}
				}
				setState(1070);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1073);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==COLON) {
				{
				setState(1071);
				match(COLON);
				setState(1072);
				type(0);
				}
			}

			setState(1079);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case OPEN_BRACE:
				{
				setState(1075);
				block();
				}
				break;
			case LAMBDA:
				{
				{
				setState(1076);
				arrow_expression_clause();
				setState(1077);
				match(NL);
				}
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Local_declaration_statementContext extends ParserRuleContext {
		public Variable_declarationContext variable_declaration() {
			return getRuleContext(Variable_declarationContext.class,0);
		}
		public TerminalNode NL() { return getToken(RavenParser2.NL, 0); }
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public TerminalNode USING() { return getToken(RavenParser2.USING, 0); }
		public List<ModifierContext> modifier() {
			return getRuleContexts(ModifierContext.class);
		}
		public ModifierContext modifier(int i) {
			return getRuleContext(ModifierContext.class,i);
		}
		public Local_declaration_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_local_declaration_statement; }
	}

	public final Local_declaration_statementContext local_declaration_statement() throws RecognitionException {
		Local_declaration_statementContext _localctx = new Local_declaration_statementContext(_ctx, getState());
		enterRule(_localctx, 124, RULE_local_declaration_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1084);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1081);
				attribute_list();
				}
				}
				setState(1086);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1088);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==USING) {
				{
				setState(1087);
				match(USING);
				}
			}

			setState(1093);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) {
				{
				{
				setState(1090);
				modifier();
				}
				}
				setState(1095);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1096);
			variable_declaration();
			setState(1097);
			match(NL);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class While_statementContext extends ParserRuleContext {
		public TerminalNode WHILE() { return getToken(RavenParser2.WHILE, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public StatementContext statement() {
			return getRuleContext(StatementContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public While_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_while_statement; }
	}

	public final While_statementContext while_statement() throws RecognitionException {
		While_statementContext _localctx = new While_statementContext(_ctx, getState());
		enterRule(_localctx, 126, RULE_while_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1102);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1099);
				attribute_list();
				}
				}
				setState(1104);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1105);
			match(WHILE);
			setState(1106);
			match(OPEN_PARENS);
			setState(1107);
			expression(0);
			setState(1108);
			match(CLOSE_PARENS);
			setState(1109);
			statement();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Using_statementContext extends ParserRuleContext {
		public TerminalNode USING() { return getToken(RavenParser2.USING, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public StatementContext statement() {
			return getRuleContext(StatementContext.class,0);
		}
		public Variable_declarationContext variable_declaration() {
			return getRuleContext(Variable_declarationContext.class,0);
		}
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public Using_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_using_statement; }
	}

	public final Using_statementContext using_statement() throws RecognitionException {
		Using_statementContext _localctx = new Using_statementContext(_ctx, getState());
		enterRule(_localctx, 128, RULE_using_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1114);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1111);
				attribute_list();
				}
				}
				setState(1116);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1117);
			match(USING);
			setState(1118);
			match(OPEN_PARENS);
			setState(1121);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case VAR:
			case VAL:
				{
				setState(1119);
				variable_declaration();
				}
				break;
			case GLOBAL:
			case SELF:
			case BOOL:
			case BOOL2:
			case BOOL3:
			case BOOL4:
			case INT:
			case INT2:
			case INT3:
			case INT4:
			case UINT:
			case UINT2:
			case UINT3:
			case UINT4:
			case FLOAT:
			case FLOAT2:
			case FLOAT3:
			case FLOAT4:
			case DOUBLE:
			case DOUBLE2:
			case DOUBLE3:
			case DOUBLE4:
			case MAT2:
			case MAT2X3:
			case MAT2X4:
			case MAT3:
			case MAT3X2:
			case MAT3X4:
			case MAT4:
			case MAT4X2:
			case MAT4X3:
			case BASE:
			case DEFAULT:
			case FALSE:
			case NULL_:
			case REF:
			case SIZEOF:
			case TRUE:
			case IDENTIFIER:
			case AT:
			case INTEGER_LITERAL:
			case HEX_INTEGER_LITERAL:
			case BIN_INTEGER_LITERAL:
			case REAL_LITERAL:
			case OPEN_BRACKET:
			case OPEN_PARENS:
			case DOT:
			case PLUS:
			case MINUS:
			case CARET:
			case BANG:
			case TILDE:
			case OP_INC:
			case OP_DEC:
			case MAT4X4:
				{
				setState(1120);
				expression(0);
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			setState(1123);
			match(CLOSE_PARENS);
			setState(1124);
			statement();
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Switch_statementContext extends ParserRuleContext {
		public TerminalNode SWITCH() { return getToken(RavenParser2.SWITCH, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<Attribute_listContext> attribute_list() {
			return getRuleContexts(Attribute_listContext.class);
		}
		public Attribute_listContext attribute_list(int i) {
			return getRuleContext(Attribute_listContext.class,i);
		}
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<Switch_sectionContext> switch_section() {
			return getRuleContexts(Switch_sectionContext.class);
		}
		public Switch_sectionContext switch_section(int i) {
			return getRuleContext(Switch_sectionContext.class,i);
		}
		public Switch_statementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_switch_statement; }
	}

	public final Switch_statementContext switch_statement() throws RecognitionException {
		Switch_statementContext _localctx = new Switch_statementContext(_ctx, getState());
		enterRule(_localctx, 130, RULE_switch_statement);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1129);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==OPEN_BRACKET) {
				{
				{
				setState(1126);
				attribute_list();
				}
				}
				setState(1131);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1132);
			match(SWITCH);
			setState(1134);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,155,_ctx) ) {
			case 1:
				{
				setState(1133);
				match(OPEN_PARENS);
				}
				break;
			}
			setState(1136);
			expression(0);
			setState(1138);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==CLOSE_PARENS) {
				{
				setState(1137);
				match(CLOSE_PARENS);
				}
			}

			setState(1140);
			match(OPEN_BRACE);
			setState(1144);
			_errHandler.sync(this);
			_la = _input.LA(1);
			while (_la==CASE || _la==DEFAULT) {
				{
				{
				setState(1141);
				switch_section();
				}
				}
				setState(1146);
				_errHandler.sync(this);
				_la = _input.LA(1);
			}
			setState(1147);
			match(CLOSE_BRACE);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Switch_sectionContext extends ParserRuleContext {
		public List<Switch_labelContext> switch_label() {
			return getRuleContexts(Switch_labelContext.class);
		}
		public Switch_labelContext switch_label(int i) {
			return getRuleContext(Switch_labelContext.class,i);
		}
		public List<StatementContext> statement() {
			return getRuleContexts(StatementContext.class);
		}
		public StatementContext statement(int i) {
			return getRuleContext(StatementContext.class,i);
		}
		public Switch_sectionContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_switch_section; }
	}

	public final Switch_sectionContext switch_section() throws RecognitionException {
		Switch_sectionContext _localctx = new Switch_sectionContext(_ctx, getState());
		enterRule(_localctx, 132, RULE_switch_section);
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(1150); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(1149);
					switch_label();
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(1152); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,158,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			setState(1155); 
			_errHandler.sync(this);
			_alt = 1;
			do {
				switch (_alt) {
				case 1:
					{
					{
					setState(1154);
					statement();
					}
					}
					break;
				default:
					throw new NoViableAltException(this);
				}
				setState(1157); 
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,159,_ctx);
			} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Switch_labelContext extends ParserRuleContext {
		public Case_pattern_switch_labelContext case_pattern_switch_label() {
			return getRuleContext(Case_pattern_switch_labelContext.class,0);
		}
		public Case_switch_labelContext case_switch_label() {
			return getRuleContext(Case_switch_labelContext.class,0);
		}
		public Default_switch_labelContext default_switch_label() {
			return getRuleContext(Default_switch_labelContext.class,0);
		}
		public Switch_labelContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_switch_label; }
	}

	public final Switch_labelContext switch_label() throws RecognitionException {
		Switch_labelContext _localctx = new Switch_labelContext(_ctx, getState());
		enterRule(_localctx, 134, RULE_switch_label);
		try {
			setState(1162);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,160,_ctx) ) {
			case 1:
				enterOuterAlt(_localctx, 1);
				{
				setState(1159);
				case_pattern_switch_label();
				}
				break;
			case 2:
				enterOuterAlt(_localctx, 2);
				{
				setState(1160);
				case_switch_label();
				}
				break;
			case 3:
				enterOuterAlt(_localctx, 3);
				{
				setState(1161);
				default_switch_label();
				}
				break;
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Case_pattern_switch_labelContext extends ParserRuleContext {
		public TerminalNode CASE() { return getToken(RavenParser2.CASE, 0); }
		public PatternContext pattern() {
			return getRuleContext(PatternContext.class,0);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public When_clauseContext when_clause() {
			return getRuleContext(When_clauseContext.class,0);
		}
		public Case_pattern_switch_labelContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_case_pattern_switch_label; }
	}

	public final Case_pattern_switch_labelContext case_pattern_switch_label() throws RecognitionException {
		Case_pattern_switch_labelContext _localctx = new Case_pattern_switch_labelContext(_ctx, getState());
		enterRule(_localctx, 136, RULE_case_pattern_switch_label);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1164);
			match(CASE);
			setState(1165);
			pattern(0);
			setState(1167);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==WHEN) {
				{
				setState(1166);
				when_clause();
				}
			}

			setState(1169);
			match(COLON);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Case_switch_labelContext extends ParserRuleContext {
		public TerminalNode CASE() { return getToken(RavenParser2.CASE, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public Case_switch_labelContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_case_switch_label; }
	}

	public final Case_switch_labelContext case_switch_label() throws RecognitionException {
		Case_switch_labelContext _localctx = new Case_switch_labelContext(_ctx, getState());
		enterRule(_localctx, 138, RULE_case_switch_label);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1171);
			match(CASE);
			setState(1172);
			expression(0);
			setState(1173);
			match(COLON);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Default_switch_labelContext extends ParserRuleContext {
		public TerminalNode DEFAULT() { return getToken(RavenParser2.DEFAULT, 0); }
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public Default_switch_labelContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_default_switch_label; }
	}

	public final Default_switch_labelContext default_switch_label() throws RecognitionException {
		Default_switch_labelContext _localctx = new Default_switch_labelContext(_ctx, getState());
		enterRule(_localctx, 140, RULE_default_switch_label);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1175);
			match(DEFAULT);
			setState(1176);
			match(COLON);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class ExpressionContext extends ParserRuleContext {
		public ExpressionContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_expression; }
	 
		public ExpressionContext() { }
		public void copyFrom(ExpressionContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ElementAccessExpressionContext extends ExpressionContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Bracketed_argument_listContext bracketed_argument_list() {
			return getRuleContext(Bracketed_argument_listContext.class,0);
		}
		public ElementAccessExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class SwitchExpressionContext extends ExpressionContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode SWITCH() { return getToken(RavenParser2.SWITCH, 0); }
		public TerminalNode OPEN_BRACE() { return getToken(RavenParser2.OPEN_BRACE, 0); }
		public TerminalNode CLOSE_BRACE() { return getToken(RavenParser2.CLOSE_BRACE, 0); }
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<Switch_expression_armContext> switch_expression_arm() {
			return getRuleContexts(Switch_expression_armContext.class);
		}
		public Switch_expression_armContext switch_expression_arm(int i) {
			return getRuleContext(Switch_expression_armContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public SwitchExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class PrefixUnaryExpressionContext extends ExpressionContext {
		public Token op;
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode BANG() { return getToken(RavenParser2.BANG, 0); }
		public TerminalNode PLUS() { return getToken(RavenParser2.PLUS, 0); }
		public TerminalNode OP_INC() { return getToken(RavenParser2.OP_INC, 0); }
		public TerminalNode MINUS() { return getToken(RavenParser2.MINUS, 0); }
		public TerminalNode OP_DEC() { return getToken(RavenParser2.OP_DEC, 0); }
		public TerminalNode CARET() { return getToken(RavenParser2.CARET, 0); }
		public TerminalNode TILDE() { return getToken(RavenParser2.TILDE, 0); }
		public PrefixUnaryExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class TupleExpressionContext extends ExpressionContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public List<ArgumentContext> argument() {
			return getRuleContexts(ArgumentContext.class);
		}
		public ArgumentContext argument(int i) {
			return getRuleContext(ArgumentContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public TupleExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class RangeExpressionContext extends ExpressionContext {
		public List<ExpressionContext> expression() {
			return getRuleContexts(ExpressionContext.class);
		}
		public ExpressionContext expression(int i) {
			return getRuleContext(ExpressionContext.class,i);
		}
		public TerminalNode DOUBLE_DOT() { return getToken(RavenParser2.DOUBLE_DOT, 0); }
		public RangeExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class InvocationExpressionContext extends ExpressionContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Argument_listContext argument_list() {
			return getRuleContext(Argument_listContext.class,0);
		}
		public InvocationExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class SizeofExpressionContext extends ExpressionContext {
		public TerminalNode SIZEOF() { return getToken(RavenParser2.SIZEOF, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public SizeofExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class RefExpressionContext extends ExpressionContext {
		public TerminalNode REF() { return getToken(RavenParser2.REF, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public RefExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class AssignmentExpressionContext extends ExpressionContext {
		public Token op;
		public List<ExpressionContext> expression() {
			return getRuleContexts(ExpressionContext.class);
		}
		public ExpressionContext expression(int i) {
			return getRuleContext(ExpressionContext.class,i);
		}
		public TerminalNode ASSIGNMENT() { return getToken(RavenParser2.ASSIGNMENT, 0); }
		public TerminalNode OP_ADD_ASSIGNMENT() { return getToken(RavenParser2.OP_ADD_ASSIGNMENT, 0); }
		public TerminalNode OP_SUB_ASSIGNMENT() { return getToken(RavenParser2.OP_SUB_ASSIGNMENT, 0); }
		public TerminalNode OP_MULT_ASSIGNMENT() { return getToken(RavenParser2.OP_MULT_ASSIGNMENT, 0); }
		public TerminalNode OP_DIV_ASSIGNMENT() { return getToken(RavenParser2.OP_DIV_ASSIGNMENT, 0); }
		public TerminalNode OP_MOD_ASSIGNMENT() { return getToken(RavenParser2.OP_MOD_ASSIGNMENT, 0); }
		public TerminalNode OP_AND_ASSIGNMENT() { return getToken(RavenParser2.OP_AND_ASSIGNMENT, 0); }
		public TerminalNode OP_XOR_ASSIGNMENT() { return getToken(RavenParser2.OP_XOR_ASSIGNMENT, 0); }
		public TerminalNode OP_OR_ASSIGNMENT() { return getToken(RavenParser2.OP_OR_ASSIGNMENT, 0); }
		public TerminalNode OP_LEFT_SHIFT_ASSIGNMENT() { return getToken(RavenParser2.OP_LEFT_SHIFT_ASSIGNMENT, 0); }
		public TerminalNode OP_RIGHT_SHIFT_ASSIGNMENT() { return getToken(RavenParser2.OP_RIGHT_SHIFT_ASSIGNMENT, 0); }
		public TerminalNode OP_UNSIGNED_RIGHT_SHIFT_ASSIGNMENT() { return getToken(RavenParser2.OP_UNSIGNED_RIGHT_SHIFT_ASSIGNMENT, 0); }
		public TerminalNode OP_COALESCING_ASSIGNMENT() { return getToken(RavenParser2.OP_COALESCING_ASSIGNMENT, 0); }
		public AssignmentExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class BinaryExpressionContext extends ExpressionContext {
		public Token op;
		public List<ExpressionContext> expression() {
			return getRuleContexts(ExpressionContext.class);
		}
		public ExpressionContext expression(int i) {
			return getRuleContext(ExpressionContext.class,i);
		}
		public TerminalNode PLUS() { return getToken(RavenParser2.PLUS, 0); }
		public TerminalNode MINUS() { return getToken(RavenParser2.MINUS, 0); }
		public TerminalNode STAR() { return getToken(RavenParser2.STAR, 0); }
		public TerminalNode DIV() { return getToken(RavenParser2.DIV, 0); }
		public TerminalNode PERCENT() { return getToken(RavenParser2.PERCENT, 0); }
		public TerminalNode OP_LEFT_SHIFT() { return getToken(RavenParser2.OP_LEFT_SHIFT, 0); }
		public TerminalNode OP_RIGHT_SHIFT() { return getToken(RavenParser2.OP_RIGHT_SHIFT, 0); }
		public TerminalNode OP_UNSIGNED_RIGHT_SHIFT() { return getToken(RavenParser2.OP_UNSIGNED_RIGHT_SHIFT, 0); }
		public TerminalNode OP_OR() { return getToken(RavenParser2.OP_OR, 0); }
		public TerminalNode OP_AND() { return getToken(RavenParser2.OP_AND, 0); }
		public TerminalNode BITWISE_OR() { return getToken(RavenParser2.BITWISE_OR, 0); }
		public TerminalNode AMP() { return getToken(RavenParser2.AMP, 0); }
		public TerminalNode CARET() { return getToken(RavenParser2.CARET, 0); }
		public TerminalNode OP_EQ() { return getToken(RavenParser2.OP_EQ, 0); }
		public TerminalNode OP_NE() { return getToken(RavenParser2.OP_NE, 0); }
		public TerminalNode LT() { return getToken(RavenParser2.LT, 0); }
		public TerminalNode OP_LE() { return getToken(RavenParser2.OP_LE, 0); }
		public TerminalNode GT() { return getToken(RavenParser2.GT, 0); }
		public TerminalNode OP_GE() { return getToken(RavenParser2.OP_GE, 0); }
		public TerminalNode IS() { return getToken(RavenParser2.IS, 0); }
		public TerminalNode AS() { return getToken(RavenParser2.AS, 0); }
		public TerminalNode OP_COALESCING() { return getToken(RavenParser2.OP_COALESCING, 0); }
		public BinaryExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class CollectionExpressionContext extends ExpressionContext {
		public TerminalNode OPEN_BRACKET() { return getToken(RavenParser2.OPEN_BRACKET, 0); }
		public TerminalNode CLOSE_BRACKET() { return getToken(RavenParser2.CLOSE_BRACKET, 0); }
		public List<TerminalNode> NL() { return getTokens(RavenParser2.NL); }
		public TerminalNode NL(int i) {
			return getToken(RavenParser2.NL, i);
		}
		public List<Collection_elementContext> collection_element() {
			return getRuleContexts(Collection_elementContext.class);
		}
		public Collection_elementContext collection_element(int i) {
			return getRuleContext(Collection_elementContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public CollectionExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ConditionalAccessExpressionContext extends ExpressionContext {
		public List<ExpressionContext> expression() {
			return getRuleContexts(ExpressionContext.class);
		}
		public ExpressionContext expression(int i) {
			return getRuleContext(ExpressionContext.class,i);
		}
		public TerminalNode OP_COALESCING() { return getToken(RavenParser2.OP_COALESCING, 0); }
		public ConditionalAccessExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class IsPatternExpressionContext extends ExpressionContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode IS() { return getToken(RavenParser2.IS, 0); }
		public PatternContext pattern() {
			return getRuleContext(PatternContext.class,0);
		}
		public IsPatternExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ParenthesizedExpressionContext extends ExpressionContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public ParenthesizedExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class MemberAccessExpressionContext extends ExpressionContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode DOT() { return getToken(RavenParser2.DOT, 0); }
		public Simple_nameContext simple_name() {
			return getRuleContext(Simple_nameContext.class,0);
		}
		public MemberAccessExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class TypeExpressionContext extends ExpressionContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TypeExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class DeclarationExpressionContext extends ExpressionContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Variable_designationContext variable_designation() {
			return getRuleContext(Variable_designationContext.class,0);
		}
		public DeclarationExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class LiteralExpressionContext extends ExpressionContext {
		public Literal_expressionContext literal_expression() {
			return getRuleContext(Literal_expressionContext.class,0);
		}
		public LiteralExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ConditionalExpressionContext extends ExpressionContext {
		public List<ExpressionContext> expression() {
			return getRuleContexts(ExpressionContext.class);
		}
		public ExpressionContext expression(int i) {
			return getRuleContext(ExpressionContext.class,i);
		}
		public TerminalNode INTERR() { return getToken(RavenParser2.INTERR, 0); }
		public TerminalNode COLON() { return getToken(RavenParser2.COLON, 0); }
		public ConditionalExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ImplicitElementAccessContext extends ExpressionContext {
		public Bracketed_argument_listContext bracketed_argument_list() {
			return getRuleContext(Bracketed_argument_listContext.class,0);
		}
		public ImplicitElementAccessContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class PostfixUnaryExpressionContext extends ExpressionContext {
		public Token op;
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode OP_INC() { return getToken(RavenParser2.OP_INC, 0); }
		public TerminalNode OP_DEC() { return getToken(RavenParser2.OP_DEC, 0); }
		public TerminalNode BANG() { return getToken(RavenParser2.BANG, 0); }
		public PostfixUnaryExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class InstanceExpressionContext extends ExpressionContext {
		public Token op;
		public TerminalNode BASE() { return getToken(RavenParser2.BASE, 0); }
		public TerminalNode SELF() { return getToken(RavenParser2.SELF, 0); }
		public InstanceExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class DefaultExpressionContext extends ExpressionContext {
		public TerminalNode DEFAULT() { return getToken(RavenParser2.DEFAULT, 0); }
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public DefaultExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class CastExpressionContext extends ExpressionContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public CastExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class MemberBindingExpressionContext extends ExpressionContext {
		public TerminalNode DOT() { return getToken(RavenParser2.DOT, 0); }
		public Simple_nameContext simple_name() {
			return getRuleContext(Simple_nameContext.class,0);
		}
		public MemberBindingExpressionContext(ExpressionContext ctx) { copyFrom(ctx); }
	}

	public final ExpressionContext expression() throws RecognitionException {
		return expression(0);
	}

	private ExpressionContext expression(int _p) throws RecognitionException {
		ParserRuleContext _parentctx = _ctx;
		int _parentState = getState();
		ExpressionContext _localctx = new ExpressionContext(_ctx, _parentState);
		ExpressionContext _prevctx = _localctx;
		int _startState = 142;
		enterRecursionRule(_localctx, 142, RULE_expression, _p);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(1252);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,169,_ctx) ) {
			case 1:
				{
				_localctx = new CastExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;

				setState(1179);
				match(OPEN_PARENS);
				setState(1180);
				type(0);
				setState(1181);
				match(CLOSE_PARENS);
				setState(1182);
				expression(23);
				}
				break;
			case 2:
				{
				_localctx = new CollectionExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1184);
				match(OPEN_BRACKET);
				setState(1188);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,162,_ctx);
				while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
					if ( _alt==1 ) {
						{
						{
						setState(1185);
						match(NL);
						}
						} 
					}
					setState(1190);
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,162,_ctx);
				}
				setState(1205);
				_errHandler.sync(this);
				_la = _input.LA(1);
				if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908934413824L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7044053859194391105L) != 0) || _la==MAT4X4) {
					{
					setState(1191);
					collection_element();
					setState(1202);
					_errHandler.sync(this);
					_la = _input.LA(1);
					while (_la==COMMA) {
						{
						{
						setState(1192);
						match(COMMA);
						setState(1196);
						_errHandler.sync(this);
						_la = _input.LA(1);
						while (_la==NL) {
							{
							{
							setState(1193);
							match(NL);
							}
							}
							setState(1198);
							_errHandler.sync(this);
							_la = _input.LA(1);
						}
						setState(1199);
						collection_element();
						}
						}
						setState(1204);
						_errHandler.sync(this);
						_la = _input.LA(1);
					}
					}
				}

				setState(1210);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==NL) {
					{
					{
					setState(1207);
					match(NL);
					}
					}
					setState(1212);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				setState(1213);
				match(CLOSE_BRACKET);
				}
				break;
			case 3:
				{
				_localctx = new DeclarationExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1214);
				type(0);
				setState(1215);
				variable_designation();
				}
				break;
			case 4:
				{
				_localctx = new DefaultExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1217);
				match(DEFAULT);
				setState(1218);
				match(OPEN_PARENS);
				setState(1219);
				type(0);
				setState(1220);
				match(CLOSE_PARENS);
				}
				break;
			case 5:
				{
				_localctx = new ImplicitElementAccessContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1222);
				bracketed_argument_list();
				}
				break;
			case 6:
				{
				_localctx = new InstanceExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1223);
				((InstanceExpressionContext)_localctx).op = _input.LT(1);
				_la = _input.LA(1);
				if ( !(_la==SELF || _la==BASE) ) {
					((InstanceExpressionContext)_localctx).op = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				}
				break;
			case 7:
				{
				_localctx = new LiteralExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1224);
				literal_expression();
				}
				break;
			case 8:
				{
				_localctx = new MemberBindingExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1225);
				match(DOT);
				setState(1226);
				simple_name();
				}
				break;
			case 9:
				{
				_localctx = new ParenthesizedExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1227);
				match(OPEN_PARENS);
				setState(1228);
				expression(0);
				setState(1229);
				match(CLOSE_PARENS);
				}
				break;
			case 10:
				{
				_localctx = new PrefixUnaryExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1231);
				((PrefixUnaryExpressionContext)_localctx).op = _input.LT(1);
				_la = _input.LA(1);
				if ( !(((((_la - 114)) & ~0x3f) == 0 && ((1L << (_la - 114)) & 50051L) != 0)) ) {
					((PrefixUnaryExpressionContext)_localctx).op = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				setState(1232);
				expression(7);
				}
				break;
			case 11:
				{
				_localctx = new RefExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1233);
				match(REF);
				setState(1234);
				expression(5);
				}
				break;
			case 12:
				{
				_localctx = new SizeofExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1235);
				match(SIZEOF);
				setState(1236);
				match(OPEN_PARENS);
				setState(1237);
				type(0);
				setState(1238);
				match(CLOSE_PARENS);
				}
				break;
			case 13:
				{
				_localctx = new TupleExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1240);
				match(OPEN_PARENS);
				setState(1241);
				argument();
				setState(1244); 
				_errHandler.sync(this);
				_alt = 1;
				do {
					switch (_alt) {
					case 1:
						{
						{
						setState(1242);
						match(COMMA);
						setState(1243);
						argument();
						}
						}
						break;
					default:
						throw new NoViableAltException(this);
					}
					setState(1246); 
					_errHandler.sync(this);
					_alt = getInterpreter().adaptivePredict(_input,167,_ctx);
				} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
				setState(1249);
				_errHandler.sync(this);
				switch ( getInterpreter().adaptivePredict(_input,168,_ctx) ) {
				case 1:
					{
					setState(1248);
					match(CLOSE_PARENS);
					}
					break;
				}
				}
				break;
			case 14:
				{
				_localctx = new TypeExpressionContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1251);
				type(0);
				}
				break;
			}
			_ctx.stop = _input.LT(-1);
			setState(1310);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,175,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					if ( _parseListeners!=null ) triggerExitRuleEvent();
					_prevctx = _localctx;
					{
					setState(1308);
					_errHandler.sync(this);
					switch ( getInterpreter().adaptivePredict(_input,174,_ctx) ) {
					case 1:
						{
						_localctx = new AssignmentExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1254);
						if (!(precpred(_ctx, 25))) throw new FailedPredicateException(this, "precpred(_ctx, 25)");
						setState(1255);
						((AssignmentExpressionContext)_localctx).op = _input.LT(1);
						_la = _input.LA(1);
						if ( !(((((_la - 124)) & ~0x3f) == 0 && ((1L << (_la - 124)) & 91222017L) != 0)) ) {
							((AssignmentExpressionContext)_localctx).op = (Token)_errHandler.recoverInline(this);
						}
						else {
							if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
							_errHandler.reportMatch(this);
							consume();
						}
						setState(1256);
						expression(26);
						}
						break;
					case 2:
						{
						_localctx = new BinaryExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1257);
						if (!(precpred(_ctx, 24))) throw new FailedPredicateException(this, "precpred(_ctx, 24)");
						setState(1258);
						((BinaryExpressionContext)_localctx).op = _input.LT(1);
						_la = _input.LA(1);
						if ( !(((((_la - 58)) & ~0x3f) == 0 && ((1L << (_la - 58)) & -72057594037911551L) != 0) || ((((_la - 125)) & ~0x3f) == 0 && ((1L << (_la - 125)) & 21497831L) != 0)) ) {
							((BinaryExpressionContext)_localctx).op = (Token)_errHandler.recoverInline(this);
						}
						else {
							if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
							_errHandler.reportMatch(this);
							consume();
						}
						setState(1259);
						expression(25);
						}
						break;
					case 3:
						{
						_localctx = new ConditionalAccessExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1260);
						if (!(precpred(_ctx, 21))) throw new FailedPredicateException(this, "precpred(_ctx, 21)");
						setState(1261);
						match(OP_COALESCING);
						setState(1262);
						expression(22);
						}
						break;
					case 4:
						{
						_localctx = new ConditionalExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1263);
						if (!(precpred(_ctx, 20))) throw new FailedPredicateException(this, "precpred(_ctx, 20)");
						setState(1264);
						match(INTERR);
						setState(1265);
						expression(0);
						setState(1266);
						match(COLON);
						setState(1267);
						expression(21);
						}
						break;
					case 5:
						{
						_localctx = new RangeExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1269);
						if (!(precpred(_ctx, 6))) throw new FailedPredicateException(this, "precpred(_ctx, 6)");
						setState(1270);
						match(DOUBLE_DOT);
						setState(1271);
						expression(7);
						}
						break;
					case 6:
						{
						_localctx = new ElementAccessExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1272);
						if (!(precpred(_ctx, 17))) throw new FailedPredicateException(this, "precpred(_ctx, 17)");
						setState(1273);
						bracketed_argument_list();
						}
						break;
					case 7:
						{
						_localctx = new InvocationExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1274);
						if (!(precpred(_ctx, 14))) throw new FailedPredicateException(this, "precpred(_ctx, 14)");
						setState(1275);
						argument_list();
						}
						break;
					case 8:
						{
						_localctx = new IsPatternExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1276);
						if (!(precpred(_ctx, 13))) throw new FailedPredicateException(this, "precpred(_ctx, 13)");
						setState(1277);
						match(IS);
						setState(1278);
						pattern(0);
						}
						break;
					case 9:
						{
						_localctx = new MemberAccessExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1279);
						if (!(precpred(_ctx, 11))) throw new FailedPredicateException(this, "precpred(_ctx, 11)");
						setState(1280);
						match(DOT);
						setState(1281);
						simple_name();
						}
						break;
					case 10:
						{
						_localctx = new PostfixUnaryExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1282);
						if (!(precpred(_ctx, 8))) throw new FailedPredicateException(this, "precpred(_ctx, 8)");
						setState(1283);
						((PostfixUnaryExpressionContext)_localctx).op = _input.LT(1);
						_la = _input.LA(1);
						if ( !(((((_la - 122)) & ~0x3f) == 0 && ((1L << (_la - 122)) & 193L) != 0)) ) {
							((PostfixUnaryExpressionContext)_localctx).op = (Token)_errHandler.recoverInline(this);
						}
						else {
							if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
							_errHandler.reportMatch(this);
							consume();
						}
						}
						break;
					case 11:
						{
						_localctx = new SwitchExpressionContext(new ExpressionContext(_parentctx, _parentState));
						pushNewRecursionContext(_localctx, _startState, RULE_expression);
						setState(1284);
						if (!(precpred(_ctx, 3))) throw new FailedPredicateException(this, "precpred(_ctx, 3)");
						setState(1285);
						match(SWITCH);
						setState(1286);
						match(OPEN_BRACE);
						setState(1288); 
						_errHandler.sync(this);
						_alt = 1;
						do {
							switch (_alt) {
							case 1:
								{
								{
								setState(1287);
								match(NL);
								}
								}
								break;
							default:
								throw new NoViableAltException(this);
							}
							setState(1290); 
							_errHandler.sync(this);
							_alt = getInterpreter().adaptivePredict(_input,170,_ctx);
						} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
						setState(1300);
						_errHandler.sync(this);
						_la = _input.LA(1);
						if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908909235712L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7908744987649526337L) != 0) || ((((_la - 132)) & ~0x3f) == 0 && ((1L << (_la - 132)) & 4611686018427387919L) != 0)) {
							{
							setState(1292);
							switch_expression_arm();
							setState(1297);
							_errHandler.sync(this);
							_la = _input.LA(1);
							while (_la==COMMA) {
								{
								{
								setState(1293);
								match(COMMA);
								setState(1294);
								switch_expression_arm();
								}
								}
								setState(1299);
								_errHandler.sync(this);
								_la = _input.LA(1);
							}
							}
						}

						setState(1303); 
						_errHandler.sync(this);
						_la = _input.LA(1);
						do {
							{
							{
							setState(1302);
							match(NL);
							}
							}
							setState(1305); 
							_errHandler.sync(this);
							_la = _input.LA(1);
						} while ( _la==NL );
						setState(1307);
						match(CLOSE_BRACE);
						}
						break;
					}
					} 
				}
				setState(1312);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,175,_ctx);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			unrollRecursionContexts(_parentctx);
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Literal_expressionContext extends ParserRuleContext {
		public TerminalNode DEFAULT() { return getToken(RavenParser2.DEFAULT, 0); }
		public TerminalNode FALSE() { return getToken(RavenParser2.FALSE, 0); }
		public TerminalNode TRUE() { return getToken(RavenParser2.TRUE, 0); }
		public TerminalNode NULL_() { return getToken(RavenParser2.NULL_, 0); }
		public Numeric_literal_tokenContext numeric_literal_token() {
			return getRuleContext(Numeric_literal_tokenContext.class,0);
		}
		public Literal_expressionContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_literal_expression; }
	}

	public final Literal_expressionContext literal_expression() throws RecognitionException {
		Literal_expressionContext _localctx = new Literal_expressionContext(_ctx, getState());
		enterRule(_localctx, 144, RULE_literal_expression);
		try {
			setState(1318);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case DEFAULT:
				enterOuterAlt(_localctx, 1);
				{
				setState(1313);
				match(DEFAULT);
				}
				break;
			case FALSE:
				enterOuterAlt(_localctx, 2);
				{
				setState(1314);
				match(FALSE);
				}
				break;
			case TRUE:
				enterOuterAlt(_localctx, 3);
				{
				setState(1315);
				match(TRUE);
				}
				break;
			case NULL_:
				enterOuterAlt(_localctx, 4);
				{
				setState(1316);
				match(NULL_);
				}
				break;
			case INTEGER_LITERAL:
			case HEX_INTEGER_LITERAL:
			case BIN_INTEGER_LITERAL:
			case REAL_LITERAL:
				enterOuterAlt(_localctx, 5);
				{
				setState(1317);
				numeric_literal_token();
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Equals_value_clauseContext extends ParserRuleContext {
		public TerminalNode ASSIGNMENT() { return getToken(RavenParser2.ASSIGNMENT, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Equals_value_clauseContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_equals_value_clause; }
	}

	public final Equals_value_clauseContext equals_value_clause() throws RecognitionException {
		Equals_value_clauseContext _localctx = new Equals_value_clauseContext(_ctx, getState());
		enterRule(_localctx, 146, RULE_equals_value_clause);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1320);
			match(ASSIGNMENT);
			setState(1321);
			expression(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Arrow_expression_clauseContext extends ParserRuleContext {
		public TerminalNode LAMBDA() { return getToken(RavenParser2.LAMBDA, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public Arrow_expression_clauseContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_arrow_expression_clause; }
	}

	public final Arrow_expression_clauseContext arrow_expression_clause() throws RecognitionException {
		Arrow_expression_clauseContext _localctx = new Arrow_expression_clauseContext(_ctx, getState());
		enterRule(_localctx, 148, RULE_arrow_expression_clause);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1323);
			match(LAMBDA);
			setState(1324);
			expression(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Collection_elementContext extends ParserRuleContext {
		public Collection_elementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_collection_element; }
	 
		public Collection_elementContext() { }
		public void copyFrom(Collection_elementContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ExpressionElementContext extends Collection_elementContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public ExpressionElementContext(Collection_elementContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class SpreadElementContext extends Collection_elementContext {
		public TerminalNode DOUBLE_DOT() { return getToken(RavenParser2.DOUBLE_DOT, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public SpreadElementContext(Collection_elementContext ctx) { copyFrom(ctx); }
	}

	public final Collection_elementContext collection_element() throws RecognitionException {
		Collection_elementContext _localctx = new Collection_elementContext(_ctx, getState());
		enterRule(_localctx, 150, RULE_collection_element);
		try {
			setState(1329);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case GLOBAL:
			case SELF:
			case BOOL:
			case BOOL2:
			case BOOL3:
			case BOOL4:
			case INT:
			case INT2:
			case INT3:
			case INT4:
			case UINT:
			case UINT2:
			case UINT3:
			case UINT4:
			case FLOAT:
			case FLOAT2:
			case FLOAT3:
			case FLOAT4:
			case DOUBLE:
			case DOUBLE2:
			case DOUBLE3:
			case DOUBLE4:
			case MAT2:
			case MAT2X3:
			case MAT2X4:
			case MAT3:
			case MAT3X2:
			case MAT3X4:
			case MAT4:
			case MAT4X2:
			case MAT4X3:
			case BASE:
			case DEFAULT:
			case FALSE:
			case NULL_:
			case REF:
			case SIZEOF:
			case TRUE:
			case IDENTIFIER:
			case AT:
			case INTEGER_LITERAL:
			case HEX_INTEGER_LITERAL:
			case BIN_INTEGER_LITERAL:
			case REAL_LITERAL:
			case OPEN_BRACKET:
			case OPEN_PARENS:
			case DOT:
			case PLUS:
			case MINUS:
			case CARET:
			case BANG:
			case TILDE:
			case OP_INC:
			case OP_DEC:
			case MAT4X4:
				_localctx = new ExpressionElementContext(_localctx);
				enterOuterAlt(_localctx, 1);
				{
				setState(1326);
				expression(0);
				}
				break;
			case DOUBLE_DOT:
				_localctx = new SpreadElementContext(_localctx);
				enterOuterAlt(_localctx, 2);
				{
				setState(1327);
				match(DOUBLE_DOT);
				setState(1328);
				expression(0);
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Switch_expression_armContext extends ParserRuleContext {
		public PatternContext pattern() {
			return getRuleContext(PatternContext.class,0);
		}
		public TerminalNode LAMBDA() { return getToken(RavenParser2.LAMBDA, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public When_clauseContext when_clause() {
			return getRuleContext(When_clauseContext.class,0);
		}
		public Switch_expression_armContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_switch_expression_arm; }
	}

	public final Switch_expression_armContext switch_expression_arm() throws RecognitionException {
		Switch_expression_armContext _localctx = new Switch_expression_armContext(_ctx, getState());
		enterRule(_localctx, 152, RULE_switch_expression_arm);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1331);
			pattern(0);
			setState(1333);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==WHEN) {
				{
				setState(1332);
				when_clause();
				}
			}

			setState(1335);
			match(LAMBDA);
			setState(1336);
			expression(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class PatternContext extends ParserRuleContext {
		public PatternContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_pattern; }
	 
		public PatternContext() { }
		public void copyFrom(PatternContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ParenthesizedPatternContext extends PatternContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public PatternContext pattern() {
			return getRuleContext(PatternContext.class,0);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public ParenthesizedPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class VarPatternContext extends PatternContext {
		public Token op;
		public Variable_designationContext variable_designation() {
			return getRuleContext(Variable_designationContext.class,0);
		}
		public TerminalNode VAL() { return getToken(RavenParser2.VAL, 0); }
		public TerminalNode VAR() { return getToken(RavenParser2.VAR, 0); }
		public VarPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class BinaryPatternContext extends PatternContext {
		public Token op;
		public List<PatternContext> pattern() {
			return getRuleContexts(PatternContext.class);
		}
		public PatternContext pattern(int i) {
			return getRuleContext(PatternContext.class,i);
		}
		public TerminalNode OR() { return getToken(RavenParser2.OR, 0); }
		public TerminalNode AND() { return getToken(RavenParser2.AND, 0); }
		public BinaryPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ConstantPatternContext extends PatternContext {
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public ConstantPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class DiscardPatternContext extends PatternContext {
		public TerminalNode DISCARD() { return getToken(RavenParser2.DISCARD, 0); }
		public DiscardPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class RelationalPatternContext extends PatternContext {
		public Token op;
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public TerminalNode OP_NE() { return getToken(RavenParser2.OP_NE, 0); }
		public TerminalNode LT() { return getToken(RavenParser2.LT, 0); }
		public TerminalNode OP_LE() { return getToken(RavenParser2.OP_LE, 0); }
		public TerminalNode OP_EQ() { return getToken(RavenParser2.OP_EQ, 0); }
		public TerminalNode GT() { return getToken(RavenParser2.GT, 0); }
		public TerminalNode OP_GE() { return getToken(RavenParser2.OP_GE, 0); }
		public RelationalPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class SlicePatternContext extends PatternContext {
		public TerminalNode DOUBLE_DOT() { return getToken(RavenParser2.DOUBLE_DOT, 0); }
		public PatternContext pattern() {
			return getRuleContext(PatternContext.class,0);
		}
		public SlicePatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class UnaryPatternContext extends PatternContext {
		public TerminalNode NOT() { return getToken(RavenParser2.NOT, 0); }
		public PatternContext pattern() {
			return getRuleContext(PatternContext.class,0);
		}
		public UnaryPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ListPatternContext extends PatternContext {
		public TerminalNode OPEN_BRACKET() { return getToken(RavenParser2.OPEN_BRACKET, 0); }
		public TerminalNode CLOSE_BRACKET() { return getToken(RavenParser2.CLOSE_BRACKET, 0); }
		public List<PatternContext> pattern() {
			return getRuleContexts(PatternContext.class);
		}
		public PatternContext pattern(int i) {
			return getRuleContext(PatternContext.class,i);
		}
		public Variable_designationContext variable_designation() {
			return getRuleContext(Variable_designationContext.class,0);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public ListPatternContext(PatternContext ctx) { copyFrom(ctx); }
	}

	public final PatternContext pattern() throws RecognitionException {
		return pattern(0);
	}

	private PatternContext pattern(int _p) throws RecognitionException {
		ParserRuleContext _parentctx = _ctx;
		int _parentState = getState();
		PatternContext _localctx = new PatternContext(_ctx, _parentState);
		PatternContext _prevctx = _localctx;
		int _startState = 154;
		enterRecursionRule(_localctx, 154, RULE_pattern, _p);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(1370);
			_errHandler.sync(this);
			switch ( getInterpreter().adaptivePredict(_input,183,_ctx) ) {
			case 1:
				{
				_localctx = new ConstantPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;

				setState(1339);
				expression(0);
				}
				break;
			case 2:
				{
				_localctx = new DiscardPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1340);
				match(DISCARD);
				}
				break;
			case 3:
				{
				_localctx = new ListPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1341);
				match(OPEN_BRACKET);
				setState(1350);
				_errHandler.sync(this);
				_la = _input.LA(1);
				if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908909235712L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7908744987649526337L) != 0) || ((((_la - 132)) & ~0x3f) == 0 && ((1L << (_la - 132)) & 4611686018427387919L) != 0)) {
					{
					setState(1342);
					pattern(0);
					setState(1347);
					_errHandler.sync(this);
					_la = _input.LA(1);
					while (_la==COMMA) {
						{
						{
						setState(1343);
						match(COMMA);
						setState(1344);
						pattern(0);
						}
						}
						setState(1349);
						_errHandler.sync(this);
						_la = _input.LA(1);
					}
					}
				}

				setState(1352);
				match(CLOSE_BRACKET);
				setState(1354);
				_errHandler.sync(this);
				switch ( getInterpreter().adaptivePredict(_input,181,_ctx) ) {
				case 1:
					{
					setState(1353);
					variable_designation();
					}
					break;
				}
				}
				break;
			case 4:
				{
				_localctx = new ParenthesizedPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1356);
				match(OPEN_PARENS);
				setState(1357);
				pattern(0);
				setState(1358);
				match(CLOSE_PARENS);
				}
				break;
			case 5:
				{
				_localctx = new RelationalPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1360);
				((RelationalPatternContext)_localctx).op = _input.LT(1);
				_la = _input.LA(1);
				if ( !(((((_la - 125)) & ~0x3f) == 0 && ((1L << (_la - 125)) & 1923L) != 0)) ) {
					((RelationalPatternContext)_localctx).op = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				setState(1361);
				expression(0);
				}
				break;
			case 6:
				{
				_localctx = new SlicePatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1362);
				match(DOUBLE_DOT);
				setState(1364);
				_errHandler.sync(this);
				switch ( getInterpreter().adaptivePredict(_input,182,_ctx) ) {
				case 1:
					{
					setState(1363);
					pattern(0);
					}
					break;
				}
				}
				break;
			case 7:
				{
				_localctx = new UnaryPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1366);
				match(NOT);
				setState(1367);
				pattern(2);
				}
				break;
			case 8:
				{
				_localctx = new VarPatternContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1368);
				((VarPatternContext)_localctx).op = _input.LT(1);
				_la = _input.LA(1);
				if ( !(_la==VAR || _la==VAL) ) {
					((VarPatternContext)_localctx).op = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				setState(1369);
				variable_designation();
				}
				break;
			}
			_ctx.stop = _input.LT(-1);
			setState(1377);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,184,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					if ( _parseListeners!=null ) triggerExitRuleEvent();
					_prevctx = _localctx;
					{
					{
					_localctx = new BinaryPatternContext(new PatternContext(_parentctx, _parentState));
					pushNewRecursionContext(_localctx, _startState, RULE_pattern);
					setState(1372);
					if (!(precpred(_ctx, 9))) throw new FailedPredicateException(this, "precpred(_ctx, 9)");
					setState(1373);
					((BinaryPatternContext)_localctx).op = _input.LT(1);
					_la = _input.LA(1);
					if ( !(_la==OR || _la==AND) ) {
						((BinaryPatternContext)_localctx).op = (Token)_errHandler.recoverInline(this);
					}
					else {
						if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
						_errHandler.reportMatch(this);
						consume();
					}
					setState(1374);
					pattern(10);
					}
					} 
				}
				setState(1379);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,184,_ctx);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			unrollRecursionContexts(_parentctx);
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Variable_designationContext extends ParserRuleContext {
		public Variable_designationContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_variable_designation; }
	 
		public Variable_designationContext() { }
		public void copyFrom(Variable_designationContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class DiscardDesignationContext extends Variable_designationContext {
		public TerminalNode DISCARD() { return getToken(RavenParser2.DISCARD, 0); }
		public DiscardDesignationContext(Variable_designationContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ParenthesizedVariableDesignationContext extends Variable_designationContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<Variable_designationContext> variable_designation() {
			return getRuleContexts(Variable_designationContext.class);
		}
		public Variable_designationContext variable_designation(int i) {
			return getRuleContext(Variable_designationContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public ParenthesizedVariableDesignationContext(Variable_designationContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class SimpleVariableDesignationContext extends Variable_designationContext {
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public SimpleVariableDesignationContext(Variable_designationContext ctx) { copyFrom(ctx); }
	}

	public final Variable_designationContext variable_designation() throws RecognitionException {
		Variable_designationContext _localctx = new Variable_designationContext(_ctx, getState());
		enterRule(_localctx, 156, RULE_variable_designation);
		int _la;
		try {
			setState(1394);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case DISCARD:
				_localctx = new DiscardDesignationContext(_localctx);
				enterOuterAlt(_localctx, 1);
				{
				setState(1380);
				match(DISCARD);
				}
				break;
			case OPEN_PARENS:
				_localctx = new ParenthesizedVariableDesignationContext(_localctx);
				enterOuterAlt(_localctx, 2);
				{
				setState(1381);
				match(OPEN_PARENS);
				setState(1390);
				_errHandler.sync(this);
				_la = _input.LA(1);
				if (_la==DISCARD || ((((_la - 94)) & ~0x3f) == 0 && ((1L << (_la - 94)) & 1027L) != 0)) {
					{
					setState(1382);
					variable_designation();
					setState(1387);
					_errHandler.sync(this);
					_la = _input.LA(1);
					while (_la==COMMA) {
						{
						{
						setState(1383);
						match(COMMA);
						setState(1384);
						variable_designation();
						}
						}
						setState(1389);
						_errHandler.sync(this);
						_la = _input.LA(1);
					}
					}
				}

				setState(1392);
				match(CLOSE_PARENS);
				}
				break;
			case IDENTIFIER:
			case AT:
				_localctx = new SimpleVariableDesignationContext(_localctx);
				enterOuterAlt(_localctx, 3);
				{
				setState(1393);
				identifier_token();
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class When_clauseContext extends ParserRuleContext {
		public TerminalNode WHEN() { return getToken(RavenParser2.WHEN, 0); }
		public ExpressionContext expression() {
			return getRuleContext(ExpressionContext.class,0);
		}
		public When_clauseContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_when_clause; }
	}

	public final When_clauseContext when_clause() throws RecognitionException {
		When_clauseContext _localctx = new When_clauseContext(_ctx, getState());
		enterRule(_localctx, 158, RULE_when_clause);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1396);
			match(WHEN);
			setState(1397);
			expression(0);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class TypeContext extends ParserRuleContext {
		public TypeContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_type; }
	 
		public TypeContext() { }
		public void copyFrom(TypeContext ctx) {
			super.copyFrom(ctx);
		}
	}
	@SuppressWarnings("CheckReturnValue")
	public static class ArrayTypeContext extends TypeContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public List<Array_rank_specifierContext> array_rank_specifier() {
			return getRuleContexts(Array_rank_specifierContext.class);
		}
		public Array_rank_specifierContext array_rank_specifier(int i) {
			return getRuleContext(Array_rank_specifierContext.class,i);
		}
		public ArrayTypeContext(TypeContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class TupleTypeContext extends TypeContext {
		public TerminalNode OPEN_PARENS() { return getToken(RavenParser2.OPEN_PARENS, 0); }
		public List<Tuple_elementContext> tuple_element() {
			return getRuleContexts(Tuple_elementContext.class);
		}
		public Tuple_elementContext tuple_element(int i) {
			return getRuleContext(Tuple_elementContext.class,i);
		}
		public TerminalNode CLOSE_PARENS() { return getToken(RavenParser2.CLOSE_PARENS, 0); }
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public TupleTypeContext(TypeContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class NameTypeContext extends TypeContext {
		public NameContext name() {
			return getRuleContext(NameContext.class,0);
		}
		public NameTypeContext(TypeContext ctx) { copyFrom(ctx); }
	}
	@SuppressWarnings("CheckReturnValue")
	public static class PredefinedTypeContext extends TypeContext {
		public Token pType;
		public TerminalNode BOOL() { return getToken(RavenParser2.BOOL, 0); }
		public TerminalNode BOOL2() { return getToken(RavenParser2.BOOL2, 0); }
		public TerminalNode BOOL3() { return getToken(RavenParser2.BOOL3, 0); }
		public TerminalNode BOOL4() { return getToken(RavenParser2.BOOL4, 0); }
		public TerminalNode INT() { return getToken(RavenParser2.INT, 0); }
		public TerminalNode INT2() { return getToken(RavenParser2.INT2, 0); }
		public TerminalNode INT3() { return getToken(RavenParser2.INT3, 0); }
		public TerminalNode INT4() { return getToken(RavenParser2.INT4, 0); }
		public TerminalNode UINT() { return getToken(RavenParser2.UINT, 0); }
		public TerminalNode UINT2() { return getToken(RavenParser2.UINT2, 0); }
		public TerminalNode UINT3() { return getToken(RavenParser2.UINT3, 0); }
		public TerminalNode UINT4() { return getToken(RavenParser2.UINT4, 0); }
		public TerminalNode FLOAT() { return getToken(RavenParser2.FLOAT, 0); }
		public TerminalNode FLOAT2() { return getToken(RavenParser2.FLOAT2, 0); }
		public TerminalNode FLOAT3() { return getToken(RavenParser2.FLOAT3, 0); }
		public TerminalNode FLOAT4() { return getToken(RavenParser2.FLOAT4, 0); }
		public TerminalNode DOUBLE() { return getToken(RavenParser2.DOUBLE, 0); }
		public TerminalNode DOUBLE2() { return getToken(RavenParser2.DOUBLE2, 0); }
		public TerminalNode DOUBLE3() { return getToken(RavenParser2.DOUBLE3, 0); }
		public TerminalNode DOUBLE4() { return getToken(RavenParser2.DOUBLE4, 0); }
		public TerminalNode MAT2() { return getToken(RavenParser2.MAT2, 0); }
		public TerminalNode MAT2X3() { return getToken(RavenParser2.MAT2X3, 0); }
		public TerminalNode MAT2X4() { return getToken(RavenParser2.MAT2X4, 0); }
		public TerminalNode MAT3() { return getToken(RavenParser2.MAT3, 0); }
		public TerminalNode MAT3X2() { return getToken(RavenParser2.MAT3X2, 0); }
		public TerminalNode MAT3X4() { return getToken(RavenParser2.MAT3X4, 0); }
		public TerminalNode MAT4() { return getToken(RavenParser2.MAT4, 0); }
		public TerminalNode MAT4X2() { return getToken(RavenParser2.MAT4X2, 0); }
		public TerminalNode MAT4X3() { return getToken(RavenParser2.MAT4X3, 0); }
		public TerminalNode MAT4X4() { return getToken(RavenParser2.MAT4X4, 0); }
		public PredefinedTypeContext(TypeContext ctx) { copyFrom(ctx); }
	}

	public final TypeContext type() throws RecognitionException {
		return type(0);
	}

	private TypeContext type(int _p) throws RecognitionException {
		ParserRuleContext _parentctx = _ctx;
		int _parentState = getState();
		TypeContext _localctx = new TypeContext(_ctx, _parentState);
		TypeContext _prevctx = _localctx;
		int _startState = 160;
		enterRecursionRule(_localctx, 160, RULE_type, _p);
		int _la;
		try {
			int _alt;
			enterOuterAlt(_localctx, 1);
			{
			setState(1412);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case GLOBAL:
			case IDENTIFIER:
			case AT:
				{
				_localctx = new NameTypeContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;

				setState(1400);
				name(0);
				}
				break;
			case BOOL:
			case BOOL2:
			case BOOL3:
			case BOOL4:
			case INT:
			case INT2:
			case INT3:
			case INT4:
			case UINT:
			case UINT2:
			case UINT3:
			case UINT4:
			case FLOAT:
			case FLOAT2:
			case FLOAT3:
			case FLOAT4:
			case DOUBLE:
			case DOUBLE2:
			case DOUBLE3:
			case DOUBLE4:
			case MAT2:
			case MAT2X3:
			case MAT2X4:
			case MAT3:
			case MAT3X2:
			case MAT3X4:
			case MAT4:
			case MAT4X2:
			case MAT4X3:
			case MAT4X4:
				{
				_localctx = new PredefinedTypeContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1401);
				((PredefinedTypeContext)_localctx).pType = _input.LT(1);
				_la = _input.LA(1);
				if ( !((((_la) & ~0x3f) == 0 && ((1L << _la) & 288230375614840832L) != 0) || _la==MAT4X4) ) {
					((PredefinedTypeContext)_localctx).pType = (Token)_errHandler.recoverInline(this);
				}
				else {
					if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
					_errHandler.reportMatch(this);
					consume();
				}
				}
				break;
			case OPEN_PARENS:
				{
				_localctx = new TupleTypeContext(_localctx);
				_ctx = _localctx;
				_prevctx = _localctx;
				setState(1402);
				match(OPEN_PARENS);
				setState(1403);
				tuple_element();
				setState(1406); 
				_errHandler.sync(this);
				_la = _input.LA(1);
				do {
					{
					{
					setState(1404);
					match(COMMA);
					setState(1405);
					tuple_element();
					}
					}
					setState(1408); 
					_errHandler.sync(this);
					_la = _input.LA(1);
				} while ( _la==COMMA );
				setState(1410);
				match(CLOSE_PARENS);
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
			_ctx.stop = _input.LT(-1);
			setState(1422);
			_errHandler.sync(this);
			_alt = getInterpreter().adaptivePredict(_input,191,_ctx);
			while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER ) {
				if ( _alt==1 ) {
					if ( _parseListeners!=null ) triggerExitRuleEvent();
					_prevctx = _localctx;
					{
					{
					_localctx = new ArrayTypeContext(new TypeContext(_parentctx, _parentState));
					pushNewRecursionContext(_localctx, _startState, RULE_type);
					setState(1414);
					if (!(precpred(_ctx, 4))) throw new FailedPredicateException(this, "precpred(_ctx, 4)");
					setState(1416); 
					_errHandler.sync(this);
					_alt = 1;
					do {
						switch (_alt) {
						case 1:
							{
							{
							setState(1415);
							array_rank_specifier();
							}
							}
							break;
						default:
							throw new NoViableAltException(this);
						}
						setState(1418); 
						_errHandler.sync(this);
						_alt = getInterpreter().adaptivePredict(_input,190,_ctx);
					} while ( _alt!=2 && _alt!=org.antlr.v4.runtime.atn.ATN.INVALID_ALT_NUMBER );
					}
					} 
				}
				setState(1424);
				_errHandler.sync(this);
				_alt = getInterpreter().adaptivePredict(_input,191,_ctx);
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			unrollRecursionContexts(_parentctx);
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Tuple_elementContext extends ParserRuleContext {
		public TypeContext type() {
			return getRuleContext(TypeContext.class,0);
		}
		public Identifier_tokenContext identifier_token() {
			return getRuleContext(Identifier_tokenContext.class,0);
		}
		public Tuple_elementContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_tuple_element; }
	}

	public final Tuple_elementContext tuple_element() throws RecognitionException {
		Tuple_elementContext _localctx = new Tuple_elementContext(_ctx, getState());
		enterRule(_localctx, 162, RULE_tuple_element);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1425);
			type(0);
			setState(1427);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==IDENTIFIER || _la==AT) {
				{
				setState(1426);
				identifier_token();
				}
			}

			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Array_rank_specifierContext extends ParserRuleContext {
		public TerminalNode OPEN_BRACKET() { return getToken(RavenParser2.OPEN_BRACKET, 0); }
		public TerminalNode CLOSE_BRACKET() { return getToken(RavenParser2.CLOSE_BRACKET, 0); }
		public List<ExpressionContext> expression() {
			return getRuleContexts(ExpressionContext.class);
		}
		public ExpressionContext expression(int i) {
			return getRuleContext(ExpressionContext.class,i);
		}
		public List<TerminalNode> COMMA() { return getTokens(RavenParser2.COMMA); }
		public TerminalNode COMMA(int i) {
			return getToken(RavenParser2.COMMA, i);
		}
		public Array_rank_specifierContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_array_rank_specifier; }
	}

	public final Array_rank_specifierContext array_rank_specifier() throws RecognitionException {
		Array_rank_specifierContext _localctx = new Array_rank_specifierContext(_ctx, getState());
		enterRule(_localctx, 164, RULE_array_rank_specifier);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1429);
			match(OPEN_BRACKET);
			setState(1438);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if ((((_la) & ~0x3f) == 0 && ((1L << _la) & -8358680908934413824L) != 0) || ((((_la - 67)) & ~0x3f) == 0 && ((1L << (_la - 67)) & 7044052759682763329L) != 0) || _la==MAT4X4) {
				{
				setState(1430);
				expression(0);
				setState(1435);
				_errHandler.sync(this);
				_la = _input.LA(1);
				while (_la==COMMA) {
					{
					{
					setState(1431);
					match(COMMA);
					setState(1432);
					expression(0);
					}
					}
					setState(1437);
					_errHandler.sync(this);
					_la = _input.LA(1);
				}
				}
			}

			setState(1440);
			match(CLOSE_BRACKET);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Identifier_tokenContext extends ParserRuleContext {
		public TerminalNode IDENTIFIER() { return getToken(RavenParser2.IDENTIFIER, 0); }
		public TerminalNode AT() { return getToken(RavenParser2.AT, 0); }
		public Identifier_tokenContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_identifier_token; }
	}

	public final Identifier_tokenContext identifier_token() throws RecognitionException {
		Identifier_tokenContext _localctx = new Identifier_tokenContext(_ctx, getState());
		enterRule(_localctx, 166, RULE_identifier_token);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1443);
			_errHandler.sync(this);
			_la = _input.LA(1);
			if (_la==AT) {
				{
				setState(1442);
				match(AT);
				}
			}

			setState(1445);
			match(IDENTIFIER);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Numeric_literal_tokenContext extends ParserRuleContext {
		public Integer_literal_tokenContext integer_literal_token() {
			return getRuleContext(Integer_literal_tokenContext.class,0);
		}
		public Real_literal_tokenContext real_literal_token() {
			return getRuleContext(Real_literal_tokenContext.class,0);
		}
		public Numeric_literal_tokenContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_numeric_literal_token; }
	}

	public final Numeric_literal_tokenContext numeric_literal_token() throws RecognitionException {
		Numeric_literal_tokenContext _localctx = new Numeric_literal_tokenContext(_ctx, getState());
		enterRule(_localctx, 168, RULE_numeric_literal_token);
		try {
			setState(1449);
			_errHandler.sync(this);
			switch (_input.LA(1)) {
			case INTEGER_LITERAL:
			case HEX_INTEGER_LITERAL:
			case BIN_INTEGER_LITERAL:
				enterOuterAlt(_localctx, 1);
				{
				setState(1447);
				integer_literal_token();
				}
				break;
			case REAL_LITERAL:
				enterOuterAlt(_localctx, 2);
				{
				setState(1448);
				real_literal_token();
				}
				break;
			default:
				throw new NoViableAltException(this);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Real_literal_tokenContext extends ParserRuleContext {
		public TerminalNode REAL_LITERAL() { return getToken(RavenParser2.REAL_LITERAL, 0); }
		public Real_literal_tokenContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_real_literal_token; }
	}

	public final Real_literal_tokenContext real_literal_token() throws RecognitionException {
		Real_literal_tokenContext _localctx = new Real_literal_tokenContext(_ctx, getState());
		enterRule(_localctx, 170, RULE_real_literal_token);
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1451);
			match(REAL_LITERAL);
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class Integer_literal_tokenContext extends ParserRuleContext {
		public TerminalNode INTEGER_LITERAL() { return getToken(RavenParser2.INTEGER_LITERAL, 0); }
		public TerminalNode HEX_INTEGER_LITERAL() { return getToken(RavenParser2.HEX_INTEGER_LITERAL, 0); }
		public TerminalNode BIN_INTEGER_LITERAL() { return getToken(RavenParser2.BIN_INTEGER_LITERAL, 0); }
		public Integer_literal_tokenContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_integer_literal_token; }
	}

	public final Integer_literal_tokenContext integer_literal_token() throws RecognitionException {
		Integer_literal_tokenContext _localctx = new Integer_literal_tokenContext(_ctx, getState());
		enterRule(_localctx, 172, RULE_integer_literal_token);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1453);
			_la = _input.LA(1);
			if ( !(((((_la - 96)) & ~0x3f) == 0 && ((1L << (_la - 96)) & 7L) != 0)) ) {
			_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	@SuppressWarnings("CheckReturnValue")
	public static class ModifierContext extends ParserRuleContext {
		public TerminalNode ABSTRACT() { return getToken(RavenParser2.ABSTRACT, 0); }
		public TerminalNode CONST() { return getToken(RavenParser2.CONST, 0); }
		public TerminalNode OVERRIDE() { return getToken(RavenParser2.OVERRIDE, 0); }
		public TerminalNode PARTIAL() { return getToken(RavenParser2.PARTIAL, 0); }
		public TerminalNode PRIVATE() { return getToken(RavenParser2.PRIVATE, 0); }
		public TerminalNode PROTECTED() { return getToken(RavenParser2.PROTECTED, 0); }
		public TerminalNode PUBLIC() { return getToken(RavenParser2.PUBLIC, 0); }
		public TerminalNode READONLY() { return getToken(RavenParser2.READONLY, 0); }
		public TerminalNode STATIC() { return getToken(RavenParser2.STATIC, 0); }
		public ModifierContext(ParserRuleContext parent, int invokingState) {
			super(parent, invokingState);
		}
		@Override public int getRuleIndex() { return RULE_modifier; }
	}

	public final ModifierContext modifier() throws RecognitionException {
		ModifierContext _localctx = new ModifierContext(_ctx, getState());
		enterRule(_localctx, 174, RULE_modifier);
		int _la;
		try {
			enterOuterAlt(_localctx, 1);
			{
			setState(1455);
			_la = _input.LA(1);
			if ( !(((((_la - 85)) & ~0x3f) == 0 && ((1L << (_la - 85)) & 511L) != 0)) ) {
			_errHandler.recoverInline(this);
			}
			else {
				if ( _input.LA(1)==Token.EOF ) matchedEOF = true;
				_errHandler.reportMatch(this);
				consume();
			}
			}
		}
		catch (RecognitionException re) {
			_localctx.exception = re;
			_errHandler.reportError(this, re);
			_errHandler.recover(this, re);
		}
		finally {
			exitRule();
		}
		return _localctx;
	}

	public boolean sempred(RuleContext _localctx, int ruleIndex, int predIndex) {
		switch (ruleIndex) {
		case 10:
			return name_sempred((NameContext)_localctx, predIndex);
		case 71:
			return expression_sempred((ExpressionContext)_localctx, predIndex);
		case 77:
			return pattern_sempred((PatternContext)_localctx, predIndex);
		case 80:
			return type_sempred((TypeContext)_localctx, predIndex);
		}
		return true;
	}
	private boolean name_sempred(NameContext _localctx, int predIndex) {
		switch (predIndex) {
		case 0:
			return precpred(_ctx, 2);
		}
		return true;
	}
	private boolean expression_sempred(ExpressionContext _localctx, int predIndex) {
		switch (predIndex) {
		case 1:
			return precpred(_ctx, 25);
		case 2:
			return precpred(_ctx, 24);
		case 3:
			return precpred(_ctx, 21);
		case 4:
			return precpred(_ctx, 20);
		case 5:
			return precpred(_ctx, 6);
		case 6:
			return precpred(_ctx, 17);
		case 7:
			return precpred(_ctx, 14);
		case 8:
			return precpred(_ctx, 13);
		case 9:
			return precpred(_ctx, 11);
		case 10:
			return precpred(_ctx, 8);
		case 11:
			return precpred(_ctx, 3);
		}
		return true;
	}
	private boolean pattern_sempred(PatternContext _localctx, int predIndex) {
		switch (predIndex) {
		case 12:
			return precpred(_ctx, 9);
		}
		return true;
	}
	private boolean type_sempred(TypeContext _localctx, int predIndex) {
		switch (predIndex) {
		case 13:
			return precpred(_ctx, 4);
		}
		return true;
	}

	public static final String _serializedATN =
		"\u0004\u0001\u00c2\u05b2\u0002\u0000\u0007\u0000\u0002\u0001\u0007\u0001"+
		"\u0002\u0002\u0007\u0002\u0002\u0003\u0007\u0003\u0002\u0004\u0007\u0004"+
		"\u0002\u0005\u0007\u0005\u0002\u0006\u0007\u0006\u0002\u0007\u0007\u0007"+
		"\u0002\b\u0007\b\u0002\t\u0007\t\u0002\n\u0007\n\u0002\u000b\u0007\u000b"+
		"\u0002\f\u0007\f\u0002\r\u0007\r\u0002\u000e\u0007\u000e\u0002\u000f\u0007"+
		"\u000f\u0002\u0010\u0007\u0010\u0002\u0011\u0007\u0011\u0002\u0012\u0007"+
		"\u0012\u0002\u0013\u0007\u0013\u0002\u0014\u0007\u0014\u0002\u0015\u0007"+
		"\u0015\u0002\u0016\u0007\u0016\u0002\u0017\u0007\u0017\u0002\u0018\u0007"+
		"\u0018\u0002\u0019\u0007\u0019\u0002\u001a\u0007\u001a\u0002\u001b\u0007"+
		"\u001b\u0002\u001c\u0007\u001c\u0002\u001d\u0007\u001d\u0002\u001e\u0007"+
		"\u001e\u0002\u001f\u0007\u001f\u0002 \u0007 \u0002!\u0007!\u0002\"\u0007"+
		"\"\u0002#\u0007#\u0002$\u0007$\u0002%\u0007%\u0002&\u0007&\u0002\'\u0007"+
		"\'\u0002(\u0007(\u0002)\u0007)\u0002*\u0007*\u0002+\u0007+\u0002,\u0007"+
		",\u0002-\u0007-\u0002.\u0007.\u0002/\u0007/\u00020\u00070\u00021\u0007"+
		"1\u00022\u00072\u00023\u00073\u00024\u00074\u00025\u00075\u00026\u0007"+
		"6\u00027\u00077\u00028\u00078\u00029\u00079\u0002:\u0007:\u0002;\u0007"+
		";\u0002<\u0007<\u0002=\u0007=\u0002>\u0007>\u0002?\u0007?\u0002@\u0007"+
		"@\u0002A\u0007A\u0002B\u0007B\u0002C\u0007C\u0002D\u0007D\u0002E\u0007"+
		"E\u0002F\u0007F\u0002G\u0007G\u0002H\u0007H\u0002I\u0007I\u0002J\u0007"+
		"J\u0002K\u0007K\u0002L\u0007L\u0002M\u0007M\u0002N\u0007N\u0002O\u0007"+
		"O\u0002P\u0007P\u0002Q\u0007Q\u0002R\u0007R\u0002S\u0007S\u0002T\u0007"+
		"T\u0002U\u0007U\u0002V\u0007V\u0002W\u0007W\u0001\u0000\u0001\u0000\u0005"+
		"\u0000\u00b3\b\u0000\n\u0000\f\u0000\u00b6\t\u0000\u0001\u0000\u0005\u0000"+
		"\u00b9\b\u0000\n\u0000\f\u0000\u00bc\t\u0000\u0001\u0000\u0001\u0000\u0001"+
		"\u0001\u0001\u0001\u0001\u0001\u0004\u0001\u00c3\b\u0001\u000b\u0001\f"+
		"\u0001\u00c4\u0001\u0002\u0003\u0002\u00c8\b\u0002\u0001\u0002\u0001\u0002"+
		"\u0003\u0002\u00cc\b\u0002\u0001\u0002\u0001\u0002\u0004\u0002\u00d0\b"+
		"\u0002\u000b\u0002\f\u0002\u00d1\u0001\u0003\u0001\u0003\u0003\u0003\u00d6"+
		"\b\u0003\u0001\u0003\u0001\u0003\u0001\u0003\u0005\u0003\u00db\b\u0003"+
		"\n\u0003\f\u0003\u00de\t\u0003\u0001\u0003\u0001\u0003\u0004\u0003\u00e2"+
		"\b\u0003\u000b\u0003\f\u0003\u00e3\u0001\u0004\u0003\u0004\u00e7\b\u0004"+
		"\u0001\u0004\u0003\u0004\u00ea\b\u0004\u0003\u0004\u00ec\b\u0004\u0001"+
		"\u0004\u0001\u0004\u0001\u0005\u0001\u0005\u0003\u0005\u00f2\b\u0005\u0001"+
		"\u0006\u0001\u0006\u0001\u0006\u0001\u0006\u0005\u0006\u00f8\b\u0006\n"+
		"\u0006\f\u0006\u00fb\t\u0006\u0003\u0006\u00fd\b\u0006\u0001\u0006\u0001"+
		"\u0006\u0001\u0007\u0003\u0007\u0102\b\u0007\u0001\u0007\u0001\u0007\u0001"+
		"\b\u0001\b\u0001\b\u0001\b\u0005\b\u010a\b\b\n\b\f\b\u010d\t\b\u0003\b"+
		"\u010f\b\b\u0001\b\u0001\b\u0001\t\u0005\t\u0114\b\t\n\t\f\t\u0117\t\t"+
		"\u0001\t\u0005\t\u011a\b\t\n\t\f\t\u011d\t\t\u0001\t\u0001\t\u0001\t\u0003"+
		"\t\u0122\b\t\u0001\t\u0003\t\u0125\b\t\u0001\n\u0001\n\u0001\n\u0001\n"+
		"\u0001\n\u0001\n\u0003\n\u012d\b\n\u0001\n\u0001\n\u0001\n\u0005\n\u0132"+
		"\b\n\n\n\f\n\u0135\t\n\u0001\u000b\u0001\u000b\u0003\u000b\u0139\b\u000b"+
		"\u0001\f\u0001\f\u0001\f\u0001\r\u0001\r\u0001\r\u0001\r\u0005\r\u0142"+
		"\b\r\n\r\f\r\u0145\t\r\u0003\r\u0147\b\r\u0001\r\u0001\r\u0001\u000e\u0001"+
		"\u000e\u0001\u000e\u0001\u000f\u0001\u000f\u0003\u000f\u0150\b\u000f\u0001"+
		"\u0010\u0001\u0010\u0005\u0010\u0154\b\u0010\n\u0010\f\u0010\u0157\t\u0010"+
		"\u0001\u0010\u0001\u0010\u0005\u0010\u015b\b\u0010\n\u0010\f\u0010\u015e"+
		"\t\u0010\u0001\u0010\u0001\u0010\u0005\u0010\u0162\b\u0010\n\u0010\f\u0010"+
		"\u0165\t\u0010\u0001\u0010\u0001\u0010\u0005\u0010\u0169\b\u0010\n\u0010"+
		"\f\u0010\u016c\t\u0010\u0003\u0010\u016e\b\u0010\u0001\u0011\u0001\u0011"+
		"\u0003\u0011\u0172\b\u0011\u0001\u0012\u0005\u0012\u0175\b\u0012\n\u0012"+
		"\f\u0012\u0178\t\u0012\u0001\u0012\u0005\u0012\u017b\b\u0012\n\u0012\f"+
		"\u0012\u017e\t\u0012\u0001\u0012\u0001\u0012\u0004\u0012\u0182\b\u0012"+
		"\u000b\u0012\f\u0012\u0183\u0001\u0013\u0001\u0013\u0001\u0013\u0001\u0013"+
		"\u0001\u0013\u0003\u0013\u018b\b\u0013\u0001\u0014\u0005\u0014\u018e\b"+
		"\u0014\n\u0014\f\u0014\u0191\t\u0014\u0001\u0014\u0005\u0014\u0194\b\u0014"+
		"\n\u0014\f\u0014\u0197\t\u0014\u0001\u0014\u0001\u0014\u0001\u0014\u0003"+
		"\u0014\u019c\b\u0014\u0001\u0014\u0001\u0014\u0001\u0014\u0001\u0014\u0003"+
		"\u0014\u01a2\b\u0014\u0001\u0015\u0001\u0015\u0001\u0015\u0001\u0015\u0001"+
		"\u0016\u0005\u0016\u01a9\b\u0016\n\u0016\f\u0016\u01ac\t\u0016\u0001\u0016"+
		"\u0005\u0016\u01af\b\u0016\n\u0016\f\u0016\u01b2\t\u0016\u0001\u0016\u0001"+
		"\u0016\u0001\u0016\u0001\u0016\u0001\u0016\u0001\u0016\u0001\u0016\u0003"+
		"\u0016\u01bb\b\u0016\u0001\u0017\u0005\u0017\u01be\b\u0017\n\u0017\f\u0017"+
		"\u01c1\t\u0017\u0001\u0017\u0005\u0017\u01c4\b\u0017\n\u0017\f\u0017\u01c7"+
		"\t\u0017\u0001\u0017\u0001\u0017\u0003\u0017\u01cb\b\u0017\u0001\u0017"+
		"\u0001\u0017\u0003\u0017\u01cf\b\u0017\u0001\u0017\u0001\u0017\u0005\u0017"+
		"\u01d3\b\u0017\n\u0017\f\u0017\u01d6\t\u0017\u0001\u0017\u0001\u0017\u0003"+
		"\u0017\u01da\b\u0017\u0001\u0017\u0001\u0017\u0001\u0017\u0001\u0017\u0003"+
		"\u0017\u01e0\b\u0017\u0001\u0018\u0001\u0018\u0001\u0018\u0001\u0019\u0005"+
		"\u0019\u01e6\b\u0019\n\u0019\f\u0019\u01e9\t\u0019\u0001\u0019\u0005\u0019"+
		"\u01ec\b\u0019\n\u0019\f\u0019\u01ef\t\u0019\u0001\u0019\u0001\u0019\u0003"+
		"\u0019\u01f3\b\u0019\u0001\u0019\u0001\u0019\u0001\u0019\u0003\u0019\u01f8"+
		"\b\u0019\u0001\u0019\u0001\u0019\u0001\u0019\u0003\u0019\u01fd\b\u0019"+
		"\u0001\u0019\u0001\u0019\u0003\u0019\u0201\b\u0019\u0001\u001a\u0001\u001a"+
		"\u0005\u001a\u0205\b\u001a\n\u001a\f\u001a\u0208\t\u001a\u0001\u001a\u0005"+
		"\u001a\u020b\b\u001a\n\u001a\f\u001a\u020e\t\u001a\u0001\u001a\u0005\u001a"+
		"\u0211\b\u001a\n\u001a\f\u001a\u0214\t\u001a\u0001\u001a\u0001\u001a\u0001"+
		"\u001b\u0005\u001b\u0219\b\u001b\n\u001b\f\u001b\u021c\t\u001b\u0001\u001b"+
		"\u0005\u001b\u021f\b\u001b\n\u001b\f\u001b\u0222\t\u001b\u0001\u001b\u0001"+
		"\u001b\u0001\u001b\u0001\u001b\u0001\u001b\u0003\u001b\u0229\b\u001b\u0001"+
		"\u001b\u0005\u001b\u022c\b\u001b\n\u001b\f\u001b\u022f\t\u001b\u0001\u001c"+
		"\u0005\u001c\u0232\b\u001c\n\u001c\f\u001c\u0235\t\u001c\u0001\u001c\u0005"+
		"\u001c\u0238\b\u001c\n\u001c\f\u001c\u023b\t\u001c\u0001\u001c\u0001\u001c"+
		"\u0003\u001c\u023f\b\u001c\u0001\u001c\u0001\u001c\u0001\u001c\u0001\u001c"+
		"\u0001\u001c\u0001\u001c\u0003\u001c\u0247\b\u001c\u0001\u001d\u0001\u001d"+
		"\u0001\u001d\u0001\u001d\u0005\u001d\u024d\b\u001d\n\u001d\f\u001d\u0250"+
		"\t\u001d\u0001\u001d\u0001\u001d\u0001\u001e\u0005\u001e\u0255\b\u001e"+
		"\n\u001e\f\u001e\u0258\t\u001e\u0001\u001e\u0005\u001e\u025b\b\u001e\n"+
		"\u001e\f\u001e\u025e\t\u001e\u0001\u001e\u0001\u001e\u0003\u001e\u0262"+
		"\b\u001e\u0001\u001e\u0001\u001e\u0001\u001e\u0001\u001e\u0001\u001e\u0001"+
		"\u001e\u0001\u001e\u0003\u001e\u026b\b\u001e\u0001\u001f\u0005\u001f\u026e"+
		"\b\u001f\n\u001f\f\u001f\u0271\t\u001f\u0001\u001f\u0005\u001f\u0274\b"+
		"\u001f\n\u001f\f\u001f\u0277\t\u001f\u0001\u001f\u0001\u001f\u0003\u001f"+
		"\u027b\b\u001f\u0001\u001f\u0001\u001f\u0001\u001f\u0001\u001f\u0001\u001f"+
		"\u0001\u001f\u0001\u001f\u0003\u001f\u0284\b\u001f\u0001 \u0001 \u0003"+
		" \u0288\b \u0001!\u0001!\u0003!\u028c\b!\u0001\"\u0005\"\u028f\b\"\n\""+
		"\f\"\u0292\t\"\u0001\"\u0005\"\u0295\b\"\n\"\f\"\u0298\t\"\u0001\"\u0001"+
		"\"\u0001\"\u0003\"\u029d\b\"\u0001\"\u0003\"\u02a0\b\"\u0001\"\u0003\""+
		"\u02a3\b\"\u0001\"\u0005\"\u02a6\b\"\n\"\f\"\u02a9\t\"\u0001\"\u0001\""+
		"\u0004\"\u02ad\b\"\u000b\"\f\"\u02ae\u0001\"\u0005\"\u02b2\b\"\n\"\f\""+
		"\u02b5\t\"\u0001\"\u0004\"\u02b8\b\"\u000b\"\f\"\u02b9\u0001\"\u0003\""+
		"\u02bd\b\"\u0001\"\u0001\"\u0001#\u0005#\u02c2\b#\n#\f#\u02c5\t#\u0001"+
		"#\u0005#\u02c8\b#\n#\f#\u02cb\t#\u0001#\u0001#\u0001#\u0003#\u02d0\b#"+
		"\u0001#\u0003#\u02d3\b#\u0001#\u0003#\u02d6\b#\u0001#\u0005#\u02d9\b#"+
		"\n#\f#\u02dc\t#\u0001#\u0001#\u0004#\u02e0\b#\u000b#\f#\u02e1\u0001#\u0005"+
		"#\u02e5\b#\n#\f#\u02e8\t#\u0001#\u0004#\u02eb\b#\u000b#\f#\u02ec\u0001"+
		"#\u0003#\u02f0\b#\u0001#\u0001#\u0001$\u0005$\u02f5\b$\n$\f$\u02f8\t$"+
		"\u0001$\u0005$\u02fb\b$\n$\f$\u02fe\t$\u0001$\u0001$\u0001$\u0003$\u0303"+
		"\b$\u0001$\u0001$\u0004$\u0307\b$\u000b$\f$\u0308\u0001$\u0001$\u0001"+
		"$\u0005$\u030e\b$\n$\f$\u0311\t$\u0003$\u0313\b$\u0001$\u0001$\u0001$"+
		"\u0001%\u0005%\u0319\b%\n%\f%\u031c\t%\u0001%\u0005%\u031f\b%\n%\f%\u0322"+
		"\t%\u0001%\u0001%\u0003%\u0326\b%\u0001&\u0001&\u0001&\u0001&\u0005&\u032c"+
		"\b&\n&\f&\u032f\t&\u0001&\u0001&\u0001\'\u0005\'\u0334\b\'\n\'\f\'\u0337"+
		"\t\'\u0001\'\u0003\'\u033a\b\'\u0001\'\u0001\'\u0001(\u0001(\u0001(\u0001"+
		"(\u0001(\u0001(\u0005(\u0344\b(\n(\f(\u0347\t(\u0001)\u0001)\u0003)\u034b"+
		"\b)\u0001*\u0001*\u0001*\u0001*\u0005*\u0351\b*\n*\f*\u0354\t*\u0001+"+
		"\u0001+\u0003+\u0358\b+\u0001,\u0001,\u0001,\u0001-\u0001-\u0001.\u0001"+
		".\u0001.\u0001.\u0003.\u0363\b.\u0001.\u0003.\u0366\b.\u0001/\u0001/\u0001"+
		"/\u0001/\u0005/\u036c\b/\n/\f/\u036f\t/\u0003/\u0371\b/\u0001/\u0001/"+
		"\u00010\u00030\u0376\b0\u00010\u00030\u0379\b0\u00010\u00010\u00011\u0001"+
		"1\u00011\u00011\u00051\u0381\b1\n1\f1\u0384\t1\u00011\u00011\u00012\u0001"+
		"2\u00052\u038a\b2\n2\f2\u038d\t2\u00012\u00052\u0390\b2\n2\f2\u0393\t"+
		"2\u00012\u00052\u0396\b2\n2\f2\u0399\t2\u00012\u00012\u00013\u00013\u0001"+
		"3\u00013\u00013\u00013\u00013\u00013\u00013\u00013\u00013\u00013\u0001"+
		"3\u00013\u00033\u03ab\b3\u00014\u00054\u03ae\b4\n4\f4\u03b1\t4\u00014"+
		"\u00014\u00044\u03b5\b4\u000b4\f4\u03b6\u00015\u00055\u03ba\b5\n5\f5\u03bd"+
		"\t5\u00015\u00015\u00045\u03c1\b5\u000b5\f5\u03c2\u00016\u00056\u03c6"+
		"\b6\n6\f6\u03c9\t6\u00016\u00016\u00016\u00016\u00016\u00016\u00016\u0004"+
		"6\u03d2\b6\u000b6\f6\u03d3\u00017\u00057\u03d7\b7\n7\f7\u03da\t7\u0001"+
		"7\u00047\u03dd\b7\u000b7\f7\u03de\u00018\u00058\u03e2\b8\n8\f8\u03e5\t"+
		"8\u00018\u00018\u00048\u03e9\b8\u000b8\f8\u03ea\u00019\u00059\u03ee\b"+
		"9\n9\f9\u03f1\t9\u00019\u00019\u00019\u00019\u00019\u00019\u00019\u0001"+
		"9\u0001:\u0005:\u03fc\b:\n:\f:\u03ff\t:\u0001:\u0001:\u0001:\u0001:\u0001"+
		":\u0001:\u0003:\u0407\b:\u0001;\u0001;\u0001;\u0001<\u0005<\u040d\b<\n"+
		"<\f<\u0410\t<\u0001<\u0001<\u0003<\u0414\b<\u0001<\u0001<\u0001=\u0005"+
		"=\u0419\b=\n=\f=\u041c\t=\u0001=\u0005=\u041f\b=\n=\f=\u0422\t=\u0001"+
		"=\u0001=\u0001=\u0003=\u0427\b=\u0001=\u0001=\u0005=\u042b\b=\n=\f=\u042e"+
		"\t=\u0001=\u0001=\u0003=\u0432\b=\u0001=\u0001=\u0001=\u0001=\u0003=\u0438"+
		"\b=\u0001>\u0005>\u043b\b>\n>\f>\u043e\t>\u0001>\u0003>\u0441\b>\u0001"+
		">\u0005>\u0444\b>\n>\f>\u0447\t>\u0001>\u0001>\u0001>\u0001?\u0005?\u044d"+
		"\b?\n?\f?\u0450\t?\u0001?\u0001?\u0001?\u0001?\u0001?\u0001?\u0001@\u0005"+
		"@\u0459\b@\n@\f@\u045c\t@\u0001@\u0001@\u0001@\u0001@\u0003@\u0462\b@"+
		"\u0001@\u0001@\u0001@\u0001A\u0005A\u0468\bA\nA\fA\u046b\tA\u0001A\u0001"+
		"A\u0003A\u046f\bA\u0001A\u0001A\u0003A\u0473\bA\u0001A\u0001A\u0005A\u0477"+
		"\bA\nA\fA\u047a\tA\u0001A\u0001A\u0001B\u0004B\u047f\bB\u000bB\fB\u0480"+
		"\u0001B\u0004B\u0484\bB\u000bB\fB\u0485\u0001C\u0001C\u0001C\u0003C\u048b"+
		"\bC\u0001D\u0001D\u0001D\u0003D\u0490\bD\u0001D\u0001D\u0001E\u0001E\u0001"+
		"E\u0001E\u0001F\u0001F\u0001F\u0001G\u0001G\u0001G\u0001G\u0001G\u0001"+
		"G\u0001G\u0001G\u0005G\u04a3\bG\nG\fG\u04a6\tG\u0001G\u0001G\u0001G\u0005"+
		"G\u04ab\bG\nG\fG\u04ae\tG\u0001G\u0005G\u04b1\bG\nG\fG\u04b4\tG\u0003"+
		"G\u04b6\bG\u0001G\u0005G\u04b9\bG\nG\fG\u04bc\tG\u0001G\u0001G\u0001G"+
		"\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001"+
		"G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001"+
		"G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0004G\u04dd"+
		"\bG\u000bG\fG\u04de\u0001G\u0003G\u04e2\bG\u0001G\u0003G\u04e5\bG\u0001"+
		"G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001"+
		"G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001"+
		"G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001G\u0001"+
		"G\u0001G\u0001G\u0001G\u0004G\u0509\bG\u000bG\fG\u050a\u0001G\u0001G\u0001"+
		"G\u0005G\u0510\bG\nG\fG\u0513\tG\u0003G\u0515\bG\u0001G\u0004G\u0518\b"+
		"G\u000bG\fG\u0519\u0001G\u0005G\u051d\bG\nG\fG\u0520\tG\u0001H\u0001H"+
		"\u0001H\u0001H\u0001H\u0003H\u0527\bH\u0001I\u0001I\u0001I\u0001J\u0001"+
		"J\u0001J\u0001K\u0001K\u0001K\u0003K\u0532\bK\u0001L\u0001L\u0003L\u0536"+
		"\bL\u0001L\u0001L\u0001L\u0001M\u0001M\u0001M\u0001M\u0001M\u0001M\u0001"+
		"M\u0005M\u0542\bM\nM\fM\u0545\tM\u0003M\u0547\bM\u0001M\u0001M\u0003M"+
		"\u054b\bM\u0001M\u0001M\u0001M\u0001M\u0001M\u0001M\u0001M\u0001M\u0003"+
		"M\u0555\bM\u0001M\u0001M\u0001M\u0001M\u0003M\u055b\bM\u0001M\u0001M\u0001"+
		"M\u0005M\u0560\bM\nM\fM\u0563\tM\u0001N\u0001N\u0001N\u0001N\u0001N\u0005"+
		"N\u056a\bN\nN\fN\u056d\tN\u0003N\u056f\bN\u0001N\u0001N\u0003N\u0573\b"+
		"N\u0001O\u0001O\u0001O\u0001P\u0001P\u0001P\u0001P\u0001P\u0001P\u0001"+
		"P\u0004P\u057f\bP\u000bP\fP\u0580\u0001P\u0001P\u0003P\u0585\bP\u0001"+
		"P\u0001P\u0004P\u0589\bP\u000bP\fP\u058a\u0005P\u058d\bP\nP\fP\u0590\t"+
		"P\u0001Q\u0001Q\u0003Q\u0594\bQ\u0001R\u0001R\u0001R\u0001R\u0005R\u059a"+
		"\bR\nR\fR\u059d\tR\u0003R\u059f\bR\u0001R\u0001R\u0001S\u0003S\u05a4\b"+
		"S\u0001S\u0001S\u0001T\u0001T\u0003T\u05aa\bT\u0001U\u0001U\u0001V\u0001"+
		"V\u0001W\u0001W\u0001W\u0000\u0004\u0014\u008e\u009a\u00a0X\u0000\u0002"+
		"\u0004\u0006\b\n\f\u000e\u0010\u0012\u0014\u0016\u0018\u001a\u001c\u001e"+
		" \"$&(*,.02468:<>@BDFHJLNPRTVXZ\\^`bdfhjlnprtvxz|~\u0080\u0082\u0084\u0086"+
		"\u0088\u008a\u008c\u008e\u0090\u0092\u0094\u0096\u0098\u009a\u009c\u009e"+
		"\u00a0\u00a2\u00a4\u00a6\u00a8\u00aa\u00ac\u00ae\u0000\u0010\u0002\u0000"+
		"\u0015\u0015;;\u0001\u0000\u000e\u0011\u0002\u0000BBFF\n\u0000CCHHQQr"+
		"{}~\u0080\u0081\u0084\u0087\u0090\u0090\u0093\u0093\u0095\u0095\u0002"+
		"\u0000GGKK\u0001\u0000\u0017\u0018\u0002\u0000GGKL\u0003\u0000rsy{\u0080"+
		"\u0081\u0005\u0000||\u0088\u008f\u0091\u0092\u0094\u0094\u0096\u0096\b"+
		"\u0000::HHry}\u007f\u0082\u0087\u0090\u0090\u0093\u0093\u0095\u0095\u0002"+
		"\u0000zz\u0080\u0081\u0002\u0000}~\u0084\u0087\u0001\u0000\n\u000b\u0002"+
		"\u0000\u001d9\u00c2\u00c2\u0001\u0000`b\u0001\u0000U]\u0650\u0000\u00b0"+
		"\u0001\u0000\u0000\u0000\u0002\u00bf\u0001\u0000\u0000\u0000\u0004\u00c7"+
		"\u0001\u0000\u0000\u0000\u0006\u00d3\u0001\u0000\u0000\u0000\b\u00eb\u0001"+
		"\u0000\u0000\u0000\n\u00ef\u0001\u0000\u0000\u0000\f\u00f3\u0001\u0000"+
		"\u0000\u0000\u000e\u0101\u0001\u0000\u0000\u0000\u0010\u0105\u0001\u0000"+
		"\u0000\u0000\u0012\u0115\u0001\u0000\u0000\u0000\u0014\u012c\u0001\u0000"+
		"\u0000\u0000\u0016\u0138\u0001\u0000\u0000\u0000\u0018\u013a\u0001\u0000"+
		"\u0000\u0000\u001a\u013d\u0001\u0000\u0000\u0000\u001c\u014a\u0001\u0000"+
		"\u0000\u0000\u001e\u014f\u0001\u0000\u0000\u0000 \u016d\u0001\u0000\u0000"+
		"\u0000\"\u0171\u0001\u0000\u0000\u0000$\u0176\u0001\u0000\u0000\u0000"+
		"&\u018a\u0001\u0000\u0000\u0000(\u018f\u0001\u0000\u0000\u0000*\u01a3"+
		"\u0001\u0000\u0000\u0000,\u01aa\u0001\u0000\u0000\u0000.\u01bf\u0001\u0000"+
		"\u0000\u00000\u01e1\u0001\u0000\u0000\u00002\u01e7\u0001\u0000\u0000\u0000"+
		"4\u0202\u0001\u0000\u0000\u00006\u021a\u0001\u0000\u0000\u00008\u0233"+
		"\u0001\u0000\u0000\u0000:\u0248\u0001\u0000\u0000\u0000<\u0256\u0001\u0000"+
		"\u0000\u0000>\u026f\u0001\u0000\u0000\u0000@\u0287\u0001\u0000\u0000\u0000"+
		"B\u028b\u0001\u0000\u0000\u0000D\u0290\u0001\u0000\u0000\u0000F\u02c3"+
		"\u0001\u0000\u0000\u0000H\u02f6\u0001\u0000\u0000\u0000J\u031a\u0001\u0000"+
		"\u0000\u0000L\u0327\u0001\u0000\u0000\u0000N\u0335\u0001\u0000\u0000\u0000"+
		"P\u033d\u0001\u0000\u0000\u0000R\u034a\u0001\u0000\u0000\u0000T\u034c"+
		"\u0001\u0000\u0000\u0000V\u0357\u0001\u0000\u0000\u0000X\u0359\u0001\u0000"+
		"\u0000\u0000Z\u035c\u0001\u0000\u0000\u0000\\\u035e\u0001\u0000\u0000"+
		"\u0000^\u0367\u0001\u0000\u0000\u0000`\u0375\u0001\u0000\u0000\u0000b"+
		"\u037c\u0001\u0000\u0000\u0000d\u0387\u0001\u0000\u0000\u0000f\u03aa\u0001"+
		"\u0000\u0000\u0000h\u03af\u0001\u0000\u0000\u0000j\u03bb\u0001\u0000\u0000"+
		"\u0000l\u03c7\u0001\u0000\u0000\u0000n\u03d8\u0001\u0000\u0000\u0000p"+
		"\u03e3\u0001\u0000\u0000\u0000r\u03ef\u0001\u0000\u0000\u0000t\u03fd\u0001"+
		"\u0000\u0000\u0000v\u0408\u0001\u0000\u0000\u0000x\u040e\u0001\u0000\u0000"+
		"\u0000z\u041a\u0001\u0000\u0000\u0000|\u043c\u0001\u0000\u0000\u0000~"+
		"\u044e\u0001\u0000\u0000\u0000\u0080\u045a\u0001\u0000\u0000\u0000\u0082"+
		"\u0469\u0001\u0000\u0000\u0000\u0084\u047e\u0001\u0000\u0000\u0000\u0086"+
		"\u048a\u0001\u0000\u0000\u0000\u0088\u048c\u0001\u0000\u0000\u0000\u008a"+
		"\u0493\u0001\u0000\u0000\u0000\u008c\u0497\u0001\u0000\u0000\u0000\u008e"+
		"\u04e4\u0001\u0000\u0000\u0000\u0090\u0526\u0001\u0000\u0000\u0000\u0092"+
		"\u0528\u0001\u0000\u0000\u0000\u0094\u052b\u0001\u0000\u0000\u0000\u0096"+
		"\u0531\u0001\u0000\u0000\u0000\u0098\u0533\u0001\u0000\u0000\u0000\u009a"+
		"\u055a\u0001\u0000\u0000\u0000\u009c\u0572\u0001\u0000\u0000\u0000\u009e"+
		"\u0574\u0001\u0000\u0000\u0000\u00a0\u0584\u0001\u0000\u0000\u0000\u00a2"+
		"\u0591\u0001\u0000\u0000\u0000\u00a4\u0595\u0001\u0000\u0000\u0000\u00a6"+
		"\u05a3\u0001\u0000\u0000\u0000\u00a8\u05a9\u0001\u0000\u0000\u0000\u00aa"+
		"\u05ab\u0001\u0000\u0000\u0000\u00ac\u05ad\u0001\u0000\u0000\u0000\u00ae"+
		"\u05af\u0001\u0000\u0000\u0000\u00b0\u00b4\u0003\u0002\u0001\u0000\u00b1"+
		"\u00b3\u0003\u0004\u0002\u0000\u00b2\u00b1\u0001\u0000\u0000\u0000\u00b3"+
		"\u00b6\u0001\u0000\u0000\u0000\u00b4\u00b2\u0001\u0000\u0000\u0000\u00b4"+
		"\u00b5\u0001\u0000\u0000\u0000\u00b5\u00ba\u0001\u0000\u0000\u0000\u00b6"+
		"\u00b4\u0001\u0000\u0000\u0000\u00b7\u00b9\u0003 \u0010\u0000\u00b8\u00b7"+
		"\u0001\u0000\u0000\u0000\u00b9\u00bc\u0001\u0000\u0000\u0000\u00ba\u00b8"+
		"\u0001\u0000\u0000\u0000\u00ba\u00bb\u0001\u0000\u0000\u0000\u00bb\u00bd"+
		"\u0001\u0000\u0000\u0000\u00bc\u00ba\u0001\u0000\u0000\u0000\u00bd\u00be"+
		"\u0005\u0000\u0000\u0001\u00be\u0001\u0001\u0000\u0000\u0000\u00bf\u00c0"+
		"\u0005\u001b\u0000\u0000\u00c0\u00c2\u0003\u0014\n\u0000\u00c1\u00c3\u0005"+
		"\b\u0000\u0000\u00c2\u00c1\u0001\u0000\u0000\u0000\u00c3\u00c4\u0001\u0000"+
		"\u0000\u0000\u00c4\u00c2\u0001\u0000\u0000\u0000\u00c4\u00c5\u0001\u0000"+
		"\u0000\u0000\u00c5\u0003\u0001\u0000\u0000\u0000\u00c6\u00c8\u0005\t\u0000"+
		"\u0000\u00c7\u00c6\u0001\u0000\u0000\u0000\u00c7\u00c8\u0001\u0000\u0000"+
		"\u0000\u00c8\u00c9\u0001\u0000\u0000\u0000\u00c9\u00cb\u0005\u001a\u0000"+
		"\u0000\u00ca\u00cc\u0005]\u0000\u0000\u00cb\u00ca\u0001\u0000\u0000\u0000"+
		"\u00cb\u00cc\u0001\u0000\u0000\u0000\u00cc\u00cd\u0001\u0000\u0000\u0000"+
		"\u00cd\u00cf\u0003\u0014\n\u0000\u00ce\u00d0\u0005\b\u0000\u0000\u00cf"+
		"\u00ce\u0001\u0000\u0000\u0000\u00d0\u00d1\u0001\u0000\u0000\u0000\u00d1"+
		"\u00cf\u0001\u0000\u0000\u0000\u00d1\u00d2\u0001\u0000\u0000\u0000\u00d2"+
		"\u0005\u0001\u0000\u0000\u0000\u00d3\u00d5\u0005f\u0000\u0000\u00d4\u00d6"+
		"\u0003\b\u0004\u0000\u00d5\u00d4\u0001\u0000\u0000\u0000\u00d5\u00d6\u0001"+
		"\u0000\u0000\u0000\u00d6\u00d7\u0001\u0000\u0000\u0000\u00d7\u00dc\u0003"+
		"\n\u0005\u0000\u00d8\u00d9\u0005l\u0000\u0000\u00d9\u00db\u0003\n\u0005"+
		"\u0000\u00da\u00d8\u0001\u0000\u0000\u0000\u00db\u00de\u0001\u0000\u0000"+
		"\u0000\u00dc\u00da\u0001\u0000\u0000\u0000\u00dc\u00dd\u0001\u0000\u0000"+
		"\u0000\u00dd\u00df\u0001\u0000\u0000\u0000\u00de\u00dc\u0001\u0000\u0000"+
		"\u0000\u00df\u00e1\u0005g\u0000\u0000\u00e0\u00e2\u0005\b\u0000\u0000"+
		"\u00e1\u00e0\u0001\u0000\u0000\u0000\u00e2\u00e3\u0001\u0000\u0000\u0000"+
		"\u00e3\u00e1\u0001\u0000\u0000\u0000\u00e3\u00e4\u0001\u0000\u0000\u0000"+
		"\u00e4\u0007\u0001\u0000\u0000\u0000\u00e5\u00e7\u0003\u00a0P\u0000\u00e6"+
		"\u00e5\u0001\u0000\u0000\u0000\u00e6\u00e7\u0001\u0000\u0000\u0000\u00e7"+
		"\u00ec\u0001\u0000\u0000\u0000\u00e8\u00ea\u0003\u00a6S\u0000\u00e9\u00e8"+
		"\u0001\u0000\u0000\u0000\u00e9\u00ea\u0001\u0000\u0000\u0000\u00ea\u00ec"+
		"\u0001\u0000\u0000\u0000\u00eb\u00e6\u0001\u0000\u0000\u0000\u00eb\u00e9"+
		"\u0001\u0000\u0000\u0000\u00ec\u00ed\u0001\u0000\u0000\u0000\u00ed\u00ee"+
		"\u0005m\u0000\u0000\u00ee\t\u0001\u0000\u0000\u0000\u00ef\u00f1\u0003"+
		"\u0014\n\u0000\u00f0\u00f2\u0003\f\u0006\u0000\u00f1\u00f0\u0001\u0000"+
		"\u0000\u0000\u00f1\u00f2\u0001\u0000\u0000\u0000\u00f2\u000b\u0001\u0000"+
		"\u0000\u0000\u00f3\u00fc\u0005h\u0000\u0000\u00f4\u00f9\u0003\u000e\u0007"+
		"\u0000\u00f5\u00f6\u0005l\u0000\u0000\u00f6\u00f8\u0003\u000e\u0007\u0000"+
		"\u00f7\u00f5\u0001\u0000\u0000\u0000\u00f8\u00fb\u0001\u0000\u0000\u0000"+
		"\u00f9\u00f7\u0001\u0000\u0000\u0000\u00f9\u00fa\u0001\u0000\u0000\u0000"+
		"\u00fa\u00fd\u0001\u0000\u0000\u0000\u00fb\u00f9\u0001\u0000\u0000\u0000"+
		"\u00fc\u00f4\u0001\u0000\u0000\u0000\u00fc\u00fd\u0001\u0000\u0000\u0000"+
		"\u00fd\u00fe\u0001\u0000\u0000\u0000\u00fe\u00ff\u0005i\u0000\u0000\u00ff"+
		"\r\u0001\u0000\u0000\u0000\u0100\u0102\u0003\u001c\u000e\u0000\u0101\u0100"+
		"\u0001\u0000\u0000\u0000\u0101\u0102\u0001\u0000\u0000\u0000\u0102\u0103"+
		"\u0001\u0000\u0000\u0000\u0103\u0104\u0003\u008eG\u0000\u0104\u000f\u0001"+
		"\u0000\u0000\u0000\u0105\u010e\u0005h\u0000\u0000\u0106\u010b\u0003\u0012"+
		"\t\u0000\u0107\u0108\u0005l\u0000\u0000\u0108\u010a\u0003\u0012\t\u0000"+
		"\u0109\u0107\u0001\u0000\u0000\u0000\u010a\u010d\u0001\u0000\u0000\u0000"+
		"\u010b\u0109\u0001\u0000\u0000\u0000\u010b\u010c\u0001\u0000\u0000\u0000"+
		"\u010c\u010f\u0001\u0000\u0000\u0000\u010d\u010b\u0001\u0000\u0000\u0000"+
		"\u010e\u0106\u0001\u0000\u0000\u0000\u010e\u010f\u0001\u0000\u0000\u0000"+
		"\u010f\u0110\u0001\u0000\u0000\u0000\u0110\u0111\u0005i\u0000\u0000\u0111"+
		"\u0011\u0001\u0000\u0000\u0000\u0112\u0114\u0003\u0006\u0003\u0000\u0113"+
		"\u0112\u0001\u0000\u0000\u0000\u0114\u0117\u0001\u0000\u0000\u0000\u0115"+
		"\u0113\u0001\u0000\u0000\u0000\u0115\u0116\u0001\u0000\u0000\u0000\u0116"+
		"\u011b\u0001\u0000\u0000\u0000\u0117\u0115\u0001\u0000\u0000\u0000\u0118"+
		"\u011a\u0003\u00aeW\u0000\u0119\u0118\u0001\u0000\u0000\u0000\u011a\u011d"+
		"\u0001\u0000\u0000\u0000\u011b\u0119\u0001\u0000\u0000\u0000\u011b\u011c"+
		"\u0001\u0000\u0000\u0000\u011c\u011e\u0001\u0000\u0000\u0000\u011d\u011b"+
		"\u0001\u0000\u0000\u0000\u011e\u0121\u0003\u00a6S\u0000\u011f\u0120\u0005"+
		"m\u0000\u0000\u0120\u0122\u0003\u00a0P\u0000\u0121\u011f\u0001\u0000\u0000"+
		"\u0000\u0121\u0122\u0001\u0000\u0000\u0000\u0122\u0124\u0001\u0000\u0000"+
		"\u0000\u0123\u0125\u0003\u0092I\u0000\u0124\u0123\u0001\u0000\u0000\u0000"+
		"\u0124\u0125\u0001\u0000\u0000\u0000\u0125\u0013\u0001\u0000\u0000\u0000"+
		"\u0126\u0127\u0006\n\uffff\uffff\u0000\u0127\u0128\u0003\u001e\u000f\u0000"+
		"\u0128\u0129\u0005n\u0000\u0000\u0129\u012a\u0003\u0016\u000b\u0000\u012a"+
		"\u012d\u0001\u0000\u0000\u0000\u012b\u012d\u0003\u0016\u000b\u0000\u012c"+
		"\u0126\u0001\u0000\u0000\u0000\u012c\u012b\u0001\u0000\u0000\u0000\u012d"+
		"\u0133\u0001\u0000\u0000\u0000\u012e\u012f\n\u0002\u0000\u0000\u012f\u0130"+
		"\u0005j\u0000\u0000\u0130\u0132\u0003\u0016\u000b\u0000\u0131\u012e\u0001"+
		"\u0000\u0000\u0000\u0132\u0135\u0001\u0000\u0000\u0000\u0133\u0131\u0001"+
		"\u0000\u0000\u0000\u0133\u0134\u0001\u0000\u0000\u0000\u0134\u0015\u0001"+
		"\u0000\u0000\u0000\u0135\u0133\u0001\u0000\u0000\u0000\u0136\u0139\u0003"+
		"\u0018\f\u0000\u0137\u0139\u0003\u001e\u000f\u0000\u0138\u0136\u0001\u0000"+
		"\u0000\u0000\u0138\u0137\u0001\u0000\u0000\u0000\u0139\u0017\u0001\u0000"+
		"\u0000\u0000\u013a\u013b\u0003\u00a6S\u0000\u013b\u013c\u0003\u001a\r"+
		"\u0000\u013c\u0019\u0001\u0000\u0000\u0000\u013d\u0146\u0005}\u0000\u0000"+
		"\u013e\u0143\u0003\u00a0P\u0000\u013f\u0140\u0005l\u0000\u0000\u0140\u0142"+
		"\u0003\u00a0P\u0000\u0141\u013f\u0001\u0000\u0000\u0000\u0142\u0145\u0001"+
		"\u0000\u0000\u0000\u0143\u0141\u0001\u0000\u0000\u0000\u0143\u0144\u0001"+
		"\u0000\u0000\u0000\u0144\u0147\u0001\u0000\u0000\u0000\u0145\u0143\u0001"+
		"\u0000\u0000\u0000\u0146\u013e\u0001\u0000\u0000\u0000\u0146\u0147\u0001"+
		"\u0000\u0000\u0000\u0147\u0148\u0001\u0000\u0000\u0000\u0148\u0149\u0005"+
		"~\u0000\u0000\u0149\u001b\u0001\u0000\u0000\u0000\u014a\u014b\u0003\u001e"+
		"\u000f\u0000\u014b\u014c\u0005m\u0000\u0000\u014c\u001d\u0001\u0000\u0000"+
		"\u0000\u014d\u0150\u0005\t\u0000\u0000\u014e\u0150\u0003\u00a6S\u0000"+
		"\u014f\u014d\u0001\u0000\u0000\u0000\u014f\u014e\u0001\u0000\u0000\u0000"+
		"\u0150\u001f\u0001\u0000\u0000\u0000\u0151\u0155\u0003$\u0012\u0000\u0152"+
		"\u0154\u0005\b\u0000\u0000\u0153\u0152\u0001\u0000\u0000\u0000\u0154\u0157"+
		"\u0001\u0000\u0000\u0000\u0155\u0153\u0001\u0000\u0000\u0000\u0155\u0156"+
		"\u0001\u0000\u0000\u0000\u0156\u016e\u0001\u0000\u0000\u0000\u0157\u0155"+
		"\u0001\u0000\u0000\u0000\u0158\u015c\u0003&\u0013\u0000\u0159\u015b\u0005"+
		"\b\u0000\u0000\u015a\u0159\u0001\u0000\u0000\u0000\u015b\u015e\u0001\u0000"+
		"\u0000\u0000\u015c\u015a\u0001\u0000\u0000\u0000\u015c\u015d\u0001\u0000"+
		"\u0000\u0000\u015d\u016e\u0001\u0000\u0000\u0000\u015e\u015c\u0001\u0000"+
		"\u0000\u0000\u015f\u0163\u0003\"\u0011\u0000\u0160\u0162\u0005\b\u0000"+
		"\u0000\u0161\u0160\u0001\u0000\u0000\u0000\u0162\u0165\u0001\u0000\u0000"+
		"\u0000\u0163\u0161\u0001\u0000\u0000\u0000\u0163\u0164\u0001\u0000\u0000"+
		"\u0000\u0164\u016e\u0001\u0000\u0000\u0000\u0165\u0163\u0001\u0000\u0000"+
		"\u0000\u0166\u016a\u0003@ \u0000\u0167\u0169\u0005\b\u0000\u0000\u0168"+
		"\u0167\u0001\u0000\u0000\u0000\u0169\u016c\u0001\u0000\u0000\u0000\u016a"+
		"\u0168\u0001\u0000\u0000\u0000\u016a\u016b\u0001\u0000\u0000\u0000\u016b"+
		"\u016e\u0001\u0000\u0000\u0000\u016c\u016a\u0001\u0000\u0000\u0000\u016d"+
		"\u0151\u0001\u0000\u0000\u0000\u016d\u0158\u0001\u0000\u0000\u0000\u016d"+
		"\u015f\u0001\u0000\u0000\u0000\u016d\u0166\u0001\u0000\u0000\u0000\u016e"+
		"!\u0001\u0000\u0000\u0000\u016f\u0172\u00038\u001c\u0000\u0170\u0172\u0003"+
		"2\u0019\u0000\u0171\u016f\u0001\u0000\u0000\u0000\u0171\u0170\u0001\u0000"+
		"\u0000\u0000\u0172#\u0001\u0000\u0000\u0000\u0173\u0175\u0003\u0006\u0003"+
		"\u0000\u0174\u0173\u0001\u0000\u0000\u0000\u0175\u0178\u0001\u0000\u0000"+
		"\u0000\u0176\u0174\u0001\u0000\u0000\u0000\u0176\u0177\u0001\u0000\u0000"+
		"\u0000\u0177\u017c\u0001\u0000\u0000\u0000\u0178\u0176\u0001\u0000\u0000"+
		"\u0000\u0179\u017b\u0003\u00aeW\u0000\u017a\u0179\u0001\u0000\u0000\u0000"+
		"\u017b\u017e\u0001\u0000\u0000\u0000\u017c\u017a\u0001\u0000\u0000\u0000"+
		"\u017c\u017d\u0001\u0000\u0000\u0000\u017d\u017f\u0001\u0000\u0000\u0000"+
		"\u017e\u017c\u0001\u0000\u0000\u0000\u017f\u0181\u0003\\.\u0000\u0180"+
		"\u0182\u0005\b\u0000\u0000\u0181\u0180\u0001\u0000\u0000\u0000\u0182\u0183"+
		"\u0001\u0000\u0000\u0000\u0183\u0181\u0001\u0000\u0000\u0000\u0183\u0184"+
		"\u0001\u0000\u0000\u0000\u0184%\u0001\u0000\u0000\u0000\u0185\u018b\u0003"+
		"(\u0014\u0000\u0186\u018b\u0003<\u001e\u0000\u0187\u018b\u0003,\u0016"+
		"\u0000\u0188\u018b\u0003.\u0017\u0000\u0189\u018b\u0003>\u001f\u0000\u018a"+
		"\u0185\u0001\u0000\u0000\u0000\u018a\u0186\u0001\u0000\u0000\u0000\u018a"+
		"\u0187\u0001\u0000\u0000\u0000\u018a\u0188\u0001\u0000\u0000\u0000\u018a"+
		"\u0189\u0001\u0000\u0000\u0000\u018b\'\u0001\u0000\u0000\u0000\u018c\u018e"+
		"\u0003\u0006\u0003\u0000\u018d\u018c\u0001\u0000\u0000\u0000\u018e\u0191"+
		"\u0001\u0000\u0000\u0000\u018f\u018d\u0001\u0000\u0000\u0000\u018f\u0190"+
		"\u0001\u0000\u0000\u0000\u0190\u0195\u0001\u0000\u0000\u0000\u0191\u018f"+
		"\u0001\u0000\u0000\u0000\u0192\u0194\u0003\u00aeW\u0000\u0193\u0192\u0001"+
		"\u0000\u0000\u0000\u0194\u0197\u0001\u0000\u0000\u0000\u0195\u0193\u0001"+
		"\u0000\u0000\u0000\u0195\u0196\u0001\u0000\u0000\u0000\u0196\u0198\u0001"+
		"\u0000\u0000\u0000\u0197\u0195\u0001\u0000\u0000\u0000\u0198\u0199\u0005"+
		"\u001c\u0000\u0000\u0199\u019b\u0003\u0010\b\u0000\u019a\u019c\u0003*"+
		"\u0015\u0000\u019b\u019a\u0001\u0000\u0000\u0000\u019b\u019c\u0001\u0000"+
		"\u0000\u0000\u019c\u01a1\u0001\u0000\u0000\u0000\u019d\u01a2\u0003d2\u0000"+
		"\u019e\u019f\u0003\u0094J\u0000\u019f\u01a0\u0005\b\u0000\u0000\u01a0"+
		"\u01a2\u0001\u0000\u0000\u0000\u01a1\u019d\u0001\u0000\u0000\u0000\u01a1"+
		"\u019e\u0001\u0000\u0000\u0000\u01a2)\u0001\u0000\u0000\u0000\u01a3\u01a4"+
		"\u0005m\u0000\u0000\u01a4\u01a5\u0007\u0000\u0000\u0000\u01a5\u01a6\u0003"+
		"^/\u0000\u01a6+\u0001\u0000\u0000\u0000\u01a7\u01a9\u0003\u0006\u0003"+
		"\u0000\u01a8\u01a7\u0001\u0000\u0000\u0000\u01a9\u01ac\u0001\u0000\u0000"+
		"\u0000\u01aa\u01a8\u0001\u0000\u0000\u0000\u01aa\u01ab\u0001\u0000\u0000"+
		"\u0000\u01ab\u01b0\u0001\u0000\u0000\u0000\u01ac\u01aa\u0001\u0000\u0000"+
		"\u0000\u01ad\u01af\u0003\u00aeW\u0000\u01ae\u01ad\u0001\u0000\u0000\u0000"+
		"\u01af\u01b2\u0001\u0000\u0000\u0000\u01b0\u01ae\u0001\u0000\u0000\u0000"+
		"\u01b0\u01b1\u0001\u0000\u0000\u0000\u01b1\u01b3\u0001\u0000\u0000\u0000"+
		"\u01b2\u01b0\u0001\u0000\u0000\u0000\u01b3\u01b4\u0005{\u0000\u0000\u01b4"+
		"\u01b5\u0005\u001c\u0000\u0000\u01b5\u01ba\u0003\u0010\b\u0000\u01b6\u01bb"+
		"\u0003d2\u0000\u01b7\u01b8\u0003\u0094J\u0000\u01b8\u01b9\u0005\b\u0000"+
		"\u0000\u01b9\u01bb\u0001\u0000\u0000\u0000\u01ba\u01b6\u0001\u0000\u0000"+
		"\u0000\u01ba\u01b7\u0001\u0000\u0000\u0000\u01bb-\u0001\u0000\u0000\u0000"+
		"\u01bc\u01be\u0003\u0006\u0003\u0000\u01bd\u01bc\u0001\u0000\u0000\u0000"+
		"\u01be\u01c1\u0001\u0000\u0000\u0000\u01bf\u01bd\u0001\u0000\u0000\u0000"+
		"\u01bf\u01c0\u0001\u0000\u0000\u0000\u01c0\u01c5\u0001\u0000\u0000\u0000"+
		"\u01c1\u01bf\u0001\u0000\u0000\u0000\u01c2\u01c4\u0003\u00aeW\u0000\u01c3"+
		"\u01c2\u0001\u0000\u0000\u0000\u01c4\u01c7\u0001\u0000\u0000\u0000\u01c5"+
		"\u01c3\u0001\u0000\u0000\u0000\u01c5\u01c6\u0001\u0000\u0000\u0000\u01c6"+
		"\u01c8\u0001\u0000\u0000\u0000\u01c7\u01c5\u0001\u0000\u0000\u0000\u01c8"+
		"\u01ca\u0005\u0013\u0000\u0000\u01c9\u01cb\u00030\u0018\u0000\u01ca\u01c9"+
		"\u0001\u0000\u0000\u0000\u01ca\u01cb\u0001\u0000\u0000\u0000\u01cb\u01cc"+
		"\u0001\u0000\u0000\u0000\u01cc\u01ce\u0003\u00a6S\u0000\u01cd\u01cf\u0003"+
		"L&\u0000\u01ce\u01cd\u0001\u0000\u0000\u0000\u01ce\u01cf\u0001\u0000\u0000"+
		"\u0000\u01cf\u01d0\u0001\u0000\u0000\u0000\u01d0\u01d4\u0003\u0010\b\u0000"+
		"\u01d1\u01d3\u0003P(\u0000\u01d2\u01d1\u0001\u0000\u0000\u0000\u01d3\u01d6"+
		"\u0001\u0000\u0000\u0000\u01d4\u01d2\u0001\u0000\u0000\u0000\u01d4\u01d5"+
		"\u0001\u0000\u0000\u0000\u01d5\u01d9\u0001\u0000\u0000\u0000\u01d6\u01d4"+
		"\u0001\u0000\u0000\u0000\u01d7\u01d8\u0005m\u0000\u0000\u01d8\u01da\u0003"+
		"\u00a0P\u0000\u01d9\u01d7\u0001\u0000\u0000\u0000\u01d9\u01da\u0001\u0000"+
		"\u0000\u0000\u01da\u01df\u0001\u0000\u0000\u0000\u01db\u01e0\u0003d2\u0000"+
		"\u01dc\u01dd\u0003\u0094J\u0000\u01dd\u01de\u0005\b\u0000\u0000\u01de"+
		"\u01e0\u0001\u0000\u0000\u0000\u01df\u01db\u0001\u0000\u0000\u0000\u01df"+
		"\u01dc\u0001\u0000\u0000\u0000\u01e0/\u0001\u0000\u0000\u0000\u01e1\u01e2"+
		"\u0003\u0014\n\u0000\u01e2\u01e3\u0005j\u0000\u0000\u01e31\u0001\u0000"+
		"\u0000\u0000\u01e4\u01e6\u0003\u0006\u0003\u0000\u01e5\u01e4\u0001\u0000"+
		"\u0000\u0000\u01e6\u01e9\u0001\u0000\u0000\u0000\u01e7\u01e5\u0001\u0000"+
		"\u0000\u0000\u01e7\u01e8\u0001\u0000\u0000\u0000\u01e8\u01ed\u0001\u0000"+
		"\u0000\u0000\u01e9\u01e7\u0001\u0000\u0000\u0000\u01ea\u01ec\u0003\u00ae"+
		"W\u0000\u01eb\u01ea\u0001\u0000\u0000\u0000\u01ec\u01ef\u0001\u0000\u0000"+
		"\u0000\u01ed\u01eb\u0001\u0000\u0000\u0000\u01ed\u01ee\u0001\u0000\u0000"+
		"\u0000\u01ee\u01f0\u0001\u0000\u0000\u0000\u01ef\u01ed\u0001\u0000\u0000"+
		"\u0000\u01f0\u01f2\u0005\u0017\u0000\u0000\u01f1\u01f3\u00030\u0018\u0000"+
		"\u01f2\u01f1\u0001\u0000\u0000\u0000\u01f2\u01f3\u0001\u0000\u0000\u0000"+
		"\u01f3\u01f4\u0001\u0000\u0000\u0000\u01f4\u01f7\u0003\u00a6S\u0000\u01f5"+
		"\u01f6\u0005m\u0000\u0000\u01f6\u01f8\u0003\u00a0P\u0000\u01f7\u01f5\u0001"+
		"\u0000\u0000\u0000\u01f7\u01f8\u0001\u0000\u0000\u0000\u01f8\u0200\u0001"+
		"\u0000\u0000\u0000\u01f9\u0201\u00034\u001a\u0000\u01fa\u01fd\u0003\u0094"+
		"J\u0000\u01fb\u01fd\u0003\u0092I\u0000\u01fc\u01fa\u0001\u0000\u0000\u0000"+
		"\u01fc\u01fb\u0001\u0000\u0000\u0000\u01fd\u01fe\u0001\u0000\u0000\u0000"+
		"\u01fe\u01ff\u0005\b\u0000\u0000\u01ff\u0201\u0001\u0000\u0000\u0000\u0200"+
		"\u01f9\u0001\u0000\u0000\u0000\u0200\u01fc\u0001\u0000\u0000\u0000\u0201"+
		"3\u0001\u0000\u0000\u0000\u0202\u0206\u0005d\u0000\u0000\u0203\u0205\u0005"+
		"\b\u0000\u0000\u0204\u0203\u0001\u0000\u0000\u0000\u0205\u0208\u0001\u0000"+
		"\u0000\u0000\u0206\u0204\u0001\u0000\u0000\u0000\u0206\u0207\u0001\u0000"+
		"\u0000\u0000\u0207\u020c\u0001\u0000\u0000\u0000\u0208\u0206\u0001\u0000"+
		"\u0000\u0000\u0209\u020b\u00036\u001b\u0000\u020a\u0209\u0001\u0000\u0000"+
		"\u0000\u020b\u020e\u0001\u0000\u0000\u0000\u020c\u020a\u0001\u0000\u0000"+
		"\u0000\u020c\u020d\u0001\u0000\u0000\u0000\u020d\u0212\u0001\u0000\u0000"+
		"\u0000\u020e\u020c\u0001\u0000\u0000\u0000\u020f\u0211\u0005\b\u0000\u0000"+
		"\u0210\u020f\u0001\u0000\u0000\u0000\u0211\u0214\u0001\u0000\u0000\u0000"+
		"\u0212\u0210\u0001\u0000\u0000\u0000\u0212\u0213\u0001\u0000\u0000\u0000"+
		"\u0213\u0215\u0001\u0000\u0000\u0000\u0214\u0212\u0001\u0000\u0000\u0000"+
		"\u0215\u0216\u0005e\u0000\u0000\u02165\u0001\u0000\u0000\u0000\u0217\u0219"+
		"\u0003\u0006\u0003\u0000\u0218\u0217\u0001\u0000\u0000\u0000\u0219\u021c"+
		"\u0001\u0000\u0000\u0000\u021a\u0218\u0001\u0000\u0000\u0000\u021a\u021b"+
		"\u0001\u0000\u0000\u0000\u021b\u0220\u0001\u0000\u0000\u0000\u021c\u021a"+
		"\u0001\u0000\u0000\u0000\u021d\u021f\u0003\u00aeW\u0000\u021e\u021d\u0001"+
		"\u0000\u0000\u0000\u021f\u0222\u0001\u0000\u0000\u0000\u0220\u021e\u0001"+
		"\u0000\u0000\u0000\u0220\u0221\u0001\u0000\u0000\u0000\u0221\u0223\u0001"+
		"\u0000\u0000\u0000\u0222\u0220\u0001\u0000\u0000\u0000\u0223\u0228\u0007"+
		"\u0001\u0000\u0000\u0224\u0229\u0003d2\u0000\u0225\u0226\u0003\u0094J"+
		"\u0000\u0226\u0227\u0005\b\u0000\u0000\u0227\u0229\u0001\u0000\u0000\u0000"+
		"\u0228\u0224\u0001\u0000\u0000\u0000\u0228\u0225\u0001\u0000\u0000\u0000"+
		"\u0229\u022d\u0001\u0000\u0000\u0000\u022a\u022c\u0005\b\u0000\u0000\u022b"+
		"\u022a\u0001\u0000\u0000\u0000\u022c\u022f\u0001\u0000\u0000\u0000\u022d"+
		"\u022b\u0001\u0000\u0000\u0000\u022d\u022e\u0001\u0000\u0000\u0000\u022e"+
		"7\u0001\u0000\u0000\u0000\u022f\u022d\u0001\u0000\u0000\u0000\u0230\u0232"+
		"\u0003\u0006\u0003\u0000\u0231\u0230\u0001\u0000\u0000\u0000\u0232\u0235"+
		"\u0001\u0000\u0000\u0000\u0233\u0231\u0001\u0000\u0000\u0000\u0233\u0234"+
		"\u0001\u0000\u0000\u0000\u0234\u0239\u0001\u0000\u0000\u0000\u0235\u0233"+
		"\u0001\u0000\u0000\u0000\u0236\u0238\u0003\u00aeW\u0000\u0237\u0236\u0001"+
		"\u0000\u0000\u0000\u0238\u023b\u0001\u0000\u0000\u0000\u0239\u0237\u0001"+
		"\u0000\u0000\u0000\u0239\u023a\u0001\u0000\u0000\u0000\u023a\u023c\u0001"+
		"\u0000\u0000\u0000\u023b\u0239\u0001\u0000\u0000\u0000\u023c\u023e\u0003"+
		"\u00a0P\u0000\u023d\u023f\u00030\u0018\u0000\u023e\u023d\u0001\u0000\u0000"+
		"\u0000\u023e\u023f\u0001\u0000\u0000\u0000\u023f\u0240\u0001\u0000\u0000"+
		"\u0000\u0240\u0241\u0005\u0015\u0000\u0000\u0241\u0246\u0003:\u001d\u0000"+
		"\u0242\u0247\u00034\u001a\u0000\u0243\u0244\u0003\u0094J\u0000\u0244\u0245"+
		"\u0005\b\u0000\u0000\u0245\u0247\u0001\u0000\u0000\u0000\u0246\u0242\u0001"+
		"\u0000\u0000\u0000\u0246\u0243\u0001\u0000\u0000\u0000\u02479\u0001\u0000"+
		"\u0000\u0000\u0248\u0249\u0005f\u0000\u0000\u0249\u024e\u0003\u0012\t"+
		"\u0000\u024a\u024b\u0005l\u0000\u0000\u024b\u024d\u0003\u0012\t\u0000"+
		"\u024c\u024a\u0001\u0000\u0000\u0000\u024d\u0250\u0001\u0000\u0000\u0000"+
		"\u024e\u024c\u0001\u0000\u0000\u0000\u024e\u024f\u0001\u0000\u0000\u0000"+
		"\u024f\u0251\u0001\u0000\u0000\u0000\u0250\u024e\u0001\u0000\u0000\u0000"+
		"\u0251\u0252\u0005g\u0000\u0000\u0252;\u0001\u0000\u0000\u0000\u0253\u0255"+
		"\u0003\u0006\u0003\u0000\u0254\u0253\u0001\u0000\u0000\u0000\u0255\u0258"+
		"\u0001\u0000\u0000\u0000\u0256\u0254\u0001\u0000\u0000\u0000\u0256\u0257"+
		"\u0001\u0000\u0000\u0000\u0257\u025c\u0001\u0000\u0000\u0000\u0258\u0256"+
		"\u0001\u0000\u0000\u0000\u0259\u025b\u0003\u00aeW\u0000\u025a\u0259\u0001"+
		"\u0000\u0000\u0000\u025b\u025e\u0001\u0000\u0000\u0000\u025c\u025a\u0001"+
		"\u0000\u0000\u0000\u025c\u025d\u0001\u0000\u0000\u0000\u025d\u025f\u0001"+
		"\u0000\u0000\u0000\u025e\u025c\u0001\u0000\u0000\u0000\u025f\u0261\u0007"+
		"\u0002\u0000\u0000\u0260\u0262\u00030\u0018\u0000\u0261\u0260\u0001\u0000"+
		"\u0000\u0000\u0261\u0262\u0001\u0000\u0000\u0000\u0262\u0263\u0001\u0000"+
		"\u0000\u0000\u0263\u0264\u0005J\u0000\u0000\u0264\u0265\u0003\u00a0P\u0000"+
		"\u0265\u026a\u0003\u0010\b\u0000\u0266\u026b\u0003d2\u0000\u0267\u0268"+
		"\u0003\u0094J\u0000\u0268\u0269\u0005\b\u0000\u0000\u0269\u026b\u0001"+
		"\u0000\u0000\u0000\u026a\u0266\u0001\u0000\u0000\u0000\u026a\u0267\u0001"+
		"\u0000\u0000\u0000\u026b=\u0001\u0000\u0000\u0000\u026c\u026e\u0003\u0006"+
		"\u0003\u0000\u026d\u026c\u0001\u0000\u0000\u0000\u026e\u0271\u0001\u0000"+
		"\u0000\u0000\u026f\u026d\u0001\u0000\u0000\u0000\u026f\u0270\u0001\u0000"+
		"\u0000\u0000\u0270\u0275\u0001\u0000\u0000\u0000\u0271\u026f\u0001\u0000"+
		"\u0000\u0000\u0272\u0274\u0003\u00aeW\u0000\u0273\u0272\u0001\u0000\u0000"+
		"\u0000\u0274\u0277\u0001\u0000\u0000\u0000\u0275\u0273\u0001\u0000\u0000"+
		"\u0000\u0275\u0276\u0001\u0000\u0000\u0000\u0276\u0278\u0001\u0000\u0000"+
		"\u0000\u0277\u0275\u0001\u0000\u0000\u0000\u0278\u027a\u0003\u00a0P\u0000"+
		"\u0279\u027b\u00030\u0018\u0000\u027a\u0279\u0001\u0000\u0000\u0000\u027a"+
		"\u027b\u0001\u0000\u0000\u0000\u027b\u027c\u0001\u0000\u0000\u0000\u027c"+
		"\u027d\u0005J\u0000\u0000\u027d\u027e\u0007\u0003\u0000\u0000\u027e\u0283"+
		"\u0003\u0010\b\u0000\u027f\u0284\u0003d2\u0000\u0280\u0281\u0003\u0094"+
		"J\u0000\u0281\u0282\u0005\b\u0000\u0000\u0282\u0284\u0001\u0000\u0000"+
		"\u0000\u0283\u027f\u0001\u0000\u0000\u0000\u0283\u0280\u0001\u0000\u0000"+
		"\u0000\u0284?\u0001\u0000\u0000\u0000\u0285\u0288\u0003H$\u0000\u0286"+
		"\u0288\u0003B!\u0000\u0287\u0285\u0001\u0000\u0000\u0000\u0287\u0286\u0001"+
		"\u0000\u0000\u0000\u0288A\u0001\u0000\u0000\u0000\u0289\u028c\u0003D\""+
		"\u0000\u028a\u028c\u0003F#\u0000\u028b\u0289\u0001\u0000\u0000\u0000\u028b"+
		"\u028a\u0001\u0000\u0000\u0000\u028cC\u0001\u0000\u0000\u0000\u028d\u028f"+
		"\u0003\u0006\u0003\u0000\u028e\u028d\u0001\u0000\u0000\u0000\u028f\u0292"+
		"\u0001\u0000\u0000\u0000\u0290\u028e\u0001\u0000\u0000\u0000\u0290\u0291"+
		"\u0001\u0000\u0000\u0000\u0291\u0296\u0001\u0000\u0000\u0000\u0292\u0290"+
		"\u0001\u0000\u0000\u0000\u0293\u0295\u0003\u00aeW\u0000\u0294\u0293\u0001"+
		"\u0000\u0000\u0000\u0295\u0298\u0001\u0000\u0000\u0000\u0296\u0294\u0001"+
		"\u0000\u0000\u0000\u0296\u0297\u0001\u0000\u0000\u0000\u0297\u0299\u0001"+
		"\u0000\u0000\u0000\u0298\u0296\u0001\u0000\u0000\u0000\u0299\u029a\u0005"+
		"\u0016\u0000\u0000\u029a\u029c\u0003\u00a6S\u0000\u029b\u029d\u0003L&"+
		"\u0000\u029c\u029b\u0001\u0000\u0000\u0000\u029c\u029d\u0001\u0000\u0000"+
		"\u0000\u029d\u029f\u0001\u0000\u0000\u0000\u029e\u02a0\u0003\u0010\b\u0000"+
		"\u029f\u029e\u0001\u0000\u0000\u0000\u029f\u02a0\u0001\u0000\u0000\u0000"+
		"\u02a0\u02a2\u0001\u0000\u0000\u0000\u02a1\u02a3\u0003T*\u0000\u02a2\u02a1"+
		"\u0001\u0000\u0000\u0000\u02a2\u02a3\u0001\u0000\u0000\u0000\u02a3\u02a7"+
		"\u0001\u0000\u0000\u0000\u02a4\u02a6\u0003P(\u0000\u02a5\u02a4\u0001\u0000"+
		"\u0000\u0000\u02a6\u02a9\u0001\u0000\u0000\u0000\u02a7\u02a5\u0001\u0000"+
		"\u0000\u0000\u02a7\u02a8\u0001\u0000\u0000\u0000\u02a8\u02bc\u0001\u0000"+
		"\u0000\u0000\u02a9\u02a7\u0001\u0000\u0000\u0000\u02aa\u02ac\u0005d\u0000"+
		"\u0000\u02ab\u02ad\u0005\b\u0000\u0000\u02ac\u02ab\u0001\u0000\u0000\u0000"+
		"\u02ad\u02ae\u0001\u0000\u0000\u0000\u02ae\u02ac\u0001\u0000\u0000\u0000"+
		"\u02ae\u02af\u0001\u0000\u0000\u0000\u02af\u02b3\u0001\u0000\u0000\u0000"+
		"\u02b0\u02b2\u0003 \u0010\u0000\u02b1\u02b0\u0001\u0000\u0000\u0000\u02b2"+
		"\u02b5\u0001\u0000\u0000\u0000\u02b3\u02b1\u0001\u0000\u0000\u0000\u02b3"+
		"\u02b4\u0001\u0000\u0000\u0000\u02b4\u02b7\u0001\u0000\u0000\u0000\u02b5"+
		"\u02b3\u0001\u0000\u0000\u0000\u02b6\u02b8\u0005\b\u0000\u0000\u02b7\u02b6"+
		"\u0001\u0000\u0000\u0000\u02b8\u02b9\u0001\u0000\u0000\u0000\u02b9\u02b7"+
		"\u0001\u0000\u0000\u0000\u02b9\u02ba\u0001\u0000\u0000\u0000\u02ba\u02bb"+
		"\u0001\u0000\u0000\u0000\u02bb\u02bd\u0005e\u0000\u0000\u02bc\u02aa\u0001"+
		"\u0000\u0000\u0000\u02bc\u02bd\u0001\u0000\u0000\u0000\u02bd\u02be\u0001"+
		"\u0000\u0000\u0000\u02be\u02bf\u0005\b\u0000\u0000\u02bfE\u0001\u0000"+
		"\u0000\u0000\u02c0\u02c2\u0003\u0006\u0003\u0000\u02c1\u02c0\u0001\u0000"+
		"\u0000\u0000\u02c2\u02c5\u0001\u0000\u0000\u0000\u02c3\u02c1\u0001\u0000"+
		"\u0000\u0000\u02c3\u02c4\u0001\u0000\u0000\u0000\u02c4\u02c9\u0001\u0000"+
		"\u0000\u0000\u02c5\u02c3\u0001\u0000\u0000\u0000\u02c6\u02c8\u0003\u00ae"+
		"W\u0000\u02c7\u02c6\u0001\u0000\u0000\u0000\u02c8\u02cb\u0001\u0000\u0000"+
		"\u0000\u02c9\u02c7\u0001\u0000\u0000\u0000\u02c9\u02ca\u0001\u0000\u0000"+
		"\u0000\u02ca\u02cc\u0001\u0000\u0000\u0000\u02cb\u02c9\u0001\u0000\u0000"+
		"\u0000\u02cc\u02cd\u0005\u0014\u0000\u0000\u02cd\u02cf\u0003\u00a6S\u0000"+
		"\u02ce\u02d0\u0003L&\u0000\u02cf\u02ce\u0001\u0000\u0000\u0000\u02cf\u02d0"+
		"\u0001\u0000\u0000\u0000\u02d0\u02d2\u0001\u0000\u0000\u0000\u02d1\u02d3"+
		"\u0003\u0010\b\u0000\u02d2\u02d1\u0001\u0000\u0000\u0000\u02d2\u02d3\u0001"+
		"\u0000\u0000\u0000\u02d3\u02d5\u0001\u0000\u0000\u0000\u02d4\u02d6\u0003"+
		"T*\u0000\u02d5\u02d4\u0001\u0000\u0000\u0000\u02d5\u02d6\u0001\u0000\u0000"+
		"\u0000\u02d6\u02da\u0001\u0000\u0000\u0000\u02d7\u02d9\u0003P(\u0000\u02d8"+
		"\u02d7\u0001\u0000\u0000\u0000\u02d9\u02dc\u0001\u0000\u0000\u0000\u02da"+
		"\u02d8\u0001\u0000\u0000\u0000\u02da\u02db\u0001\u0000\u0000\u0000\u02db"+
		"\u02ef\u0001\u0000\u0000\u0000\u02dc\u02da\u0001\u0000\u0000\u0000\u02dd"+
		"\u02df\u0005d\u0000\u0000\u02de\u02e0\u0005\b\u0000\u0000\u02df\u02de"+
		"\u0001\u0000\u0000\u0000\u02e0\u02e1\u0001\u0000\u0000\u0000\u02e1\u02df"+
		"\u0001\u0000\u0000\u0000\u02e1\u02e2\u0001\u0000\u0000\u0000\u02e2\u02e6"+
		"\u0001\u0000\u0000\u0000\u02e3\u02e5\u0003 \u0010\u0000\u02e4\u02e3\u0001"+
		"\u0000\u0000\u0000\u02e5\u02e8\u0001\u0000\u0000\u0000\u02e6\u02e4\u0001"+
		"\u0000\u0000\u0000\u02e6\u02e7\u0001\u0000\u0000\u0000\u02e7\u02ea\u0001"+
		"\u0000\u0000\u0000\u02e8\u02e6\u0001\u0000\u0000\u0000\u02e9\u02eb\u0005"+
		"\b\u0000\u0000\u02ea\u02e9\u0001\u0000\u0000\u0000\u02eb\u02ec\u0001\u0000"+
		"\u0000\u0000\u02ec\u02ea\u0001\u0000\u0000\u0000\u02ec\u02ed\u0001\u0000"+
		"\u0000\u0000\u02ed\u02ee\u0001\u0000\u0000\u0000\u02ee\u02f0\u0005e\u0000"+
		"\u0000\u02ef\u02dd\u0001\u0000\u0000\u0000\u02ef\u02f0\u0001\u0000\u0000"+
		"\u0000\u02f0\u02f1\u0001\u0000\u0000\u0000\u02f1\u02f2\u0005\b\u0000\u0000"+
		"\u02f2G\u0001\u0000\u0000\u0000\u02f3\u02f5\u0003\u0006\u0003\u0000\u02f4"+
		"\u02f3\u0001\u0000\u0000\u0000\u02f5\u02f8\u0001\u0000\u0000\u0000\u02f6"+
		"\u02f4\u0001\u0000\u0000\u0000\u02f6\u02f7\u0001\u0000\u0000\u0000\u02f7"+
		"\u02fc\u0001\u0000\u0000\u0000\u02f8\u02f6\u0001\u0000\u0000\u0000\u02f9"+
		"\u02fb\u0003\u00aeW\u0000\u02fa\u02f9\u0001\u0000\u0000\u0000\u02fb\u02fe"+
		"\u0001\u0000\u0000\u0000\u02fc\u02fa\u0001\u0000\u0000\u0000\u02fc\u02fd"+
		"\u0001\u0000\u0000\u0000\u02fd\u02ff\u0001\u0000\u0000\u0000\u02fe\u02fc"+
		"\u0001\u0000\u0000\u0000\u02ff\u0300\u0005A\u0000\u0000\u0300\u0302\u0003"+
		"\u00a6S\u0000\u0301\u0303\u0003T*\u0000\u0302\u0301\u0001\u0000\u0000"+
		"\u0000\u0302\u0303\u0001\u0000\u0000\u0000\u0303\u0304\u0001\u0000\u0000"+
		"\u0000\u0304\u0306\u0005d\u0000\u0000\u0305\u0307\u0005\b\u0000\u0000"+
		"\u0306\u0305\u0001\u0000\u0000\u0000\u0307\u0308\u0001\u0000\u0000\u0000"+
		"\u0308\u0306\u0001\u0000\u0000\u0000\u0308\u0309\u0001\u0000\u0000\u0000"+
		"\u0309\u0312\u0001\u0000\u0000\u0000\u030a\u030f\u0003J%\u0000\u030b\u030c"+
		"\u0005l\u0000\u0000\u030c\u030e\u0003J%\u0000\u030d\u030b\u0001\u0000"+
		"\u0000\u0000\u030e\u0311\u0001\u0000\u0000\u0000\u030f\u030d\u0001\u0000"+
		"\u0000\u0000\u030f\u0310\u0001\u0000\u0000\u0000\u0310\u0313\u0001\u0000"+
		"\u0000\u0000\u0311\u030f\u0001\u0000\u0000\u0000\u0312\u030a\u0001\u0000"+
		"\u0000\u0000\u0312\u0313\u0001\u0000\u0000\u0000\u0313\u0314\u0001\u0000"+
		"\u0000\u0000\u0314\u0315\u0005e\u0000\u0000\u0315\u0316\u0005\b\u0000"+
		"\u0000\u0316I\u0001\u0000\u0000\u0000\u0317\u0319\u0003\u0006\u0003\u0000"+
		"\u0318\u0317\u0001\u0000\u0000\u0000\u0319\u031c\u0001\u0000\u0000\u0000"+
		"\u031a\u0318\u0001\u0000\u0000\u0000\u031a\u031b\u0001\u0000\u0000\u0000"+
		"\u031b\u0320\u0001\u0000\u0000\u0000\u031c\u031a\u0001\u0000\u0000\u0000"+
		"\u031d\u031f\u0003\u00aeW\u0000\u031e\u031d\u0001\u0000\u0000\u0000\u031f"+
		"\u0322\u0001\u0000\u0000\u0000\u0320\u031e\u0001\u0000\u0000\u0000\u0320"+
		"\u0321\u0001\u0000\u0000\u0000\u0321\u0323\u0001\u0000\u0000\u0000\u0322"+
		"\u0320\u0001\u0000\u0000\u0000\u0323\u0325\u0003\u00a6S\u0000\u0324\u0326"+
		"\u0003\u0092I\u0000\u0325\u0324\u0001\u0000\u0000\u0000\u0325\u0326\u0001"+
		"\u0000\u0000\u0000\u0326K\u0001\u0000\u0000\u0000\u0327\u0328\u0005}\u0000"+
		"\u0000\u0328\u032d\u0003N\'\u0000\u0329\u032a\u0005l\u0000\u0000\u032a"+
		"\u032c\u0003N\'\u0000\u032b\u0329\u0001\u0000\u0000\u0000\u032c\u032f"+
		"\u0001\u0000\u0000\u0000\u032d\u032b\u0001\u0000\u0000\u0000\u032d\u032e"+
		"\u0001\u0000\u0000\u0000\u032e\u0330\u0001\u0000\u0000\u0000\u032f\u032d"+
		"\u0001\u0000\u0000\u0000\u0330\u0331\u0005~\u0000\u0000\u0331M\u0001\u0000"+
		"\u0000\u0000\u0332\u0334\u0003\u0006\u0003\u0000\u0333\u0332\u0001\u0000"+
		"\u0000\u0000\u0334\u0337\u0001\u0000\u0000\u0000\u0335\u0333\u0001\u0000"+
		"\u0000\u0000\u0335\u0336\u0001\u0000\u0000\u0000\u0336\u0339\u0001\u0000"+
		"\u0000\u0000\u0337\u0335\u0001\u0000\u0000\u0000\u0338\u033a\u0007\u0004"+
		"\u0000\u0000\u0339\u0338\u0001\u0000\u0000\u0000\u0339\u033a\u0001\u0000"+
		"\u0000\u0000\u033a\u033b\u0001\u0000\u0000\u0000\u033b\u033c\u0003\u00a6"+
		"S\u0000\u033cO\u0001\u0000\u0000\u0000\u033d\u033e\u0005T\u0000\u0000"+
		"\u033e\u033f\u0003\u001e\u000f\u0000\u033f\u0340\u0005m\u0000\u0000\u0340"+
		"\u0345\u0003R)\u0000\u0341\u0342\u0005l\u0000\u0000\u0342\u0344\u0003"+
		"R)\u0000\u0343\u0341\u0001\u0000\u0000\u0000\u0344\u0347\u0001\u0000\u0000"+
		"\u0000\u0345\u0343\u0001\u0000\u0000\u0000\u0345\u0346\u0001\u0000\u0000"+
		"\u0000\u0346Q\u0001\u0000\u0000\u0000\u0347\u0345\u0001\u0000\u0000\u0000"+
		"\u0348\u034b\u0005?\u0000\u0000\u0349\u034b\u0003\u00a0P\u0000\u034a\u0348"+
		"\u0001\u0000\u0000\u0000\u034a\u0349\u0001\u0000\u0000\u0000\u034bS\u0001"+
		"\u0000\u0000\u0000\u034c\u034d\u0005m\u0000\u0000\u034d\u0352\u0003V+"+
		"\u0000\u034e\u034f\u0005l\u0000\u0000\u034f\u0351\u0003V+\u0000\u0350"+
		"\u034e\u0001\u0000\u0000\u0000\u0351\u0354\u0001\u0000\u0000\u0000\u0352"+
		"\u0350\u0001\u0000\u0000\u0000\u0352\u0353\u0001\u0000\u0000\u0000\u0353"+
		"U\u0001\u0000\u0000\u0000\u0354\u0352\u0001\u0000\u0000\u0000\u0355\u0358"+
		"\u0003X,\u0000\u0356\u0358\u0003Z-\u0000\u0357\u0355\u0001\u0000\u0000"+
		"\u0000\u0357\u0356\u0001\u0000\u0000\u0000\u0358W\u0001\u0000\u0000\u0000"+
		"\u0359\u035a\u0003\u00a0P\u0000\u035a\u035b\u0003^/\u0000\u035bY\u0001"+
		"\u0000\u0000\u0000\u035c\u035d\u0003\u00a0P\u0000\u035d[\u0001\u0000\u0000"+
		"\u0000\u035e\u035f\u0007\u0005\u0000\u0000\u035f\u0362\u0003\u00a6S\u0000"+
		"\u0360\u0361\u0005m\u0000\u0000\u0361\u0363\u0003\u00a0P\u0000\u0362\u0360"+
		"\u0001\u0000\u0000\u0000\u0362\u0363\u0001\u0000\u0000\u0000\u0363\u0365"+
		"\u0001\u0000\u0000\u0000\u0364\u0366\u0003\u0092I\u0000\u0365\u0364\u0001"+
		"\u0000\u0000\u0000\u0365\u0366\u0001\u0000\u0000\u0000\u0366]\u0001\u0000"+
		"\u0000\u0000\u0367\u0370\u0005h\u0000\u0000\u0368\u036d\u0003`0\u0000"+
		"\u0369\u036a\u0005l\u0000\u0000\u036a\u036c\u0003`0\u0000\u036b\u0369"+
		"\u0001\u0000\u0000\u0000\u036c\u036f\u0001\u0000\u0000\u0000\u036d\u036b"+
		"\u0001\u0000\u0000\u0000\u036d\u036e\u0001\u0000\u0000\u0000\u036e\u0371"+
		"\u0001\u0000\u0000\u0000\u036f\u036d\u0001\u0000\u0000\u0000\u0370\u0368"+
		"\u0001\u0000\u0000\u0000\u0370\u0371\u0001\u0000\u0000\u0000\u0371\u0372"+
		"\u0001\u0000\u0000\u0000\u0372\u0373\u0005i\u0000\u0000\u0373_\u0001\u0000"+
		"\u0000\u0000\u0374\u0376\u0003\u001c\u000e\u0000\u0375\u0374\u0001\u0000"+
		"\u0000\u0000\u0375\u0376\u0001\u0000\u0000\u0000\u0376\u0378\u0001\u0000"+
		"\u0000\u0000\u0377\u0379\u0007\u0006\u0000\u0000\u0378\u0377\u0001\u0000"+
		"\u0000\u0000\u0378\u0379\u0001\u0000\u0000\u0000\u0379\u037a\u0001\u0000"+
		"\u0000\u0000\u037a\u037b\u0003\u008eG\u0000\u037ba\u0001\u0000\u0000\u0000"+
		"\u037c\u037d\u0005f\u0000\u0000\u037d\u0382\u0003`0\u0000\u037e\u037f"+
		"\u0005l\u0000\u0000\u037f\u0381\u0003`0\u0000\u0380\u037e\u0001\u0000"+
		"\u0000\u0000\u0381\u0384\u0001\u0000\u0000\u0000\u0382\u0380\u0001\u0000"+
		"\u0000\u0000\u0382\u0383\u0001\u0000\u0000\u0000\u0383\u0385\u0001\u0000"+
		"\u0000\u0000\u0384\u0382\u0001\u0000\u0000\u0000\u0385\u0386\u0005g\u0000"+
		"\u0000\u0386c\u0001\u0000\u0000\u0000\u0387\u038b\u0005d\u0000\u0000\u0388"+
		"\u038a\u0005\b\u0000\u0000\u0389\u0388\u0001\u0000\u0000\u0000\u038a\u038d"+
		"\u0001\u0000\u0000\u0000\u038b\u0389\u0001\u0000\u0000\u0000\u038b\u038c"+
		"\u0001\u0000\u0000\u0000\u038c\u0391\u0001\u0000\u0000\u0000\u038d\u038b"+
		"\u0001\u0000\u0000\u0000\u038e\u0390\u0003f3\u0000\u038f\u038e\u0001\u0000"+
		"\u0000\u0000\u0390\u0393\u0001\u0000\u0000\u0000\u0391\u038f\u0001\u0000"+
		"\u0000\u0000\u0391\u0392\u0001\u0000\u0000\u0000\u0392\u0397\u0001\u0000"+
		"\u0000\u0000\u0393\u0391\u0001\u0000\u0000\u0000\u0394\u0396\u0005\b\u0000"+
		"\u0000\u0395\u0394\u0001\u0000\u0000\u0000\u0396\u0399\u0001\u0000\u0000"+
		"\u0000\u0397\u0395\u0001\u0000\u0000\u0000\u0397\u0398\u0001\u0000\u0000"+
		"\u0000\u0398\u039a\u0001\u0000\u0000\u0000\u0399\u0397\u0001\u0000\u0000"+
		"\u0000\u039a\u039b\u0005e\u0000\u0000\u039be\u0001\u0000\u0000\u0000\u039c"+
		"\u03ab\u0003d2\u0000\u039d\u03ab\u0003h4\u0000\u039e\u03ab\u0003j5\u0000"+
		"\u039f\u03ab\u0003l6\u0000\u03a0\u03ab\u0003n7\u0000\u03a1\u03ab\u0003"+
		"p8\u0000\u03a2\u03ab\u0003r9\u0000\u03a3\u03ab\u0003t:\u0000\u03a4\u03ab"+
		"\u0003|>\u0000\u03a5\u03ab\u0003z=\u0000\u03a6\u03ab\u0003x<\u0000\u03a7"+
		"\u03ab\u0003\u0082A\u0000\u03a8\u03ab\u0003\u0080@\u0000\u03a9\u03ab\u0003"+
		"~?\u0000\u03aa\u039c\u0001\u0000\u0000\u0000\u03aa\u039d\u0001\u0000\u0000"+
		"\u0000\u03aa\u039e\u0001\u0000\u0000\u0000\u03aa\u039f\u0001\u0000\u0000"+
		"\u0000\u03aa\u03a0\u0001\u0000\u0000\u0000\u03aa\u03a1\u0001\u0000\u0000"+
		"\u0000\u03aa\u03a2\u0001\u0000\u0000\u0000\u03aa\u03a3\u0001\u0000\u0000"+
		"\u0000\u03aa\u03a4\u0001\u0000\u0000\u0000\u03aa\u03a5\u0001\u0000\u0000"+
		"\u0000\u03aa\u03a6\u0001\u0000\u0000\u0000\u03aa\u03a7\u0001\u0000\u0000"+
		"\u0000\u03aa\u03a8\u0001\u0000\u0000\u0000\u03aa\u03a9\u0001\u0000\u0000"+
		"\u0000\u03abg\u0001\u0000\u0000\u0000\u03ac\u03ae\u0003\u0006\u0003\u0000"+
		"\u03ad\u03ac\u0001\u0000\u0000\u0000\u03ae\u03b1\u0001\u0000\u0000\u0000"+
		"\u03af\u03ad\u0001\u0000\u0000\u0000\u03af\u03b0\u0001\u0000\u0000\u0000"+
		"\u03b0\u03b2\u0001\u0000\u0000\u0000\u03b1\u03af\u0001\u0000\u0000\u0000"+
		"\u03b2\u03b4\u0005<\u0000\u0000\u03b3\u03b5\u0005\b\u0000\u0000\u03b4"+
		"\u03b3\u0001\u0000\u0000\u0000\u03b5\u03b6\u0001\u0000\u0000\u0000\u03b6"+
		"\u03b4\u0001\u0000\u0000\u0000\u03b6\u03b7\u0001\u0000\u0000\u0000\u03b7"+
		"i\u0001\u0000\u0000\u0000\u03b8\u03ba\u0003\u0006\u0003\u0000\u03b9\u03b8"+
		"\u0001\u0000\u0000\u0000\u03ba\u03bd\u0001\u0000\u0000\u0000\u03bb\u03b9"+
		"\u0001\u0000\u0000\u0000\u03bb\u03bc\u0001\u0000\u0000\u0000\u03bc\u03be"+
		"\u0001\u0000\u0000\u0000\u03bd\u03bb\u0001\u0000\u0000\u0000\u03be\u03c0"+
		"\u0005>\u0000\u0000\u03bf\u03c1\u0005\b\u0000\u0000\u03c0\u03bf\u0001"+
		"\u0000\u0000\u0000\u03c1\u03c2\u0001\u0000\u0000\u0000\u03c2\u03c0\u0001"+
		"\u0000\u0000\u0000\u03c2\u03c3\u0001\u0000\u0000\u0000\u03c3k\u0001\u0000"+
		"\u0000\u0000\u03c4\u03c6\u0003\u0006\u0003\u0000\u03c5\u03c4\u0001\u0000"+
		"\u0000\u0000\u03c6\u03c9\u0001\u0000\u0000\u0000\u03c7\u03c5\u0001\u0000"+
		"\u0000\u0000\u03c7\u03c8\u0001\u0000\u0000\u0000\u03c8\u03ca\u0001\u0000"+
		"\u0000\u0000\u03c9\u03c7\u0001\u0000\u0000\u0000\u03ca\u03cb\u0005\u0019"+
		"\u0000\u0000\u03cb\u03cc\u0003f3\u0000\u03cc\u03cd\u0005R\u0000\u0000"+
		"\u03cd\u03ce\u0005h\u0000\u0000\u03ce\u03cf\u0003\u008eG\u0000\u03cf\u03d1"+
		"\u0005i\u0000\u0000\u03d0\u03d2\u0005\b\u0000\u0000\u03d1\u03d0\u0001"+
		"\u0000\u0000\u0000\u03d2\u03d3\u0001\u0000\u0000\u0000\u03d3\u03d1\u0001"+
		"\u0000\u0000\u0000\u03d3\u03d4\u0001\u0000\u0000\u0000\u03d4m\u0001\u0000"+
		"\u0000\u0000\u03d5\u03d7\u0003\u0006\u0003\u0000\u03d6\u03d5\u0001\u0000"+
		"\u0000\u0000\u03d7\u03da\u0001\u0000\u0000\u0000\u03d8\u03d6\u0001\u0000"+
		"\u0000\u0000\u03d8\u03d9\u0001\u0000\u0000\u0000\u03d9\u03dc\u0001\u0000"+
		"\u0000\u0000\u03da\u03d8\u0001\u0000\u0000\u0000\u03db\u03dd\u0005\b\u0000"+
		"\u0000\u03dc\u03db\u0001\u0000\u0000\u0000\u03dd\u03de\u0001\u0000\u0000"+
		"\u0000\u03de\u03dc\u0001\u0000\u0000\u0000\u03de\u03df\u0001\u0000\u0000"+
		"\u0000\u03dfo\u0001\u0000\u0000\u0000\u03e0\u03e2\u0003\u0006\u0003\u0000"+
		"\u03e1\u03e0\u0001\u0000\u0000\u0000\u03e2\u03e5\u0001\u0000\u0000\u0000"+
		"\u03e3\u03e1\u0001\u0000\u0000\u0000\u03e3\u03e4\u0001\u0000\u0000\u0000"+
		"\u03e4\u03e6\u0001\u0000\u0000\u0000\u03e5\u03e3\u0001\u0000\u0000\u0000"+
		"\u03e6\u03e8\u0003\u008eG\u0000\u03e7\u03e9\u0005\b\u0000\u0000\u03e8"+
		"\u03e7\u0001\u0000\u0000\u0000\u03e9\u03ea\u0001\u0000\u0000\u0000\u03ea"+
		"\u03e8\u0001\u0000\u0000\u0000\u03ea\u03eb\u0001\u0000\u0000\u0000\u03eb"+
		"q\u0001\u0000\u0000\u0000\u03ec\u03ee\u0003\u0006\u0003\u0000\u03ed\u03ec"+
		"\u0001\u0000\u0000\u0000\u03ee\u03f1\u0001\u0000\u0000\u0000\u03ef\u03ed"+
		"\u0001\u0000\u0000\u0000\u03ef\u03f0\u0001\u0000\u0000\u0000\u03f0\u03f2"+
		"\u0001\u0000\u0000\u0000\u03f1\u03ef\u0001\u0000\u0000\u0000\u03f2\u03f3"+
		"\u0005D\u0000\u0000\u03f3\u03f4\u0005h\u0000\u0000\u03f4\u03f5\u0003\u00a6"+
		"S\u0000\u03f5\u03f6\u0005G\u0000\u0000\u03f6\u03f7\u0003\u008eG\u0000"+
		"\u03f7\u03f8\u0005i\u0000\u0000\u03f8\u03f9\u0003d2\u0000\u03f9s\u0001"+
		"\u0000\u0000\u0000\u03fa\u03fc\u0003\u0006\u0003\u0000\u03fb\u03fa\u0001"+
		"\u0000\u0000\u0000\u03fc\u03ff\u0001\u0000\u0000\u0000\u03fd\u03fb\u0001"+
		"\u0000\u0000\u0000\u03fd\u03fe\u0001\u0000\u0000\u0000\u03fe\u0400\u0001"+
		"\u0000\u0000\u0000\u03ff\u03fd\u0001\u0000\u0000\u0000\u0400\u0401\u0005"+
		"E\u0000\u0000\u0401\u0402\u0005h\u0000\u0000\u0402\u0403\u0003\u008eG"+
		"\u0000\u0403\u0404\u0005i\u0000\u0000\u0404\u0406\u0003d2\u0000\u0405"+
		"\u0407\u0003v;\u0000\u0406\u0405\u0001\u0000\u0000\u0000\u0406\u0407\u0001"+
		"\u0000\u0000\u0000\u0407u\u0001\u0000\u0000\u0000\u0408\u0409\u0005@\u0000"+
		"\u0000\u0409\u040a\u0003d2\u0000\u040aw\u0001\u0000\u0000\u0000\u040b"+
		"\u040d\u0003\u0006\u0003\u0000\u040c\u040b\u0001\u0000\u0000\u0000\u040d"+
		"\u0410\u0001\u0000\u0000\u0000\u040e\u040c\u0001\u0000\u0000\u0000\u040e"+
		"\u040f\u0001\u0000\u0000\u0000\u040f\u0411\u0001\u0000\u0000\u0000\u0410"+
		"\u040e\u0001\u0000\u0000\u0000\u0411\u0413\u0005M\u0000\u0000\u0412\u0414"+
		"\u0003\u008eG\u0000\u0413\u0412\u0001\u0000\u0000\u0000\u0413\u0414\u0001"+
		"\u0000\u0000\u0000\u0414\u0415\u0001\u0000\u0000\u0000\u0415\u0416\u0005"+
		"\b\u0000\u0000\u0416y\u0001\u0000\u0000\u0000\u0417\u0419\u0003\u0006"+
		"\u0003\u0000\u0418\u0417\u0001\u0000\u0000\u0000\u0419\u041c\u0001\u0000"+
		"\u0000\u0000\u041a\u0418\u0001\u0000\u0000\u0000\u041a\u041b\u0001\u0000"+
		"\u0000\u0000\u041b\u0420\u0001\u0000\u0000\u0000\u041c\u041a\u0001\u0000"+
		"\u0000\u0000\u041d\u041f\u0003\u00aeW\u0000\u041e\u041d\u0001\u0000\u0000"+
		"\u0000\u041f\u0422\u0001\u0000\u0000\u0000\u0420\u041e\u0001\u0000\u0000"+
		"\u0000\u0420\u0421\u0001\u0000\u0000\u0000\u0421\u0423\u0001\u0000\u0000"+
		"\u0000\u0422\u0420\u0001\u0000\u0000\u0000\u0423\u0424\u0005\u0013\u0000"+
		"\u0000\u0424\u0426\u0003\u00a6S\u0000\u0425\u0427\u0003L&\u0000\u0426"+
		"\u0425\u0001\u0000\u0000\u0000\u0426\u0427\u0001\u0000\u0000\u0000\u0427"+
		"\u0428\u0001\u0000\u0000\u0000\u0428\u042c\u0003\u0010\b\u0000\u0429\u042b"+
		"\u0003P(\u0000\u042a\u0429\u0001\u0000\u0000\u0000\u042b\u042e\u0001\u0000"+
		"\u0000\u0000\u042c\u042a\u0001\u0000\u0000\u0000\u042c\u042d\u0001\u0000"+
		"\u0000\u0000\u042d\u0431\u0001\u0000\u0000\u0000\u042e\u042c\u0001\u0000"+
		"\u0000\u0000\u042f\u0430\u0005m\u0000\u0000\u0430\u0432\u0003\u00a0P\u0000"+
		"\u0431\u042f\u0001\u0000\u0000\u0000\u0431\u0432\u0001\u0000\u0000\u0000"+
		"\u0432\u0437\u0001\u0000\u0000\u0000\u0433\u0438\u0003d2\u0000\u0434\u0435"+
		"\u0003\u0094J\u0000\u0435\u0436\u0005\b\u0000\u0000\u0436\u0438\u0001"+
		"\u0000\u0000\u0000\u0437\u0433\u0001\u0000\u0000\u0000\u0437\u0434\u0001"+
		"\u0000\u0000\u0000\u0438{\u0001\u0000\u0000\u0000\u0439\u043b\u0003\u0006"+
		"\u0003\u0000\u043a\u0439\u0001\u0000\u0000\u0000\u043b\u043e\u0001\u0000"+
		"\u0000\u0000\u043c\u043a\u0001\u0000\u0000\u0000\u043c\u043d\u0001\u0000"+
		"\u0000\u0000\u043d\u0440\u0001\u0000\u0000\u0000\u043e\u043c\u0001\u0000"+
		"\u0000\u0000\u043f\u0441\u0005\u0012\u0000\u0000\u0440\u043f\u0001\u0000"+
		"\u0000\u0000\u0440\u0441\u0001\u0000\u0000\u0000\u0441\u0445\u0001\u0000"+
		"\u0000\u0000\u0442\u0444\u0003\u00aeW\u0000\u0443\u0442\u0001\u0000\u0000"+
		"\u0000\u0444\u0447\u0001\u0000\u0000\u0000\u0445\u0443\u0001\u0000\u0000"+
		"\u0000\u0445\u0446\u0001\u0000\u0000\u0000\u0446\u0448\u0001\u0000\u0000"+
		"\u0000\u0447\u0445\u0001\u0000\u0000\u0000\u0448\u0449\u0003\\.\u0000"+
		"\u0449\u044a\u0005\b\u0000\u0000\u044a}\u0001\u0000\u0000\u0000\u044b"+
		"\u044d\u0003\u0006\u0003\u0000\u044c\u044b\u0001\u0000\u0000\u0000\u044d"+
		"\u0450\u0001\u0000\u0000\u0000\u044e\u044c\u0001\u0000\u0000\u0000\u044e"+
		"\u044f\u0001\u0000\u0000\u0000\u044f\u0451\u0001\u0000\u0000\u0000\u0450"+
		"\u044e\u0001\u0000\u0000\u0000\u0451\u0452\u0005R\u0000\u0000\u0452\u0453"+
		"\u0005h\u0000\u0000\u0453\u0454\u0003\u008eG\u0000\u0454\u0455\u0005i"+
		"\u0000\u0000\u0455\u0456\u0003f3\u0000\u0456\u007f\u0001\u0000\u0000\u0000"+
		"\u0457\u0459\u0003\u0006\u0003\u0000\u0458\u0457\u0001\u0000\u0000\u0000"+
		"\u0459\u045c\u0001\u0000\u0000\u0000\u045a\u0458\u0001\u0000\u0000\u0000"+
		"\u045a\u045b\u0001\u0000\u0000\u0000\u045b\u045d\u0001\u0000\u0000\u0000"+
		"\u045c\u045a\u0001\u0000\u0000\u0000\u045d\u045e\u0005\u0012\u0000\u0000"+
		"\u045e\u0461\u0005h\u0000\u0000\u045f\u0462\u0003\\.\u0000\u0460\u0462"+
		"\u0003\u008eG\u0000\u0461\u045f\u0001\u0000\u0000\u0000\u0461\u0460\u0001"+
		"\u0000\u0000\u0000\u0462\u0463\u0001\u0000\u0000\u0000\u0463\u0464\u0005"+
		"i\u0000\u0000\u0464\u0465\u0003f3\u0000\u0465\u0081\u0001\u0000\u0000"+
		"\u0000\u0466\u0468\u0003\u0006\u0003\u0000\u0467\u0466\u0001\u0000\u0000"+
		"\u0000\u0468\u046b\u0001\u0000\u0000\u0000\u0469\u0467\u0001\u0000\u0000"+
		"\u0000\u0469\u046a\u0001\u0000\u0000\u0000\u046a\u046c\u0001\u0000\u0000"+
		"\u0000\u046b\u0469\u0001\u0000\u0000\u0000\u046c\u046e\u0005P\u0000\u0000"+
		"\u046d\u046f\u0005h\u0000\u0000\u046e\u046d\u0001\u0000\u0000\u0000\u046e"+
		"\u046f\u0001\u0000\u0000\u0000\u046f\u0470\u0001\u0000\u0000\u0000\u0470"+
		"\u0472\u0003\u008eG\u0000\u0471\u0473\u0005i\u0000\u0000\u0472\u0471\u0001"+
		"\u0000\u0000\u0000\u0472\u0473\u0001\u0000\u0000\u0000\u0473\u0474\u0001"+
		"\u0000\u0000\u0000\u0474\u0478\u0005d\u0000\u0000\u0475\u0477\u0003\u0084"+
		"B\u0000\u0476\u0475\u0001\u0000\u0000\u0000\u0477\u047a\u0001\u0000\u0000"+
		"\u0000\u0478\u0476\u0001\u0000\u0000\u0000\u0478\u0479\u0001\u0000\u0000"+
		"\u0000\u0479\u047b\u0001\u0000\u0000\u0000\u047a\u0478\u0001\u0000\u0000"+
		"\u0000\u047b\u047c\u0005e\u0000\u0000\u047c\u0083\u0001\u0000\u0000\u0000"+
		"\u047d\u047f\u0003\u0086C\u0000\u047e\u047d\u0001\u0000\u0000\u0000\u047f"+
		"\u0480\u0001\u0000\u0000\u0000\u0480\u047e\u0001\u0000\u0000\u0000\u0480"+
		"\u0481\u0001\u0000\u0000\u0000\u0481\u0483\u0001\u0000\u0000\u0000\u0482"+
		"\u0484\u0003f3\u0000\u0483\u0482\u0001\u0000\u0000\u0000\u0484\u0485\u0001"+
		"\u0000\u0000\u0000\u0485\u0483\u0001\u0000\u0000\u0000\u0485\u0486\u0001"+
		"\u0000\u0000\u0000\u0486\u0085\u0001\u0000\u0000\u0000\u0487\u048b\u0003"+
		"\u0088D\u0000\u0488\u048b\u0003\u008aE\u0000\u0489\u048b\u0003\u008cF"+
		"\u0000\u048a\u0487\u0001\u0000\u0000\u0000\u048a\u0488\u0001\u0000\u0000"+
		"\u0000\u048a\u0489\u0001\u0000\u0000\u0000\u048b\u0087\u0001\u0000\u0000"+
		"\u0000\u048c\u048d\u0005=\u0000\u0000\u048d\u048f\u0003\u009aM\u0000\u048e"+
		"\u0490\u0003\u009eO\u0000\u048f\u048e\u0001\u0000\u0000\u0000\u048f\u0490"+
		"\u0001\u0000\u0000\u0000\u0490\u0491\u0001\u0000\u0000\u0000\u0491\u0492"+
		"\u0005m\u0000\u0000\u0492\u0089\u0001\u0000\u0000\u0000\u0493\u0494\u0005"+
		"=\u0000\u0000\u0494\u0495\u0003\u008eG\u0000\u0495\u0496\u0005m\u0000"+
		"\u0000\u0496\u008b\u0001\u0000\u0000\u0000\u0497\u0498\u0005?\u0000\u0000"+
		"\u0498\u0499\u0005m\u0000\u0000\u0499\u008d\u0001\u0000\u0000\u0000\u049a"+
		"\u049b\u0006G\uffff\uffff\u0000\u049b\u049c\u0005h\u0000\u0000\u049c\u049d"+
		"\u0003\u00a0P\u0000\u049d\u049e\u0005i\u0000\u0000\u049e\u049f\u0003\u008e"+
		"G\u0017\u049f\u04e5\u0001\u0000\u0000\u0000\u04a0\u04a4\u0005f\u0000\u0000"+
		"\u04a1\u04a3\u0005\b\u0000\u0000\u04a2\u04a1\u0001\u0000\u0000\u0000\u04a3"+
		"\u04a6\u0001\u0000\u0000\u0000\u04a4\u04a2\u0001\u0000\u0000\u0000\u04a4"+
		"\u04a5\u0001\u0000\u0000\u0000\u04a5\u04b5\u0001\u0000\u0000\u0000\u04a6"+
		"\u04a4\u0001\u0000\u0000\u0000\u04a7\u04b2\u0003\u0096K\u0000\u04a8\u04ac"+
		"\u0005l\u0000\u0000\u04a9\u04ab\u0005\b\u0000\u0000\u04aa\u04a9\u0001"+
		"\u0000\u0000\u0000\u04ab\u04ae\u0001\u0000\u0000\u0000\u04ac\u04aa\u0001"+
		"\u0000\u0000\u0000\u04ac\u04ad\u0001\u0000\u0000\u0000\u04ad\u04af\u0001"+
		"\u0000\u0000\u0000\u04ae\u04ac\u0001\u0000\u0000\u0000\u04af\u04b1\u0003"+
		"\u0096K\u0000\u04b0\u04a8\u0001\u0000\u0000\u0000\u04b1\u04b4\u0001\u0000"+
		"\u0000\u0000\u04b2\u04b0\u0001\u0000\u0000\u0000\u04b2\u04b3\u0001\u0000"+
		"\u0000\u0000\u04b3\u04b6\u0001\u0000\u0000\u0000\u04b4\u04b2\u0001\u0000"+
		"\u0000\u0000\u04b5\u04a7\u0001\u0000\u0000\u0000\u04b5\u04b6\u0001\u0000"+
		"\u0000\u0000\u04b6\u04ba\u0001\u0000\u0000\u0000\u04b7\u04b9\u0005\b\u0000"+
		"\u0000\u04b8\u04b7\u0001\u0000\u0000\u0000\u04b9\u04bc\u0001\u0000\u0000"+
		"\u0000\u04ba\u04b8\u0001\u0000\u0000\u0000\u04ba\u04bb\u0001\u0000\u0000"+
		"\u0000\u04bb\u04bd\u0001\u0000\u0000\u0000\u04bc\u04ba\u0001\u0000\u0000"+
		"\u0000\u04bd\u04e5\u0005g\u0000\u0000\u04be\u04bf\u0003\u00a0P\u0000\u04bf"+
		"\u04c0\u0003\u009cN\u0000\u04c0\u04e5\u0001\u0000\u0000\u0000\u04c1\u04c2"+
		"\u0005?\u0000\u0000\u04c2\u04c3\u0005h\u0000\u0000\u04c3\u04c4\u0003\u00a0"+
		"P\u0000\u04c4\u04c5\u0005i\u0000\u0000\u04c5\u04e5\u0001\u0000\u0000\u0000"+
		"\u04c6\u04e5\u0003b1\u0000\u04c7\u04e5\u0007\u0000\u0000\u0000\u04c8\u04e5"+
		"\u0003\u0090H\u0000\u04c9\u04ca\u0005j\u0000\u0000\u04ca\u04e5\u0003\u0016"+
		"\u000b\u0000\u04cb\u04cc\u0005h\u0000\u0000\u04cc\u04cd\u0003\u008eG\u0000"+
		"\u04cd\u04ce\u0005i\u0000\u0000\u04ce\u04e5\u0001\u0000\u0000\u0000\u04cf"+
		"\u04d0\u0007\u0007\u0000\u0000\u04d0\u04e5\u0003\u008eG\u0007\u04d1\u04d2"+
		"\u0005L\u0000\u0000\u04d2\u04e5\u0003\u008eG\u0005\u04d3\u04d4\u0005N"+
		"\u0000\u0000\u04d4\u04d5\u0005h\u0000\u0000\u04d5\u04d6\u0003\u00a0P\u0000"+
		"\u04d6\u04d7\u0005i\u0000\u0000\u04d7\u04e5\u0001\u0000\u0000\u0000\u04d8"+
		"\u04d9\u0005h\u0000\u0000\u04d9\u04dc\u0003`0\u0000\u04da\u04db\u0005"+
		"l\u0000\u0000\u04db\u04dd\u0003`0\u0000\u04dc\u04da\u0001\u0000\u0000"+
		"\u0000\u04dd\u04de\u0001\u0000\u0000\u0000\u04de\u04dc\u0001\u0000\u0000"+
		"\u0000\u04de\u04df\u0001\u0000\u0000\u0000\u04df\u04e1\u0001\u0000\u0000"+
		"\u0000\u04e0\u04e2\u0005i\u0000\u0000\u04e1\u04e0\u0001\u0000\u0000\u0000"+
		"\u04e1\u04e2\u0001\u0000\u0000\u0000\u04e2\u04e5\u0001\u0000\u0000\u0000"+
		"\u04e3\u04e5\u0003\u00a0P\u0000\u04e4\u049a\u0001\u0000\u0000\u0000\u04e4"+
		"\u04a0\u0001\u0000\u0000\u0000\u04e4\u04be\u0001\u0000\u0000\u0000\u04e4"+
		"\u04c1\u0001\u0000\u0000\u0000\u04e4\u04c6\u0001\u0000\u0000\u0000\u04e4"+
		"\u04c7\u0001\u0000\u0000\u0000\u04e4\u04c8\u0001\u0000\u0000\u0000\u04e4"+
		"\u04c9\u0001\u0000\u0000\u0000\u04e4\u04cb\u0001\u0000\u0000\u0000\u04e4"+
		"\u04cf\u0001\u0000\u0000\u0000\u04e4\u04d1\u0001\u0000\u0000\u0000\u04e4"+
		"\u04d3\u0001\u0000\u0000\u0000\u04e4\u04d8\u0001\u0000\u0000\u0000\u04e4"+
		"\u04e3\u0001\u0000\u0000\u0000\u04e5\u051e\u0001\u0000\u0000\u0000\u04e6"+
		"\u04e7\n\u0019\u0000\u0000\u04e7\u04e8\u0007\b\u0000\u0000\u04e8\u051d"+
		"\u0003\u008eG\u001a\u04e9\u04ea\n\u0018\u0000\u0000\u04ea\u04eb\u0007"+
		"\t\u0000\u0000\u04eb\u051d\u0003\u008eG\u0019\u04ec\u04ed\n\u0015\u0000"+
		"\u0000\u04ed\u04ee\u0005\u007f\u0000\u0000\u04ee\u051d\u0003\u008eG\u0016"+
		"\u04ef\u04f0\n\u0014\u0000\u0000\u04f0\u04f1\u0005p\u0000\u0000\u04f1"+
		"\u04f2\u0003\u008eG\u0000\u04f2\u04f3\u0005m\u0000\u0000\u04f3\u04f4\u0003"+
		"\u008eG\u0015\u04f4\u051d\u0001\u0000\u0000\u0000\u04f5\u04f6\n\u0006"+
		"\u0000\u0000\u04f6\u04f7\u0005k\u0000\u0000\u04f7\u051d\u0003\u008eG\u0007"+
		"\u04f8\u04f9\n\u0011\u0000\u0000\u04f9\u051d\u0003b1\u0000\u04fa\u04fb"+
		"\n\u000e\u0000\u0000\u04fb\u051d\u0003^/\u0000\u04fc\u04fd\n\r\u0000\u0000"+
		"\u04fd\u04fe\u0005H\u0000\u0000\u04fe\u051d\u0003\u009aM\u0000\u04ff\u0500"+
		"\n\u000b\u0000\u0000\u0500\u0501\u0005j\u0000\u0000\u0501\u051d\u0003"+
		"\u0016\u000b\u0000\u0502\u0503\n\b\u0000\u0000\u0503\u051d\u0007\n\u0000"+
		"\u0000\u0504\u0505\n\u0003\u0000\u0000\u0505\u0506\u0005P\u0000\u0000"+
		"\u0506\u0508\u0005d\u0000\u0000\u0507\u0509\u0005\b\u0000\u0000\u0508"+
		"\u0507\u0001\u0000\u0000\u0000\u0509\u050a\u0001\u0000\u0000\u0000\u050a"+
		"\u0508\u0001\u0000\u0000\u0000\u050a\u050b\u0001\u0000\u0000\u0000\u050b"+
		"\u0514\u0001\u0000\u0000\u0000\u050c\u0511\u0003\u0098L\u0000\u050d\u050e"+
		"\u0005l\u0000\u0000\u050e\u0510\u0003\u0098L\u0000\u050f\u050d\u0001\u0000"+
		"\u0000\u0000\u0510\u0513\u0001\u0000\u0000\u0000\u0511\u050f\u0001\u0000"+
		"\u0000\u0000\u0511\u0512\u0001\u0000\u0000\u0000\u0512\u0515\u0001\u0000"+
		"\u0000\u0000\u0513\u0511\u0001\u0000\u0000\u0000\u0514\u050c\u0001\u0000"+
		"\u0000\u0000\u0514\u0515\u0001\u0000\u0000\u0000\u0515\u0517\u0001\u0000"+
		"\u0000\u0000\u0516\u0518\u0005\b\u0000\u0000\u0517\u0516\u0001\u0000\u0000"+
		"\u0000\u0518\u0519\u0001\u0000\u0000\u0000\u0519\u0517\u0001\u0000\u0000"+
		"\u0000\u0519\u051a\u0001\u0000\u0000\u0000\u051a\u051b\u0001\u0000\u0000"+
		"\u0000\u051b\u051d\u0005e\u0000\u0000\u051c\u04e6\u0001\u0000\u0000\u0000"+
		"\u051c\u04e9\u0001\u0000\u0000\u0000\u051c\u04ec\u0001\u0000\u0000\u0000"+
		"\u051c\u04ef\u0001\u0000\u0000\u0000\u051c\u04f5\u0001\u0000\u0000\u0000"+
		"\u051c\u04f8\u0001\u0000\u0000\u0000\u051c\u04fa\u0001\u0000\u0000\u0000"+
		"\u051c\u04fc\u0001\u0000\u0000\u0000\u051c\u04ff\u0001\u0000\u0000\u0000"+
		"\u051c\u0502\u0001\u0000\u0000\u0000\u051c\u0504\u0001\u0000\u0000\u0000"+
		"\u051d\u0520\u0001\u0000\u0000\u0000\u051e\u051c\u0001\u0000\u0000\u0000"+
		"\u051e\u051f\u0001\u0000\u0000\u0000\u051f\u008f\u0001\u0000\u0000\u0000"+
		"\u0520\u051e\u0001\u0000\u0000\u0000\u0521\u0527\u0005?\u0000\u0000\u0522"+
		"\u0527\u0005C\u0000\u0000\u0523\u0527\u0005Q\u0000\u0000\u0524\u0527\u0005"+
		"I\u0000\u0000\u0525\u0527\u0003\u00a8T\u0000\u0526\u0521\u0001\u0000\u0000"+
		"\u0000\u0526\u0522\u0001\u0000\u0000\u0000\u0526\u0523\u0001\u0000\u0000"+
		"\u0000\u0526\u0524\u0001\u0000\u0000\u0000\u0526\u0525\u0001\u0000\u0000"+
		"\u0000\u0527\u0091\u0001\u0000\u0000\u0000\u0528\u0529\u0005|\u0000\u0000"+
		"\u0529\u052a\u0003\u008eG\u0000\u052a\u0093\u0001\u0000\u0000\u0000\u052b"+
		"\u052c\u0005q\u0000\u0000\u052c\u052d\u0003\u008eG\u0000\u052d\u0095\u0001"+
		"\u0000\u0000\u0000\u052e\u0532\u0003\u008eG\u0000\u052f\u0530\u0005k\u0000"+
		"\u0000\u0530\u0532\u0003\u008eG\u0000\u0531\u052e\u0001\u0000\u0000\u0000"+
		"\u0531\u052f\u0001\u0000\u0000\u0000\u0532\u0097\u0001\u0000\u0000\u0000"+
		"\u0533\u0535\u0003\u009aM\u0000\u0534\u0536\u0003\u009eO\u0000\u0535\u0534"+
		"\u0001\u0000\u0000\u0000\u0535\u0536\u0001\u0000\u0000\u0000\u0536\u0537"+
		"\u0001\u0000\u0000\u0000\u0537\u0538\u0005q\u0000\u0000\u0538\u0539\u0003"+
		"\u008eG\u0000\u0539\u0099\u0001\u0000\u0000\u0000\u053a\u053b\u0006M\uffff"+
		"\uffff\u0000\u053b\u055b\u0003\u008eG\u0000\u053c\u055b\u0005\r\u0000"+
		"\u0000\u053d\u0546\u0005f\u0000\u0000\u053e\u0543\u0003\u009aM\u0000\u053f"+
		"\u0540\u0005l\u0000\u0000\u0540\u0542\u0003\u009aM\u0000\u0541\u053f\u0001"+
		"\u0000\u0000\u0000\u0542\u0545\u0001\u0000\u0000\u0000\u0543\u0541\u0001"+
		"\u0000\u0000\u0000\u0543\u0544\u0001\u0000\u0000\u0000\u0544\u0547\u0001"+
		"\u0000\u0000\u0000\u0545\u0543\u0001\u0000\u0000\u0000\u0546\u053e\u0001"+
		"\u0000\u0000\u0000\u0546\u0547\u0001\u0000\u0000\u0000\u0547\u0548\u0001"+
		"\u0000\u0000\u0000\u0548\u054a\u0005g\u0000\u0000\u0549\u054b\u0003\u009c"+
		"N\u0000\u054a\u0549\u0001\u0000\u0000\u0000\u054a\u054b\u0001\u0000\u0000"+
		"\u0000\u054b\u055b\u0001\u0000\u0000\u0000\u054c\u054d\u0005h\u0000\u0000"+
		"\u054d\u054e\u0003\u009aM\u0000\u054e\u054f\u0005i\u0000\u0000\u054f\u055b"+
		"\u0001\u0000\u0000\u0000\u0550\u0551\u0007\u000b\u0000\u0000\u0551\u055b"+
		"\u0003\u008eG\u0000\u0552\u0554\u0005k\u0000\u0000\u0553\u0555\u0003\u009a"+
		"M\u0000\u0554\u0553\u0001\u0000\u0000\u0000\u0554\u0555\u0001\u0000\u0000"+
		"\u0000\u0555\u055b\u0001\u0000\u0000\u0000\u0556\u0557\u0005\f\u0000\u0000"+
		"\u0557\u055b\u0003\u009aM\u0002\u0558\u0559\u0007\u0005\u0000\u0000\u0559"+
		"\u055b\u0003\u009cN\u0000\u055a\u053a\u0001\u0000\u0000\u0000\u055a\u053c"+
		"\u0001\u0000\u0000\u0000\u055a\u053d\u0001\u0000\u0000\u0000\u055a\u054c"+
		"\u0001\u0000\u0000\u0000\u055a\u0550\u0001\u0000\u0000\u0000\u055a\u0552"+
		"\u0001\u0000\u0000\u0000\u055a\u0556\u0001\u0000\u0000\u0000\u055a\u0558"+
		"\u0001\u0000\u0000\u0000\u055b\u0561\u0001\u0000\u0000\u0000\u055c\u055d"+
		"\n\t\u0000\u0000\u055d\u055e\u0007\f\u0000\u0000\u055e\u0560\u0003\u009a"+
		"M\n\u055f\u055c\u0001\u0000\u0000\u0000\u0560\u0563\u0001\u0000\u0000"+
		"\u0000\u0561\u055f\u0001\u0000\u0000\u0000\u0561\u0562\u0001\u0000\u0000"+
		"\u0000\u0562\u009b\u0001\u0000\u0000\u0000\u0563\u0561\u0001\u0000\u0000"+
		"\u0000\u0564\u0573\u0005\r\u0000\u0000\u0565\u056e\u0005h\u0000\u0000"+
		"\u0566\u056b\u0003\u009cN\u0000\u0567\u0568\u0005l\u0000\u0000\u0568\u056a"+
		"\u0003\u009cN\u0000\u0569\u0567\u0001\u0000\u0000\u0000\u056a\u056d\u0001"+
		"\u0000\u0000\u0000\u056b\u0569\u0001\u0000\u0000\u0000\u056b\u056c\u0001"+
		"\u0000\u0000\u0000\u056c\u056f\u0001\u0000\u0000\u0000\u056d\u056b\u0001"+
		"\u0000\u0000\u0000\u056e\u0566\u0001\u0000\u0000\u0000\u056e\u056f\u0001"+
		"\u0000\u0000\u0000\u056f\u0570\u0001\u0000\u0000\u0000\u0570\u0573\u0005"+
		"i\u0000\u0000\u0571\u0573\u0003\u00a6S\u0000\u0572\u0564\u0001\u0000\u0000"+
		"\u0000\u0572\u0565\u0001\u0000\u0000\u0000\u0572\u0571\u0001\u0000\u0000"+
		"\u0000\u0573\u009d\u0001\u0000\u0000\u0000\u0574\u0575\u0005S\u0000\u0000"+
		"\u0575\u0576\u0003\u008eG\u0000\u0576\u009f\u0001\u0000\u0000\u0000\u0577"+
		"\u0578\u0006P\uffff\uffff\u0000\u0578\u0585\u0003\u0014\n\u0000\u0579"+
		"\u0585\u0007\r\u0000\u0000\u057a\u057b\u0005h\u0000\u0000\u057b\u057e"+
		"\u0003\u00a2Q\u0000\u057c\u057d\u0005l\u0000\u0000\u057d\u057f\u0003\u00a2"+
		"Q\u0000\u057e\u057c\u0001\u0000\u0000\u0000\u057f\u0580\u0001\u0000\u0000"+
		"\u0000\u0580\u057e\u0001\u0000\u0000\u0000\u0580\u0581\u0001\u0000\u0000"+
		"\u0000\u0581\u0582\u0001\u0000\u0000\u0000\u0582\u0583\u0005i\u0000\u0000"+
		"\u0583\u0585\u0001\u0000\u0000\u0000\u0584\u0577\u0001\u0000\u0000\u0000"+
		"\u0584\u0579\u0001\u0000\u0000\u0000\u0584\u057a\u0001\u0000\u0000\u0000"+
		"\u0585\u058e\u0001\u0000\u0000\u0000\u0586\u0588\n\u0004\u0000\u0000\u0587"+
		"\u0589\u0003\u00a4R\u0000\u0588\u0587\u0001\u0000\u0000\u0000\u0589\u058a"+
		"\u0001\u0000\u0000\u0000\u058a\u0588\u0001\u0000\u0000\u0000\u058a\u058b"+
		"\u0001\u0000\u0000\u0000\u058b\u058d\u0001\u0000\u0000\u0000\u058c\u0586"+
		"\u0001\u0000\u0000\u0000\u058d\u0590\u0001\u0000\u0000\u0000\u058e\u058c"+
		"\u0001\u0000\u0000\u0000\u058e\u058f\u0001\u0000\u0000\u0000\u058f\u00a1"+
		"\u0001\u0000\u0000\u0000\u0590\u058e\u0001\u0000\u0000\u0000\u0591\u0593"+
		"\u0003\u00a0P\u0000\u0592\u0594\u0003\u00a6S\u0000\u0593\u0592\u0001\u0000"+
		"\u0000\u0000\u0593\u0594\u0001\u0000\u0000\u0000\u0594\u00a3\u0001\u0000"+
		"\u0000\u0000\u0595\u059e\u0005f\u0000\u0000\u0596\u059b\u0003\u008eG\u0000"+
		"\u0597\u0598\u0005l\u0000\u0000\u0598\u059a\u0003\u008eG\u0000\u0599\u0597"+
		"\u0001\u0000\u0000\u0000\u059a\u059d\u0001\u0000\u0000\u0000\u059b\u0599"+
		"\u0001\u0000\u0000\u0000\u059b\u059c\u0001\u0000\u0000\u0000\u059c\u059f"+
		"\u0001\u0000\u0000\u0000\u059d\u059b\u0001\u0000\u0000\u0000\u059e\u0596"+
		"\u0001\u0000\u0000\u0000\u059e\u059f\u0001\u0000\u0000\u0000\u059f\u05a0"+
		"\u0001\u0000\u0000\u0000\u05a0\u05a1\u0005g\u0000\u0000\u05a1\u00a5\u0001"+
		"\u0000\u0000\u0000\u05a2\u05a4\u0005_\u0000\u0000\u05a3\u05a2\u0001\u0000"+
		"\u0000\u0000\u05a3\u05a4\u0001\u0000\u0000\u0000\u05a4\u05a5\u0001\u0000"+
		"\u0000\u0000\u05a5\u05a6\u0005^\u0000\u0000\u05a6\u00a7\u0001\u0000\u0000"+
		"\u0000\u05a7\u05aa\u0003\u00acV\u0000\u05a8\u05aa\u0003\u00aaU\u0000\u05a9"+
		"\u05a7\u0001\u0000\u0000\u0000\u05a9\u05a8\u0001\u0000\u0000\u0000\u05aa"+
		"\u00a9\u0001\u0000\u0000\u0000\u05ab\u05ac\u0005c\u0000\u0000\u05ac\u00ab"+
		"\u0001\u0000\u0000\u0000\u05ad\u05ae\u0007\u000e\u0000\u0000\u05ae\u00ad"+
		"\u0001\u0000\u0000\u0000\u05af\u05b0\u0007\u000f\u0000\u0000\u05b0\u00af"+
		"\u0001\u0000\u0000\u0000\u00c5\u00b4\u00ba\u00c4\u00c7\u00cb\u00d1\u00d5"+
		"\u00dc\u00e3\u00e6\u00e9\u00eb\u00f1\u00f9\u00fc\u0101\u010b\u010e\u0115"+
		"\u011b\u0121\u0124\u012c\u0133\u0138\u0143\u0146\u014f\u0155\u015c\u0163"+
		"\u016a\u016d\u0171\u0176\u017c\u0183\u018a\u018f\u0195\u019b\u01a1\u01aa"+
		"\u01b0\u01ba\u01bf\u01c5\u01ca\u01ce\u01d4\u01d9\u01df\u01e7\u01ed\u01f2"+
		"\u01f7\u01fc\u0200\u0206\u020c\u0212\u021a\u0220\u0228\u022d\u0233\u0239"+
		"\u023e\u0246\u024e\u0256\u025c\u0261\u026a\u026f\u0275\u027a\u0283\u0287"+
		"\u028b\u0290\u0296\u029c\u029f\u02a2\u02a7\u02ae\u02b3\u02b9\u02bc\u02c3"+
		"\u02c9\u02cf\u02d2\u02d5\u02da\u02e1\u02e6\u02ec\u02ef\u02f6\u02fc\u0302"+
		"\u0308\u030f\u0312\u031a\u0320\u0325\u032d\u0335\u0339\u0345\u034a\u0352"+
		"\u0357\u0362\u0365\u036d\u0370\u0375\u0378\u0382\u038b\u0391\u0397\u03aa"+
		"\u03af\u03b6\u03bb\u03c2\u03c7\u03d3\u03d8\u03de\u03e3\u03ea\u03ef\u03fd"+
		"\u0406\u040e\u0413\u041a\u0420\u0426\u042c\u0431\u0437\u043c\u0440\u0445"+
		"\u044e\u045a\u0461\u0469\u046e\u0472\u0478\u0480\u0485\u048a\u048f\u04a4"+
		"\u04ac\u04b2\u04b5\u04ba\u04de\u04e1\u04e4\u050a\u0511\u0514\u0519\u051c"+
		"\u051e\u0526\u0531\u0535\u0543\u0546\u054a\u0554\u055a\u0561\u056b\u056e"+
		"\u0572\u0580\u0584\u058a\u058e\u0593\u059b\u059e\u05a3\u05a9";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}