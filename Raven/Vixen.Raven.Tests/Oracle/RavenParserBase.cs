// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Antlr4.Runtime;

namespace Vixen.Raven.Grammar;

public abstract class RavenParserBase(ITokenStream input) : Parser(input);
