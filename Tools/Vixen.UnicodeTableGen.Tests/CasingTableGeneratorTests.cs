// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Xunit;

namespace Vixen.UnicodeTableGen.Tests;

/// <summary>The three casing arms of the generator, over a database small enough to read.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two of these arms have never been run.</b> <c>CombiningClassTable.g.cs</c> and
///         <c>SoftDottedTable.g.cs</c> are the tables all four remaining UAX #21 conditions wait on
///         (#913), and the generator was taught to write them without the sources being present —
///         so the code that produces them was, until this file, only ever read. The enabling act
///         this issue names is "fetch two files and run one command", and a command nobody has run
///         is not an enabling act, it is a guess.
///     </para>
///     <para>
///         ⚠ <b>The fixtures are written here rather than fetched, and that is the point.</b> A test
///         that needed the real UCD would be a test that runs on nobody's machine, which is the
///         situation it exists to improve. What is under test is the <i>parse and the shape</i> —
///         the <c>range ; value</c> syntax, the comment column, the class the table deliberately
///         drops, the ranges it merges, the one property it keeps out of a file of many, and where
///         each table's version comes from — none of which needs a hundred thousand code points to
///         exercise. The data itself is gated elsewhere: <c>GeneratedUnicodeVersionTests</c> holds
///         every committed table to one release.
///     </para>
///     <para>
///         ⚠ <b>The three fixtures deliberately do not all say the same version.</b>
///         <c>SpecialCasing.txt</c> here is 16.0.0 where its two neighbours are 17.0.0, which no
///         real fetch would produce — it is how <see cref="Each_table_takes_its_version_from_its_own_source_file" />
///         can tell "read from its own header" apart from "read once and written three times". The
///         second is what every other table in the generator does, and it is what made a table say
///         13.0.0 beside nine saying 17.0.0 for four major versions (#544).
///     </para>
/// </remarks>
public sealed class CasingTableGeneratorTests : IDisposable {
    /// <summary>A combining-class database with one range of every shape that matters.</summary>
    /// <remarks>
    ///     Class 0 is present and must not reach the table; 0x0320..0x0321 and 0x0322 are adjacent
    ///     and share a class and must merge into one; 0x0300..0x0314 and 0x031A share a class and
    ///     are <i>not</i> adjacent and must not; and the file is deliberately not sorted by code
    ///     point, because the UCD groups by property value and the generator is what sorts.
    /// </remarks>
    const string CombiningClasses = """
        # DerivedCombiningClass-17.0.0.txt
        # Date: 2025-04-01
        #
        # Unicode Character Database
        # @missing: 0000..10FFFF; Not_Reordered

        # ================================================

        # Canonical_Combining_Class=Not_Reordered

        0000..02FF    ; 0 # Cc [768] <control-0000>..MODIFIER LETTER LOW LEFT ARROW

        # ================================================

        # Canonical_Combining_Class=Above

        0300..0314    ; 230 # Mn [21] COMBINING GRAVE ACCENT..COMBINING DOUBLE ACUTE ACCENT
        031A          ; 230 # Mn      COMBINING LEFT ANGLE ABOVE

        # ================================================

        # Canonical_Combining_Class=Above_Right

        0315          ; 232 # Mn      COMBINING COMMA ABOVE RIGHT

        # ================================================

        # Canonical_Combining_Class=Below

        0316..0319    ; 220 # Mn  [4] COMBINING GRAVE ACCENT BELOW..COMBINING RIGHT TACK BELOW
        0320..0321    ; 220 # Mn  [2] COMBINING MINUS SIGN BELOW..COMBINING PALATALIZED HOOK BELOW
        0322          ; 220 # Mn      COMBINING RETROFLEX HOOK BELOW

        # ================================================

        # Canonical_Combining_Class=Attached_Below

        05B0          ; 202 # Mn      HEBREW POINT SHEVA

        # EOF
        """;

