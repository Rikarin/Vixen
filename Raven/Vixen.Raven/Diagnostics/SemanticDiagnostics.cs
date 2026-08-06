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

    /// <summary>
    ///     A struct whose storage reaches itself — <c>RVN2008</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not <see cref="CircularDefinition" />, and the difference is the whole reason this
    ///         code exists: <c>RVN2005</c> is about resolution that does not terminate, and
    ///         <c>struct T { var f: T }</c> resolves perfectly happily. What it cannot do is have a
    ///         size. So this is a check at layout time rather than at resolution time, and it names
    ///         the whole route rather than the type — <c>A</c> containing <c>B</c> containing
    ///         <c>A</c> is the case that is hard to see by reading, and naming only <c>A</c> sends
    ///         the author to the wrong file.
    ///     </para>
    ///     <para>
    ///         The message says what a language with references would let the author do, because
    ///         that is the question this diagnostic raises: Raven has no pointer and no reference,
    ///         so a field always holds its value in place and there is nothing to break the cycle
    ///         with. The nearest thing is <c>Buffer&lt;T&gt;</c>, which is a descriptor and may only
    ///         be a shader field (<c>RVN2053</c>) — never a struct member — so it is not an escape
    ///         hatch either.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor RecursiveStructLayout = new(
        "RVN2008",
        "Recursive struct layout",
        "'{0}' contains itself — {1} — so it has no finite size; a field holds its value in place "
        + "and Raven has no reference for one to hold instead",
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

    public static readonly DiagnosticDescriptor ComposeDefaultMustBeShaderName = new(
        "RVN2072",
        "compose slot's default is not a shader name",
        "Compose slot '{0}' can only be initialized with the name of a shader, which is its default when "
        + "the compilation binds nothing",
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

    // --- Streams ----------------------------------------------------------

    /// <summary><c>stream</c> on something that is not a shader's field.</summary>
    /// <remarks>
    ///     A stream is pipeline state — written by one stage and read by the next — so it only
    ///     means anything on the type that <em>is</em> the pipeline. On a struct it would look like
    ///     an ordinary field while claiming to cross a stage boundary that a struct has no part in.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamMustBeShaderField = new(
        "RVN2100",
        "Stream must be a shader field",
        "'{0}' is declared 'stream', which is only meaningful on a shader's field",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary><c>stream</c> combined with a modifier that makes the value not per-invocation.</summary>
    /// <remarks>
    ///     A <c>const</c> and a <c>[Permutation]</c> key are folded at every use, and a
    ///     <c>compose</c> slot holds no value at all. None of the three has storage to thread
    ///     between stages, so combining them with <c>stream</c> asks for two different things at
    ///     once.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamCannotBeConstant = new(
        "RVN2101",
        "Stream cannot also be a constant or a slot",
        "Stream '{0}' cannot also be {1}: a stream is per-invocation storage threaded between stages",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>A stream with an initializer.</summary>
    /// <remarks>
    ///     There is nowhere for the value to come from. A binding's default is host-side data
    ///     (<c>RVN4003</c>); a stream's value is produced by the stage that writes it, per
    ///     invocation, so an initializer would be dead text.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamCannotHaveInitializer = new(
        "RVN2102",
        "Stream cannot have an initializer",
        "Stream '{0}' cannot have an initializer: its value comes from the stage that writes it",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>A stream of a type no stage interface can carry.</summary>
    /// <remarks>
    ///     The same restriction stage inputs and outputs already have, and for the same reason
    ///     rather than by analogy: Vulkan has no boolean interface type, and an aggregate would
    ///     need a location for every leaf. Reported at the declaration instead of as
    ///     <c>RVN4001</c> from each backend, because the declaration is what has to change.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamTypeNotSupported = new(
        "RVN2103",
        "Stream type is not supported",
        "Stream '{0}' has type '{1}'; a stream must be a non-boolean scalar or vector, which is what a "
        + "stage interface can carry",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- The compute stage -------------------------------------------------

    /// <summary>A compute entry point with no workgroup size.</summary>
    /// <remarks>
    ///     Required rather than defaulted to <c>(1, 1, 1)</c>. A default would compile, run, and
    ///     be wrong by whatever factor the author assumed — one invocation per workgroup where 64
    ///     were intended reads past the end of every tile — and no later stage could tell that the
    ///     size was guessed rather than chosen.
    /// </remarks>
    public static readonly DiagnosticDescriptor ComputeNeedsWorkgroupSize = new(
        "RVN2104",
        "Compute entry point needs a workgroup size",
        "Compute entry point '{0}' needs a workgroup size: write it as [ComputeShader(x, y, z)], "
        + "where the dimensions not given are 1",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>A workgroup size that could not be read.</summary>
    public static readonly DiagnosticDescriptor WorkgroupSizeNotValid = new(
        "RVN2105",
        "Workgroup size is not valid",
        "The workgroup size on '{0}' must be one to three positive integer literals, given positionally",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     A workgroup size on a stage that has no workgroups.
    /// </summary>
    /// <remarks>
    ///     The RVN2091 policy: legal syntax that changes nothing, so it is named rather than
    ///     ignored. Only a compute dispatch has a workgroup — a vertex or fragment stage's invocation
    ///     count is the draw's, not the shader's.
    /// </remarks>
    public static readonly DiagnosticDescriptor WorkgroupSizeOnGraphicsStage = new(
        "RVN2106",
        "Workgroup size has no effect on this stage",
        "'{0}' is a {1} entry point, which has no workgroups, so the size has no effect",
        Shader,
        DiagnosticSeverity.Warning
    );

    /// <summary>
    ///     A compute entry point returning a value, or taking a parameter that is not a dispatch
    ///     built-in.
    /// </summary>
    /// <remarks>
    ///     A compute stage has no pipeline interface: no vertex attributes to feed a parameter and
    ///     no framebuffer to take a return value. So a parameter has to be one of the dispatch
    ///     built-ins, and a result has to be written to a resource — reported here rather than
    ///     emitted as a location nothing binds.
    /// </remarks>
    public static readonly DiagnosticDescriptor ComputeHasNoStageInterface = new(
        "RVN2107",
        "Compute stage has no pipeline interface",
        "{0} on compute entry point '{1}': a compute stage has no {2}",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>[Semantic("…")]</c> that names no dispatch built-in on a compute parameter.</summary>
    public static readonly DiagnosticDescriptor UnknownComputeSemantic = new(
        "RVN2108",
        "Unknown compute semantic",
        "'{0}' is not a dispatch built-in; a compute parameter must be one of {1}",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>A dispatch built-in declared with a type it cannot have.</summary>
    public static readonly DiagnosticDescriptor ComputeSemanticTypeMismatch = new(
        "RVN2109",
        "Dispatch built-in has the wrong type",
        "'{0}' is a {1} in both targets, but '{2}' is declared '{3}'",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- inout -------------------------------------------------------------

    /// <summary>An <c>inout</c> argument that is not storage the callee's value can be written back to.</summary>
    /// <remarks>
    ///     Reported instead of leaving overload resolution to fail, so the message names the
    ///     parameter and the reason rather than saying no overload applies. A property is refused
    ///     here too: it has no storage, and copying out through a setter would call an accessor the
    ///     call site never wrote.
    /// </remarks>
    public static readonly DiagnosticDescriptor InOutArgumentMustBeAssignable = new(
        "RVN2110",
        "inout argument must be assignable storage",
        "The argument for the inout parameter '{0}' must be assignable storage, because the "
        + "parameter's value is written back to it when the call returns",
        Binding,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>inout</c> argument whose type needs a conversion.</summary>
    /// <remarks>
    ///     Exact rather than implicitly convertible, and this is the one rule people are surprised
    ///     by. A widening on the way in would have to narrow on the way out, which loses the value
    ///     the callee wrote — so an <c>int</c> passed to an <c>inout float</c> is refused rather
    ///     than silently round-tripped through a conversion that cannot be undone.
    /// </remarks>
    public static readonly DiagnosticDescriptor InOutArgumentTypeMustMatch = new(
        "RVN2111",
        "inout argument type must match exactly",
        "The argument for the inout parameter '{0}' has type '{1}' but the parameter is '{2}'; an "
        + "inout argument must match exactly, because a conversion on the way in cannot be undone "
        + "on the way out",
        Binding,
        DiagnosticSeverity.Error
    );

    /// <summary><c>inout</c> on an entry point's parameter.</summary>
    /// <remarks>
    ///     An entry point's parameters come from the pipeline — a vertex attribute, a dispatch
    ///     built-in — and there is nothing on the other side of the call to write back to.
    /// </remarks>
    public static readonly DiagnosticDescriptor InOutOnEntryPoint = new(
        "RVN2112",
        "Entry point parameter cannot be inout",
        "Parameter '{0}' of entry point '{1}' cannot be inout: an entry point is called by the "
        + "pipeline, which has nowhere to copy the value back to",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>inout</c> parameter with a default value.</summary>
    /// <remarks>
    ///     A default exists so the argument can be omitted, and an omitted argument has no storage
    ///     to write back to — so the two features contradict each other rather than compose.
    /// </remarks>
    public static readonly DiagnosticDescriptor InOutCannotHaveDefault = new(
        "RVN2113",
        "inout parameter cannot have a default",
        "The inout parameter '{0}' cannot have a default value: an omitted argument has no storage "
        + "to copy back to",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary><c>inout</c> on an operator's parameter.</summary>
    /// <remarks>
    ///     An operator is invoked by expression syntax, so there is no call site at which to write
    ///     one down — <c>a + b</c> cannot say that <c>a</c> is passed by reference, and an operator
    ///     that mutated its operand would make an expression's meaning depend on evaluation order.
    /// </remarks>
    public static readonly DiagnosticDescriptor InOutOnOperator = new(
        "RVN2114",
        "Operator parameter cannot be inout",
        "Parameter '{0}' of operator '{1}' cannot be inout: an operator is invoked as an expression, "
        + "which has no syntax for passing an argument by reference",
        Declaration,
        DiagnosticSeverity.Error
    );

    // --- Array sizes ------------------------------------------------------

    /// <summary>An array size that is not a compile-time constant.</summary>
    /// <remarks>
    ///     A GPU allocates no memory at run time: the length is part of the type, decorated into the
    ///     SPIR-V and written into the GLSL declaration, and the host reads it back out of the
    ///     reflection to size the buffer it uploads. A size known only at run time has no answer to
    ///     give any of them. A <c>const</c> field, an enum member and a <c>[Permutation] val</c> all
    ///     qualify — the last is the interesting one, because it lets the host pick the length.
    /// </remarks>
    public static readonly DiagnosticDescriptor ArraySizeNotConstant = new(
        "RVN2115",
        "Array size must be a constant",
        "The size of an array must be a compile-time constant, and '{0}' is not",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>An array size that folds to something other than a positive integer.</summary>
    /// <remarks>
    ///     Zero is excluded along with the negatives: <c>OpTypeArray</c> requires a length greater
    ///     than zero, and a zero-length array in GLSL is a compile error too. Reported here rather
    ///     than left for the backends, so the two cannot disagree.
    /// </remarks>
    public static readonly DiagnosticDescriptor ArraySizeNotPositive = new(
        "RVN2116",
        "Array size must be positive",
        "An array size must be an integer greater than zero; '{0}' is {1}",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A constant index outside a sized array's bounds.</summary>
    /// <remarks>
    ///     Out-of-bounds access is undefined behaviour on a GPU, and undefined there means a wrong
    ///     pixel on one driver and a device loss on another. When both the index and the length are
    ///     known at compile time there is no reason to find out which.
    /// </remarks>
    public static readonly DiagnosticDescriptor IndexOutOfRange = new(
        "RVN2117",
        "Index is outside the array",
        "Index {0} is outside '{1}', which has {2} element(s)",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Writable resources -----------------------------------------------

    /// <summary>A storage buffer whose element type has no memory layout.</summary>
    /// <remarks>
    ///     A buffer's element is host-written memory, so it needs an offset for every leaf. A texture
    ///     or a sampler is a descriptor rather than a value and has no bytes to lay out; a nested
    ///     buffer is a second descriptor, which is what a pointer would be and Raven has none.
    /// </remarks>
    public static readonly DiagnosticDescriptor BufferElementNotStorable = new(
        "RVN2118",
        "Buffer element has no memory layout",
        "'{0}' cannot be the element type of a '{1}': a buffer's elements are host-written memory, "
        + "and this type has no layout the host could write",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A write to a binding the host uploads rather than the shader produces.</summary>
    /// <remarks>
    ///     <para>
    ///         Pre-existing and stage-independent, and both reference compilers reject the store — a
    ///         uniform is read-only in GLSL and a <c>Uniform</c>-class pointer is not writable in
    ///         SPIR-V. It went unreported for as long as it did because a shader with nothing writable
    ///         had no correct alternative to suggest. <c>RWBuffer&lt;T&gt;</c> is that alternative,
    ///         which is what makes this worth reporting rather than merely noting.
    ///     </para>
    ///     <para>
    ///         The read-only <c>Buffer&lt;T&gt;</c> is included: it is the same descriptor as the
    ///         writable form, so the mistake is a one-character fix, and leaving it to the driver
    ///         would mean a <c>NonWritable</c> decoration contradicting a store in the same module.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor CannotWriteToBinding = new(
        "RVN2119",
        "Binding is read-only",
        "'{0}' cannot be assigned to: {1}",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Push constants ----------------------------------------------------

    /// <summary>A <c>[PushConstant]</c> on something that is not bytes the host can push.</summary>
    /// <remarks>
    ///     A push constant is a small run of memory written into the command buffer. A texture, a
    ///     sampler or a buffer is a <em>descriptor</em> — a handle the driver resolves — so there is
    ///     nothing to push, and both targets reject a push-constant block containing one. Reported
    ///     at the declaration rather than left to the backends, because the declaration is what has
    ///     to change and two backends would otherwise say it twice.
    /// </remarks>
    public static readonly DiagnosticDescriptor PushConstantMustBeAValue = new(
        "RVN2120",
        "Push constant is not a value",
        "'{0}' cannot be a push constant: a push constant is bytes the host writes into the command "
        + "buffer, and '{1}' is a descriptor rather than a value",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A descriptor-set marker on a field that is a push constant.</summary>
    /// <remarks>
    ///     On the <c>RVN2091</c> policy: the shader still compiles and the marker is simply
    ///     dropped, but the author believes the value lives in a descriptor set and it does not —
    ///     that is the one thing a push constant is defined by not doing.
    /// </remarks>
    public static readonly DiagnosticDescriptor PushConstantHasNoSet = new(
        "RVN2121",
        "Descriptor-set marker on a push constant",
        "'[{1}]' on '{0}' has no effect: a push constant is not in a descriptor set",
        Binding,
        DiagnosticSeverity.Warning
    );

    // --- Flow --------------------------------------------------------------

    /// <summary>A local read on a path that never assigned it.</summary>
    /// <remarks>
    ///     An error rather than a warning, because of what it costs on a GPU: an unassigned local is
    ///     not an exception and not a zero, it is whatever was in the register — which differs
    ///     between drivers, between invocations and between debug and release. That is the shape of
    ///     bug that reproduces on one machine and nowhere else.
    /// </remarks>
    public static readonly DiagnosticDescriptor UseOfUnassignedLocal = new(
        "RVN2127",
        "Local is read before it is assigned",
        "'{0}' is read here on a path that has not assigned it. An unassigned local holds whatever "
        + "the target left in the register, so this reads a different value on every driver",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A statement no path reaches.</summary>
    /// <remarks>
    ///     A warning, on the <c>RVN2091</c> policy: the shader still means what it says, but the
    ///     author believes this code runs and it does not. Said once per run of unreachable
    ///     statements rather than once per statement.
    /// </remarks>
    public static readonly DiagnosticDescriptor UnreachableStatement = new(
        "RVN2128",
        "Statement is unreachable",
        "This statement cannot be reached: the path to it ends in {0}",
        Declaration,
        DiagnosticSeverity.Warning
    );

    /// <summary>A value-returning function whose end is reachable.</summary>
    /// <remarks>
    ///     The same undefined value <c>RVN2127</c> is about, seen from the other end: falling off
    ///     the end of a function that promises a value hands the caller whatever the target had.
    ///     Neither backend can diagnose it, because by then the return is simply missing.
    /// </remarks>
    public static readonly DiagnosticDescriptor NotAllPathsReturn = new(
        "RVN2129",
        "Not all paths return a value",
        "'{0}' can reach its end without returning, and its result would be whatever the target "
        + "left behind",
        Declaration,
        DiagnosticSeverity.Error
    );

    // --- Arrays ------------------------------------------------------------

    /// <summary>An array type with no length.</summary>
    /// <remarks>
    ///     <para>
    ///         The length is not a detail of an array type, it <em>is</em> part of it: SPIR-V's
    ///         <c>OpTypeArray</c> takes a constant extent, GLSL writes one into the declaration,
    ///         <c>ArrayStride</c> is computed from it, and the host reads it back to size the buffer
    ///         it uploads. So there is nowhere an unsized array can go — not a binding, not a
    ///         parameter (both targets pass arrays by value), not a local.
    ///     </para>
    ///     <para>
    ///         Reported at the declaration rather than left to the backends' <c>RVN4001</c>, because
    ///         the declaration is what has to change and there are exactly two ways to change it:
    ///         give it a length, or make it a <c>Buffer&lt;T&gt;</c>, which is what a count the host
    ///         decides actually is. Both backends said so twice with no source span between them,
    ///         which is the shape worth removing rather than just the message.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ArrayNeedsLength = new(
        "RVN2126",
        "Array type has no length",
        "'{0}' has no length, and an array's length is part of its type on both targets. Give it one "
        + "— '{1}[4]' — or declare it '{2}<{1}>', which is an array whose count the host decides",
        Declaration,
        DiagnosticSeverity.Error
    );

    // --- Storage images ----------------------------------------------------

    /// <summary>A storage image whose element is not a four-lane texel.</summary>
    /// <remarks>
    ///     Both targets read and write four components whatever the format stores — an
    ///     <c>r32f</c> image reads as <c>(r, 0, 0, 1)</c>. Declaring <c>RWTexture2D&lt;float&gt;</c>
    ///     would be a shape neither target has, so it is refused rather than quietly widened.
    /// </remarks>
    public static readonly DiagnosticDescriptor StorageImageElementNotATexel = new(
        "RVN2122",
        "Storage image element is not a texel",
        "'{0}' cannot be the element type of a '{1}': a storage image is read and written four "
        + "components at a time, so the element must be 'float4', 'int4' or 'uint4'",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A storage image with no <c>[Format("…")]</c>.</summary>
    /// <remarks>
    ///     Required rather than defaulted. GLSL needs the layout qualifier on any image that is
    ///     read, and SPIR-V needs a known <c>ImageFormat</c> or the module must declare
    ///     <c>StorageImageReadWithoutFormat</c> — a capability not every device offers. There is
    ///     also no format that could be guessed: the host creates the view, and only the shader
    ///     author knows what it will hold.
    /// </remarks>
    public static readonly DiagnosticDescriptor StorageImageNeedsFormat = new(
        "RVN2123",
        "Storage image has no format",
        "'{0}' needs a '[Format(\"…\")]': a storage image's texel format is part of its "
        + "declaration in both targets. One of: {1}",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>[Format]</c> naming something that is not a format, or not this image's.</summary>
    public static readonly DiagnosticDescriptor StorageImageFormatMismatch = new(
        "RVN2124",
        "Storage image format does not match its element",
        "'{0}' is declared '[Format(\"{1}\")]', which {2}",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>Texture2D&lt;T&gt;</c> whose element is not an integer texel.</summary>
    /// <remarks>
    ///     The angle-bracket form exists for the views the plain <c>Texture2D</c> cannot describe:
    ///     integer formats, whose component type Vulkan checks against the descriptor. A float
    ///     element would be the built-in spelled twice — one of them without <c>Sample</c> — so it
    ///     is refused and the message points back at the spelling that works. Scalars are refused
    ///     for the reason <c>RVN2122</c> refuses them on a storage image: a fetch returns four
    ///     lanes on both targets, whatever the format stores.
    /// </remarks>
    public static readonly DiagnosticDescriptor SampledTextureElementNotIntegral = new(
        "RVN2136",
        "Sampled texture element is not an integer texel",
        "'{0}' cannot be the element type of a '{1}': the angle-bracket form is for integer-sampled "
        + "textures and a fetch returns four lanes, so the element must be 'int4' or 'uint4' — a "
        + "float-sampled texture is the plain 'Texture2D'",
        Declaration,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>[Format]</c> on something that is not a storage image.</summary>
    /// <remarks>
    ///     On the <c>RVN2091</c> policy: nothing else in the language has a texel format, so the
    ///     attribute is dropped — but the author believes it says something and it does not.
    /// </remarks>
    public static readonly DiagnosticDescriptor FormatOnNonImage = new(
        "RVN2125",
        "Format on a declaration that has no texels",
        "'[Format]' on '{0}' has no effect: only a storage image has a texel format",
        Binding,
        DiagnosticSeverity.Warning
    );

    // --- Atomics -----------------------------------------------------------

    /// <summary>An atomic on something no atomic can operate on.</summary>
    /// <remarks>
    ///     <para>
    ///         Two shapes, one rule. The target has to be <em>memory</em>, because the whole content
    ///         of "atomic" is that the read and the write are one operation on one location —
    ///         <c>atomicAdd(count + 1u, 1u)</c> would have to modify a copy, which is an ordinary add
    ///         spelled expensively. And it has to be memory <em>the dispatch shares</em>, because an
    ///         atomic on storage only one invocation can reach has nothing to be indivisible against.
    ///     </para>
    ///     <para>
    ///         The second half is not pedantry: GLSL refuses it outright — <i>"only l-values
    ///         corresponding to shader block storage or shared variables can be used with atomic
    ///         memory functions"</i> — so allowing it here would be a shader that binds, verifies and
    ///         then fails in one backend. Reported at the call for the same reason every other
    ///         two-backend rule is reported before them.
    ///     </para>
    ///     <para>
    ///         Checked after overload resolution, for the same reason <c>inout</c>'s check is:
    ///         nothing at the call site marks an argument as storage, so folding it into
    ///         applicability would turn "you passed an expression" into "no overload applies", which
    ///         names neither the argument nor the reason.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor AtomicTargetMustBeStorage = new(
        "RVN2130",
        "Atomic target is not shared memory",
        "The first argument of '{0}' cannot be operated on atomically: {1}",
        Binding,
        DiagnosticSeverity.Error
    );

    // --- Workgroup-shared storage ------------------------------------------

    /// <summary>
    ///     <c>groupshared</c> outside a shader.
    /// </summary>
    /// <remarks>
    ///     A workgroup is a property of a dispatch, so the storage belongs to the shader that
    ///     declares the entry point. On a struct it would be a field with a storage class its
    ///     containing value knows nothing about — and a struct is copied, which shared memory
    ///     cannot be.
    /// </remarks>
    public static readonly DiagnosticDescriptor GroupSharedMustBeShaderField = new(
        "RVN2131",
        "Group-shared storage must be a shader member",
        "'{0}' is 'groupshared' but is not declared on a shader: workgroup storage belongs to a "
        + "dispatch, which only a shader has",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     <c>groupshared</c> combined with something that decides where the value lives.
    /// </summary>
    /// <remarks>
    ///     Each of these already answers the question <c>groupshared</c> answers, and differently: a
    ///     <c>const</c> has no storage, a <c>compose</c> slot is not data, and a <c>stream</c> is
    ///     per-invocation in the pipeline's interface. One declaration cannot be two of them.
    /// </remarks>
    public static readonly DiagnosticDescriptor GroupSharedConflict = new(
        "RVN2132",
        "Group-shared storage conflicts with another modifier",
        "'{0}' cannot be both 'groupshared' and {1}: the two say different things about where the "
        + "value lives",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>groupshared</c> declaration of a type that cannot be workgroup storage.</summary>
    /// <remarks>
    ///     A descriptor is not a value: a texture, a sampler or a buffer is a handle the host binds,
    ///     and neither target has a <c>Workgroup</c> variable of one. Reported at the declaration
    ///     rather than as a backend failure, because the declaration is what has to change.
    /// </remarks>
    public static readonly DiagnosticDescriptor GroupSharedTypeNotSupported = new(
        "RVN2133",
        "Group-shared type is not supported",
        "'{0}' is 'groupshared' at type '{1}': workgroup storage holds values, and a resource is a "
        + "descriptor the host binds",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     A <c>groupshared</c> declaration with an initializer.
    /// </summary>
    /// <remarks>
    ///     There is nothing that could run it. Workgroup storage is uninitialized in both targets,
    ///     and one invocation writing a value every other invocation would also write is a race
    ///     rather than an initialization — which is exactly why the pattern is a store followed by a
    ///     <c>barrier()</c>, written where the author can see it.
    /// </remarks>
    public static readonly DiagnosticDescriptor GroupSharedCannotHaveInitializer = new(
        "RVN2134",
        "Group-shared storage cannot have an initializer",
        "'{0}' is 'groupshared' and cannot have an initializer: workgroup storage starts undefined, "
        + "so write it and then 'barrier()'",
        Shader,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     A <c>groupshared</c> declaration that is also <c>val</c> or <c>readonly</c>.
    /// </summary>
    /// <remarks>
    ///     Reported rather than tolerated because it is the whole point of the storage: nothing else
    ///     can ever write it — there is no host, no initializer and no pipeline stage upstream — so
    ///     a read-only workgroup variable is guaranteed to be undefined at every read.
    /// </remarks>
    public static readonly DiagnosticDescriptor GroupSharedCannotBeReadOnly = new(
        "RVN2135",
        "Group-shared storage cannot be read-only",
        "'{0}' is 'groupshared' and read-only: nothing else can write workgroup storage, so every "
        + "read of it would be undefined — declare it 'var'",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- Bindings that a storage class cannot hold -------------------------

    /// <summary>A binding whose type contains a <c>bool</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>SPIR-V forbids it outright</strong>: <c>OpTypeBool</c> is only legal in the
    ///         storage classes that are not externally visible — <c>Workgroup</c>, <c>Private</c>,
    ///         <c>Function</c>, <c>Input</c>, <c>Output</c> and the ray-tracing payloads — and a
    ///         binding is in <c>Uniform</c>, <c>StorageBuffer</c> or <c>PushConstant</c>. The reason
    ///         is the same one <c>StageInterface.CanCarry</c> refuses a boolean varying for:
    ///         a bool has no size a host can write, because how many bytes it occupies and which of
    ///         them mean true is the implementation's business and not the module's.
    ///     </para>
    ///     <para>
    ///         Refused rather than widened to a <c>uint</c>, which is what HLSL and GLSL do. Widening
    ///         is not a lowering detail here — it is a host-side ABI rule, because the engine writes
    ///         these buffers from the reflection and would have to be told that this member is four
    ///         bytes holding 0 or 1 rather than the <c>bool</c> the shader declares. Nothing wanted
    ///         it: every <c>bool</c> at shader scope in the shipped library is a
    ///         <c>[Permutation]</c> key, which is the honest spelling of a flag that is constant for
    ///         a draw, and a flag that really does vary per draw is a <c>uint</c> the author can see
    ///         the width of.
    ///     </para>
    ///     <para>
    ///         Reported at the declaration on the <c>RVN2126</c> principle — the declaration is what
    ///         has to change, and there are exactly two ways to change it.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor BindingCannotBeBoolean = new(
        "RVN2137",
        "Binding cannot contain a boolean",
        "'{0}' is a binding of type '{1}', and a boolean has no representation a host can write: "
        + "SPIR-V allows 'bool' only in storage classes that are not externally visible. Mark it "
        + "'[Permutation]' if it is constant for a draw, or declare it 'uint'",
        Shader,
        DiagnosticSeverity.Error
    );

    // --- Attributes --------------------------------------------------------

    /// <summary>An attribute whose name is not one the compiler reads.</summary>
    /// <remarks>
    ///     <para>
    ///         On the <c>RVN2091</c> policy, and it is the member of that family with the sharpest
    ///         teeth. An attribute Raven does not know is dropped in silence, and dropping one
    ///         <em>changes what the declaration means</em> rather than merely failing to add to it:
    ///         a <c>[Permutaton]</c> with a letter missing stops being a compile-time key and becomes
    ///         an ordinary uniform, so a branch the author expected to be eliminated is now
    ///         predicated on host data, and every variant of the shader collapses into one.
    ///     </para>
    ///     <para>
    ///         A warning rather than an error because Raven's attributes are pure syntax — they are
    ///         read off the tree by name and never resolved to a symbol, so there is no declaration
    ///         an author could add to make an unknown one legal, and nothing else in the file
    ///         depends on it. The name is what has to change, which is what a warning says.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor AttributeNotRecognised = new(
        "RVN2138",
        "Attribute is not recognised",
        "'{0}' is not an attribute Raven reads, so it is ignored. One of: {1}",
        Declaration,
        DiagnosticSeverity.Warning
    );

    // --- Collection literals -----------------------------------------------

    /// <summary>A <c>[]</c> with nothing in it to take an element type from.</summary>
    /// <remarks>
    ///     <para>
    ///         A collection literal in Raven is inferred from its contents and never from the place
    ///         it is going — there is no target-typed <c>[]</c>, and
    ///         docs/plan/07-raven-shader-pipeline.md § B describes the literal entirely in terms of
    ///         what its elements contribute, a spread included. So an empty one has no element type
    ///         there is any way to learn. It also has no length worth having: an array is sized, and
    ///         zero is not a size (<c>RVN2116</c>).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The type it used to get instead was <c>?[0]</c>, and every position that could
    ///         reject it did — except the one that does not look.</b> Assigning it reported
    ///         <c>RVN2020</c> and declaring it reported <c>RVN2116</c>, so the only survivor was
    ///         <c>[]</c> as an expression statement, where nothing asks what it is: the binder said
    ///         nothing, the lowerer said <c>RVN3001</c>, and the SPIR-V backend — which the fuzz
    ///         harness runs whatever the lowerer thought — emitted
    ///         <c>OpCompositeConstruct %void</c>. It is <c>Corpus/raven/9352e56acef97227.bin</c>.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor EmptyCollectionHasNoElementType = new(
        "RVN2139",
        "Empty collection literal",
        "'[]' has no elements to take an element type from, and Raven does not infer one from "
        + "context; write the elements, or an array declaration with a size",
        Binding,
        DiagnosticSeverity.Error
    );
}
