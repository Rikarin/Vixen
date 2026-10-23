// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.UnicodeTableGen.Tests;

/// <summary>
///     The array body every generated table is written with, at the lengths where its line breaks
///     can go wrong: none, one short line, exactly one full line, and one over.
/// </summary>
/// <remarks>
///     ⚠ <b>On the method and not on a generated table</b>
///     (<a href="https://github.com/Rikarin/Vixen/issues/1361">#1361</a>). No table the committed UCD
///     produces is empty, so a test that regenerated one would assert nothing about the empty case;
///     an empty sequence is a fixture that needs no database. Each case is wrapped the way every
///     caller wraps it, because the defect this method has had twice is a blank line that is only
///     visible against the closing bracket.
/// </remarks>
public sealed class AppendNumbersTests {
    [Theory]
    [InlineData(0, "[\n    ];")]
    [InlineData(1, "[\n        0x0,\n    ];")]
    [InlineData(12, "[\n        0x0, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8, 0x9, 0xA, 0xB,\n    ];")]
    [InlineData(13, "[\n        0x0, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8, 0x9, 0xA, 0xB,\n        0xC,\n    ];")]
    public void An_array_body_ends_on_its_last_value_with_no_blank_line(int length, string expected) {
        var builder = new StringBuilder("[\n");

        Program.AppendNumbers(builder, Enumerable.Range(0, length));
        builder.Append("    ];");

        Assert.Equal(expected, builder.ToString());
    }
}