    /// <summary>A property list holding <c>Soft_Dotted</c> and one other property around it.</summary>
    /// <remarks>
    ///     <c>PropList.txt</c> carries some sixty properties and the generator wants exactly one, so
    ///     the neighbour is the assertion: a table that picked up <c>White_Space</c> would answer
    ///     <c>Soft_Dotted</c> for a tab.
    /// </remarks>
    const string Properties = """
        # PropList-17.0.0.txt
        # Date: 2025-04-01
        #
        # Unicode Character Database

        0009..000D    ; White_Space # Cc   [5] <control-0009>..<control-000D>
        0020          ; White_Space # Zs       SPACE

        # Total code points: 6

        0069..006A    ; Soft_Dotted # L&   [2] LATIN SMALL LETTER I..LATIN SMALL LETTER J
        012F          ; Soft_Dotted # L&       LATIN SMALL LETTER I WITH OGONEK

        # Total code points: 3

        # EOF
        """;

    /// <summary>Two unconditional rows and two conditional ones, which is the split that matters.</summary>
    const string SpecialCasing = """
        # SpecialCasing-16.0.0.txt
        # Date: 2024-04-01
        #
        # Unicode Character Database

        00DF; 00DF; 0053 0073; 0053 0053; # LATIN SMALL LETTER SHARP S
        FB00; FB00; 0046 0066; 0046 0046; # LATIN SMALL LIGATURE FF

        # Conditional Mappings

        03A3; 03C2; 03A3; 03A3; Final_Sigma; # GREEK CAPITAL LETTER SIGMA
        0049; 0131; 0049; 0049; tr; # LATIN CAPITAL LETTER I

        # EOF
        """;

    readonly string root = Directory.CreateTempSubdirectory("vixen-unicodetablegen").FullName;

    string Ucd => Path.Combine(root, "ucd");

    string Tables => Path.Combine(root, "tables");

    string Suites => Path.Combine(root, "suites");

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>The combining-class arm writes one table and nothing else.</summary>
    [Fact]
    public void The_combining_class_arm_writes_the_one_table_it_is_named_for() {
        Given("DerivedCombiningClass.txt", CombiningClasses);

        Assert.Equal(0, Run("CombiningClass"));
        Assert.Equal(["CombiningClassTable.g.cs"], Written());
    }

    /// <summary>Class zero never reaches the table, and adjacent ranges of one class become one.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves are invisible from <c>Of</c> alone.</b> A code point of class 0 answers
    ///     0 whether the table says so in a range or says nothing at all, so a generator that
    ///     emitted the zero ranges would be right and enormous — <c>DerivedCombiningClass.txt</c> is
    ///     almost entirely class 0 — and no lookup could tell. What tells is the range count and the
    ///     starts, which is why this asserts those and not only the answers.
    /// </remarks>
    [Fact]
    public void Class_zero_is_left_out_and_adjacent_ranges_of_one_class_are_merged() {
        Given("DerivedCombiningClass.txt", CombiningClasses);
        Assert.Equal(0, Run("CombiningClass"));

        var table = Read("CombiningClassTable.g.cs");
        var starts = Numbers(table, "Starts");
        var ends = Numbers(table, "Ends");
        var classes = Numbers(table, "Classes");

        // Six ranges out of nine rows: the class-0 row is dropped, and 0x0320..0x0321 and 0x0322
        // become one. Seven would be a lost merge; ten, a table that kept the base characters.
        Assert.Equal([0x300, 0x315, 0x316, 0x31A, 0x320, 0x5B0], starts);
        Assert.Equal([0x314, 0x315, 0x319, 0x31A, 0x322, 0x5B0], ends);
        Assert.Equal([230, 232, 220, 230, 220, 202], classes);

        Assert.DoesNotContain(0, classes);
    }

    /// <summary>And the ranges answer what the conditions ask of them.</summary>
    /// <remarks>
    ///     The lookup is re-implemented here rather than compiled from the emitted source, so what
    ///     it proves is that the <i>data</i> is in the order a binary search needs — sorted,
    ///     disjoint, and with <c>Ends</c> aligned to <c>Starts</c>. A transposed pair of arrays
    ///     passes the shape test above and fails every one of these.
    /// </remarks>
    [Theory]
    [InlineData(0x0041, 0)]   // `A`, a base character the table never mentions.
    [InlineData(0x02FF, 0)]   // The last code point of the dropped class-0 row.
    [InlineData(0x0301, 230)] // COMBINING ACUTE ACCENT — `Above`, the class every condition asks for.
    [InlineData(0x0315, 232)]
    [InlineData(0x0316, 220)] // `Below` — the class that makes `Not_Before_Dot` wrong today.
    [InlineData(0x031A, 230)]
    [InlineData(0x0321, 220)] // Inside the merged range rather than at either end of it.
    [InlineData(0x05B0, 202)]
    [InlineData(0x10FFFF, 0)]
    public void A_code_point_reads_back_the_class_the_database_gave_it(int codePoint, int expected) {
        Given("DerivedCombiningClass.txt", CombiningClasses);
        Assert.Equal(0, Run("CombiningClass"));

        var table = Read("CombiningClassTable.g.cs");
        Assert.Equal(expected, Lookup(table, codePoint));
    }

