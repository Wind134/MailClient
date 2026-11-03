using System.Text.Json.Serialization;

namespace MailClient.Auth;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GmailTokenSet))]
public partial class GmailAuthJsonContext : JsonSerializerContext
{
}
