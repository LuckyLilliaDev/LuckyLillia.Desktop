using System.Text.Json;
using LuckyLilliaDesktop.Utils;
using Xunit;

namespace LuckyLilliaDesktop.Tests;

public class LLBotWebUiTests
{
    [Theory]
    [InlineData("default", true)]
    [InlineData("bot-2_x", true)]
    [InlineData("123456789", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-abc", false)]
    [InlineData("_abc", false)]
    [InlineData("中文", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    public void IsValidInstanceName_follows_llbot_rule(string? name, bool expected)
    {
        Assert.Equal(expected, LLBotWebUi.IsValidInstanceName(name));
    }

    [Fact]
    public void IsValidInstanceName_rejects_names_longer_than_64()
    {
        Assert.True(LLBotWebUi.IsValidInstanceName(new string('a', 64)));
        Assert.False(LLBotWebUi.IsValidInstanceName(new string('a', 65)));
    }

    [Fact]
    public void ParseManagerGateway_reads_gateway_section()
    {
        var endpoint = LLBotWebUi.ParseManagerGateway("""{"gateway":{"host":"0.0.0.0","port":4080},"instances":{}}""");

        Assert.Equal(new WebUiEndpoint(true, "0.0.0.0", 4080), endpoint);
        Assert.Equal("http://127.0.0.1:4080", endpoint!.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"gateway":"bad"}""")]
    public void ParseManagerGateway_returns_null_when_missing_or_invalid(string? json)
    {
        Assert.Null(LLBotWebUi.ParseManagerGateway(json));
    }

    [Fact]
    public void ParseEndpoint_reads_legacy_webui_section()
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
    public void ReadGatewayEndpoint_prefers_manager_json_and_falls_back_to_legacy_default_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "llbot-gateway-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(root, "llbot.js");
        try
        {
            Assert.Equal(LLBotWebUi.DefaultEndpoint, LLBotWebUi.ReadGatewayEndpoint(script));

            var legacyDir = Path.GetDirectoryName(LLBotWebUi.LegacyDefaultSettingsPath(script))!;
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(LLBotWebUi.LegacyDefaultSettingsPath(script), """{"webui":{"enable":false,"host":"0.0.0.0","port":3099}}""");
            Assert.Equal(new WebUiEndpoint(true, "0.0.0.0", 3099), LLBotWebUi.ReadGatewayEndpoint(script));

            File.WriteAllText(LLBotWebUi.ManagerConfigPath(script), """{"gateway":{"host":"127.0.0.1","port":4100},"instances":{}}""");
            Assert.Equal(new WebUiEndpoint(true, "127.0.0.1", 4100), LLBotWebUi.ReadGatewayEndpoint(script));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoginCookie_reads_unified_token_file_and_falls_back_to_legacy_default_token()
    {
        var root = Path.Combine(Path.GetTempPath(), "llbot-cookie-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(root, "llbot.js");
        try
        {
            Assert.Null(LLBotWebUi.LoginCookie(script, 3080));

            // Legacy default token fallback before manager migration runs
            var legacyPath = LLBotWebUi.LegacyDefaultTokenPath(script);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, "  abc\r\n");
            var legacyCookie = LLBotWebUi.LoginCookie(script, 11745);
            Assert.NotNull(legacyCookie);
            Assert.Equal("webui_token_11745", legacyCookie.Value.Name);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", legacyCookie.Value.Value);

            // Unified data/webui_token.txt wins over legacy
            File.WriteAllText(LLBotWebUi.WebUiTokenPath(script), "xyz");
            var unifiedCookie = LLBotWebUi.LoginCookie(script, 3080);
            Assert.NotNull(unifiedCookie);
            Assert.Equal("webui_token_3080", unifiedCookie.Value.Name);
            Assert.NotEqual(legacyCookie.Value.Value, unifiedCookie.Value.Value);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MigrateAutoStartToManagerJson_writes_once_and_does_not_overwrite_existing()
    {
        var root = Path.Combine(Path.GetTempPath(), "llbot-migrate-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(root, "llbot.js");
        try
        {
            // Plain ["default"] matches manager's default behavior -> no need to pre-create manager.json
            Assert.False(LLBotWebUi.MigrateAutoStartToManagerJson(script, ["default"]));
            Assert.False(File.Exists(LLBotWebUi.ManagerConfigPath(script)));

            // Multi-instance dev list migrates into data/manager.json preserving legacy gateway port
            var legacySettings = LLBotWebUi.LegacyDefaultSettingsPath(script);
            Directory.CreateDirectory(Path.GetDirectoryName(legacySettings)!);
            File.WriteAllText(legacySettings, """{"webui":{"host":"0.0.0.0","port":3088}}""");

            Assert.True(LLBotWebUi.MigrateAutoStartToManagerJson(script, ["default", "bot2", "-bad"]));
            Assert.True(File.Exists(LLBotWebUi.ManagerConfigPath(script)));

            using var doc = JsonDocument.Parse(File.ReadAllText(LLBotWebUi.ManagerConfigPath(script)));
            Assert.Equal("0.0.0.0", doc.RootElement.GetProperty("gateway").GetProperty("host").GetString());
            Assert.Equal(3088, doc.RootElement.GetProperty("gateway").GetProperty("port").GetInt32());
            Assert.True(doc.RootElement.GetProperty("instances").GetProperty("default").GetProperty("autostart").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("instances").GetProperty("bot2").GetProperty("autostart").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("instances").TryGetProperty("-bad", out _));

            // Does not overwrite an existing manager.json
            Assert.False(LLBotWebUi.MigrateAutoStartToManagerJson(script, ["bot3"]));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
