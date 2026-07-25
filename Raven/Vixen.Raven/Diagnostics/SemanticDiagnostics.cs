// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Diagnostics;

/// <summary>
///     Stable descriptors for semantic (declaration and binding) diagnostics. The
///     <c>RVN2xxx</c> range is reserved for semantics; <c>RVN1xxx</c> is syntax
///     (see <see cref="SyntaxDiagnostics" />) and later phases claim their own.
/// </summary>
public static class SemanticDiagnostics {
    const string Declaration = "Declaration";
    const string Binding = "Binding";
    const string Shader = "Shader";

    // --- Declarations -----------------------------------------------------

    public static readonly DiagnosticDescriptor DuplicateDeclaration = new(
        "RVN2001",
        "Duplicate declaration",
        "'{0}' is already declared in this scope",
        Declaration,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor TypeNotFound = new(
        "RVN2002",
        "Type not found",
        "The type or namespace name '{0}' could not be found",
        Declaration,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NotAType = new(
        "RVN2003",
        "Not a type",
        "'{0}' is not a type",
        Declaration,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor WrongTypeArgumentCount = new(
        "RVN2004",
        "Wrong number of type arguments",
        "'{0}' takes {1} type argument(s), but {2} were given",
        Declaration,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor CircularDefinition = new(
        "RVN2005",
        "Circular definition",
        "The definition of '{0}' is circular",
        Declaration,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor MissingTypeOrInitializer = new(
        "RVN2006",
        "Cannot infer type",
        "'{0}' needs either a type annotation or an initializer",
        Declaration,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor CyclicBaseType = new(
        "RVN2007",
        "Cyclic base type",
        "'{0}' cannot inherit from itself, directly or indirectly",
        Declaration,
        DiagnosticSeverity.Error
    );

    // --- Names and members ------------------------------------------------

    public static readonly DiagnosticDescriptor UndefinedName = new(
        "RVN2010",
        "Undefined name",
        "The name '{0}' does not exist in the current context",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor MemberNotFound = new(
        "RVN2011",
        "Member not found",
        "'{0}' does not contain a definition for '{1}'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor AmbiguousName = new(
        "RVN2012",
        "Ambiguous name",
        "The name '{0}' is ambiguous in this context",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor TypeUsedAsValue = new(
        "RVN2013",
        "Type used as a value",
        "'{0}' is a type, which is not valid in this context",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor SelfOutsideType = new(
        "RVN2014",
        "'self' outside a type",
        "'{0}' is only valid inside a type declaration",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NoBaseType = new(
        "RVN2015",
        "No base type",
        "'{0}' has no base type, so 'base' cannot be used here",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Conversions and operators ---------------------------------------

    public static readonly DiagnosticDescriptor CannotConvert = new(
        "RVN2020",
        "Cannot convert type",
        "Cannot implicitly convert type '{0}' to '{1}'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NoExplicitConversion = new(
        "RVN2021",
        "No conversion exists",
        "Cannot convert type '{0}' to '{1}'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor BinaryOperatorNotDefined = new(
        "RVN2022",
        "Operator not defined",
        "Operator '{0}' cannot be applied to operands of type '{1}' and '{2}'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor UnaryOperatorNotDefined = new(
        "RVN2023",
        "Operator not defined",
        "Operator '{0}' cannot be applied to an operand of type '{1}'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ConditionMustBeBool = new(
        "RVN2024",
        "Condition must be bool",
        "Cannot use a value of type '{0}' as a condition; expected 'bool'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor StringLiteralIsNotAValue = new(
        "RVN2025",
        "String literal is not a value",
        "A string literal is metadata only; it is valid in an attribute argument, not as a value",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Calls ------------------------------------------------------------

    public static readonly DiagnosticDescriptor NotInvocable = new(
        "RVN2030",
        "Not invocable",
        "'{0}' is not a method and cannot be invoked",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NoApplicableOverload = new(
        "RVN2031",
        "No applicable overload",
        "No overload of '{0}' takes the given arguments ({1})",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor AmbiguousInvocation = new(
        "RVN2032",
        "Ambiguous invocation",
        "The call to '{0}' is ambiguous between {1}",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor WrongArgumentCount = new(
        "RVN2033",
        "Wrong number of arguments",
        "'{0}' takes {1} argument(s), but {2} were given",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NoConstructor = new(
        "RVN2034",
        "No matching constructor",
        "'{0}' has no constructor taking the given arguments",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Assignment and flow ----------------------------------------------

    public static readonly DiagnosticDescriptor NotAssignable = new(
        "RVN2040",
        "Not assignable",
        "'{0}' cannot be assigned to; it is read-only",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NotAnLValue = new(
        "RVN2041",
        "Not assignable",
        "The left-hand side of an assignment must be a variable, field or property",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ReturnValueInVoidMethod = new(
        "RVN2042",
        "Unexpected return value",
        "'{0}' returns no value, so 'return' cannot take an expression",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor MissingReturnValue = new(
        "RVN2043",
        "Missing return value",
        "'{0}' returns '{1}', so 'return' requires an expression",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor CannotIndex = new(
        "RVN2044",
        "Cannot index",
        "Cannot apply indexing to a value of type '{0}'",
        Binding,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NotIterable = new(
        "RVN2045",
        "Not iterable",
        "Cannot iterate over a value of type '{0}'",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Shader semantics --------------------------------------------------

    public static readonly DiagnosticDescriptor DuplicateEntryPoint = new(
        "RVN2050",
        "Duplicate entry point",
        "Shader '{0}' declares more than one '{1}' entry point",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor EntryPointCannotBeGeneric = new(
        "RVN2051",
        "Generic entry point",
        "Entry point '{0}' cannot be generic",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor StageAttributeOutsideShader = new(
        "RVN2052",
        "Stage attribute outside a shader",
        "'{0}' is only valid on a method declared in a shader",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ResourceMustBeShaderField = new(
        "RVN2053",
        "Resource outside a shader",
        "A resource of type '{0}' can only be declared as a shader field",
        Shader,
        DiagnosticSeverity.Error
    );
}
