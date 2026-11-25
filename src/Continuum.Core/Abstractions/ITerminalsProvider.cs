using Continuum.Core.Domain;

namespace Continuum.Core.Abstractions;

/// <summary>
/// Провайдер терминалов (terminals в снапшоте, шаг 10): какие шеллы
/// запущены и в каких каталогах. Реализация читает PEB процессов
/// (NtQueryInformationProcess) - точный CWD живого процесса. Класс B/C
/// по data-sources §6: недоступность процесса - честная деградация
/// (source = unknown), исключения наружу не выходят.
/// </summary>
public interface ITerminalsProvider
{
    /// <summary>
    /// Терминалы на текущий момент. Пустой список - терминалов нет или
    /// источник недоступен: это не ошибка, снапшот пишется без terminals.
    /// Никогда не бросает.
    /// </summary>
    Task<IReadOnlyList<TerminalState>> GetTerminalsAsync(CancellationToken ct = default);
}
