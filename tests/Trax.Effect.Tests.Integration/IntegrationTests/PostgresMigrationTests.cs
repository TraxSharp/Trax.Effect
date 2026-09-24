using DbUp;
using DbUp.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Trax.Effect.Data.Postgres.Utils;

namespace Trax.Effect.Tests.Integration.IntegrationTests;

/// <summary>
/// Checks what the shipped Postgres migrations leave behind. The migration 036 indexes bound the
/// cleanup DELETE (foreign-key back-references) and the per-manifest FailedCount subquery, so a
/// missing or misnamed index would silently reintroduce the O(table) behavior. Migration 041 has
/// to leave no work_queue row unconfirmed, even one written mid-migration by an older instance.
/// </summary>
[TestFixture]
public class PostgresMigrationTests
{
    private static readonly string[] ExpectedIndexes =
    [
        "ix_metadata_parent_id",
        "ix_work_queue_metadata_id",
        "ix_dead_letter_retry_metadata_id",
        "ix_metadata_manifest_failed",
    ];

    private static string GetConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        return configuration.GetRequiredSection("Configuration")["DatabaseConnectionString"]!;
    }

    [Test]
    public async Task Migrate_CreatesFkAndManifestEvalIndexes()
    {
        var connectionString = GetConnectionString();

        // DbUp is journalled, so this is a no-op when the database is already at 036.
        await DatabaseMigrator.Migrate(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'trax';";

        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));

        foreach (var expected in ExpectedIndexes)
            indexes.Should().Contain(expected, $"index '{expected}' should exist after migration");
    }

    /// <summary>
    /// DbUp runs a script without a transaction, so an instance still on the version before
    /// 041 can insert a row between any two of its statements, without naming confirmed_at.
    /// A row left with a NULL confirmed_at reads as staged: the dispatcher skips it and the
    /// stale-staged sweep cancels it, so accepted work is lost. Every such row must end up
    /// confirmed, whichever statement it lands after.
    /// </summary>
    [Test]
    public async Task Migration041_RowsInsertedByAnOlderWriterMidMigration_AllEndConfirmed()
    {
        var builder = new NpgsqlConnectionStringBuilder(GetConnectionString())
        {
            Database = $"trax_migration_041_{Guid.NewGuid():N}",
            Pooling = false,
        };
        var database = builder.Database!;
        var maintenance = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Database = "postgres",
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(maintenance))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE DATABASE {database}");
        }

        try
        {
            var connectionString = builder.ConnectionString;
            await using (var setup = new NpgsqlConnection(connectionString))
            {
                await setup.OpenAsync();
                await Exec(setup, "CREATE SCHEMA IF NOT EXISTS trax;");
            }

            var assembly = typeof(Trax.Effect.Data.Postgres.AssemblyMarker).Assembly;
            var upTo040 = DeployChanges
                .To.PostgresqlDatabase(connectionString)
                .JournalToPostgresqlTable("trax", "migrations")
                .WithScriptsEmbeddedInAssembly(assembly, name => MigrationNumber(name) <= 40)
                .LogToNowhere()
                .Build()
                .PerformUpgrade();
            upTo040.Successful.Should().BeTrue(upTo040.Error?.ToString());

            var resource = assembly
                .GetManifestResourceNames()
                .Single(name => name.EndsWith("041_work_queue_confirmed_at.sql"));
            string script;
            await using (var stream = assembly.GetManifestResourceStream(resource)!)
            using (var reader = new StreamReader(stream))
                script = await reader.ReadToEndAsync();

            // Split exactly as production does: DatabaseMigrator hands the connection string to
            // PostgresqlDatabase, which runs each script through this manager's splitter.
            var statements = new PostgresqlConnectionManager(connectionString)
                .SplitScriptIntoCommands(script)
                .ToList();
            statements.Should().HaveCountGreaterThan(1);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var inserted = 0;
            async Task InsertAsOlderWriter() =>
                await Exec(
                    connection,
                    "INSERT INTO trax.work_queue (external_id, train_name, input, input_type_name) "
                        + $"VALUES ('older-writer-{inserted++}', 'Some.Train', NULL, NULL);"
                );

            await InsertAsOlderWriter();
            foreach (var statement in statements)
            {
                await Exec(connection, statement);
                await InsertAsOlderWriter();
            }

            await using var count = connection.CreateCommand();
            count.CommandText =
                "SELECT external_id FROM trax.work_queue WHERE confirmed_at IS NULL ORDER BY id;";
            var unconfirmed = new List<string>();
            await using (var rows = await count.ExecuteReaderAsync())
                while (await rows.ReadAsync())
                    unconfirmed.Add(rows.GetString(0));

            unconfirmed
                .Should()
                .BeEmpty(
                    "a row an older instance inserts at any point during 041 must end up confirmed, "
                        + "or the dispatcher never claims it and the stale-staged sweep cancels it"
                );
        }
        finally
        {
            await using var admin = new NpgsqlConnection(maintenance);
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static int MigrationNumber(string resourceName)
    {
        var file = resourceName[(resourceName.IndexOf(".Migrations.") + ".Migrations.".Length)..];
        return int.Parse(file[..file.IndexOf('_')]);
    }

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
