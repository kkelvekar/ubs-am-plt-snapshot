using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

internal static class CiDatabaseBootstrapper
{
    private const int MaximumConnectionAttempts = 40;
    private static readonly object InitializationLock = new();
    private static Task? initialization;

    public static void InitializeIfNeeded(IConfiguration configuration, string? environmentName)
    {
        if (!string.Equals(environmentName, "CI", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var connectionString = configuration["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Database:ConnectionString is required for CI regression tests.");
        }

        Task initializationTask;
        lock (InitializationLock)
        {
            initialization ??= InitializeAsync(connectionString);
            initializationTask = initialization;
        }

        initializationTask.GetAwaiter().GetResult();
    }

    private static async Task InitializeAsync(string connectionString)
    {
        var targetBuilder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = targetBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("The CI database connection string must name a database.");
        }

        var masterBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };

        await using (var masterConnection = await OpenWithRetryAsync(masterBuilder.ConnectionString))
        {
            await using var createDatabaseCommand = masterConnection.CreateCommand();
            createDatabaseCommand.CommandText = """
                IF DB_ID(@databaseName) IS NULL
                BEGIN
                    DECLARE @statement nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@databaseName);
                    EXEC sys.sp_executesql @statement;
                END;
                """;
            createDatabaseCommand.Parameters.Add(
                new SqlParameter("@databaseName", SqlDbType.NVarChar, 128) { Value = databaseName });
            await createDatabaseCommand.ExecuteNonQueryAsync();
        }

        await using var targetConnection = await OpenWithRetryAsync(targetBuilder.ConnectionString);
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "db", "scripts");
        var schemaScripts = Directory.GetFiles(schemaDirectory, "*.sql").Order(StringComparer.Ordinal).ToArray();
        if (schemaScripts.Length == 0)
        {
            throw new InvalidOperationException($"No database schema scripts were found in '{schemaDirectory}'.");
        }

        foreach (var schemaScript in schemaScripts)
        {
            Console.WriteLine($"Applying CI database schema script: {Path.GetFileName(schemaScript)}");
            await using var schemaCommand = targetConnection.CreateCommand();
            schemaCommand.CommandTimeout = 120;
            schemaCommand.CommandText = await File.ReadAllTextAsync(schemaScript);
            await schemaCommand.ExecuteNonQueryAsync();
        }
    }

    private static async Task<SqlConnection> OpenWithRetryAsync(string connectionString)
    {
        SqlException? lastException = null;
        for (var attempt = 1; attempt <= MaximumConnectionAttempts; attempt++)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch (SqlException exception)
            {
                await connection.DisposeAsync();
                lastException = exception;
                if (attempt < MaximumConnectionAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
        }

        throw new InvalidOperationException("SQL Server did not become ready in time.", lastException);
    }
}
