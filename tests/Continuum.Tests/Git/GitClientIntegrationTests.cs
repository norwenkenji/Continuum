using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Git;
using Continuum.Infrastructure.Processes;
using Xunit;

namespace Continuum.Tests.Git;

/// <summary>
/// Интеграционный тест с реальным git.exe во временном каталоге %TEMP%.
/// Машина без git - не красный тест: FindGitExe() вернул null - молча выходим.
/// Фикстура готовится прямыми вызовами git.exe; каталог удаляется за собой.
/// </summary>
public class GitClientIntegrationTests
{
    [Fact]
    public async Task Real_repository_lifecycle()
    {
        var client = new GitClient(new ProcessRunner());
        var git = client.FindGitExe();
        if (git is null)
        {
            return; // git не установлен - проверять нечего
        }

        var root = Path.Combine(Path.GetTempPath(), "continuum-git-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGit(git, root, "-c", "init.defaultBranch=main", "init", "--quiet");

            // Свежий репозиторий: доступен, ветка main, коммитов ещё нет, dirty пуст
            var fresh = await client.GetStateAsync(root);
            Assert.True(fresh.Available);
            Assert.Equal("main", fresh.Branch);
            Assert.Null(fresh.HeadCommit);
            Assert.Null(fresh.HeadSubject);
            Assert.Null(fresh.HeadTimestamp);
            Assert.Empty(fresh.DirtyFiles);

            // Новый файл - Untracked
            var fileA = Path.Combine(root, "a.txt");
            File.WriteAllText(fileA, "one");
            var withNewFile = await client.GetStateAsync(root);
            Assert.Contains(withNewFile.DirtyFiles, f => f.Path == "a.txt" && f.Kind == FileChangeKind.Untracked);

            // Коммит: head-поля заполнены, dirty снова пуст
            RunGit(git, root, "add", "a.txt");
            RunGit(git, root,
                "-c", "user.email=continuum-tests@example.com",
                "-c", "user.name=Continuum Tests",
                "commit", "--quiet", "-m", "initial commit");
            var committed = await client.GetStateAsync(root);
            Assert.True(committed.Available);
            Assert.Equal("main", committed.Branch);
            Assert.NotNull(committed.HeadCommit);
            Assert.Equal(40, committed.HeadCommit!.Length);
            Assert.Equal("initial commit", committed.HeadSubject);
            Assert.NotNull(committed.HeadTimestamp);
            Assert.Empty(committed.DirtyFiles);

            // Изменение файла - Modified
            File.AppendAllText(fileA, "two");
            var modified = await client.GetStateAsync(root);
            Assert.Contains(modified.DirtyFiles, f => f.Path == "a.txt" && f.Kind == FileChangeKind.Modified);

            // Ещё один новый файл рядом - Untracked
            File.WriteAllText(Path.Combine(root, "b.txt"), "new");
            var withSecondFile = await client.GetStateAsync(root);
            Assert.Contains(withSecondFile.DirtyFiles, f => f.Path == "b.txt" && f.Kind == FileChangeKind.Untracked);

            // Кириллица и пробел в имени: core.quotePath=false даёт сырой UTF-8
            // (без восьмеричных эскейпов), кавычки вокруг пути парсер снимает
            File.WriteAllText(Path.Combine(root, "новый файл.txt"), "cyr");
            var withCyrillic = await client.GetStateAsync(root);
            Assert.Contains(withCyrillic.DirtyFiles,
                f => f.Path == "новый файл.txt" && f.Kind == FileChangeKind.Untracked);

            // GetRepositoryRootAsync согласуется с канонической формой
            var subDir = Path.Combine(root, "sub");
            Directory.CreateDirectory(subDir);
            var resolvedRoot = await client.GetRepositoryRootAsync(subDir);
            var expectedRoot = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
            Assert.Equal(expectedRoot, resolvedRoot);
        }
        finally
        {
            // .git содержит read-only файлы - снимаем атрибуты перед удалением
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Прямой вызов git.exe для подготовки фикстуры; падение команды - красный тест.</summary>
    private static void RunGit(string gitExe, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = gitExe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30_000), "git не завершился за 30 секунд");
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} завершился кодом {process.ExitCode}: {stdErr}");
    }
}
