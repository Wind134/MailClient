using MailClient.Providers.Setup;

namespace MailClient.ViewModels.AddAccount;

/// <summary>引导里的一个链接（点击后用系统默认浏览器打开）。</summary>
public sealed record GuideLink(string Text, string Url)
{
    public Uri Uri => new(Url);
}

/// <summary>引导里的一步：编号 + 说明 + 可选的一个链接。</summary>
public sealed record GuideStep(string Number, string Text, string? LinkText = null, string? LinkUrl = null)
{
    public bool HasLink => !string.IsNullOrEmpty(LinkUrl);

    /// <summary>没有链接时返回一个占位地址（x:Bind 不能绑 null 的 Uri 到 NavigateUri 时安全）；界面上由 <see cref="HasLink"/> 控制是否显示。</summary>
    public Uri LinkUri => new(LinkUrl ?? "about:blank");
}

/// <summary>
/// 添加账号向导里每种邮箱的“怎么设置”引导：分步说明 + 一键打开的网页链接 + 注意事项。
/// 内容全部内置在软件里，不依赖外部文档；链接用系统默认浏览器打开。
/// </summary>
public sealed record ProviderGuide(
    string Title,
    string Intro,
    IReadOnlyList<GuideStep> Steps,
    IReadOnlyList<string> Notes,
    IReadOnlyList<GuideLink> Links)
{
    public const string GmailAppPasswordUrl = "https://myaccount.google.com/apppasswords";

    /// <summary>WAM / 系统账号 Broker 重定向 URI 模板（把 {ClientId} 换成应用程序客户端 ID）。</summary>
    public const string MicrosoftBrokerRedirectUriTemplate = "ms-appx-web://microsoft.aad.brokerplugin/{ClientId}";

    public bool HasNotes => Notes.Count > 0;
    public bool HasLinks => Links.Count > 0;

    /// <summary>所有出现过的网址（单元测试逐个检查格式；发布前用 curl 验证）。</summary>
    public IEnumerable<string> AllUrls => Steps.Where(s => s.HasLink).Select(s => s.LinkUrl!).Concat(Links.Select(l => l.Url));

    public static ProviderGuide For(SetupProviderKind kind) => kind switch
    {
        SetupProviderKind.Gmail => Gmail,
        SetupProviderKind.Outlook => Outlook,
        SetupProviderKind.Qq => Qq,
        SetupProviderKind.NetEase => NetEase,
        _ => OtherImap
    };

    public static ProviderGuide Gmail { get; } = new(
        "Gmail：一次性设置（约 5 分钟，免费）",
        "Google 要求每个桌面邮件软件自带 OAuth 凭据。这里的凭据是你自己创建的，只有你能用，不会把邮件交给任何第三方。",
        [
            new("1", "打开 Google Cloud 控制台，新建一个项目（名称随意，例如 MailClient）。", "新建项目", "https://console.cloud.google.com/projectcreate"),
            new("2", "打开「Google Auth Platform」，点「开始」：填应用名称（例如 MailClient）和你的邮箱；受众群体选「外部」。", "打开 Google Auth Platform", "https://console.cloud.google.com/auth/overview"),
            new("3", "在「受众群体」页面的「测试用户」里，添加你要登录的 Gmail 地址并保存。", "打开「受众群体」", "https://console.cloud.google.com/auth/audience"),
            new("4", "在「数据访问」页面点「添加或移除范围」，手动添加范围 https://mail.google.com/ 并保存。", "打开「数据访问」", "https://console.cloud.google.com/auth/scopes"),
            new("5", "在「客户端」页面点「创建客户端」，应用类型选「桌面应用」，创建后复制「客户端 ID」和「客户端密钥」。", "打开「客户端」", "https://console.cloud.google.com/auth/clients"),
            new("6", "回到本窗口，把客户端 ID 和客户端密钥粘贴到下方「ClientId 设置」里，再点「用 Google 登录」。"),
            new("7", "浏览器若提示「Google 尚未验证此应用」，这是正常的（应用是你自己创建的）：点「高级」→「转至 … （不安全）」，勾选授权后继续。")
        ],
        [
            "应用处于「测试」状态时，Google 会让授权每 7 天失效一次。想避免重复登录：在「受众群体」页面点「发布应用」即可（个人自用无需审核，登录时仍会出现“未验证”提示）。",
            "不想折腾云控制台？也可以给 Gmail 开启两步验证，生成「应用专用密码」，然后回到第一步选「其他邮箱 (IMAP)」添加（服务器 imap.gmail.com，SSL/TLS 993）。"
        ],
        [new GuideLink("生成 Gmail 应用专用密码", GmailAppPasswordUrl)]);

    public static ProviderGuide Outlook { get; } = new(
        "Outlook / Microsoft 365：一次性设置（约 5 分钟，免费）",
        "微软同样要求应用自带「应用程序（客户端）ID」。在 Microsoft Entra 里免费注册一个即可，个人 Outlook.com 账号也能用。本客户端默认用系统浏览器登录（不用 WAM）。",
        [
            new("1", "打开 Microsoft Entra 管理中心 →「应用注册」→「新注册」。", "打开 Microsoft Entra", "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"),
            new("2", "名称随意（例如 MailClient）；「受支持的账户类型」选「任何组织目录中的账户和个人 Microsoft 账户」，这样 Outlook.com / Hotmail 个人邮箱也能登录。"),
            new("3", "「重定向 URI」：平台选「移动和桌面应用程序」，至少添加 http://localhost （系统浏览器登录要用）。"),
            new("4", "同一「身份验证」页再添加一条：ms-appx-web://microsoft.aad.brokerplugin/{你的客户端ID}（把大括号整段换成概述页的应用程序客户端 ID）。即使本客户端默认不用系统账号（WAM），补上这条可避免个别环境仍走 WAM 时报错。"),
            new("5", "进入「API 权限」→「添加权限」→ Microsoft Graph →「委托的权限」，勾选：User.Read、Mail.Read、Mail.ReadWrite、Mail.Send、offline_access。"),
            new("6", "回到「概述」页，复制「应用程序(客户端) ID」，粘贴到下方「ClientId 设置」的 ClientId 里（租户保持 common 即可）。"),
            new("7", "点「用 Microsoft 登录」：会打开系统浏览器完成登录并同意权限（不要关本软件窗口）。")
        ],
        [
            "公司 / 学校账号如果提示“需要管理员批准”，请联系管理员，或改用个人邮箱。",
            "用纯个人账号登录 Entra 时若提示没有目录，可先免费创建一个 Azure 账户（只用来注册应用，不会扣费）。",
            "若仍弹出「执行此操作需要 Internet」或 WAM Error，多半是系统账号组件误报：本客户端已默认改用浏览器；请确认 http://localhost 已登记，然后重试。",
            "这个 ID 不是机密，保存在本机 %LocalAppData%\\MailClient\\settings.json。"
        ],
        [new GuideLink("官方教程：注册应用", "https://learn.microsoft.com/entra/identity-platform/quickstart-register-app")]);

    public static ProviderGuide Qq { get; } = new(
        "QQ 邮箱：需要「授权码」（不是 QQ 密码）",
        "QQ 邮箱默认不允许第三方软件登录。需要先在网页版开启 IMAP/SMTP 服务，并生成一个 16 位授权码。",
        [
            new("1", "用浏览器登录网页版 QQ 邮箱。", "打开 mail.qq.com", "https://mail.qq.com"),
            new("2", "点右上角头像或顶部「设置」→「账号与安全」→「安全设置」。"),
            new("3", "找到「POP3/IMAP/SMTP/Exchange/CardDAV/CalDAV 服务」，点「开启服务」，按提示用手机验证。"),
            new("4", "服务开启后点「生成授权码」，复制弹出的 16 位授权码。"),
            new("5", "回到本窗口：邮箱地址填完整的 QQ 邮箱（如 123456@qq.com），把授权码粘贴到下面的「授权码」框里。")
        ],
        [
            "授权码只显示一次；忘记了就在网页版重新生成。修改 QQ 密码后旧授权码会失效，也需要重新生成。"
        ],
        [new GuideLink("查看 QQ 邮箱官方说明", "https://help.mail.qq.com/detail/0/985")]);

    public static ProviderGuide NetEase { get; } = new(
        "网易邮箱（163 / 126 / yeah.net）：需要「客户端授权码」",
        "网易邮箱不允许用网页登录密码连接第三方软件。需要先在网页版开启 IMAP/SMTP 服务，并新增一个授权码。",
        [
            new("1", "用浏览器登录网页版网易邮箱。", "打开 mail.163.com", "https://mail.163.com"),
            new("2", "点顶部「设置」→「POP3/SMTP/IMAP」。"),
            new("3", "开启「IMAP/SMTP 服务」，按提示用手机验证。"),
            new("4", "在下方「授权密码管理」里点「新增授权密码」（授权码），复制弹出的授权码。"),
            new("5", "回到本窗口：邮箱地址填完整的网易邮箱，把授权码粘贴到下面的「授权码」框里。")
        ],
        [
            "授权码只显示一次，丢了就新增一个。126 邮箱请从 mail.126.com 登录，步骤相同。",
            "若登录时报 “Unsafe Login”，说明服务器限制了该客户端：确认授权码是新生成的、IMAP/SMTP 服务已开启，稍后再试。"
        ],
        [new GuideLink("打开 mail.126.com", "https://mail.126.com"), new GuideLink("查看网易邮箱官方说明", "https://help.mail.163.com/faqDetail.do?code=d7a5dc8471cd0c0e8b4b8f4f8e49998b374173cfe9171305fa1ce630d7f67ac2a5feb28b66796d3b")]);

    public static ProviderGuide OtherImap { get; } = new(
        "其他邮箱 (IMAP)：该填什么？",
        "任何支持 IMAP / SMTP 的邮箱都能添加。大多数情况下只要邮箱地址 + 密码，软件会自动找到服务器。",
        [
            new("1", "填写完整的邮箱地址，点「自动检测服务器设置」。软件会按常见规则和 Mozilla ISPDB 预填收信（IMAP）与发信（SMTP）服务器。"),
            new("2", "「密码」处：Gmail、QQ、网易、iCloud、Yahoo 等多数服务商要求使用「应用专用密码 / 授权码」，而不是登录密码——请到该邮箱网页版的「设置 → 安全 / 账号」里生成。"),
            new("3", "自动检测不准或失败时，展开「高级设置」手动填：服务器是服务商提供的域名（如 imap.example.com / smtp.example.com）；端口 IMAP 一般 993、SMTP 一般 465 或 587。"),
            new("4", "「安全连接」：993 / 465 选 SSL/TLS，143 / 587 选 STARTTLS；尽量不要选「无（不加密）」。"),
            new("5", "「用户名」默认就是邮箱地址；个别服务器只要 @ 前面的部分，可在高级设置里改。")
        ],
        [
            "验证失败时，错误提示会指出是登录被拒（密码 / 授权码问题）还是连不上（地址 / 端口 / 加密方式问题）。"
        ],
        []);
}
