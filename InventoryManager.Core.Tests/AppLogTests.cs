using InventoryManager.Core.Logging;

namespace InventoryManager.Core.Tests;

public class AppLogFormatterTests
{
    [Fact]
    public void FormatDisplayLine_MatchesExpectedShape()
    {
        var line = AppLogFormatter.FormatDisplayLine(new DateTime(2026, 1, 1, 13, 5, 9), AppLogLevel.Warning, "Something happened");
        Assert.Equal("[13:05:09] WARN: Something happened", line);
    }

    [Fact]
    public void FormatFileLine_MatchesExpectedShape()
    {
        var line = AppLogFormatter.FormatFileLine(new DateTime(2026, 1, 1, 13, 5, 9), AppLogLevel.Error, "Boom");
        Assert.Equal("2026-01-01 13:05:09 ERROR Boom\r\n", line);
    }

    [Theory]
    [InlineData(AppLogLevel.Info, "INFO")]
    [InlineData(AppLogLevel.Warning, "WARN")]
    [InlineData(AppLogLevel.Error, "ERROR")]
    public void LevelPrefix_MapsCorrectly(AppLogLevel level, string expected)
    {
        Assert.Equal(expected, AppLogFormatter.LevelPrefix(level));
    }

    [Fact]
    public void FormatDisplayLine_WithWho_IncludesIt()
    {
        var line = AppLogFormatter.FormatDisplayLine(new DateTime(2026, 1, 1, 13, 5, 9), AppLogLevel.Warning, "Something happened", "sujdoe");
        Assert.Equal("[13:05:09] sujdoe WARN: Something happened", line);
    }

    [Fact]
    public void FormatFileLine_WithWho_IncludesIt()
    {
        var line = AppLogFormatter.FormatFileLine(new DateTime(2026, 1, 1, 13, 5, 9), AppLogLevel.Error, "Boom", "sumislaj");
        Assert.Equal("2026-01-01 13:05:09 sumislaj ERROR Boom\r\n", line);
    }

    [Fact]
    public void TryParseFileLine_NewFormatWithWho_RoundTrips()
    {
        var written = AppLogFormatter.FormatFileLine(new DateTime(2026, 1, 1, 13, 5, 9), AppLogLevel.Warning, "Something happened, with spaces", "sujdoe");
        var ok = AppLogFormatter.TryParseFileLine(written.TrimEnd('\r', '\n'), out var ts, out var level, out var who, out var message);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 1, 1, 13, 5, 9), ts);
        Assert.Equal(AppLogLevel.Warning, level);
        Assert.Equal("sujdoe", who);
        Assert.Equal("Something happened, with spaces", message);
    }

    [Fact]
    public void TryParseFileLine_OldFormatWithoutWho_StillParses()
    {
        // Anything logged before the 2026-09-23 "who" field shipped, or from a call site that never
        // supplied one, must still parse correctly - just with an empty who.
        var written = AppLogFormatter.FormatFileLine(new DateTime(2026, 1, 1, 13, 5, 9), AppLogLevel.Error, "Boom with multiple words");
        var ok = AppLogFormatter.TryParseFileLine(written.TrimEnd('\r', '\n'), out var ts, out var level, out var who, out var message);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 1, 1, 13, 5, 9), ts);
        Assert.Equal(AppLogLevel.Error, level);
        Assert.Equal("", who);
        Assert.Equal("Boom with multiple words", message);
    }

    [Fact]
    public void TryParseFileLine_GarbageLine_ReturnsFalse()
    {
        Assert.False(AppLogFormatter.TryParseFileLine("not a log line at all", out _, out _, out _, out _));
    }
}

public class AppLogWriterTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"logtest_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, recursive: true);
        }
    }

    [Fact]
    public void Write_CreatesDatedLogFile()
    {
        var writer = new AppLogWriter(_logDir);
        writer.Write("Hello");
        var expectedPath = Path.Combine(_logDir, $"InventoryManager_{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(File.Exists(expectedPath));
        Assert.Contains("Hello", File.ReadAllText(expectedPath));
    }

    [Fact]
    public void Write_MultipleCalls_AppendRatherThanOverwrite()
    {
        var writer = new AppLogWriter(_logDir);
        writer.Write("First");
        writer.Write("Second");
        var expectedPath = Path.Combine(_logDir, $"InventoryManager_{DateTime.Now:yyyy-MM-dd}.log");
        var content = File.ReadAllText(expectedPath);
        Assert.Contains("First", content);
        Assert.Contains("Second", content);
    }
}
