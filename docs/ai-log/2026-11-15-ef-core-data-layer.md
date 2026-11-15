# 2026-11-15 - Слой данных на Entity Framework Core

## Задача

Преподаватель вернул ТЗ с замечанием: доступ к базе данных должен выполняться через Entity Framework Core (DbContext + миграции EF Core), а не через Microsoft.Data.Sqlite с миграциями по PRAGMA user_version. ТЗ приведено к требуемому шаблону и EF Core (отдельная правка документа), после чего код приведён в соответствие: на защите ТЗ и реализация не должны расходиться.

## Что сделано

### Модель и контекст

- `Infrastructure/Database/Ef/EntityRows.cs` - строки таблиц (13 сущностей: sessions, events, file_activity, projects, applications, snapshots, git_state, terminals, open_files, browser_tabs, restore_plans, restore_steps, settings). Отдельные классы, а не доменные record-ы: Core не знает про EF, домен остался чистым. Времена - unix-секунды (как в схеме), enum-ы - snake_case строкой.
- `Infrastructure/Database/Ef/ContinuumDbContext.cs` - `DbContext` с полной конфигурацией: имена колонок snake_case, уникальные индексы (`ux_applications_name_exe`, `root_path`), индексы запросов (`ix_events_session_ts`, `ix_events_ts`, `ix_fileact_session_ts`, `ix_fileact_path`, `ix_snapshots_session`, `ix_restore_steps_plan`), внешние ключи с `ON DELETE NO ACTION` (как в прежней схеме), конвертеры enum ↔ snake_case (`EnumCodec`). Соединение открывает прежняя `SqliteConnectionFactory` (WAL, foreign_keys, synchronous), владение им - у контекста.
- `Infrastructure/Database/Ef/DesignTimeDbContextFactory.cs` - для `dotnet ef migrations add` (соединение в памяти).

### Миграция вместо V001

- Сгенерирована миграция `20261115123052_InitialCreate` (+ `ContinuumDbContextModelSnapshot`). Журнал - `__EFMigrationsHistory` (EF 10 дополнительно ведёт `__EFMigrationsLock`).
- Удалены: `Migration.cs`, `MigrationRunner.cs`, `EmbeddedMigrationLoader.cs`, `Migrations/V001__init.sql`, встроенный ресурс в `.csproj`; из тестов - `MigrationRunnerTests`, `EmbeddedMigrationLoaderTests`, `MigrationV001Tests`.
- `DatabaseInitializer` теперь: бэкфилл журнала для БД до перехода (схема есть, `__EFMigrationsHistory` нет - журнал дописывается скриптами самого EF через `IHistoryRepository`, данные не трогаются), затем `Database.Migrate()`.

### Репозиторий

`SqliteRepository` переписан на EF Core с тем же контрактом `IRepository`: выборки - LINQ (`AsNoTracking`, явные сортировки как раньше), запись - `SaveChanges`. Снапшот со всеми дочерними наборами и план с шагами пишутся одним `SaveChanges` (одна транзакция). Upsert проекта и приложения - прежняя семантика `ON CONFLICT`: name/last_seen обновляются, git_remote и adapter_key - только если пришло непустое, first_seen не меняется.

### Пакеты

`Microsoft.EntityFrameworkCore.Sqlite` 10.0.14, `Microsoft.EntityFrameworkCore.Design` 10.0.14 (PrivateAssets), `Microsoft.Data.Sqlite` / `Microsoft.Extensions.*` подняты до 10.0.14 (требование EF: NU1605 на понижение версии).

## Проверка

- Сборка: 0 ошибок, 0 предупреждений.
- Тесты: 287 зелёных (было 289: минус 15 тестов старой миграционной инфраструктуры, плюс 13 новых).
- Новые тесты:
  - `tests/Continuum.Tests/Database/EfSchemaTests.cs` (4): миграции создают все таблицы; уникальные индексы и индексы запросов на месте, ссылочная целостность работает (событие с несуществующей сессией отклоняется); повторная инициализация идемпотентна (одна строка в журнале); БД без журнала миграций подхватывается без потери данных.
  - `tests/Continuum.Tests/Database/SqliteRepositoryTests.cs` (9): жизненный цикл сессии, порядок событий и `GetLastEventTs`, активность файлов, upsert проекта (имя обновлено, first_seen и git_remote сохранены), дедупликация приложений по (name, exe_path), снапшот с дочерними наборами, план с шагами и обновлением результатов, настройки.
- Живой прогон (обычный режим, репозиторий в %TEMP%, dev-швы): БД создана миграцией EF - 16 таблиц с `__EFMigrationsHistory` (1 запись, ProductVersion 10.0.14); сессия `ended/user`; события `session_start`/`app_focused`/`app_exited`; файловая активность `watcher added`, `watcher modified x3`, `git untracked`; снапшот `session_end`; `git_state available=1 branch=main`.
- Живой прогон `--demo`: сессия `ended/user`, 45 событий, 20 строк file_activity, снапшот с open_files (README:42, Program.cs:10), `git_state available=0`.

## Нашлось и исправлено по ходу

- **Демо-сессия оставалась открытой**: `DemoSeeder` передавал `SessionStatus.Ended` в `CreateSessionAsync`, а контракт репозитория сознательно игнорирует статус и конец сессии (сессию открывает и закрывает жизненный цикл). На `FakeRepository` расхождение не было видно (фейк сохраняет запись как есть), на реальной БД демо-сессия висела в `running`, и хронология показывала «идёт». Исправлено: сеятель создаёт сессию и сразу закрывает её через `EndSessionAsync`; контракт закреплён тестом `Create_session_always_starts_running`.
- Первый живой прогон дал 0 строк файловой активности - не баг: файл создавался слишком рано (до первого тика watcher и вне окна дедупликации). При окне 45 с и правках существующего файла всё пишется.

## Что осталось как было

- `SqliteConnectionFactory` (единые PRAGMA соединения) - это настройка подключения, а не доступ к данным.
- VS Code адаптер: чужие `state.vscdb` открываются отдельной строкой подключения в ReadOnly - спека §7.2, к EF-модели отношения не имеет.
- Служебный SQL в `DatabaseInitializer` - только скрипты истории миграций, которые генерирует сам EF (`IHistoryRepository`), и запрос к каталогу SQLite (`sqlite_master`).

## Отложено

- Разделение `DbContext` на отдельную сборку инфраструктуры - не требуется курсовой, Core остаётся без EF.
- Подкаталог `Migrations` содержит три EF-файла; при появлении схемных изменений миграции добавляются командой `dotnet ef migrations add <Имя>`.
