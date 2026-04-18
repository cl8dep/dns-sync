using DnsSync.Config;
using Shouldly;

namespace DnsSync.Tests.Config;

public class DotEnvLoaderTests : IDisposable
{
    private readonly string _tmpFile;
    private readonly List<string> _keysToClean = new();

    public DotEnvLoaderTests()
    {
        _tmpFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        File.Delete(_tmpFile);
        foreach (var key in _keysToClean)
            Environment.SetEnvironmentVariable(key, null);
    }

    private int LoadContent(string content)
    {
        File.WriteAllText(_tmpFile, content);
        return DotEnvLoader.Load(_tmpFile);
    }

    private void Track(string key) => _keysToClean.Add(key);

    [Fact]
    public void Load_SimpleKeyValue_SetsVariable()
    {
        Track("DOTENV_TEST_SIMPLE");
        LoadContent("DOTENV_TEST_SIMPLE=hello");
        Environment.GetEnvironmentVariable("DOTENV_TEST_SIMPLE").ShouldBe("hello");
    }

    [Fact]
    public void Load_DoubleQuotedValue_StripsQuotes()
    {
        Track("DOTENV_TEST_DQ");
        LoadContent("DOTENV_TEST_DQ=\"quoted value\"");
        Environment.GetEnvironmentVariable("DOTENV_TEST_DQ").ShouldBe("quoted value");
    }

    [Fact]
    public void Load_SingleQuotedValue_StripsQuotes()
    {
        Track("DOTENV_TEST_SQ");
        LoadContent("DOTENV_TEST_SQ='single quoted'");
        Environment.GetEnvironmentVariable("DOTENV_TEST_SQ").ShouldBe("single quoted");
    }

    [Fact]
    public void Load_ExportPrefix_IsStripped()
    {
        Track("DOTENV_TEST_EXPORT");
        LoadContent("export DOTENV_TEST_EXPORT=exported");
        Environment.GetEnvironmentVariable("DOTENV_TEST_EXPORT").ShouldBe("exported");
    }

    [Fact]
    public void Load_InlineComment_IsStripped()
    {
        Track("DOTENV_TEST_COMMENT");
        LoadContent("DOTENV_TEST_COMMENT=value # this is a comment");
        Environment.GetEnvironmentVariable("DOTENV_TEST_COMMENT").ShouldBe("value");
    }

    [Fact]
    public void Load_InlineCommentInsideDoubleQuotes_IsNotStripped()
    {
        Track("DOTENV_TEST_HASH_IN_QUOTES");
        LoadContent("DOTENV_TEST_HASH_IN_QUOTES=\"value # not a comment\"");
        Environment.GetEnvironmentVariable("DOTENV_TEST_HASH_IN_QUOTES").ShouldBe("value # not a comment");
    }

    [Fact]
    public void Load_CommentLines_AreSkipped()
    {
        Track("DOTENV_TEST_AFTER_COMMENT");
        LoadContent("# this is a comment\nDOTENV_TEST_AFTER_COMMENT=real");
        Environment.GetEnvironmentVariable("DOTENV_TEST_AFTER_COMMENT").ShouldBe("real");
    }

    [Fact]
    public void Load_BlankLines_AreSkipped()
    {
        Track("DOTENV_TEST_BLANK");
        LoadContent("\n\nDOTENV_TEST_BLANK=value\n\n");
        Environment.GetEnvironmentVariable("DOTENV_TEST_BLANK").ShouldBe("value");
    }

    [Fact]
    public void Load_DoesNotOverrideExistingEnvVar()
    {
        Track("DOTENV_TEST_EXISTING");
        Environment.SetEnvironmentVariable("DOTENV_TEST_EXISTING", "original");
        var count = LoadContent("DOTENV_TEST_EXISTING=overridden");
        Environment.GetEnvironmentVariable("DOTENV_TEST_EXISTING").ShouldBe("original");
        count.ShouldBe(0);
    }

    [Fact]
    public void Load_ReturnsCountOfLoadedVars()
    {
        Track("DOTENV_COUNT_A");
        Track("DOTENV_COUNT_B");
        var count = LoadContent("DOTENV_COUNT_A=1\nDOTENV_COUNT_B=2");
        count.ShouldBe(2);
    }

    [Fact]
    public void Load_EscapeSequencesInDoubleQuotes_AreInterpreted()
    {
        Track("DOTENV_TEST_ESCAPE");
        LoadContent("DOTENV_TEST_ESCAPE=\"line1\\nline2\"");
        Environment.GetEnvironmentVariable("DOTENV_TEST_ESCAPE").ShouldBe("line1\nline2");
    }

    [Fact]
    public void TryLoad_ReturnsFalseForMissingFile()
    {
        var result = DotEnvLoader.TryLoad("/nonexistent/.env", out var loaded);
        result.ShouldBeFalse();
        loaded.ShouldBe(0);
    }
}
