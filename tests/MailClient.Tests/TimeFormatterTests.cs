using MailClient.ViewModels;

namespace MailClient.Tests;

public class TimeFormatterTests
{
    private static readonly TimeSpan Cn = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 0, 0, Cn); // 周六

    [Fact]
    public void Today_Shows_Clock_With_Period()
        => Assert.Equal("上午 10:24", TimeFormatter.FormatShort(new DateTimeOffset(2026, 10, 31, 10, 24, 0, Cn), Now));

    [Fact]
    public void Yesterday_Shows_Word()
        => Assert.Equal("昨天", TimeFormatter.FormatShort(new DateTimeOffset(2026, 10, 30, 22, 0, 0, Cn), Now));

    [Fact]
    public void Earlier_This_Year_Shows_Month_Day()
        => Assert.Equal("3月5日", TimeFormatter.FormatShort(new DateTimeOffset(2026, 3, 5, 9, 0, 0, Cn), Now));

    [Fact]
    public void Other_Year_Shows_Full_Date()
        => Assert.Equal("2025/12/1", TimeFormatter.FormatShort(new DateTimeOffset(2025, 12, 1, 9, 0, 0, Cn), Now));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(2048, "2 KB")]
    public void FormatSize_Units(long bytes, string expected)
        => Assert.Equal(expected, TimeFormatter.FormatSize(bytes));
}
