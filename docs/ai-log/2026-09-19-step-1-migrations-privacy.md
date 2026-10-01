# 2026-09-19 - Шаг 1: миграция V001 + privacy-конвейер (агент)

## Задача

Делегированная часть шага 1 (readme, «Порядок доведения до MVP»):

1. **Миграция V001** - `V001__init.sql`: первая миграция схемы БД v1 (13 таблиц + индексы) из readme, раздел «Схема БД». Встраивается в сборку, применяется `MigrationRunner`'ом через `PRAGMA user_version` (сам runner и инфраструктура БД написаны вне этой делегации).
2. **Privacy-конвейер** - реализации контрактов `IExclusionSet`, `ISanitizer`, `IPrivacyFilter` из `Continuum.Core.Abstractions` по нормативной спецификации `docs/specs/privacy-pipeline.md` (исключения §4, санитизация §5, порядок §2), плюс тесты по §9.

Остальное шага 1 (доменные модели, `IRepository`, инфраструктура БД, DI-разводка, часть тестов) делалось вне делегации и в этом журнале не фиксируется.

## Агент

- **Модель:** Kimi-k3 (Moonshot AI)
- **Способ:** задача делегирована промптом ниже, отчёт получен и проверен независимым прогоном.

## Промпт (передан агенту)

