using MailClient.Core.Logging;

namespace MailClient.Tests;

public sealed class RotatingLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-log-" + Guid.NewGuid().ToString("N"));

    public RotatingLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private string P(string name = "test.log") => Path.Combine(_dir, name);

    [Fact]
    public void Append_Below_Limit_Does_Not_Rotate()
    {
        RotatingLog.Append(P(), "hello\n", maxBytes: 100);
        RotatingLog.Append(P(), "world\n", maxBytes: 100);
        Assert.Equal("hello\nworld\n", File.ReadAllText(P()));
        Assert.False(File.Exists(P() + ".1"));
    }

    [Fact]
    public void Rotates_To_Dot1_When_Limit_Exceeded()
    {
        RotatingLog.Append(P(), new string('a', 60) + "\n", maxBytes: 100);
        RotatingLog.Append(P(), new string('b', 60) + "\n", maxBytes: 100);
        Assert.StartsWith("b", File.ReadAllText(P()));
        Assert.StartsWith("a", File.ReadAllText(P() + ".1"));
    }

    [Fact]
    public void Keeps_At_Most_Two_Old_Files_And_Drops_Oldest()
    {
        foreach (var c in "abcde")
        {
            RotatingLog.Append(P(), new string(c, 60) + "\n", maxBytes: 100, keep: 2);
        }

        Assert.StartsWith("e", File.ReadAllText(P()));
        Assert.StartsWith("d", File.ReadAllText(P() + ".1"));
        Assert.StartsWith("c", File.ReadAllText(P() + ".2"));
        Assert.False(File.Exists(P() + ".3"));
    }

    [Fact]
    public void Total_Stays_Bounded_Under_Heavy_Writing()
    {
        const int max = 1000;
        for (var i = 0; i < 500; i++)
        {
            RotatingLog.Append(P(), $"line {i:D5} {new string('x', 40)}\n", maxBytes: max, keep: 2);
        }

        // 当前文件 ≤ max，最多 2 份旧文件，每份 ≤ max
        Assert.True(new FileInfo(P()).Length <= max);
        Assert.True(RotatingLog.TotalBytes(P(), 2) <= 3L * max);
        Assert.False(File.Exists(P() + ".3"));
    }

    [Fact]
    public void RotateIfNeeded_Handles_Oversized_Legacy_File()
    {
        File.WriteAllText(P(), new string('z', 5000));
        Assert.False(RotatingLog.RotateIfNeeded(P("none.log"), 1000));
        Assert.True(RotatingLog.RotateIfNeeded(P(), 1000));
        Assert.False(File.Exists(P()));
        Assert.Equal(5000, new FileInfo(P() + ".1").Length);
        Assert.False(RotatingLog.RotateIfNeeded(P(), 1000));
    }

    [Fact]
    public void TotalBytes_And_DeleteAll_Cover_Rotated_Files()
    {
        File.WriteAllText(P(), "12345");
        File.WriteAllText(P() + ".1", "123");
        File.WriteAllText(P() + ".2", "12");
        Assert.Equal(10, RotatingLog.TotalBytes(P()));
        Assert.Equal(10, RotatingLog.DeleteAll(P()));
        Assert.Equal(0, RotatingLog.TotalBytes(P()));
    }

    [Fact]
    public void Append_Never_Throws_On_Bad_Path()
    {
        var ex = Record.Exception(() => RotatingLog.Append(Path.Combine(_dir, "a\0b", "x.log"), "text"));
        Assert.Null(ex);
    }

    [Fact]
    public void Concurrent_Appends_Do_Not_Lose_Or_Throw()
    {
        var errors = 0;
        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 100; i++)
            {
                try { RotatingLog.Append(P(), $"t{t}-{i}\n", maxBytes: 4000, keep: 2); }
                catch { Interlocked.Increment(ref errors); }
            }
        });
        Assert.Equal(0, errors);
        Assert.True(RotatingLog.TotalBytes(P()) <= 3 * 4000);
    }

    [Fact]
    public void Default_Cap_Is_About_One_Megabyte_With_Two_Rotations()
    {
        Assert.InRange(RotatingLog.DefaultMaxBytes, 900_000, 1_100_000);
        Assert.Equal(2, RotatingLog.DefaultKeep);
    }
}
