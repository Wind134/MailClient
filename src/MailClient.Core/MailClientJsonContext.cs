using System.Text.Json.Serialization;

namespace MailClient.Core;

/// <summary>
/// 应用自有 DTO 的 STJ 源生成上下文。
/// Graph / Kiota / MSAL 仍依赖反射，因此打包默认关闭 PublishTrimmed；
/// 本地类型仍走 source-gen，以便以后再尝试裁剪时少踩坑。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(OutgoingMailMessage))]
public partial class MailClientJsonContext : JsonSerializerContext
{
}
