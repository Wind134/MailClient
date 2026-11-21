using System.Globalization;
using MailClient.Core;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

internal static class SqliteHelpers
{
    /// <summary>
    /// 单条语句 IN(...) 的批量上限。SQLite 默认 SQLITE_MAX_VARIABLE_NUMBER 为 999，
    /// 这里留出余量给同语句的其他参数，避免各处自写 400 / 500 两套数字。
    /// </summary>
    public const int MaxParametersPerStatement = 400;

    public static string Fmt(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseDate(string? value)
        => string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string EnumName<T>(T value) where T : struct, Enum
        => value.ToString();

    public static T ParseEnum<T>(string value) where T : struct, Enum
        => Enum.Parse<T>(value, ignoreCase: true);

    public static T? ParseEnumOrNull<T>(string? value) where T : struct, Enum
        => string.IsNullOrEmpty(value) ? null : Enum.Parse<T>(value, ignoreCase: true);

    public static void AddParam(this SqliteParameterCollection parameters, string name, object? value)
        => parameters.AddWithValue(name, value ?? DBNull.Value);
}
