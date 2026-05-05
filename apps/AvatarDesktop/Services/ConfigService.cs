using System.IO;
using System.Text.Json;
using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public string ConfigPath { get; }

    public ConfigService(string configPath)
    {
        ConfigPath = configPath;
    }

    public AppConfig Load(out string? warning)
    {
        warning = null;

        if (!File.Exists(ConfigPath))
        {
            var config = new AppConfig();
            Save(config);
            warning = $"Config not found. Created default: {ConfigPath}";
            return config;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is null)
            {
                warning = $"Config parse returned null. Using defaults: {ConfigPath}";
                return new AppConfig();
            }

            loaded.BaseUrl = string.IsNullOrWhiteSpace(loaded.BaseUrl) ? "http://127.0.0.1:1234/v1" : loaded.BaseUrl;
            loaded.Model = string.IsNullOrWhiteSpace(loaded.Model) ? "local-model" : loaded.Model;
            if (loaded.MaxTokens <= 0)
            {
                loaded.MaxTokens = 256;
            }

            return loaded;
        }
        catch (Exception ex)
        {
            warning = $"Config load error: {ex.Message}. Using defaults.";
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
