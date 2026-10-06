using System;
using System.IO;
using System.Text;
using Continuum.Core.Abstractions;
using Continuum.Infrastructure.Git.Native;

namespace Continuum.Infrastructure.Git;

/// <summary>
/// Определение проекта по пути (data-sources §4.4): подъём от каталога пути
/// до внешнего git root по маркеру «.git». Чистая файловая операция - git.exe
/// не запускается. Ничего не бросает: битый или несуществующий путь - null.
///
/// Bare-репозитории сознательно не матчатся автоматически: у них нет записи
/// «.git» внутри рабочего дерева. Отдельной логики для них в MVP нет.
/// </summary>
public sealed class ProjectResolver : IProjectResolver
{
    private const string GitMarker = ".git";

    /// <inheritdoc/>
    public string? ResolveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string startDirectory;
        try
        {
            var full = Path.GetFullPath(path.Trim());

            if (File.Exists(full))
            {
                // Существующий файл - подъём от его каталога
                var directory = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }

                startDirectory = directory;
            }
            else if (Directory.Exists(full))
            {
                // Каталог (CWD) - подъём от него самого
                startDirectory = full;
            }
            else
            {
                return null; // несуществующий путь - не угадываем
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // битый путь - не проект, не ошибка
        }

        // Подъём до корня файловой системы, собираем ВСЕ каталоги с маркером;
        // корень проекта - самый верхний (внешний). Вложенные репозитории
        // считаются одним проектом - принятое ограничение MVP.
        string? outermost = null;
        var current = startDirectory;
        while (current is not null)
        {
            var marker = Path.Combine(current, GitMarker);
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                outermost = current;
            }

            current = Path.GetDirectoryName(current);
        }

        return outermost is null ? null : Canonicalize(outermost);
    }

    /// <summary>
    /// Каноническая форма корня проекта: полный путь (разрешает «..»), без
    /// завершающего разделителя, subst-диск заменён реальным путём, нижний
    /// регистр. Та же каноническая форма - в GitClient.NormalizeGitPath.
    /// </summary>
    internal static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return ResolveSubstDrive(full).ToLowerInvariant();
    }

    /// <summary>
    /// subst-диск («S:\work») → реальный путь («C:\real\work»). Обычный диск
    /// и UNC проходят без изменений.
    /// </summary>
    private static string ResolveSubstDrive(string fullPath)
    {
        if (fullPath.Length < 2 || fullPath[1] != ':')
        {
            return fullPath; // UNC или относительный остаток - не трогаем
        }

        var buffer = new StringBuilder(GitNativeMethods.DevicePathBufferCapacity);
        var written = GitNativeMethods.QueryDosDeviceW(
            fullPath[..2], buffer, buffer.Capacity);
        if (written == 0)
        {
            return fullPath; // устройство не найдено - оставляем как есть
        }

        var target = buffer.ToString();
        var nulIndex = target.IndexOf('\0');
        if (nulIndex >= 0)
        {
            target = target[..nulIndex]; // возможны несколько значений через NUL - берём первое
        }

        const string devicePrefix = "\\??\\";
        if (!target.StartsWith(devicePrefix, StringComparison.Ordinal))
        {
            return fullPath;
        }

        var mappedRoot = target[devicePrefix.Length..];
        if (mappedRoot.Length < 2 || mappedRoot[1] != ':')
        {
            return fullPath; // ссылка не на диск (например, UNC) - не трогаем
        }

        var remainder = fullPath[2..].TrimStart(Path.DirectorySeparatorChar);
        return remainder.Length == 0
            ? mappedRoot.TrimEnd(Path.DirectorySeparatorChar)
            : mappedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + remainder;
    }
}
