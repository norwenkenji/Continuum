using System;
using System.Linq;
using System.Text;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Двусторонне детерминированное кодирование enum: PascalCase ↔ snake_case.
/// В БД хранится snake_case (sessions.status = 'running', events.kind = 'app_focused').
/// </summary>
public static class EnumCodec
{
    public static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum
        => ToSnake(value.ToString());

    public static TEnum FromDb<TEnum>(string value) where TEnum : struct, Enum
    {
        var pascal = string.Concat(value
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        return Enum.Parse<TEnum>(pascal);
    }

    internal static string ToSnake(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
