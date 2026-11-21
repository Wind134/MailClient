namespace MailClient.Core;

/// <summary>演示数据的账号标识。演示账号没有服务器可连，多个模块据此跳过网络操作。</summary>
public static class DemoAccounts
{
    public static readonly Guid Outlook = new("0d6f1c5e-3b1a-4c55-9a4e-0a1b2c3d4e01");

    public static readonly Guid Qq = new("0d6f1c5e-3b1a-4c55-9a4e-0a1b2c3d4e02");

    public static bool IsDemoAccount(Guid accountId) => accountId == Outlook || accountId == Qq;
}
