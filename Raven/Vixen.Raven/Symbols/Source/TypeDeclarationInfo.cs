// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>
///     A uniform view over the five type declaration syntaxes. <c>shader</c>,
///     <c>struct</c>, <c>class</c>, <c>protocol</c> and <c>enum</c> have the same
///     shape but no common base node, so the declaration pass reads them through
///     this instead of switching everywhere.
/// </summary>
public sealed class TypeDeclarationInfo {
    public MemberDeclarationSyntax Syntax { get; }
    public SyntaxList<AttributeListSyntax> AttributeLists { get; }
    public SyntaxList<SyntaxToken> Modifiers { get; }
    public SyntaxToken Identifier { get; }
    public TypeParameterListSyntax? TypeParameterList { get; }
    public BaseListSyntax? BaseList { get; }
    public SyntaxList<TypeParameterConstraintClauseSyntax> ConstraintClauses { get; }
    public IReadOnlyList<MemberDeclarationSyntax> Members { get; }
    public TypeKind Kind { get; }

    public string Name => Identifier.ValueText;

    TypeDeclarationInfo(
        MemberDeclarationSyntax syntax,
        SyntaxList<AttributeListSyntax> attributeLists,
        SyntaxList<SyntaxToken> modifiers,
        SyntaxToken identifier,
        TypeParameterListSyntax? typeParameterList,
        BaseListSyntax? baseList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        IReadOnlyList<MemberDeclarationSyntax> members,
        TypeKind kind
    ) {
        Syntax = syntax;
        AttributeLists = attributeLists;
        Modifiers = modifiers;
        Identifier = identifier;
        TypeParameterList = typeParameterList;
        BaseList = baseList;
        ConstraintClauses = constraintClauses;
        Members = members;
        Kind = kind;
    }

    /// <summary>Returns the uniform view of a type declaration, or null for any other member.</summary>
    public static TypeDeclarationInfo? From(MemberDeclarationSyntax syntax) =>
        syntax switch {
            ShaderDeclarationSyntax s => new(
                s,
                s.AttributeLists,
                s.Modifiers,
                s.Identifier,
                s.TypeParameterList,
                s.BaseList,
                s.ConstraintClauses,
                Materialize(s.Members),
                TypeKind.Shader
            ),
            StructDeclarationSyntax s => new(
                s,
                s.AttributeLists,
                s.Modifiers,
                s.Identifier,
                s.TypeParameterList,
                s.BaseList,
                s.ConstraintClauses,
                Materialize(s.Members),
                TypeKind.Struct
            ),
            ProtocolDeclarationSyntax s => new(
                s,
                s.AttributeLists,
                s.Modifiers,
                s.Identifier,
                s.TypeParameterList,
                s.BaseList,
                s.ConstraintClauses,
                Materialize(s.Members),
                TypeKind.Protocol
            ),
            EnumDeclarationSyntax s => new(
                s,
                s.AttributeLists,
                s.Modifiers,
                s.Identifier,
                null,
                s.BaseList,
                default,
                s.Members.Cast<MemberDeclarationSyntax>().ToArray(),
                TypeKind.Enum
            ),
            _ => null
        };

    static IReadOnlyList<MemberDeclarationSyntax> Materialize(SyntaxList<MemberDeclarationSyntax> members) {
        List<MemberDeclarationSyntax> result = [];
        foreach (var member in members) {
            result.Add(member);
        }

        return result;
    }
}