    /// <summary>The table names the class the conditions are written in terms of.</summary>
    [Fact]
    public void The_table_names_class_230_so_a_condition_does_not_have_to_spell_it() {
        Given("DerivedCombiningClass.txt", CombiningClasses);
        Assert.Equal(0, Run("CombiningClass"));

        Assert.Contains("public const byte Above = 230;", Read("CombiningClassTable.g.cs"), StringComparison.Ordinal);
    }

    /// <summary>One property comes out of a file of many, and the others stay out.</summary>
    [Fact]
    public void The_soft_dotted_arm_keeps_one_property_out_of_a_file_of_many() {
        Given("PropList.txt", Properties);
        Assert.Equal(0, Run("SoftDotted"));

        var table = Read("SoftDottedTable.g.cs");

        Assert.Equal([0x69, 0x12F], Numbers(table, "Starts"));
        Assert.Equal([0x6A, 0x12F], Numbers(table, "Ends"));

        // The neighbour property is the assertion: it is in the file, it is the majority of the
        // rows, and nothing about it may reach the enum or the ranges.
        Assert.DoesNotContain("WhiteSpace", table, StringComparison.Ordinal);
        Assert.DoesNotContain("White_Space", table, StringComparison.Ordinal);
        Assert.Contains("SoftDottedClass.SoftDotted", table, StringComparison.Ordinal);
    }

    /// <summary>Each of the three tables reads its version out of the file it was written from.</summary>
    /// <remarks>
    ///     ⚠ The fixtures disagree on purpose — see the class remarks. A generator that read one
    ///     version and stamped it on all three would put 16.0.0 on the two tables that came from a
    ///     17.0.0 file, and the failure would be a number nobody could source.
    /// </remarks>
    [Fact]
    public void Each_table_takes_its_version_from_its_own_source_file() {
        Given("SpecialCasing.txt", SpecialCasing);
        Given("DerivedCombiningClass.txt", CombiningClasses);
        Given("PropList.txt", Properties);

        Assert.Equal(0, Run("Casing"));

        Assert.Equal(
            ["CombiningClassTable.g.cs", "SoftDottedTable.g.cs", "SpecialCasingTable.g.cs"],
            Written()
        );

        Assert.Contains("version 16.0.0.", Read("SpecialCasingTable.g.cs"), StringComparison.Ordinal);
        Assert.Contains("version 17.0.0.", Read("CombiningClassTable.g.cs"), StringComparison.Ordinal);
        Assert.Contains("version 17.0.0.", Read("SoftDottedTable.g.cs"), StringComparison.Ordinal);
    }

    /// <summary>The conditional rows are dropped, and the count of them is in the file.</summary>
    /// <remarks>
    ///     The number is the whole of what #697 has to work from: a reader of
    ///     <c>SpecialCasingTable.g.cs</c> is told how many rows the table is not answering for,
    ///     rather than being left to infer it from a table that looks complete.
    /// </remarks>
    [Fact]
    public void The_conditional_rows_are_dropped_and_counted() {
        Given("SpecialCasing.txt", SpecialCasing);
        Assert.Equal(0, Run("SpecialCasing"));

        var table = Read("SpecialCasingTable.g.cs");

        Assert.Contains("The 2 conditional rows", table, StringComparison.Ordinal);
        Assert.Contains("\\u0053\\u0053", table, StringComparison.Ordinal); // ß uppercases to SS.
        Assert.DoesNotContain("\\u0131", table, StringComparison.Ordinal);  // The `tr` row is one of the two.
    }

