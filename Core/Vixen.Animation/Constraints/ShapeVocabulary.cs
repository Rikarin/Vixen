// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;

namespace Vixen.Animation.Constraints;

/// <summary>A shape name a project uses, and what it means.</summary>
/// <param name="Name">The name — <c>belly</c>, <c>left-palm</c>.</param>
/// <param name="Meaning">What a shape called that is, in a sentence somebody can read.</param>
public readonly record struct ShapeTerm(Symbol Name, string Meaning);

/// <summary>A tag a project uses, and what it affords.</summary>
/// <param name="Tag">The tag — <c>affords=grip-surface</c>.</param>
/// <param name="Meaning">What carrying it entitles a constraint to assume.</param>
public readonly record struct TagTerm(Facet Tag, string Meaning);

/// <summary>One shape an entity of some class is expected to carry.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Kind">Which primitive it has to be.</param>
/// <param name="Tags">What it has to afford.</param>
/// <param name="Default">How big it is on the template body.</param>
/// <param name="Required">
///     Whether a set missing it is invalid, or merely incomplete for the clips that name it.
/// </param>
public readonly record struct ShapeClassMember(
    Symbol Name,
    ShapeKind Kind,
    FacetSet Tags,
    ShapeParams Default,
    bool Required
);

/// <summary>The full set of shapes an entity of some kind is expected to carry.</summary>
/// <param name="Name">The class — <c>humanoid</c>, <c>quadruped</c>, <c>vehicle</c>.</param>
/// <param name="Members">What one of them has.</param>
/// <remarks>
///     ⚠ <b>A name alone says "if you have a belly, call it <c>belly</c>". A class says "a humanoid
///     <em>has</em> a belly"</b>, and that is the statement a clip authored on one member needs in
///     order to be portable to every other. It is also the declaration a generator derives against:
///     [33 § D15](../../../docs/plan/33-character-creator.md) produces a shape set from a character
///     archetype rather than authoring it, and a generator needs a specification of what to generate —
///     the same one this uses to validate. Two documents needing the same declaration is the argument
///     for it being one file rather than a convention in each.
/// </remarks>
public sealed record ShapeClass(Symbol Name, ShapeClassMember[] Members);

