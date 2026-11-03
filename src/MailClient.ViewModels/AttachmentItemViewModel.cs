using System.Windows.Input;
using MailClient.Core;

namespace MailClient.ViewModels;

public sealed class AttachmentItemViewModel
{
    public AttachmentItemViewModel(AttachmentEntity entity, ICommand? downloadCommand = null)
    {
        Id = entity.Id;
        FileName = entity.FileName;
        SizeText = TimeFormatter.FormatSize(entity.SizeBytes);
        Glyph = Path.GetExtension(entity.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" => "\uEB9F",
            _ => "\uE723"
        };
        DownloadCommand = downloadCommand;
    }

    public string Id { get; }
    public string FileName { get; }
    public string SizeText { get; }
    public string Glyph { get; }
    public ICommand? DownloadCommand { get; }
}
