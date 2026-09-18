namespace Continuum.Core.Abstractions;

/// <summary>
/// Набор исключений: приложения, директории и правила «не записывать никогда».
/// См. docs/specs/privacy-pipeline.md, §2 и §4.
/// </summary>
public interface IExclusionSet
{
    bool IsApplicationExcluded(string applicationName, string? exePath);

    bool IsDirectoryExcluded(string path);

    bool IsNeverRecorded(Observable o);
}