    /// <summary>A name the generator does not know writes nothing and says so.</summary>
    [Fact]
    public void An_unknown_artefact_name_writes_nothing_and_fails() {
        Given("DerivedCombiningClass.txt", CombiningClasses);

        Assert.Equal(1, Run("CombiningClasses"));
        Assert.Empty(Written());
    }

    /// <summary>A source file that is not there is a failure, not an empty table.</summary>
    /// <remarks>
    ///     ⚠ <b>Verify the instrument first, and this is the instrument.</b> The UCD is fetched file
    ///     by file, so one file arriving before the others is ordinary; a generator that skipped
    ///     what it could not read and exited 0 would write a table saying no code point has a
    ///     combining class, and every condition built on it would quietly answer "no". The failure
    ///     has to happen before anything is written, which is the second assertion.
    /// </remarks>
    [Fact]
    public void A_missing_source_file_is_a_failure_and_not_a_table_of_nothing() {
        Directory.CreateDirectory(Ucd);

        Assert.Throws<FileNotFoundException>(() => {
            Run("CombiningClass");
        });

        Assert.Empty(Written());
    }

    /// <summary>A UCD directory that does not exist is refused before anything is created.</summary>
    [Fact]
    public void A_missing_database_directory_is_refused() {
        Assert.Equal(1, Run("CombiningClass"));
        Assert.False(Directory.Exists(Tables));
    }

    void Given(string name, string content) {
        Directory.CreateDirectory(Ucd);
        File.WriteAllText(Path.Combine(Ucd, name), content);
    }

    int Run(string only) => global::Vixen.UnicodeTableGen.Program.Main([Ucd, Tables, Suites, only]);

    string Read(string name) => File.ReadAllText(Path.Combine(Tables, name));

    string[] Written() {
        if (!Directory.Exists(Tables)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.GetFiles(Tables)) {
            names.Add(Path.GetFileName(path));
        }

        names.Sort(StringComparer.Ordinal);

        return [.. names];
    }

    /// <summary>Reads one of the generated arrays back out of the source that declares it.</summary>
    /// <param name="table">The generated file.</param>
    /// <param name="name">The array's name — <c>Starts</c>, <c>Ends</c> or <c>Classes</c>.</param>
    /// <returns>Its elements, in order.</returns>
    /// <remarks>
    ///     <c>Classes</c> is a byte array in one table and an enum array in the other, so the digits
    ///     are taken wherever they are and the enum members are not: every element of the enum array
    ///     is the same single-valued member, which the <c>Soft_Dotted</c> test asserts by name
    ///     instead.
    /// </remarks>
    static int[] Numbers(string table, string name) {
        var opening = table.IndexOf($" {name} = [", StringComparison.Ordinal);
        Assert.True(opening >= 0, $"the generated table declares no array called {name}");

        var body = table[(opening + name.Length + 4)..];
        body = body[..body.IndexOf("];", StringComparison.Ordinal)];

        var separators = new[] { ',', '\n', '\r', ' ' };
        var values = new List<int>();

        foreach (var field in body.Split(separators, StringSplitOptions.RemoveEmptyEntries)) {
            if (field.StartsWith("0x", StringComparison.Ordinal)) {
                values.Add(int.Parse(field[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                continue;
            }

            if (field.Length > 0 && field.All(char.IsAsciiDigit)) {
                values.Add(int.Parse(field, CultureInfo.InvariantCulture));
            }
        }

        return [.. values];
    }

    /// <summary>The lookup the generated <c>Of</c> performs, over the arrays as emitted.</summary>
    static int Lookup(string table, int codePoint) {
        var starts = Numbers(table, "Starts");
        var ends = Numbers(table, "Ends");
        var classes = Numbers(table, "Classes");

        Assert.Equal(starts.Length, ends.Length);
        Assert.Equal(starts.Length, classes.Length);

        for (var i = 0; i < starts.Length; i++) {
            // Sorted and disjoint is what the binary search in the generated file assumes, so it is
            // asserted here rather than worked around: a linear scan that agreed with an unsorted
            // table would be a test that passed where the shipped lookup does not.
            if (i > 0) {
                Assert.True(starts[i] > ends[i - 1], "the ranges are not sorted and disjoint");
            }

            if (codePoint >= starts[i] && codePoint <= ends[i]) {
                return classes[i];
            }
        }

        return 0;
    }
}
