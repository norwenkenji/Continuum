namespace Continuum.Infrastructure.Database;

/// <summary>
/// Одна миграция схемы БД. Version монотонно растёт и совпадает
/// со значением PRAGMA user_version после применения.
/// </summary>
public sealed record Migration(int Version, string Name, string Sql);