/// <summary>What went wrong when a set was checked against a vocabulary.</summary>
/// <param name="Shape">Which shape it is about, or <see cref="Symbol.None" /> for the set itself.</param>
/// <param name="Message">What is wrong, naming the set and the shape.</param>
public readonly record struct ShapeValidation(Symbol Shape, string Message) {
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>The shape names and tags a project uses, and the classes it declares.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A declared asset, not a convention, and this looks like bureaucracy right up until it
///         is the difference between the feature working and not.</b> A clip's constraint refers to a
///         shape by name, and the clip is portable exactly as far as that name is present and means
///         the same thing on every body it might play on. Without a declared vocabulary the failure is
///         a clip that silently does nothing on one character, discovered by a player. With one, it is
///         a validation error at import naming the set and the missing name.
///     </para>
/// </remarks>
public sealed class ShapeVocabulary {
    readonly ShapeTerm[] shapes;
    readonly TagTerm[] tags;
    readonly ShapeClass[] classes;

    /// <summary>Declares a vocabulary.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="shapes">The shape names it allows.</param>
    /// <param name="tags">The tags it allows.</param>
    /// <param name="classes">The classes it declares.</param>
    public ShapeVocabulary(
        string name,
        IEnumerable<ShapeTerm>? shapes = null,
        IEnumerable<TagTerm>? tags = null,
        IEnumerable<ShapeClass>? classes = null
    ) {
        ArgumentNullException.ThrowIfNull(name);

        Name = Symbol.Intern(name);
        this.shapes = shapes is null ? [] : [.. shapes];
        this.tags = tags is null ? [] : [.. tags];
        this.classes = classes is null ? [] : [.. classes];
    }

    /// <summary>What it is called.</summary>
    public Symbol Name { get; }

    /// <summary>The shape names it allows.</summary>
    /// <returns>The names.</returns>
    public ReadOnlySpan<ShapeTerm> Shapes => shapes;

    /// <summary>The tags it allows.</summary>
    /// <returns>The tags.</returns>
    public ReadOnlySpan<TagTerm> Tags => tags;

    /// <summary>The classes it declares.</summary>
    /// <returns>The classes.</returns>
    public ReadOnlySpan<ShapeClass> Classes => classes;

    /// <summary>The class with a name, or <see langword="null" />.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The class, or <see langword="null" />.</returns>
    public ShapeClass? Class(Symbol name) {
        foreach (var declared in classes) {
            if (declared.Name == name) {
                return declared;
            }
        }

        return null;
    }

    /// <summary>Checks a set against the vocabulary, and optionally against one of its classes.</summary>
    /// <param name="set">The set.</param>
    /// <param name="into">Where the findings go.</param>
    /// <param name="memberOf">
    ///     Which class the set claims to be a member of, or <see cref="Symbol.None" /> to check only
    ///     the names and tags.
    /// </param>
    /// <returns>Whether it passed.</returns>
    /// <remarks>
    ///     ⚠ <b>Findings rather than an exception, and every one names the set.</b> A vocabulary
    ///     check is run over a whole project at import, and the useful output is a list an author can
    ///     work through — not the first problem, thrown.
    /// </remarks>
    public bool Validate(ProxyShapeSet set, ICollection<ShapeValidation> into, Symbol memberOf = default) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(into);

        var before = into.Count;

        if (shapes.Length > 0) {
            foreach (var shape in set.Shapes) {
                if (!Declares(shape.Name)) {
                    into.Add(
                        new(
                            shape.Name,
                            $"The proxy shape set '{set.Name}' has a shape called '{shape.Name}', which the "
                            + $"vocabulary '{Name}' does not declare. A clip naming it would work here and "
                            + "nowhere else."
                        )
                    );
                }
            }
        }

        if (tags.Length > 0) {
            foreach (var shape in set.Shapes) {
                foreach (var tag in shape.Tags.Facets) {
                    if (!Declares(tag)) {
                        into.Add(
                            new(
                                shape.Name,
                                $"The proxy shape set '{set.Name}' tags '{shape.Name}' with '{tag}', which the "
                                + $"vocabulary '{Name}' does not declare."
                            )
                        );
                    }
                }
            }
        }

        if (memberOf.IsSome) {
            ValidateClass(set, into, memberOf);
        }

        return into.Count == before;
    }

    void ValidateClass(ProxyShapeSet set, ICollection<ShapeValidation> into, Symbol memberOf) {
        if (Class(memberOf) is not { } declared) {
            into.Add(new(Symbol.None, $"The vocabulary '{Name}' declares no class called '{memberOf}'."));
            return;
        }

        foreach (var member in declared.Members) {
            var index = set.IndexOf(member.Name);

            if (index < 0) {
                if (member.Required) {
                    into.Add(
                        new(
                            member.Name,
                            $"The proxy shape set '{set.Name}' claims to be a '{memberOf}' and has no "
                            + $"'{member.Name}'. Every clip with a contact there does nothing on this body."
                        )
                    );
                }

                continue;
            }

            var shape = set[index];

            if (shape.Kind != member.Kind) {
                into.Add(
                    new(
                        member.Name,
                        $"The proxy shape set '{set.Name}' has '{member.Name}' as a {shape.Kind}, and a "
                        + $"'{memberOf}' declares it a {member.Kind}. A surface coordinate authored on one "
                        + "parameterisation means something else on the other."
                    )
                );
            }

            if (member.Tags.Count > 0 && !shape.Tags.ContainsAll(member.Tags)) {
                into.Add(
                    new(
                        member.Name,
                        $"The proxy shape set '{set.Name}' has '{member.Name}' without {member.Tags}, which a "
                        + $"'{memberOf}' declares it affords."
                    )
                );
            }
        }
    }

    bool Declares(Symbol shape) {
        foreach (var term in shapes) {
            if (term.Name == shape) {
                return true;
            }
        }

        return false;
    }

    bool Declares(Facet tag) {
        foreach (var term in tags) {
            if (term.Tag == tag) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({shapes.Length} names, {classes.Length} classes)";
}