```text
Ты - исполнитель части шага 1 проекта Continuum (WPF, .NET 10, локальная система
восстановления рабочего контекста). Рабочая директория: C:\need\projects\UTILS\Continuum -
git-репозиторий, ветка main. SDK: dotnet 10.0.401. Сборка сейчас: 0 ошибок, 25 тестов зелёные.

ОБЯЗАТЕЛЬНО ПРОЧИТАЙ ПЕРЕД РАБОТОЙ:
1. readme.md, раздел «Схема БД» - SQL-драфт, который ты переносишь в миграцию.
2. docs/specs/privacy-pipeline.md - нормативная спецификация приватности
   (особенно §2 контракты, §4 исключения, §5 санитизация, §9 обязательные тесты).
3. src/Continuum.Core/Abstractions/ - готовые контракты: ObservableKind, Observable,
   IPrivacyFilter, ISanitizer, IExclusionSet. Менять их НЕЛЬЗЯ, только реализовать.

ЗАДАЧА - РОВНО ДВЕ ЧАСТИ.

ЧАСТЬ 1. Миграция V001
Создай ровно один файл: src/Continuum.App/Infrastructure/Database/Migrations/V001__init.sql
- Содержимое: схема из readme.md «Схема БД» - 13 таблиц (settings, applications,
  projects, sessions, events, file_activity, snapshots, git_state, terminals,
  open_files, browser_tabs, restore_plans, restore_steps) и все индексы оттуда.
- ОДНО осознанное отступление от драфта readme (оно обязательно, не опционально):
  applications.exe_path объяви как TEXT NOT NULL DEFAULT '' и добавь
  CREATE UNIQUE INDEX ux_applications_name_exe ON applications(name, exe_path);
  Причина: SqliteRepository.UpsertApplicationAsync уже написан и использует
  ON CONFLICT(name, exe_path) - с nullable exe_path уникальность не работает,
  потому что SQLite считает NULL-ы различными.
- Требования к файлу: чистый SQLite DDL; CREATE TABLE IF NOT EXISTS и
  CREATE INDEX IF NOT EXISTS; внешние ключи как в readme; НИКАКИХ PRAGMA
  user_version внутри файла (версию ставит MigrationRunner); никаких DROP;
  комментарии по-русски.
- Csproj уже настроен: *.sql в этом каталоге встраивается в сборку автоматически,
  csproj не трогай.

ЧАСТЬ 2. Реализация privacy-конвейера
Каталог src/Continuum.App/Infrastructure/Privacy/, namespace Continuum.Infrastructure.Privacy
(НЕ Continuum.App.* - такой namespace конфликтует с классом Continuum.App и не компилируется):

1) ExclusionSet.cs : IExclusionSet
   - Дефолты по спецификации §4.1: менеджеры паролей (1password, keepass, bitwarden,
     enpass, dashlane, lastpass, nordpass, stickypasswords, keeper) и собственное
     приложение continuum - неудаляемо; §4.2: %APPDATA%\Microsoft\Credentials,
     %LOCALAPPDATA%\Microsoft\Credentials, %USERPROFILE%\.ssh, %USERPROFILE%\.gnupg.
   - Конструктор: ExclusionSet(IEnumerable<string>? extraApplications = null,
     IEnumerable<string>? extraDirectories = null) - дефолты + пользовательские списки.
   - Сравнения регистронезависимые. Директории: нормализация через Path.GetFullPath,
     обрезка завершающего разделителя, сравнение по префиксу с границей каталога
     (C:\work не должен матчить C:\workspace-secret - только C:\work и C:\work\...).
   - IsApplicationExcluded: по имени процесса без расширения и (если задан) по exe-пути.

2) SecretSanitizer.cs : ISanitizer
   - Командные строки (ObservableKind.CommandLine): маскирование по спецификации §5.1 -
     пары --password=X / -p X, токены по форме (ghp_…, sk-…, xox[baprs]-…, AKIA…),
     PEM-блоки private key. Маска ровно "***": длина секрета не раскрывается,
     имя параметра сохраняется (--password=***).
   - URL (ObservableKind.Url): по §5.2 - убрать userinfo (user:pass@), замаскировать
     значения query-параметров token/key/sig/auth/code/password/secret,
     удалить fragment целиком. Остальные виды Observable - возвращать без изменений.

3) PrivacyFilter.cs : IPrivacyFilter
   - Конструктор PrivacyFilter(IExclusionSet exclusions).
   - Allow(o): false, если приложение исключено; false, если Kind is FilePath или Cwd
     и путь в исключённой директории; false, если IsNeverRecorded(o); false, если
     Kind is FilePath и имя файла матчит список §5.3 (*.pem, *.key, *.pfx, *.p12,
     id_rsa, id_ed25519, *.kdbx, .env, .npmrc, .netrc, credentials.json -
     отбрасывается событие целиком, не только имя). Иначе true.

ЧАСТЬ 3. Тесты (tests/Continuum.Tests/, namespace Continuum.Tests.Privacy /
Continuum.Tests.Database, xunit [Fact]/[Theory], using Xunit - глобального нет):
- SecretSanitizerTests: каждый паттерн маскируется; имя параметра сохраняется;
  длина секрета не утекает; userinfo удалён; fragment удалён; query-параметр
  token=abc → token=***.
- PrivacyFilterTests: исключённое приложение отброшено; исключённая директория
  отбрасывает FilePath/Cwd, но не WindowTitle; файл .env/id_rsa → событие
  отброшено; continuum исключён всегда; граница каталога (C:\work vs C:\workspace-secret).
- ExclusionSetTests: регистронезависимость, префикс директории, exe-путь.
- MigrationV001Tests: через DatabaseInitializer (ctor по умолчанию) на временной БД -
  после Initialize(): PRAGMA user_version == 1; в sqlite_master ровно 13 таблиц
  из списка выше и все индексы из миграции; повторный Initialize() - без ошибок
  и user_version остаётся 1. Временную БД удалять после теста, предварительно
  вызвав Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools() (иначе файл залочен).

ЗАПРЕЩЕНО:
- git commit / push / reset / rebase / stash - дерево оставь незакоммиченным;
- менять readme.md, docs/, .gitignore, любые csproj, чужие существующие файлы
  (Core, Database, чужие тесты). Если считаешь, что там ошибка - НЕ правь,
  опиши в отчёте разделом «Замечания к остальному коду»;
- новые NuGet-пакеты; CJK-символы. Комментарии - по-русски.

ПРОВЕРКА (выполнить и приложить вывод):
- dotnet build Continuum.sln → 0 ошибок, 0 предупреждений;
- dotnet test Continuum.sln → все тесты зелёные (было 25 + твои новые);
- git status --short.

ОТЧЁТ В КОНЦЕ: список созданных файлов; итог build/test; любые отступления
и причины; замечания к остальному коду, если есть.
```

## Что сделал агент

### Файлы созданы

