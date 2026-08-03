// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>One shape, as a file holds it.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Kind">Which primitive.</param>
/// <param name="Joint">Which joint it hangs off, <b>by name</b>.</param>
/// <param name="Position">Where it sits relative to that joint.</param>
/// <param name="Rotation">Which way it is turned.</param>
/// <param name="Extents">Half its size at the base.</param>
/// <param name="TopExtents">Half its size at the top, or zero to match the base.</param>
/// <param name="Tags">What it affords, as <c>key=value</c>.</param>
/// <param name="Coarse">Whether it survives the coarse generator unmerged.</param>
/// <remarks>
///     ⚠ <b>The joint is a name and not an index</b>, for <c>AnimationChannel</c>'s reason: an index
///     is a fact about the rig the set was authored against, and a set that survives a joint being
///     inserted is worth more than one that loads a byte faster.
/// </remarks>
[DataContract("ProxyShapeRecord")]
public sealed record ProxyShapeRecord(
    string Name,
    ShapeKind Kind,
    string Joint,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Extents,
    Vector3 TopExtents,
    string[] Tags,
    bool Coarse
) {
    /// <summary>A record with nothing filled in, for a deserialiser to write into.</summary>
    public ProxyShapeRecord() : this("", ShapeKind.Box, "", Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.Zero, [], false) {
    }
}

/// <summary>A proxy shape set, as an artefact holds it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no separate authored type, unlike <c>AnimationClipContent</c>, and the
///         difference is real rather than an inconsistency.</b> A clip is authored as curves and
///         shipped as samples, so the two forms hold different things and a compile step turns one
///         into the other. A shape set is authored as exactly what it ships as: names, primitives and
///         numbers. The only work the pipeline does is <em>checking</em> it — against the vocabulary,
///         and for the mistakes a text file invites — and inventing a second identical type to have
///         somewhere to do that would be ceremony.
///     </para>
///     <para>
///         The <see cref="Extensions" /> block round-trips markup this build does not understand, the
///         same as a clip's.
///     </para>
/// </remarks>
[DataContract("ProxyShapeSetContent")]
public sealed class ProxyShapeSetContent {
    /// <summary>The version this build writes.</summary>
    public const int Current = 1;

    /// <summary>The file extension.</summary>
    public const string Extension = ".vxproxyshapes";

    /// <summary>Which version of the format wrote it.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the set is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The <c>.vxshapevocab</c> it implements, by path, or empty for none.</summary>
    public string Vocabulary { get; set; } = string.Empty;

    /// <summary>The model whose skeleton it was authored against, by path, or empty for none.</summary>
    /// <remarks>
    ///     ⚠ <b>An authoring-time reference, and <see cref="Bake" /> deliberately ignores it.</b> A
    ///     set is baked against whichever rig wears it — that portability is the entire reason a
    ///     shape names its joint rather than indexing it, and a set that would only load on one body
    ///     would give it up. What the field is for is the editor: posing a shape needs a skeleton,
    ///     and a file that does not say which one leaves every panel that shows it guessing.
    /// </remarks>
    public string Rig { get; set; } = string.Empty;

    /// <summary>Which of that vocabulary's classes it claims to be a member of, or empty.</summary>
    public string Class { get; set; } = string.Empty;

    /// <summary>The shapes.</summary>
    public ProxyShapeRecord[] Shapes { get; set; } = [];

    /// <summary>Markup this build did not interpret, kept so a round trip does not drop it.</summary>
    public Dictionary<string, string> Extensions { get; set; } = [];

