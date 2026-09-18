namespace Continuum.Core.Abstractions;

/// <summary>
/// Маскирует чувствительные подстроки (токены, пароли, ключи)
/// перед сохранением. См. docs/specs/privacy-pipeline.md, §2 и §5.
/// </summary>
public interface ISanitizer
{
    string Sanitize(string value, ObservableKind kind);
}
