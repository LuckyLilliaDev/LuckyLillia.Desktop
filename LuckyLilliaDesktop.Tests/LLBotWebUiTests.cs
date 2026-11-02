using LuckyLilliaDesktop.Utils;
using Xunit;

namespace LuckyLilliaDesktop.Tests;

public class LLBotWebUiTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveInstanceName_blank_means_default(string? configured)
    {
        Assert.Equal("default", LLBotWebUi.ResolveInstanceName(configured));
    }

    [Theory]
    [InlineData("bot-2")]
    [InlineData("123456789")]
    [InlineData("a_b")]
    public void ResolveInstanceName_keeps_valid_names_trimmed(string name)
    {
        Assert.Equal(name, LLBotWebUi.ResolveInstanceName($" {name} "));
    }

    [Theory]
    [InlineData("-abc")]
    [InlineData("_abc")]
    [InlineData("中文")]
    [InlineData("a b")]
    [InlineData("a/b")]
    public void ResolveInstanceName_rejects_names_llbot_would_refuse(string name)
    {
        Assert.Null(LLBotWebUi.ResolveInstanceName(name));
    }

    [Fact]
    public void ResolveInstanceName_rejects_names_longer_than_64()
    {
        Assert.Equal(new string('a', 64), LLBotWebUi.ResolveInstanceName(new string('a', 64)));
        Assert.Null(LLBotWebUi.ResolveInstanceName(new string('a', 65)));
    }

    [Fact]
    public void ParseEndpoint_reads_webui_section()
    {
        var endpoint = LLBotWebUi.ParseEndpoint("""{"webui":{"enable":true,"host":"0.0.0.0","port":4080},"log":true}""");

        Assert.Equal(new WebUiEndpoint(true, "0.0.0.0", 4080), endpoint);
        Assert.Equal("http://127.0.0.1:4080", endpoint.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"webui":"x"}""")]
    public void ParseEndpoint_falls_back_to_llbot_defaults(string? json)
    {
        Assert.Equal(LLBotWebUi.DefaultEndpoint, LLBotWebUi.ParseEndpoint(json));
        Assert.Equal("http://127.0.0.1:3080", LLBotWebUi.ParseEndpoint(json).Url);
    }

    [Theory]
    [InlineData("""{"webui":{"port":0}}""")]
    [InlineData("""{"webui":{"port":70000}}""")]
    [InlineData("""{"webui":{"port":"3080"}}""")]
    public void ParseEndpoint_ignores_invalid_port(string json)
    {
        Assert.Equal(3080, LLBotWebUi.ParseEndpoint(json).Port);
    }

    [Fact]
    public void ParseEndpoint_reports_disabled_webui()
    {
        Assert.False(LLBotWebUi.ParseEndpoint("""{"webui":{"enable":false}}""").Enabled);
        Assert.True(LLBotWebUi.ParseEndpoint("""{"webui":{"port":3081}}""").Enabled);
    }

    [Theory]
    [InlineData("", "127.0.0.1")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("*", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    [InlineData("[::1]", "::1")]
    public void ConnectHost_maps_wildcards_to_loopback(string host, string expected)
    {
        Assert.Equal(expected, LLBotWebUi.ConnectHost(host));
    }

    [Fact]
    public void Url_brackets_ipv6_literals()
    {
        Assert.Equal("http://[::1]:3080", new WebUiEndpoint(true, "::1", 3080).Url);
        Assert.Equal("http://[fe80::1]:3080", new WebUiEndpoint(true, "[fe80::1]", 3080).Url);
    }

    [Fact]
    public void SettingsPath_is_under_the_instance_directory()
    {
        var path = LLBotWebUi.SettingsPath(Path.Combine("bin", "llbot", "llbot.js"), "default");

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith(Path.Combine("bin", "llbot", "data", "instances", "default", "settings.json"), path);
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("bot-2_x", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-abc", false)]
    [InlineData("a/b", false)]
    public void IsValidInstanceName_follows_llbot_rule(string? name, bool expected)
    {
        Assert.Equal(expected, LLBotWebUi.IsValidInstanceName(name));
    }

    [Fact]
    public void Instance_paths_follow_llbot_data_layout()
    {
        var script = Path.Combine(Path.GetTempPath(), "llbot-layout", "llbot.js");
        var root = Path.GetDirectoryName(script)!;

        Assert.Equal(Path.Combine(root, "data", "instances"), LLBotWebUi.InstancesDir(script));
        Assert.Equal(Path.Combine(root, "data", "instances", "bot2"), LLBotWebUi.InstanceDir(script, "bot2"));
        Assert.Equal(Path.Combine(root, "data", "state", "instances", "bot2.json"), LLBotWebUi.LockHolderPath(script, "bot2"));
    }

    [Fact]
    public void ListInstances_is_empty_when_the_directory_does_not_exist()
    {
        var script = Path.Combine(Path.GetTempPath(), "llbot-missing-" + Guid.NewGuid().ToString("N"), "llbot.js");
        Assert.Empty(LLBotWebUi.ListInstances(script));
    }

    [Fact]
    public void ListInstances_keeps_valid_names_with_default_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "llbot-list-" + Guid.NewGuid().ToString("N"));
        var instances = Path.Combine(root, "data", "instances");
        foreach (var name in new[] { "zeta", "default", "alpha", "-bad", "中文", "ok_1" })
            Directory.CreateDirectory(Path.Combine(instances, name));
        // 文件不是实例
        File.WriteAllText(Path.Combine(instances, "notes"), "");
        try
        {
            var list = LLBotWebUi.ListInstances(Path.Combine(root, "llbot.js"));
            Assert.Equal(new[] { "default", "alpha", "ok_1", "zeta" }, list);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
