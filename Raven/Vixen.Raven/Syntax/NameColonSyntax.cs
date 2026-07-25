// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Syntax;

public partial class SyntaxFactory {
    public static NameColonSyntax NameColon(string name) =>
        NameColon(IdentifierName(name), Token(SyntaxKind.ColonToken));
}
