using System;
using System.Collections.Generic;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;

namespace Continuum.Infrastructure.Git;

/// <summary>
/// Разбор вывода «git status --porcelain» (формат v1, «XY path»). Чистая
/// функция: пути остаются относительными к корню репозитория (склейка с
/// корнем - на вызывающем), пустые и битые строки пропускаются, пустой
/// вход - пустой список. Ничего не бросает.
/// </summary>
public static class PorcelainParser
{
    /// <summary>Разбирает вывод git status --porcelain в список dirty-файлов.</summary>
    public static IReadOnlyList<DirtyFile> Parse(string? statusOutput)
    {
        var result = new List<DirtyFile>();
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return result;
        }

        foreach (var rawLine in statusOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4 || line[2] != ' ')
            {
                continue; // битая строка: минимальная форма - «XY p»
            }

            var x = line[0];
            var y = line[1];
            var pathPart = line.Substring(3).Trim();
            if (pathPart.Length == 0)
            {
                continue;
            }

            if (x is 'R' or 'C' || y is 'R' or 'C')
            {
                // rename/copy: «R  old -> new» - два события: Deleted(old) + Added(new)
                var arrowIndex = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIndex <= 0)
                {
                    continue; // rename-строка без стрелки - битая
                }

                var oldPath = Unquote(pathPart[..arrowIndex]);
                var newPath = Unquote(pathPart[(arrowIndex + 4)..]);
                if (oldPath.Length == 0 || newPath.Length == 0)
                {
                    continue;
                }

                result.Add(new DirtyFile(oldPath, FileChangeKind.Deleted));
                result.Add(new DirtyFile(newPath, FileChangeKind.Added));
                continue;
            }

            var path = Unquote(pathPart);
            if (path.Length == 0)
            {
                continue;
            }

            result.Add(new DirtyFile(path, Classify(x, y)));
        }

        return result;
    }

    private static FileChangeKind Classify(char x, char y)
    {
        if (x == '?' && y == '?')
        {
            return FileChangeKind.Untracked;
        }

        if (x == 'A' || y == 'A')
        {
            return FileChangeKind.Added;
        }

        if (x == 'D' || y == 'D')
        {
            return FileChangeKind.Deleted;
        }

        // 'M', 'T' (смена типа), 'U' (unmerged) и любая экзотика - Modified:
        // не падать на незнакомых статусах
        return FileChangeKind.Modified;
    }

    /// <summary>Снимает кавычки core.quotePath и разэкранирует \" и \\.</summary>
    private static string Unquote(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            var body = path.Substring(1, path.Length - 2);
            return body.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return path;
    }
}
