using LuckyLilliaDesktop.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LuckyLilliaDesktop.Services;

public class ConfigManager : IConfigManager
{
    private const string ConfigPath = "app_settings.json";
    private AppConfig? _cachedConfig;
    private JsonObject? _rawJson;
    private readonly ILogger<ConfigManager> _logger;
    private readonly object _lock = new();

    public event Action<AppConfig>? ConfigSaved;

    public ConfigManager(ILogger<ConfigManager> logger)
    {
        _logger = logger;
    }

    public async Task<AppConfig> LoadConfigAsync()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                _logger.LogInformation("配置文件不存在，使用默认配置");
                _cachedConfig = AppConfig.Default;
                _rawJson = new JsonObject();
                return _cachedConfig;
            }

            var json = await File.ReadAllTextAsync(ConfigPath);
            _cachedConfig = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig) ?? AppConfig.Default;
            _rawJson = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            _logger.LogInformation("配置加载成功");
            return _cachedConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载配置失败，使用默认配置");
            _cachedConfig = AppConfig.Default;
            _rawJson = new JsonObject();
            return _cachedConfig;
        }
    }

    public async Task<bool> SaveConfigAsync(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
            await File.WriteAllTextAsync(ConfigPath, json);

            _cachedConfig = config;
            _rawJson = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            _logger.LogInformation("配置保存成功");
            ConfigSaved?.Invoke(config);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败");
            return false;
        }
    }

    public T GetSetting<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_rawJson == null) return defaultValue;

            try
            {
                if (_rawJson.TryGetPropertyValue(key, out var node) && node != null)
                {
                    var value = ConvertFromJsonNode(node, defaultValue);
                    _logger.LogDebug("读取设置: {Key}", key);
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取设置失败: {Key}", key);
            }

            return defaultValue;
        }
    }

    public IReadOnlyList<string>? GetStringListSetting(string key)
    {
        lock (_lock)
        {
            if (_rawJson == null || !_rawJson.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
                return null;
            var result = new List<string>();
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s)) result.Add(s);
            }
            return result;
        }
    }

    public int? GetIntDictionarySetting(string key, string entryKey)
    {
        lock (_lock)
        {
            if (_rawJson == null
                || !_rawJson.TryGetPropertyValue(key, out var node)
                || node is not JsonObject obj
                || !obj.TryGetPropertyValue(entryKey, out var valueNode)
                || valueNode is not JsonValue value
                || !value.TryGetValue<int>(out var port))
            {
                return null;
            }
            return port;
        }
    }

    public async Task SetSettingAsync<T>(string key, T value)
    {
        try
        {
            string json;
            lock (_lock)
            {
                _rawJson ??= new JsonObject();
                _rawJson[key] = ConvertToJsonNode(value);
                json = _rawJson.ToJsonString(AppJsonContext.Default.Options);
            }
            await File.WriteAllTextAsync(ConfigPath, json);
            _logger.LogInformation("设置已保存: {Key} = {Value}", key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败: {Key}", key);
        }
    }

    public async Task RemoveSettingAsync(string key)
    {
        try
        {
            string? json = null;
            lock (_lock)
            {
                if (_rawJson != null && _rawJson.Remove(key))
                    json = _rawJson.ToJsonString(AppJsonContext.Default.Options);
            }
            if (json != null)
            {
                await File.WriteAllTextAsync(ConfigPath, json);
                _logger.LogInformation("已移除旧设置项: {Key}", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移除设置项失败: {Key}", key);
        }
    }

    private static T ConvertFromJsonNode<T>(JsonNode node, T defaultValue)
    {
        try
        {
            var targetType = typeof(T);
            if (targetType == typeof(string))
                return (T)(object)(node.GetValue<string>() ?? string.Empty);
            if (targetType == typeof(int))
                return (T)(object)node.GetValue<int>();
            if (targetType == typeof(int?))
                return (T)(object?)node.GetValue<int>()!;
            if (targetType == typeof(bool))
                return (T)(object)node.GetValue<bool>();
            if (targetType == typeof(bool?))
                return (T)(object?)node.GetValue<bool>()!;
        }
        catch
        {
            return defaultValue;
        }

        return defaultValue;
    }

    private static JsonNode? ConvertToJsonNode<T>(T value)
    {
        return value switch
        {
            null => null,
            string stringValue => JsonValue.Create(stringValue),
            int intValue => JsonValue.Create(intValue),
            bool boolValue => JsonValue.Create(boolValue),
            IDictionary<string, int> ints => new JsonObject(ints.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            IEnumerable<string> strings => new JsonArray(strings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            _ => throw new NotSupportedException($"不支持保存设置类型: {typeof(T).FullName}")
        };
    }
}