| Файл | Содержимое |
| --- | --- |
| `src/Continuum.App/Infrastructure/Database/Migrations/V001__init.sql` | Схема v1: 13 таблиц из readme + 6 индексов из readme + уникальный `ux_applications_name_exe`. Чистый DDL, `IF NOT EXISTS` везде, без `PRAGMA user_version` (ставит runner), без `DROP`. Встроение в сборку подтверждено тестом |
| `src/Continuum.App/Infrastructure/Privacy/ExclusionSet.cs` | `IExclusionSet`: дефолты §4.1 (9 менеджеров паролей + `continuum`, неудаляемо - API удаления нет) и §4.2 (оба `Credentials`, `.ssh`, `.gnupg`); конструктор с `extraApplications`/`extraDirectories`; регистронезависимость; нормализация через `Path.GetFullPath` + обрезка завершающего разделителя; граница каталога (`C:\work` ≠ `C:\workspace-secret`); «путь важнее имени»: исключение, заданное путём exe, матчит только этот путь |
| `src/Continuum.App/Infrastructure/Privacy/SecretSanitizer.cs` | `ISanitizer`: §5.1 (пары `--password=X` / `-p X` с сохранением имени, токены `ghp_`/`sk-`/`xox…`/`AKIA`, PEM-блоки), §5.2 (userinfo удаляется, query-параметры token/key/sig/auth/code/password/secret → `=***`, fragment удаляется целиком); маска ровно `***`; токен-паттерны - данные (`extraTokenPatterns` в конструкторе); у всех regex таймаут 1 с |
| `src/Continuum.App/Infrastructure/Privacy/PrivacyFilter.cs` | `IPrivacyFilter`: порядок §2 - приложение исключено → директория исключена (только FilePath/Cwd) → `IsNeverRecorded` → секретный артефакт §5.3 (11 паттернов, событие отбрасывается целиком) |
| `tests/Continuum.Tests/Privacy/SecretSanitizerTests.cs` | 24 теста |
| `tests/Continuum.Tests/Privacy/PrivacyFilterTests.cs` | 22 теста |
| `tests/Continuum.Tests/Privacy/ExclusionSetTests.cs` | 15 тестов |
| `tests/Continuum.Tests/Database/MigrationV001Tests.cs` | 3 теста: состав схемы + `user_version` == 1, идемпотентность, работоспособность `ON CONFLICT(name, exe_path)` поверх нового уникального индекса |

### Проверка

- Агент: `dotnet build Continuum.sln` → 0 ошибок, 0 предупреждений; `dotnet test` → 92 пройдено, 0 не пройдено (было 25 + 67 новых); `git status --short` - только untracked-новые файлы, чужое не тронуто, коммитов не было.
- Независимый повторный прогон после получения: build 0/0, тесты 92/92 - сходится.

## Отступления агента

| Отступление | Оценка |
| --- | --- |
| Обязательное по заданию: `applications.exe_path TEXT NOT NULL DEFAULT ''` + `CREATE UNIQUE INDEX ux_applications_name_exe` - с комментарием причины в SQL | Выполнено и доказано тестом `Applications_name_exe_path_unique_index_supports_upsert` |
| Один тест сверх списка: `..._supports_upsert` в `MigrationV001Tests` - проверяет, что обязательное отступление реально работает | Принято: это часть проверки миграции |
| `IsNeverRecorded` возвращает `false`: правилам §4.3 неоткуда взяться до окна настроек (шаг 9); контракт подключён в `PrivacyFilter`, поведение задокументировано в коде | Принято: осознанная заглушка до шага 9 |

## Поправлено после агента

Ничего: чужие файлы агент не трогал, сборка и тесты подтвердились независимым прогоном, правок не потребовалось.

## Вопросы агента без ответа (перенесены на следующую правку spec)

1. **§4.2 упоминает `%LOCALAPPDATA%\Packages\*` (контейнеры UWP)** - в промпт эта строка не вошла, агент её не реализовал. Кандидат: добавить одной строкой дефолта в `ExclusionSet` (префиксное сравнение покрывает wildcard «всё подкаталоги Packages»).
2. **Токены `gho_`, `ghs_`, `github_pat_`** - §5.1 перечисляла только `ghp_` строгой длины {36}; реальные GitHub-токены бывают и других форм. Кандидат на дополнение списка паттернов при следующей редакции спецификации.

(Оба пункта закрыты на шаге 2: spec дополнена, `Packages` и семейство токенов реализованы - см. `2026-10-01-step-2-window-monitor.md`.)

## Что отложено (вне шага 1)

- Правила §4.3 (BY_APP_NAME / BY_HOST / BY_PATH_SUBSTR) и окно настроек - шаг 9.
- Персистентность `RestorePlan`/`RestoreStep` - шаг 6 (Restore Engine).
- Подключение privacy-конвейера к реальным коллекторам - вместе с коллекторами (шаги 2-5).
