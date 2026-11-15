using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Continuum.Infrastructure.Database.Ef;

/// <summary>
/// Фабрика контекста для инструментов EF Core (`dotnet ef migrations add`):
/// модель строится из сборки, БД не нужна - соединение в памяти.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ContinuumDbContext>
{
    public ContinuumDbContext CreateDbContext(string[] args)
        => new(new SqliteConnectionFactory(":memory:"));
}
