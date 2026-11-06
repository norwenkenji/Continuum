using System;
using System.IO;

namespace Continuum.Adapters.VSCode;

/// <summary>
/// Разбор file-URI из хранилищ VS Code (data-sources §7.1): resource в
/// entries.json и workbench.state приходит percent-закодированным -
/// "%3A" вместо ":", кириллица в виде "%D0%BE". System.Uri декодирует
/// большую часть, но закодированную букву диска ("file:///d%3A/...")
/// превращает в Unix-подобный путь с ведущим слэшем и прямыми
/// разделителями - это чинится нормализацией. Совсем нестандартный URI
/// обрабатывается ручной percent-декодировкой.
/// </summary>
internal static class VSCodePathUtil
{
    private const string FileSchemePrefix = "file:///";

    /// <summary>
    /// Локальный путь для file-URI или null, если это не файловый URI
    /// (untitled:, vscode-remote: и т.п.) или путь не разбирается.
    /// </summary>
    public static string? TryUriToLocalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        try
        {
            var parsed = new Uri(uri, UriKind.Absolute);
            if (!parsed.IsFile)
            {
                return null;
            }

            return Normalize(parsed.LocalPath);
        }
        catch (UriFormatException)
        {
            return ManualDecode(uri);
        }
    }

    /// <summary>
    /// System.Uri для "file:///d%3A/..." даёт "/d:/..." с прямыми слэшами:
    /// закодированная буква диска мешает распознать Windows-путь. Ведущий
    /// слэш перед буквой диска срезается, разделители приводятся к "\".
    /// </summary>
    private static string Normalize(string path)
    {
        if (path.Length > 2 && path[0] == '/' && path[2] == ':')
        {
            path = path[1..];
        }

        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>Резервная декодировка без System.Uri (Uri не разобрал строку).</summary>
    private static string? ManualDecode(string uri)
    {
        if (!uri.StartsWith(FileSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(uri[FileSchemePrefix.Length..])
            .Replace('/', Path.DirectorySeparatorChar);
        if (decoded.Length > 2 && decoded[0] == Path.DirectorySeparatorChar && decoded[2] == ':')
        {
            decoded = decoded[1..];
        }

        return decoded;
    }
}
