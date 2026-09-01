using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Synestra.Api.IntegrationTests.Infrastructure;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11-alpine3.24").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<PostgreSqlTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var databaseName = $"synestra_api_tests_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName
        }.ConnectionString;
        return new PostgreSqlTestDatabase(_container.GetConnectionString(), databaseName, connectionString);
    }
}

public sealed class PostgreSqlTestDatabase(
    string administrativeConnectionString,
    string databaseName,
    string connectionString) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(administrativeConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL integration tests";
}
