namespace Continuum.Core.Abstractions;

/// <summary>
/// Решает, разрешено ли наблюдение. false — отбросить целиком,
/// не записывать ничего. См. docs/specs/privacy-pipeline.md, §2.
/// </summary>
public interface IPrivacyFilter
{
    bool Allow(Observable o);
}
