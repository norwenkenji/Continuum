# 2026-11-21 - Шаг 10: адаптеры терминалов и браузера (агент)

## Задача

Последний шаг дорожной карты: адаптеры Chrome и терминала. Критерий readme: честная деградация при недоступности UI Automation. Норматив: docs/specs/data-sources.md §5 (Chrome: title - всегда класс A, URL - через UIA класс C, таймаут 1 с, кэш по (hwnd, title), не более одного обращения на окно за 2 с) и §6 (терминалы: CommandLine базовый путь, PEB точный путь, WindowsTerminal + дочерние шеллы, OpenConsole не шелл, AppContainer ACCESS_DENIED -> source=unknown).

Разделение: швы (ITerminalsProvider, IBrowserTabsProvider, BrowserTabInfo в Core; запись terminals/browser_tabs в Snapshotter с privacy-пробами и санитизацией; GetSnapshotBrowserTabsAsync в репозиторий; OpenUrl-стратегия в RestoreService; URL вкладок в композере планов) - вне делегации; агенту - сами коллекторы.

## Вне делегации (швы)

- `ITerminalsProvider` / `IBrowserTabsProvider` / `BrowserTabInfo` (Core): провайдеры опрашиваются Snapshotter'ом на момент снапшота, как IOpenFilesProvider в шаге 7.
- Snapshotter: `CollectTerminalsAsync` (Cwd через privacy-пробу ObservableKind.Cwd), `CollectBrowserTabsAsync` (заголовок - проба окна, URL - проба Url-вида + санитизация ISanitizer; исключённый браузер или BY_HOST-правило при реальном URL - вкладка не пишется целиком: privacy-first; приложение браузера апсертится для application_id).
- `IRepository.GetSnapshotBrowserTabsAsync` (+Sqlite/Fake).
- RestoreService: стратегия OpenUrl (UseShellExecute - браузер по умолчанию), шаги OpenUrl добавляются в план после файлов с дедупликацией.
- RestorePlanComposer: вкладки с URL - хост `chrome url1 url2 ...` (§5.6), существующий chrome-хост от приложений сессии получает URL в аргументы; без резолва chrome - URL шагами OpenUrl.
- 8 своих тестов: снапшот-терминалы/вкладки (privacy, санитизация, апсерт), композер-хосты (3), OpenUrl (2).

## Агент

- **Модель:** GLM-5.3-Prime (Z.ai)
- **Способ:** задача делегирована промптом, отчёт получен и проверен независимым прогоном.

## Что сделал агент

| Файл | Содержимое |
| --- | --- |
| `Collectors/Terminals/TerminalCollector.cs` + `TerminalNativeMethods.cs` | Снимок процессов Toolhelp32, дерево WindowsTerminal (OpenConsole пропускается как узел), CWD - чтение PEB (NtQueryInformationProcess, ReadProcessMemory байтами), швы SnapshotProcesses/TryReadCurrentDirectory |
| `Collectors/Browser/ChromeTabCollector.cs` | System.Windows.Automation без новых пакетов и без P/Invoke; суффиксы Chrome/Edge/Firefox, URL по ValuePattern с таймаутом/кэшем/локом, швы enumerateWindows/readUrl |
| `TerminalCollectorTests.cs` (6) + `BrowserTabCollectorTests.cs` (9) | Живой PEB-тест на cmd.exe + логика на швах |

### Проверка

- Агент: build 0/0, 331/331.
- Независимо: 331/331, и дальше серии по 5-6 прогонов.

## Отступления агента (приняты)

1. **Смещение CurrentDirectory.DosPath в RTL_USER_PROCESS_PARAMETERS - 0x38, не 0x10 из ТЗ промпта.** Живой дамп показал 0x10 - ConsoleHandle, 0x20/0x28/0x30 - StandardInput/Output/Error, 0x38 - CurrentDirectory. Именно живой тест на cmd.exe и поймал бы расхождение - смещение подтверждено на реальном процессе. PEB+0x20 (ProcessParameters) сошлось.
2. `Process.Path` не существует в .NET 10 - ExePath через `MainModule?.FileName`.
3. Живая проверка URL поднимала отдельный Chrome (--user-data-dir в %TEMP% + --force-renderer-accessibility): title+URL подтверждены, без флага accessibility дерево минимально - честный url_available=0. Стенд убран.

## Поправлено при интеграции

1. **Флейк тестов вкладок**: три теста падали с вероятностью ~30-40%. Причины: (а) швы тестов использовали продакшн-таймаут 1 с - под параллельной нагрузкой xunit внутренняя Task.Run не успевала стартовать, Wait истекал, URL становился null; (б) тест кэша зависел от реального 2-секундного окна - под нагрузкой первый вызов длился дольше окна, кэш «протухал» до второго вызова. Исправления: таймаут для швов с запасом (15 с), окно кэша вынесено в параметр конструктора (продакшн 2 с, тест 60 с), ассерт длительности таймаут-теста ослаблен до 5 с. После правок - 15 зелёных прогонов подряд.
2. **Чистая пересборка вскрывала предупреждение xUnit2030** в моём тесте шага 9 (RetentionServiceTests): Assert.NotEmpty(Where) заменён на Assert.Contains - сборка 0 предупреждений и при --no-incremental.
3. **Строка статуса readme не обновилась в шаге 9**: тихие Replace не совпали с якорями, безусловные печати солгали (в коммит 51946a1 ушла строка «шаг 8, 287 тестов»). Сейчас исправлено с контролем совпадения через точные правки; урок - после Replace проверять Contains результата.

## Живой прогон после интеграции

Стенд: реальное окно Chrome (локальный HTTP-сервер 127.0.0.1:8765, страница с тайтлом; сеть через прокси не нужна) + реальный cmd в известном каталоге + приложение 25 с.

- `terminals` (8 строк): WindowsTerminal с 4 powershell-вкладками - CWD точный (`Source=peb`, реальная сессия пользователя), автономные powershell, cmd из стенда с точным CWD;
- `browser_tabs` (1 строка): `http://127.0.0.1:8765/ | Continuum Local | url_available=1 | source=uia | chrome` - вся цепочка UIA -> BrowserTabInfo -> privacy/санитизация -> апсерт приложения -> EF;
- план восстановления: хосты `javaw, git, git-remote-https, chrome("http://127.0.0.1:8765/")` - URL слит в аргументы chrome-хоста (§5.6), dry-run Success;
- прямой живой прогон ChromeTabCollector: окно `Continuum - Google Chrome` распознано, title strip, URL прочитан через UIA.

Замечено по машине: реальный браузер пользователя - Brave (суффикс « - Brave», Chromium-семейство) - вне списка MVP (спека §5.5: Chrome/Edge по title, Firefox title-only). Запись Brave - задел после MVP.

## Что осталось

- Дорожная карта MVP (шаги 0-10) выполнена полностью: 331 тест, сборка 0/0.
- Вне MVP: пояснительная записка и материалы к защите (ТЗ §5), коалесценция справочника приложений, Brave, `terminal_activity` из командных строк (§6.3).
