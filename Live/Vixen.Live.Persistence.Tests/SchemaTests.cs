// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>
///     What can be asserted about SQL without a database, which is less than it looks and more than
///     nothing: that the numbering is a numbering, and that the statements say what the design says.
/// </summary>
/// <remarks>
///     ⚠ Whether PostgreSQL accepts any of this is the nightly leg's question, alongside the
///     <c>kind</c> and Docker placement legs (doc 27 § Testing). What is here is the class of mistake
///     that would otherwise be found by a deployment: a migration numbered twice, a gap in the
///     sequence, a version constant that has drifted from the list it describes.
/// </remarks>
public class SchemaTests {
    [Fact]
    public void The_version_constant_is_the_last_migration() {
        Assert.Equal(Schema.Version, Schema.Steps[^1].Version);
    }

    [Fact]
    public void Migrations_are_numbered_from_one_without_gaps_or_repeats() {
        Assert.Equal(
            Enumerable.Range(1, Schema.Steps.Length),
            Schema.Steps.Select(step => step.Version)
        );
    }

    [Fact]
    public void Every_migration_has_statements_and_a_note() {
        Assert.All(
            Schema.Steps,
            step => {
                Assert.NotEmpty(step.Statements);
                Assert.False(string.IsNullOrWhiteSpace(step.Note));
                Assert.All(step.Statements, statement => Assert.False(string.IsNullOrWhiteSpace(statement)));
            }
        );
    }

    /// <summary>
    ///     The one statement outside the numbering, because a migration records itself in the table
    ///     this makes and the first one would have nowhere to record it.
    /// </summary>
    [Fact]
    public void The_version_table_is_created_outside_the_numbering() {
        Assert.Contains(Schema.VersionTable, Schema.CreateVersionTable, StringComparison.Ordinal);
        Assert.Contains("if not exists", Schema.CreateVersionTable, StringComparison.Ordinal);

        Assert.All(
            Schema.Steps.SelectMany(step => step.Statements),
            statement => Assert.DoesNotContain($"create table if not exists {Schema.VersionTable}", statement, StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     Every statement is idempotent, so a migration that half-ran and is retried does not fail on
    ///     the object it already made. That is what lets there be no down migration.
    /// </summary>
    [Fact]
    public void Every_create_is_if_not_exists() {
        Assert.All(
            Schema.Steps.SelectMany(step => step.Statements).Where(statement => statement.StartsWith("create", StringComparison.Ordinal)),
            statement => Assert.Contains("if not exists", statement, StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     The journal is append-only, and the idempotency key is a primary key rather than something
    ///     the application remembers. Both are what the support tool's answers rest on.
    /// </summary>
    [Fact]
    public void The_journal_and_its_idempotency_key_are_shaped_as_the_design_says() {
        var sql = string.Join('\n', Schema.Steps.SelectMany(step => step.Statements));

        Assert.Contains("create table if not exists live_ledger (", sql, StringComparison.Ordinal);
        Assert.Contains("sequence     bigserial    primary key", sql, StringComparison.Ordinal);
        Assert.Contains("primary key (op_account, op_character, op_kind, op_id)", sql, StringComparison.Ordinal);
        Assert.Contains("primary key (account, \"character\")", sql, StringComparison.Ordinal);
    }
}
