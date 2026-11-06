using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Провайдер открытых файлов (open_files в снапшоте). Реализация - адаптер
/// VS Code (шаг 7): читает факты из state.vscdb и User/History. Шов нужен,
/// чтобы Snapshotter спрашивал состояние на момент снапшота, а не получал
/// его поток событий.
/// </summary>
public interface IOpenFilesProvider
{
    /// <summary>
    /// Открытые файлы с позициями на текущий момент. Пустой список -
/// источник недоступен (нет VS Code / нет данных): это не ошибка,
    /// снапшот пишется без open_files (честная деградация). Никогда не бросает.
    /// </summary>
    Task<IReadOnlyList<OpenFile>> GetOpenFilesAsync(CancellationToken ct = default);
}
