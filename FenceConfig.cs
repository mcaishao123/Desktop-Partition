using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace 桌面整理工具
{
    public class FenceConfig
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "新建分区";
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;
        public double Width { get; set; } = 350;
        public double Height { get; set; } = 250;
        public List<string> FilePaths { get; set; } = new List<string>();
    }

    public static class FenceConfigManager
    {
        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "桌面整理工具"
        );

        private static readonly string ConfigPath = Path.Combine(ConfigFolder, "fences_config.json");

        public static List<FenceConfig> LoadConfigs()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder))
                {
                    Directory.CreateDirectory(ConfigFolder);
                }

                if (!File.Exists(ConfigPath))
                {
                    // 返回默认初始配置
                    var defaultConfigs = GetDefaultConfigs();
                    SaveConfigs(defaultConfigs);
                    return defaultConfigs;
                }

                string json = File.ReadAllText(ConfigPath);
                var configs = JsonSerializer.Deserialize<List<FenceConfig>>(json);
                if (configs == null || configs.Count == 0)
                {
                    configs = GetDefaultConfigs();
                    SaveConfigs(configs);
                }
                return configs;
            }
            catch
            {
                var defaultConfigs = GetDefaultConfigs();
                SaveConfigs(defaultConfigs);
                return defaultConfigs;
            }
        }

        public static void SaveConfigs(List<FenceConfig> configs)
        {
            try
            {
                if (!Directory.Exists(ConfigFolder))
                {
                    Directory.CreateDirectory(ConfigFolder);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(configs, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }

        private static List<FenceConfig> GetDefaultConfigs()
        {
            // 提供两个默认分区
            return new List<FenceConfig>
            {
                new FenceConfig
                {
                    Id = Guid.NewGuid(),
                    Title = "快捷方式",
                    X = 100,
                    Y = 100,
                    Width = 320,
                    Height = 240,
                    FilePaths = new List<string>()
                },
                new FenceConfig
                {
                    Id = Guid.NewGuid(),
                    Title = "日常文档",
                    X = 450,
                    Y = 100,
                    Width = 320,
                    Height = 240,
                    FilePaths = new List<string>()
                }
            };
        }
    }
}
