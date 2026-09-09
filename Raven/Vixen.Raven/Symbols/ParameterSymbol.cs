// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>How a parameter passes its argument.</summary>
public enum RefKind {
    /// <summary>By value. The callee's changes are its own.</summary>
    None,

    /// <summary>
    ///     By reference, with copy-in/copy-out semantics: the argument's value goes in, the
    ///     parameter's value comes back out.
    /// </summary>
    /// <remarks>
    ///     Copy-in/copy-out rather than true aliasing, and that is a specification rather than an
    ///     implementation detail. GLSL's <c>inout</c> is defined the same way, and SPIR-V has no
    ///     reference type at all — so a definition that promised aliasing could not be honoured on
    ///     either target. It also means two <c>inout</c> arguments naming the same storage do not
    ///     interfere until the copies are written back, in argument order.
    /// </remarks>
    InOut
}

/// <summary>A parameter of a method, constructor, indexer or lambda.</summary>
public abstract class ParameterSymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Parameter;

    public abstract TypeSymbol Type { get; }

    /// <summary>Position in the declaring signature.</summary>
    public abstract int Ordinal { get; }

    /// <summary>How this parameter passes its argument.</summary>
    public virtual RefKind RefKind => RefKind.None;

    /// <summary>True when the declaration supplies a default (<c>count: int = 42</c>).</summary>
    public virtual bool HasDefaultValue => false;

    /// <summary>The default's constant value, when it is a literal.</summary>
    public virtual object? DefaultValue => null;

    /// <summary>The pipeline semantic from a <c>[Semantic("…")]</c> attribute, or null.</summary>
    public virtual string? SemanticName => null;

    /// <summary>
    ///     How this parameter is interpolated when it is a stage input — from
    ///     <c>[Interpolation("…")]</c>, and <see cref="InterpolationMode.Smooth" /> otherwise.
    /// </summary>
    /// <remarks>
    ///     An entry point's own parameters are the other half of the stage interface a
    ///     <c>stream</c> makes: a fragment stage may receive a varying either way, so an
    ///     interpolation that only one of the two could carry would be a hole an author falls into
    ///     without a diagnostic.
    /// </remarks>
    public virtual InterpolationMode Interpolation => InterpolationMode.Smooth;

    public override string ToDisplayString() {
        var direction = RefKind == RefKind.InOut ? "inout " : string.Empty;
        return $"{direction}{Name}: {Type.ToDisplayString()}";
    }
}

/// <summary>A parameter of a built-in signature (intrinsic function, resource method).</summary>
public sealed class SynthesizedParameterSymbol : ParameterSymbol {
    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override TypeSymbol Type { get; }
    public override int Ordinal { get; }

    internal SynthesizedParameterSymbol(Symbol container, string name, TypeSymbol type, int ordinal) {
        ContainingSymbol = container;
        Name = name;
        Type = type;
        Ordinal = ordinal;
    }
}
