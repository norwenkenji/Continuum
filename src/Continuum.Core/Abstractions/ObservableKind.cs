namespace Continuum.Core.Abstractions;

/// <summary>Вид наблюдаемого объекта. См. docs/specs/privacy-pipeline.md, §2–3.</summary>
public enum ObservableKind
{
    /// <summary>Заголовок окна.</summary>
    WindowTitle,

    /// <summary>Путь файла.</summary>
    FilePath,

    /// <summary>URL (когда реально получен).</summary>
    Url,

    /// <summary>Командная строка процесса.</summary>
    CommandLine,

    /// <summary>Рабочая директория процесса.</summary>
    Cwd
}
