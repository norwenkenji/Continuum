using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Git;
using Xunit;

namespace Continuum.Tests.Git;

/// <summary>
/// GitClient на фейковом IProcessRunner: точные аргументы команд (§4.2),
/// окружение (§4.3), таймаут 5 с и поведение при отказах. Реальные процессы
/// не запускаются.
/// </summary>
public class GitClientTests
{
    private const string FakeGitExe = "C:\\fake\\git.exe";
    private const string Repo = "C:\\repo";

    private static ProcessResult Ok(string stdout) => new(0, stdout, string.Empty, false);

    [Fact]
    public async Task GetStateAsync_runs_allowed_commands_with_safe_environment_and_timeout()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("main\n"));
        runner.Enqueue(Ok("a94a8fe5ccb19ba61c4c0873d391e987982fbbd3\t1735689600\tinitial commit\n"));
        runner.Enqueue(Ok(""));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.Equal(3, runner.Calls.Count);
        foreach (var call in runner.Calls)
        {
            Assert.Equal(FakeGitExe, call.FileName);
            Assert.Equal(Repo, call.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);

            // §4.3: ровно четыре переменные окружения
            Assert.NotNull(call.Environment);
            Assert.Equal(4, call.Environment!.Count);
            Assert.Equal("0", call.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("0", call.Environment["GIT_OPTIONAL_LOCKS"]);
            Assert.Equal("never", call.Environment["GCM_INTERACTIVE"]);
            Assert.Equal("C", call.Environment["LC_ALL"]);

            // §4.2: каждая команда с префиксом отключения credential.helper
            Assert.Equal("-c", call.Arguments[0]);
            Assert.Equal("credential.helper=", call.Arguments[1]);
        }

        Assert.Equal(
            new[] { "-c", "credential.helper=", "rev-parse", "--abbrev-ref", "HEAD" },
            runner.Calls[0].Arguments.ToArray());
        Assert.Equal(
            new[] { "-c", "credential.helper=", "log", "-1", "--format=%H%x09%ct%x09%s" },
            runner.Calls[1].Arguments.ToArray());
        Assert.Equal(
            new[] { "-c", "credential.helper=", "-c", "core.quotePath=false", "--no-optional-locks", "status", "--porcelain" },
            runner.Calls[2].Arguments.ToArray());

        Assert.True(state.Available);
    }

    [Fact]
    public async Task GetStateAsync_parses_head_line_and_dirty_files()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("main\n"));
        runner.Enqueue(Ok("a94a8fe5ccb19ba61c4c0873d391e987982fbbd3\t1735689600\tfeat: начало работы\n"));
        runner.Enqueue(Ok(" M src/a.cs\n?? notes.md\n"));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.True(state.Available);
        Assert.Equal("main", state.Branch);
        Assert.Equal("a94a8fe5ccb19ba61c4c0873d391e987982fbbd3", state.HeadCommit);
        Assert.Equal("feat: начало работы", state.HeadSubject);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), state.HeadTimestamp);

        Assert.Equal(2, state.DirtyFiles.Count);
        Assert.Contains(state.DirtyFiles, f => f.Path == "src/a.cs" && f.Kind == FileChangeKind.Modified);
        Assert.Contains(state.DirtyFiles, f => f.Path == "notes.md" && f.Kind == FileChangeKind.Untracked);
    }

    [Fact]
    public async Task GetStateAsync_detached_head_gives_null_branch()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("HEAD\n"));
        runner.Enqueue(Ok("a94a8fe5ccb19ba61c4c0873d391e987982fbbd3\t1735689600\tsubject\n"));
        runner.Enqueue(Ok(""));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.True(state.Available);
        Assert.Null(state.Branch);
        Assert.NotNull(state.HeadCommit);
    }

    [Fact]
    public async Task GetStateAsync_repository_without_commits_is_available_with_null_head()
    {
        var runner = new FakeProcessRunner();
        // На нерождённом HEAD rev-parse --abbrev-ref HEAD падает с 128 и печатает «HEAD»,
        // поэтому имя ветки добираем fallback'ом через symbolic-ref
        runner.Enqueue(new ProcessResult(128, "HEAD\n", "fatal: ambiguous argument 'HEAD'", false));
        runner.Enqueue(Ok("main\n")); // symbolic-ref --short HEAD
        runner.Enqueue(new ProcessResult(128, string.Empty, "fatal: your current branch 'main' does not have any commits yet", false));
        runner.Enqueue(Ok("?? a.txt\n"));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.True(state.Available);
        Assert.Equal("main", state.Branch);
        Assert.Null(state.HeadCommit);
        Assert.Null(state.HeadSubject);
        Assert.Null(state.HeadTimestamp);
        Assert.Single(state.DirtyFiles);
        Assert.Equal(
            new[] { "-c", "credential.helper=", "symbolic-ref", "--short", "HEAD" },
            runner.Calls[1].Arguments.ToArray());
    }

    [Fact]
    public async Task GetStateAsync_nonzero_exit_gives_unavailable_without_exception()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(128, string.Empty, "fatal: not a git repository", false));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.False(state.Available);
        Assert.Null(state.Branch);
        Assert.Empty(state.DirtyFiles);
    }

    [Fact]
    public async Task GetStateAsync_timeout_gives_unavailable_without_exception()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.False(state.Available);
    }

    [Fact]
    public async Task GetStateAsync_late_timeout_gives_unavailable()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("main\n"));
        runner.Enqueue(Ok("a94a8fe5ccb19ba61c4c0873d391e987982fbbd3\t1735689600\tsubject\n"));
        runner.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true)); // status

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.False(state.Available);
    }

    [Fact]
    public async Task GetStateAsync_garbage_log_output_gives_unavailable()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("main\n"));
        runner.Enqueue(Ok("это-не-строка-log-формата\n"));

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.False(state.Available);
    }

    [Fact]
    public async Task GetStateAsync_runner_failure_gives_unavailable_without_exception()
    {
        var runner = new FakeProcessRunner { ToThrow = new InvalidOperationException("процесс не запустился") };

        var state = await new GitClient(runner, FakeGitExe).GetStateAsync(Repo);

        Assert.False(state.Available);
    }

    [Fact]
    public async Task GetStateAsync_without_git_gives_unavailable_and_runs_nothing()
    {
        var runner = new FakeProcessRunner();

        // Пустая строка - принудительное «git не найден»
        var client = new GitClient(runner, gitExePath: string.Empty);
        Assert.Null(client.FindGitExe());

        var state = await client.GetStateAsync(Repo);

        Assert.False(state.Available);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task GetRepositoryRootAsync_normalizes_git_output()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("C:/Work/Repo/\n")); // git отвечает с '/' и завершающим разделителем

        var root = await new GitClient(runner, FakeGitExe).GetRepositoryRootAsync("C:\\repo\\sub");

        Assert.Equal("c:\\work\\repo", root);
        Assert.Equal("C:\\repo\\sub", runner.Calls[0].WorkingDirectory);
        Assert.Equal(
            new[] { "-c", "credential.helper=", "rev-parse", "--show-toplevel" },
            runner.Calls[0].Arguments.ToArray());
    }

    [Fact]
    public async Task GetRepositoryRootAsync_for_existing_file_uses_its_directory_as_cwd()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tempFile, "x");
        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue(Ok("C:/Work/Repo\n"));

            var root = await new GitClient(runner, FakeGitExe).GetRepositoryRootAsync(tempFile);

            Assert.Equal("c:\\work\\repo", root);
            Assert.Equal(Path.GetDirectoryName(tempFile), runner.Calls[0].WorkingDirectory);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task GetRepositoryRootAsync_nonzero_exit_gives_null(int exitCode)
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(exitCode, string.Empty, "fatal", false));

        var root = await new GitClient(runner, FakeGitExe).GetRepositoryRootAsync(Repo);

        Assert.Null(root);
    }

    [Fact]
    public async Task GetRepositoryRootAsync_empty_output_gives_null()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(Ok("\n"));

        var root = await new GitClient(runner, FakeGitExe).GetRepositoryRootAsync(Repo);

        Assert.Null(root);
    }

    [Fact]
    public async Task GetRepositoryRootAsync_timeout_gives_null()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var root = await new GitClient(runner, FakeGitExe).GetRepositoryRootAsync(Repo);

        Assert.Null(root);
    }

    /// <summary>Фейковый runner: записывает вызовы, отдаёт заготовленные результаты по очереди.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public sealed record Call(
            string FileName,
            IReadOnlyList<string> Arguments,
            string? WorkingDirectory,
            IReadOnlyDictionary<string, string>? Environment,
            TimeSpan Timeout);

        private readonly Queue<ProcessResult> _results = new();

        public List<Call> Calls { get; } = [];

        public Exception? ToThrow { get; set; }

        public void Enqueue(ProcessResult result) => _results.Enqueue(result);

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            Calls.Add(new Call(fileName, arguments, workingDirectory, environment, timeout));
            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new ProcessResult(0, string.Empty, string.Empty, false));
        }
    }
}