    /// <summary>Resolves the set against a skeleton.</summary>
    /// <param name="skeleton">The rig it is worn by.</param>
    /// <param name="unresolved">
    ///     Where the names of shapes whose joint the rig does not have go, or <see langword="null" />
    ///     to drop them silently.
    /// </param>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     ⚠ <b>A shape naming a joint the rig does not have is skipped, not defaulted to the root.</b>
    ///     A shape at the root is a shape in the middle of the character, and a contact resolving there
    ///     is a hand in somebody's chest — which is much harder to diagnose than a contact that does
    ///     nothing.
    /// </remarks>
    public ProxyShapeSet Bake(Skeleton skeleton, ICollection<string>? unresolved = null) {
        ArgumentNullException.ThrowIfNull(skeleton);

        List<ProxyShape> built = [];

        foreach (var record in Shapes) {
            var joint = skeleton.IndexOf(record.Joint);

            if (joint < 0) {
                unresolved?.Add(record.Name);
                continue;
            }

            built.Add(
                new() {
                    Name = Symbol.Intern(record.Name),
                    Kind = record.Kind,
                    Joint = joint,
                    Offset = new(record.Position, record.Rotation, Vector3.One),
                    Dimensions = new(record.Extents, record.TopExtents == Vector3.Zero ? record.Extents : record.TopExtents),
                    Tags = ShapeTags.Parse(record.Tags),
                    Coarse = record.Coarse
                }
            );
        }

        // ⚠ The vocabulary comes through. The importer builds its own set the same way and passes it,
        // so a bake that dropped it made two differently-populated sets out of one file — harmless
        // today because nothing at runtime reads it, and exactly the divergence that is found by the
        // first thing that does.
        return ProxyShapeSet.Of(Name, Vocabulary.Length > 0 ? Vocabulary : null, [.. built]);
    }
}

/// <summary>One declared shape name, as a file holds it.</summary>
/// <param name="Name">The name.</param>
/// <param name="Meaning">What a shape called that is.</param>
[DataContract("ShapeTermRecord")]
public sealed record ShapeTermRecord(string Name, string Meaning) {
    /// <summary>A record with nothing filled in.</summary>
    public ShapeTermRecord() : this("", "") {
    }
}

/// <summary>One declared tag, as a file holds it.</summary>
/// <param name="Tag">The tag, as <c>key=value</c>.</param>
/// <param name="Meaning">What carrying it entitles a constraint to assume.</param>
[DataContract("ShapeTagRecord")]
public sealed record ShapeTagRecord(string Tag, string Meaning) {
    /// <summary>A record with nothing filled in.</summary>
    public ShapeTagRecord() : this("", "") {
    }
}

/// <summary>One member of a declared class, as a file holds it.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Kind">Which primitive it has to be.</param>
/// <param name="Tags">What it has to afford.</param>
/// <param name="Extents">How big it is on the template body.</param>
/// <param name="TopExtents">How big at the top, or zero to match.</param>
/// <param name="Required">Whether a set missing it is invalid.</param>
[DataContract("ShapeClassMemberRecord")]
public sealed record ShapeClassMemberRecord(
    string Name,
    ShapeKind Kind,
    string[] Tags,
    Vector3 Extents,
    Vector3 TopExtents,
    bool Required
) {
    /// <summary>A record with nothing filled in.</summary>
    public ShapeClassMemberRecord() : this("", ShapeKind.Box, [], Vector3.Zero, Vector3.Zero, true) {
    }
}

/// <summary>One declared class, as a file holds it.</summary>
/// <param name="Name">The class.</param>
/// <param name="Members">What one of them has.</param>
[DataContract("ShapeClassRecord")]
public sealed record ShapeClassRecord(string Name, ShapeClassMemberRecord[] Members) {
    /// <summary>A record with nothing filled in.</summary>
    public ShapeClassRecord() : this("", []) {
    }
}

