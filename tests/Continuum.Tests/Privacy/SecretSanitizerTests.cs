using Continuum.Core.Abstractions;
using Continuum.Infrastructure.Privacy;
using Xunit;

namespace Continuum.Tests.Privacy;

/// <summary>
/// Тесты санитизации секретов. Норматив: docs/specs/privacy-pipeline.md, §5 и §9.
/// </summary>
public class SecretSanitizerTests
{
    private readonly SecretSanitizer _sanitizer = new();

    // ---------------- Командные строки (§5.1) ----------------

    [Theory]
    [InlineData("--password=SuperSecret123", "--password=***")]
    [InlineData("--password=\"Super Secret 123\"", "--password=***")]
    [InlineData("-p SuperSecret123", "-p ***")]
    [InlineData("-p \"SuperSecret123\"", "-p ***")]
    [InlineData("--token ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--token ***")]
    [InlineData("AWS_SECRET_ACCESS_KEY=abc123def456ghi789", "AWS_SECRET_ACCESS_KEY=***")]
    [InlineData("set AWS_SECRET_ACCESS_KEY=\"quoted value\"", "set AWS_SECRET_ACCESS_KEY=***")]
    public void CommandLine_parameter_pairs_are_masked_name_preserved(string input, string expected)
    {
        Assert.Equal(expected, _sanitizer.Sanitize(input, ObservableKind.CommandLine));
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnopqrst")]              // ровно 20 символов после sk-
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("xoxb-123456789012-abcdefghijkl")]
    [InlineData("xoxp-123456789-abcdefghij")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    public void CommandLine_tokens_are_masked_by_shape(string token)
    {
        var result = _sanitizer.Sanitize($"tool --host api.example.com {token}", ObservableKind.CommandLine);

        Assert.DoesNotContain(token, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void CommandLine_github_pat_is_masked_by_shape()
    {
        var token = "ghp_" + new string('a', 36);

        var result = _sanitizer.Sanitize($"git clone https://{token}@github.com/user/repo", ObservableKind.CommandLine);

        Assert.DoesNotContain(token, result);
        Assert.Contains("***", result);
    }

    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghs_")]
    [InlineData("ghu_")]
    [InlineData("ghr_")]
    public void CommandLine_github_token_family_is_masked(string prefix)
    {
        var token = prefix + new string('a', 20); // обновлённый §5.1: gh[opsur]_{20,}

        var result = _sanitizer.Sanitize($"tool {token} --verbose", ObservableKind.CommandLine);

        Assert.DoesNotContain(token, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void CommandLine_github_pat_40_chars_is_masked()
    {
        var token = "ghp_" + new string('b', 40); // длиннее старых 36 - новый диапазон {20,}

        var result = _sanitizer.Sanitize($"git clone https://{token}@github.com/user/repo", ObservableKind.CommandLine);

        Assert.DoesNotContain(token, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void CommandLine_github_fine_grained_pat_is_masked()
    {
        var token = "github_pat_" + new string('c', 22) + "_AB12"; // [A-Za-z0-9_]{20,}

        var result = _sanitizer.Sanitize($"curl -H \"Authorization: Bearer {token}\" https://api.github.com", ObservableKind.CommandLine);

        Assert.DoesNotContain(token, result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void CommandLine_pem_private_key_block_is_masked()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEvwIBADANBgkqhkiG9w0BAQEFAASC\n-----END RSA PRIVATE KEY-----";

        var result = _sanitizer.Sanitize($"tool --cert {pem}", ObservableKind.CommandLine);

        Assert.Equal("tool --cert ***", result);
    }

    [Fact]
    public void CommandLine_masking_preserves_parameter_name_and_hides_secret_length()
    {
        var shortSecret = _sanitizer.Sanitize("--password=abc", ObservableKind.CommandLine);
        var longSecret = _sanitizer.Sanitize("--password=abcdefghijklmnopqrstuvwxyz0123456789", ObservableKind.CommandLine);

        // Имя параметра сохраняется
        Assert.StartsWith("--password=", shortSecret);

        // Выход одинаков при любой длине секрета: длина не раскрывается
        Assert.Equal(shortSecret, longSecret);
        Assert.Equal("--password=***", longSecret);
    }

    // ---------------- URL (§5.2) ----------------

    [Fact]
    public void Url_userinfo_is_removed()
    {
        var result = _sanitizer.Sanitize("https://user:password@example.com/path", ObservableKind.Url);

        Assert.Equal("https://example.com/path", result);
    }

    [Fact]
    public void Url_fragment_is_removed_entirely()
    {
        var result = _sanitizer.Sanitize("https://example.com/app#/session/secret-id", ObservableKind.Url);

        Assert.Equal("https://example.com/app", result);
        Assert.DoesNotContain('#', result);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("key")]
    [InlineData("sig")]
    [InlineData("auth")]
    [InlineData("code")]
    [InlineData("password")]
    [InlineData("secret")]
    public void Url_sensitive_query_parameters_are_masked_name_preserved(string parameterName)
    {
        var url = $"https://example.com/callback?{parameterName}=abc123&next=/home";

        var result = _sanitizer.Sanitize(url, ObservableKind.Url);

        Assert.Equal($"https://example.com/callback?{parameterName}=***&next=/home", result);
    }

    [Fact]
    public void Url_token_query_parameter_masked_example_from_spec()
    {
        var result = _sanitizer.Sanitize("https://example.com/?token=abc", ObservableKind.Url);

        Assert.Equal("https://example.com/?token=***", result);
    }

    [Fact]
    public void Url_non_sensitive_parts_are_preserved()
    {
        var result = _sanitizer.Sanitize("https://docs.example.com/a/b?foo=bar&lang=ru", ObservableKind.Url);

        Assert.Equal("https://docs.example.com/a/b?foo=bar&lang=ru", result);
    }

    // ---------------- Остальные виды (§5.3) ----------------

    [Theory]
    [InlineData(ObservableKind.WindowTitle)]
    [InlineData(ObservableKind.FilePath)]
    [InlineData(ObservableKind.Cwd)]
    public void Other_kinds_are_returned_unchanged(ObservableKind kind)
    {
        const string value = "C:\\project\\secrets --password=SuperSecret123 #frag";

        Assert.Equal(value, _sanitizer.Sanitize(value, kind));
    }
}
