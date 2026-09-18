# AI-лог: Шаг 0 — каркас архитектуры (механическая часть, делегирована)

- Дата: 2026-09-18
- Агент/модель: внешний агент-исполнитель, Модель Kimi-k3(Moonshot AI); 
- Задача: механическая часть шага 0 — перемещение WPF-файлов с сохранением git-истории и создание трёх тестовых файлов под заранее зафиксированный API.
- Контекст (что было на входе): репозиторий после ревизии документации (коммит `3ce0c38`), голый WPF-шаблон одного проекта в корне. Решения: структура `src/ + tests/`, классический `.sln`, скоуп «только каркас» (без домена и SQLite), механика делегируется.

## Промпты

1. «Выполни ровно две части. (1) Перемести через `git mv` (только rename, история сохраняется): `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` → `src/Continuum.App/`, `AssemblyInfo.cs` → `src/Continuum.App/Properties/`; содержимое не менять; `Continuum.csproj`, `Continuum.slnx`, `bin/`, `obj/` не трогать. (2) Создай три тестовых файла в `tests/Continuum.Tests/` строго под зафиксированный API (`IClock.UtcNow`, `IAppPaths.DataDirectory`, `SystemClock`, `AppPaths`): `ArchitectureTests` (у сборки Core нет ссылок на WPF/Win32-стеки), `SystemClockTests` (UTC и близость к эталону; реализация `IClock`), `AppPathsTests` (путь под LocalApplicationData, последний каталог — Continuum). Запрещено: любые изменяющие git-операции, readme/docs/LICENSE/.gitignore, создание csproj/sln, другие .cs файлы. Вернуть вывод `git status --short` и содержимое файлов.»

## Что сделал агент

- Файлы созданы:
  - `tests/Continuum.Tests/ArchitectureTests.cs` — проверка, что сборка `Continuum.Core` не ссылается на `PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Windows.Forms`, `System.Drawing`, `Microsoft.Win32.Registry`, `System.Management` (две дублирующие проверки: `Assert.DoesNotContain` по каждому префиксу + сводная `Assert.False`).
  - `tests/Continuum.Tests/SystemClockTests.cs` — два факта: `UtcNow` в UTC и в пределах 5 секунд от эталона; `SystemClock` реализует `IClock`.
  - `tests/Continuum.Tests/AppPathsTests.cs` — `DataDirectory` начинается с `LocalApplicationData`, последний каталог — `Continuum`.
- Файлы изменены: нет (по заданию).
- Перемещения через `git mv` (все пять отображаются как `R`, история сохранена):
  - `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` → `src/Continuum.App/`
  - `AssemblyInfo.cs` → `src/Continuum.App/Properties/AssemblyInfo.cs`
- Решения (коротко):
  1. Отступление в `SystemClockTests`: у `DateTimeOffset` нет свойства `Kind`, проверка выполнена через `now.UtcDateTime.Kind` и дополнена проверкой `now.Offset == TimeSpan.Zero`. Смысл теста не изменён, принято.
  2. Запрещённые зоны (`Continuum.csproj`, `Continuum.slnx`, `bin/`, `obj/`, документация) не тронуты — проверено координатором по `git status` и дереву файлов.
  3. Комментарии в коде — по-русски, сигнатуры и неймспейсы из задания не изменены.

## Поправлено после агента

| Что | Было у агента | Стало | Почему |
| --- | --- | --- | --- |
| Namespace `AppPaths` в `AppPathsTests.cs` | `using Continuum.App.Infrastructure.Paths;` | `using Continuum.Infrastructure.Paths;` | Namespace `Continuum.App.Infrastructure.Paths` конфликтует с типом `Continuum.App` (WPF-класс приложения в namespace `Continuum`): C# не допускает namespace и тип с одинаковым полным именем (CS0101). Ошибка заложена в промпт, агент выполнил его буквально. Реализация `AppPaths` размещена в `Continuum.Infrastructure.Paths`. |
| Отсутствует `using Xunit;` во всех трёх тестовых файлах | `[Fact]` без импорта | `using Xunit;` добавлен в начало каждого файла | Классический xunit 2.x не подключает глобальный using для `Xunit` (в отличие от шаблонов xunit.v3). Ошибка компиляции CS0246; в промпте требование импорта указано не было. |

Итог после правок: `dotnet build Continuum.sln` — 0 ошибок, 0 предупреждений; `dotnet test` — 4/4 зелёные; приложение запускается, главное окно открывается через DI.

## Что НЕ сделано / отложено

- Создание csproj/sln, переработка `App.xaml`/`App.xaml.cs`, `CompositionRoot`, реализация `AppPaths`/`SystemClock`/контрактов Core - часть по плану шага 0, агенту не поручалась и в этом логе не фиксируется.
- Коммит и push — по указанию пользователя на этом этапе не выполняются.

## Вопросы агента без ответа

- Нет. Единственное уточнение (про `Kind` у `DateTimeOffset`) агент разрешил сам и задокументировал в отчёте.
