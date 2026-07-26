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

    // --- Permutations -----------------------------------------------------
    //
    // A [Permutation] field is a constant whose value arrives from outside the source
    // — the engine supplies it per effect variant. The rules below exist so that a
    // permutation key is always resolvable at compile time and always has a value:
    // the whole point is that branches on it fold away before codegen.

    public static readonly DiagnosticDescriptor PermutationMustBeShaderField = new(
        "RVN2060",
        "Permutation outside a shader",
        "'{0}' is marked [Permutation] but is not a shader field; only a shader declares permutation keys",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor PermutationMustBeReadOnly = new(
        "RVN2061",
        "Mutable permutation",
        "Permutation key '{0}' must be declared 'val' or 'const' — it is fixed when the shader is compiled",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor PermutationTypeNotSupported = new(
        "RVN2062",
        "Unsupported permutation type",
        "Permutation key '{0}' has type '{1}'; a permutation key must be bool, int or uint",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor PermutationNeedsDefault = new(
        "RVN2063",
        "Permutation without a default",
        "Permutation key '{0}' needs a literal initializer: it is the value used when the key is not supplied",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor PermutationValueTypeMismatch = new(
        "RVN2064",
        "Permutation value has the wrong type",
        "Permutation key '{0}' was supplied a value of type '{1}' but is declared '{2}'",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor PermutationCannotBeAssigned = new(
        "RVN2065",
        "Assignment to a permutation key",
        "Permutation key '{0}' cannot be assigned; its value is fixed when the shader is compiled",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- Composition ------------------------------------------------------
    //
    // `compose val diffuse: IDiffuseModel` is a slot: the shader is written against the
    // protocol, and each material says which shader fills it. Resolution is at compile
    // time, so every rule here exists to make the slot resolvable before codegen.

    public static readonly DiagnosticDescriptor ComposeMustBeShaderField = new(
        "RVN2070",
        "compose outside a shader",
        "'{0}' is declared 'compose' but is not a shader field; only a shader composes",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeMustBeProtocolTyped = new(
        "RVN2071",
        "compose slot is not protocol-typed",
        "Compose slot '{0}' has type '{1}'; a slot must be declared against a protocol so any implementation fits",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeCannotHaveInitializer = new(
        "RVN2072",
        "compose slot with an initializer",
        "Compose slot '{0}' cannot have an initializer; the implementation is chosen when the shader is compiled",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeNotBound = new(
        "RVN2073",
        "Unfilled compose slot",
        "Compose slot '{0}' of type '{1}' has no implementation bound",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeBindingNotFound = new(
        "RVN2074",
        "compose binding names an unknown shader",
        "Compose slot '{0}' is bound to '{1}', which is not a type in this compilation",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeBindingMustBeShader = new(
        "RVN2075",
        "compose binding is not a shader",
        "Compose slot '{0}' is bound to '{1}', which is a {2}; only a shader can fill a slot",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeBindingDoesNotImplement = new(
        "RVN2076",
        "compose binding does not implement the protocol",
        "Shader '{1}' does not implement '{2}', so it cannot fill compose slot '{0}'",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ComposeCannotBeAssigned = new(
        "RVN2077",
        "Assignment to a compose slot",
        "Compose slot '{0}' cannot be assigned; its implementation is chosen when the shader is compiled",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- Value type parameters --------------------------------------------
    //
    // `shader Blur<val TapCount: int>` parameterises a shader by a compile-time constant.
    // Unlike a [Permutation] field it has no default: the value is part of the signature,
    // so compiling without one is an error rather than a fallback.

    public static readonly DiagnosticDescriptor ValueParameterMustBeOnShader = new(
        "RVN2080",
        "Value parameter outside a shader",
        "'{0}' is a value parameter, which only a shader may declare",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ValueParameterTypeNotSupported = new(
        "RVN2081",
        "Unsupported value parameter type",
        "Value parameter '{0}' has type '{1}'; a value parameter must be bool, int or uint",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ValueParameterNotSupplied = new(
        "RVN2082",
        "Value parameter without a value",
        "Value parameter '{0}' of '{1}' has no value; supply one as '{1}.{0}=…' or '{0}=…'",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ValueParameterTypeMismatch = new(
        "RVN2083",
        "Value parameter has the wrong type",
        "Value parameter '{0}' was supplied a value of type '{1}' but is declared '{2}'",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ValueParameterCannotBeAssigned = new(
        "RVN2084",
        "Assignment to a value parameter",
        "Value parameter '{0}' cannot be assigned; its value is fixed when the shader is compiled",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- Descriptor sets ---------------------------------------------------
    //
    // `[PerFrame] var time: float` places a binding in the engine's four-set convention
    // (docs/plan/05). A field carries at most one marker, and only a field that becomes a
    // binding has anything to place.

    public static readonly DiagnosticDescriptor ResourceSetConflict = new(
        "RVN2090",
        "Conflicting descriptor-set markers",
        "'{0}' is marked both '{1}' and '{2}'; a binding belongs to exactly one descriptor set",
        Shader,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor ResourceSetOnNonBinding = new(
        "RVN2091",
        "Descriptor-set marker on something that is not a binding",
        "'{0}' is not a descriptor binding, so '{1}' has no effect",
        Shader,
        DiagnosticSeverity.Warning
    );

    /// <summary>
    ///     A shader declared an <c>init</c>. Nothing ever constructs a shader — it is the
    ///     pipeline, not a value — so the body could never run.
    /// </summary>
    /// <remarks>
    ///     An error rather than a warning because the code reads as initialising the bindings and
    ///     does not. A binding default says the same thing honestly: it becomes host-side data,
    ///     which the backend reports as <c>RVN4003</c>.
    /// </remarks>
    public static readonly DiagnosticDescriptor ShaderCannotBeConstructed = new(
        "RVN2092",
        "Constructor on a shader",
        "Shader '{0}' declares 'init', but a shader is never constructed, so it could never run; "
        + "give the binding a default instead",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- Meaningless-but-plausible source ----------------------------------
    //
    // Same policy as RVN2091: the code is still correct, but the author believes
    // something untrue about what a modifier or attribute does, so it is named
    // rather than silently ignored.

    /// <summary>
    ///     A modifier that is legal syntax but changes nothing where it stands —
    ///     <c>override</c> on a field, <c>compose</c> on a method, anything on an
    ///     <c>init</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor ModifierHasNoEffect = new(
        "RVN2093",
        "Modifier has no effect",
        "The '{0}' modifier has no effect on {1} '{2}'",
        Declaration,
        DiagnosticSeverity.Warning
    );

    /// <summary>
    ///     An enum member's initializer did not evaluate to a compile-time integer.
    ///     Reported rather than silently substituting the ordinal, which is what an
    ///     earlier version did.
    /// </summary>
    public static readonly DiagnosticDescriptor EnumMemberValueNotConstant = new(
        "RVN2094",
        "Enum member value is not a constant",
        "The value of enum member '{0}' must be a compile-time integer constant",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>Nothing reads attributes on a statement; the author probably expected a hint like [Unroll] to act.</summary>
    public static readonly DiagnosticDescriptor AttributesOnStatementHaveNoEffect = new(
        "RVN2095",
        "Attributes on a statement have no effect",
        "Attributes on a statement are not used by the compiler and have no effect",
        Binding,
        DiagnosticSeverity.Warning
    );

    /// <summary>A type argument was checked against its parameter's <c>where</c> clause and failed.</summary>
    public static readonly DiagnosticDescriptor TypeArgumentDoesNotSatisfyConstraint = new(
        "RVN2096",
        "Type argument does not satisfy constraint",
        "Type argument '{0}' does not satisfy the constraint '{1}' on type parameter '{2}' of '{3}'",
        Binding,
        DiagnosticSeverity.Error
    );
}
