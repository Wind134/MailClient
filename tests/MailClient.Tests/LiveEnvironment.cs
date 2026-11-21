namespace MailClient.Tests;

/// <summary>
/// 联调测试的环境变量入口。缺失凭据时用跳过（而非静默 return）结束用例，
/// 使测试报告能如实区分“已执行”和“未执行”。
/// </summary>
internal static class LiveEnvironment
{
    public static string Require(string name)
    {
        var value = Optional(name);
        Skip.If(value is null, $"未设置环境变量 {name}，跳过联调测试。");
        return value!;
    }

    public static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int OptionalPort(string name, int fallback)
        => int.TryParse(Optional(name), out var port) ? port : fallback;

    public static bool IsConfigured(params string[] names)
        => names.All(n => Optional(n) is not null);
}
