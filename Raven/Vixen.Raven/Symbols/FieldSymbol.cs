// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>A <c>val</c>/<c>var</c> member of a type, or an enum member.</summary>
public abstract class FieldSymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Field;

    public abstract TypeSymbol Type { get; }

    /// <summary>Declared with <c>val</c> (or synthesized read-only) — assignable only in an initializer or constructor.</summary>
    public virtual bool IsReadOnly => false;

    /// <summary>Declared <c>const</c>: a compile-time constant.</summary>
    public virtual bool IsConst => false;

    /// <summary>The folded value of a <c>const</c> field, when it could be computed.</summary>
    public virtual object? ConstantValue => null;

    /// <summary>
    ///     Declared <c>[Permutation]</c>: a constant whose value the caller supplies per
    ///     effect variant rather than the source fixing it.
    /// </summary>
    /// <remarks>
    ///     A permutation key behaves as a constant everywhere downstream —
    ///     <see cref="IsConst" /> is true and <see cref="ConstantValue" /> answers with the
    ///     supplied value, or the declared default when none was supplied — so folding and
    ///     dead-branch elimination need no special case for it.
    /// </remarks>
    public virtual bool IsPermutation => false;

    /// <summary>
    ///     A <c>val</c> type parameter — <c>shader Blur&lt;val TapCount: int&gt;</c> — rather than a
    ///     field. Like a <c>[Permutation]</c> key it is a compile-time constant, but it has no
    ///     default: a value is part of the shader's signature.
    /// </summary>
    public virtual bool IsValueParameter => false;

    /// <summary>
    ///     Declared <c>compose</c>: a protocol-typed slot filled by a concrete shader chosen
    ///     when the shader is compiled.
    /// </summary>
    public virtual bool IsCompose => false;

    /// <summary>
    ///     Declared <c>stream</c>: per-invocation storage threaded between pipeline stages.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not a binding, and that is the distinction that matters downstream: a stream has no
    ///         descriptor, no <c>(set, binding)</c> and nothing the host writes. It is a stage
    ///         input, a stage output, or both, and which of those it is follows from whether the
    ///         stage's code reads it or writes it rather than from anything said at the
    ///         declaration.
    ///     </para>
    ///     <para>
    ///         The point of declaring it once on the shader rather than threading it through
    ///         signatures is that a function anywhere in the stage's call graph can contribute to
    ///         it. That is what lets a material feature add an interstage value without every
    ///         entry point in the pipeline changing shape.
    ///     </para>
    /// </remarks>
    public virtual bool IsStream => false;

    /// <summary>
    ///     Declared <c>groupshared</c>: storage every invocation of one workgroup reaches and no
    ///     other workgroup does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not a binding, for the same reason a <c>stream</c> is not: there is no descriptor, no
    ///         <c>(set, binding)</c>, and nothing the host writes. What distinguishes it from a
    ///         local is <em>who</em> reaches it — one copy per workgroup rather than one per
    ///         invocation — and that is exactly the property hierarchical traversal needs: a
    ///         workgroup pops a node, tests its children and pushes the survivors through a queue
    ///         with a local head, which is this or it is a global atomic per child.
    ///     </para>
    ///     <para>
    ///         It is also the second thing an atomic may operate on. The first, a writable
    ///         resource, is the whole dispatch's; this one is the workgroup's, and both are shared
    ///         by more than one invocation, which is the only property an atomic actually requires.
    ///     </para>
    ///     <para>
    ///         Compute only, and refused elsewhere rather than emitted: <c>Workgroup</c> storage
    ///         exists in a graphics stage in neither target, and a fragment shader that declared one
    ///         would be describing memory the pipeline has no groups to give it.
    ///     </para>
    /// </remarks>
    public virtual bool IsGroupShared => false;

    /// <summary>
    ///     The literal written in the declaration, or null when there is none.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="ConstantValue" />, which for a <c>[Permutation]</c> key answers
    ///     with the value this compilation was <em>given</em>. This one answers with what the source
    ///     says, and — the reason it exists — reading it never records a permutation use. Describing
    ///     a shader must not change its cache key.
    /// </remarks>
    public virtual object? DeclaredValue => null;

    /// <summary>
    ///     The shader bound to this <c>compose</c> slot, or null when the field is not a slot
    ///     or nothing valid is bound. Calls through the slot go straight to this type's
    ///     members, so there is no dispatch at runtime.
    /// </summary>
    public virtual NamedTypeSymbol? ComposedType => null;

    /// <summary>How this field binds on the GPU when it is a shader member.</summary>
    public virtual ResourceKind ResourceKind => ResourceKind.None;

    /// <summary>
    ///     Whether this field becomes a descriptor binding — state the host supplies rather than
    ///     storage the shader owns.
    /// </summary>
    /// <remarks>
    ///     One answer, read by the lowerer when it collects a shader's bindings and by the binder
    ///     when it decides whether a write is legal. Two copies of this rule would mean a field the
    ///     binder let you assign to and the lowerer turned into a uniform, which both reference
    ///     compilers reject and neither Raven layer would have mentioned.
    /// </remarks>
    public bool IsBinding =>
        ContainingType is { TypeKind: TypeKind.Shader } && !IsConst && !IsCompose && !IsStream && !IsGroupShared;

    /// <summary>
    ///     The descriptor set this field's binding belongs to, from a <c>[PerFrame]</c> /
    ///     <c>[PerView]</c> / <c>[PerMaterial]</c> / <c>[PerDraw]</c> marker.
    /// </summary>
    /// <remarks>
    ///     Defaults to <see cref="Symbols.ResourceSet.PerMaterial" />: an unmarked field is a
    ///     material parameter, which is what set 2 is for. Defaulting to set 0 instead would
    ///     drop every unannotated shader into the engine's per-frame set, where it would
    ///     collide with the camera and lighting buffers.
    /// </remarks>
    public virtual ResourceSet ResourceSet => ResourceSet.PerMaterial;

    /// <summary>
    ///     Whether this field is marked <c>[PushConstant]</c>: a small value the host writes into
    ///     the command buffer rather than into a descriptor.
    /// </summary>
    /// <remarks>
    ///     Not a fifth <see cref="Symbols.ResourceSet" />, because a push constant is not in a set —
    ///     it has no descriptor at all, which is the point of it. A shader gets one push-constant
    ///     block, so the marker says "this value" rather than "this set", and
    ///     <see cref="ResourceSet" /> stops meaning anything for a field that carries it.
    /// </remarks>
    public virtual bool IsPushConstant => false;

    /// <summary>
    ///     Whether this binding is one resource for the whole compilation rather than a contribution
    ///     from each feature that declares it — <c>[Shared]</c>.
    /// </summary>
    /// <remarks>
    ///     Only meaningful on a binding a composed shader declares. Two features that both declare a
    ///     <c>[Shared]</c> binding of the same name get one binding between them, bound once and
    ///     named as it was declared rather than by the path it was reached through — which is what
    ///     the frame's texture table has to be, and what an ordinary contribution deliberately is
    ///     not.
    /// </remarks>
    public virtual bool IsShared => false;

    /// <summary>
    ///     Whether this field is the index of the material record a draw reads —
    ///     <c>[MaterialIndex]</c>.
    /// </summary>
    /// <remarks>
    ///     At most one per shader, and its presence turns that shader's per-material block into an
    ///     element of a buffer rather than a set bound per draw.
    /// </remarks>
    public virtual bool IsMaterialIndex => false;

    /// <summary>
    ///     The texel format from <c>[Format("…")]</c>, resolved. Null unless this field is a
    ///     storage image with a recognised format.
    /// </summary>
    /// <remarks>
    ///     A property of the field rather than of the type, because it is: two
    ///     <c>RWTexture2D&lt;float4&gt;</c> bindings may hold <c>rgba16f</c> and <c>rgba8</c>, and
    ///     the element type — four float lanes — is the same for both. What the type says is what
    ///     the shader sees; what this says is how the texels are stored.
    /// </remarks>
    public virtual ImageFormat? ImageFormat => null;

    /// <summary>The pipeline semantic from a <c>[Semantic("…")]</c> attribute, or null.</summary>
    public virtual string? SemanticName => null;

    public override string ToDisplayString() => ContainingType is { } type ? $"{type.ToDisplayString()}.{Name}" : Name;
}

/// <summary>
///     A field the compiler makes up rather than reading from source: vector
///     swizzles (<c>v.xy</c>), tuple elements, <c>array.Length</c>.
/// </summary>
public sealed class SynthesizedFieldSymbol : FieldSymbol {
    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override TypeSymbol Type { get; }
    public override bool IsReadOnly { get; }

    /// <summary>
    ///     Set for a sized array's <c>Length</c>, which makes it usable everywhere a constant
    ///     is — including as another array's size.
    /// </summary>
    public override object? ConstantValue { get; }

    public override bool IsConst => ConstantValue is not null;

    internal SynthesizedFieldSymbol(
        TypeSymbol containingType,
        string name,
        TypeSymbol type,
        bool isReadOnly,
        object? constantValue = null
    ) {
        ContainingSymbol = containingType;
        Name = name;
        Type = type;
        IsReadOnly = isReadOnly;
        ConstantValue = constantValue;
    }
}
