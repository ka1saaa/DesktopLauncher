using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopLauncher.Models;

namespace DesktopLauncher.Services;

/// <summary>配置读写：JSON 持久化到 %APPDATA%\DesktopLauncher\config.json。</summary>
public class ConfigStore
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopLauncher");

    public static string IconsDir { get; } = Path.Combine(AppDataDir, "icons");

    private static string ConfigPath { get; } = Path.Combine(AppDataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppConfig Config { get; private set; } = new();

    public ConfigStore()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(IconsDir);
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null)
                {
                    Config = cfg;
                    return;
                }
            }
        }
        catch
        {
            // 配置损坏时不抛异常：备份旧文件后用默认配置。
            try
            {
                if (File.Exists(ConfigPath))
                    File.Move(ConfigPath, ConfigPath + ".bak", overwrite: true);
            }
            catch { /* 忽略 */ }
        }
        Config = AppConfig.CreateDefault();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 保存失败不致崩溃（磁盘满/被占用），下次保存会再尝试。
        }
    }
}
