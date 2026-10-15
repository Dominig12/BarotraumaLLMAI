using System;
using System.IO;
using Xunit;

namespace LlmAgents.Tests;

public class SettingsTests
{
    [Fact]
    public void ShippedConfigIsValid()
    {
        var s = ModSettings.LoadFile(Path.Combine(System.AppContext.BaseDirectory, "llm_config.json"));
        Assert.True(s.Enabled);
        Assert.Contains(s.Providers, p => p.Type == "ollama");
        Assert.Contains(s.Providers, p => p.Type == "anthropic");
        Assert.Equal("cloud", s.ProviderFor("Captain")); // job lookup is case-insensitive
        Assert.Equal(s.DefaultProvider, s.ProviderFor("mechanic"));
    }

    [Fact]
    public void ParsesCommentsTrailingCommasAndCase()
    {
        var json = Fixtures.Config().Replace("\"defaultProvider\"", "// comment\n\"DefaultProvider\"").Replace("\"m-b\",", "\"m-b\",");
        var s = ModSettings.Parse(json);
        Assert.Equal("a", s.DefaultProvider);
        Assert.Equal(2, s.Providers.Count);
    }

    [Theory]
    [InlineData("\"type\": \"openai\", \"baseUrl\": \"http://a.test/v1\"", "\"type\": \"gemini\", \"baseUrl\": \"http://a.test/v1\"", "type must be")]
    [InlineData("\"baseUrl\": \"http://a.test/v1\"", "\"baseUrl\": \"not a url\"", "baseUrl")]
    [InlineData("\"model\": \"m-a\"", "\"model\": \"\"", "model is required")]
    public void RejectsBadProvider(string find, string replace, string expected)
    {
        var json = Fixtures.Config();
        int i = json.IndexOf(find, System.StringComparison.Ordinal);
        json = json.Substring(0, i) + replace + json.Substring(i + find.Length);
        var e = Assert.Throws<InvalidDataException>(() => ModSettings.Parse(json));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void RejectsUnknownProviderReferences()
    {
        Assert.Throws<InvalidDataException>(() => ModSettings.Parse(Fixtures.Config().Replace("\"defaultProvider\": \"a\"", "\"defaultProvider\": \"zzz\"")));
        Assert.Throws<InvalidDataException>(() => ModSettings.Parse(Fixtures.Config().Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"zzz\"")));
        Assert.Throws<InvalidDataException>(() => ModSettings.Parse(Fixtures.Config().Replace("\"providerByJob\": {}", "\"providerByJob\": {\"captain\": \"zzz\"}")));
    }

    [Fact]
    public void RejectsDuplicateProvidersAndInvalidJson()
    {
        var dup = Fixtures.Config().Replace("\"name\": \"b\"", "\"name\": \"A\"").Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\"");
        Assert.Contains("Duplicate", Assert.Throws<InvalidDataException>(() => ModSettings.Parse(dup)).Message);
        Assert.Throws<InvalidDataException>(() => ModSettings.Parse("{ not json"));
    }

    [Fact]
    public void ApiKeyFromEnvironment()
    {
        System.Environment.SetEnvironmentVariable("LLMAGENTS_TEST_KEY", "sekret");
        var p = new ProviderConfig { ApiKeyEnv = "LLMAGENTS_TEST_KEY" };
        Assert.Equal("sekret", p.ResolvedKey);
        p.ApiKey = "inline";
        Assert.Equal("inline", p.ResolvedKey);
    }

    [Fact]
    public void DisabledJobs()
    {
        var s = Fixtures.Settings();
        s.Bots.DisabledJobs.Add("Assistant");
        Assert.False(s.IsJobEnabled("assistant"));
        Assert.True(s.IsJobEnabled("mechanic"));
    }

    [Fact]
    public void ChatAndSpeechSettings()
    {
        var s = Fixtures.Settings();
        Assert.True(s.Chat.Enabled);
        Assert.Equal("auto", s.Global.SpeechChannel);
        Assert.Throws<InvalidDataException>(() => Fixtures.Settings(", \"speechChannel\": \"telepathy\""));
        Assert.Throws<InvalidDataException>(() => Fixtures.Settings(extraRoot: "\"chat\": { \"provider\": \"zzz\" },"));
        Assert.Equal("local", Fixtures.Settings(", \"speechChannel\": \"LOCAL\"").Global.SpeechChannel);
    }

    [Fact]
    public void MemoryAndAutonomySettings()
    {
        var s = Fixtures.Settings();
        Assert.True(s.Memory.Enabled);
        Assert.Equal("memory", s.Memory.Folder);
        Assert.Equal("always", s.Bots.Autonomy);
        Assert.True(s.Global.ShowBotMessagesInChat);
        Assert.Throws<InvalidDataException>(() => Fixtures.Settings(extraRoot: "\"memory\": { \"summaryProvider\": \"zzz\" },"));
        var custom = ModSettings.Parse(Fixtures.Config().Replace("\"allowedTools\": []", "\"allowedTools\": [], \"autonomy\": \"ORDER\""));
        Assert.Equal("order", custom.Bots.Autonomy);
        Assert.Throws<InvalidDataException>(() => ModSettings.Parse(Fixtures.Config().Replace("\"allowedTools\": []", "\"allowedTools\": [], \"autonomy\": \"sometimes\"")));
    }
}