/// <summary>Something wrong with a vocabulary, and whether it stops the file compiling.</summary>
/// <param name="Name">What it is about.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Fatal">Whether the import refuses over it.</param>
public readonly record struct VocabularyProblem(string Name, string Message, bool Fatal) {
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>A shape vocabulary, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>This is the file [33 § D15](../../../docs/plan/33-character-creator.md) generates
///     against.</b> That document derives a shape set from a character archetype rather than authoring
///     it, and a generator needs a specification of what to generate — the same one this validates
///     against. Two documents needing the same declaration is the argument for it being one file
///     rather than a convention in each.
/// </remarks>
[DataContract("ShapeVocabularyContent")]
public sealed class ShapeVocabularyContent {
    /// <summary>The version this build writes.</summary>
    public const int Current = 1;

    /// <summary>The file extension.</summary>
    public const string Extension = ".vxshapevocab";

    /// <summary>Which version of the format wrote it.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the vocabulary is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The shape names it allows.</summary>
    public ShapeTermRecord[] Shapes { get; set; } = [];

    /// <summary>The tags it allows.</summary>
    public ShapeTagRecord[] Tags { get; set; } = [];

    /// <summary>The classes it declares.</summary>
    public ShapeClassRecord[] Classes { get; set; } = [];

    /// <summary>Markup this build did not interpret.</summary>
    public Dictionary<string, string> Extensions { get; set; } = [];

    /// <summary>What is wrong with the vocabulary itself, before any set is checked against it.</summary>
    /// <returns>What it found, worst first.</returns>
    /// <remarks>
    ///     ⚠ <b>Here rather than in the importer, because two things ask.</b> The build asks so it can
    ///     refuse, and the editor asks so somebody can see it while they type — and two copies of
    ///     "a class may not require a shape this file does not declare" is one copy that will be
    ///     wrong. The importer maps <see cref="VocabularyProblem.Fatal" /> onto its severities.
    /// </remarks>
    public IReadOnlyList<VocabularyProblem> Problems() {
        List<VocabularyProblem> found = [];
        HashSet<string> declared = new(StringComparer.Ordinal);

        foreach (var term in Shapes) {
            if (!declared.Add(term.Name)) {
                found.Add(
                    new(
                        term.Name,
                        $"'{term.Name}' is declared more than once. The first meaning is the one anybody reading "
                        + "this file will find.",
                        false
                    )
                );
            }
        }

        foreach (var declaredClass in Classes) {
            foreach (var member in declaredClass.Members) {
                // ⚠ A class member the vocabulary does not declare is the class demanding a shape and
                // the vocabulary forbidding it, in one file — so every set that honoured the class
                // would fail the name check.
                if (declared.Count > 0 && !declared.Contains(member.Name)) {
                    found.Add(
                        new(
                            member.Name,
                            $"The class '{declaredClass.Name}' requires a shape called '{member.Name}', which this "
                            + "vocabulary does not declare. Every set that honoured the class would fail the name "
                            + "check.",
                            true
                        )
                    );
                }
            }
        }

        found.Sort(static (left, right) => right.Fatal.CompareTo(left.Fatal));
        return found;
    }

    /// <summary>Turns it into the vocabulary a check runs against.</summary>
    /// <returns>The vocabulary.</returns>
    public ShapeVocabulary Bake() =>
        new(
            Name,
            Shapes.Select(term => new ShapeTerm(Symbol.Intern(term.Name), term.Meaning)),
            Tags.Select(term => new TagTerm(ShapeTags.Parse(term.Tag), term.Meaning)),
            Classes.Select(
                declared => new ShapeClass(
                    Symbol.Intern(declared.Name),
                    [
                        .. declared.Members.Select(
                            member => new ShapeClassMember(
                                Symbol.Intern(member.Name),
                                member.Kind,
                                ShapeTags.Parse(member.Tags),
                                new(member.Extents, member.TopExtents == Vector3.Zero ? member.Extents : member.TopExtents),
                                member.Required
                            )
                        )
                    ]
                )
            )
        );
}

/// <summary>Reading and writing a tag as text.</summary>
/// <remarks>
///     <c>key=value</c>, because a file holding a mapping per tag would be three lines of YAML for
///     what an author writes as one word. A tag with no <c>=</c> takes the key <c>affords</c>, which
///     is the one every project's first tag has.
/// </remarks>
public static class ShapeTags {
    /// <summary>The default key for a tag written as a bare word.</summary>
    public const string DefaultKey = "affords";

    /// <summary>Reads one tag.</summary>
    /// <param name="text">The tag, as <c>key=value</c> or as a bare value.</param>
    /// <returns>The tag.</returns>
    public static Facet Parse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return default;
        }

        var split = text.IndexOf('=', StringComparison.Ordinal);

        return split < 0
            ? Facet.Of(DefaultKey, text.Trim())
            : Facet.Of(text[..split].Trim(), text[(split + 1)..].Trim());
    }

    /// <summary>Reads a list of tags.</summary>
    /// <param name="text">The tags.</param>
    /// <returns>The set.</returns>
    public static FacetSet Parse(IReadOnlyList<string>? text) {
        if (text is null || text.Count == 0) {
            return FacetSet.Empty;
        }

        var facets = new Facet[text.Count];

        for (var index = 0; index < text.Count; index++) {
            facets[index] = Parse(text[index]);
        }

        return FacetSet.Of(facets);
    }

    /// <summary>Writes one tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The text.</returns>
    public static string Write(Facet tag) => $"{tag.Key}={tag.Value}";
}
