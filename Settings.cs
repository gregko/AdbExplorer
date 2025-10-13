using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AdbExplorer.Models;

namespace AdbExplorer
{
    public class Settings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdbExplorer",
            "settings.json");

        public string? LastDevice { get; set; }
        public string? LastPath { get; set; }
        public int WindowWidth { get; set; } = 1200;
        public int WindowHeight { get; set; } = 700;
        public double WindowLeft { get; set; } = 100;
        public double WindowTop { get; set; } = 100;
        public List<FavoriteItem> Favorites { get; set; } = new List<FavoriteItem>();

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            }

            return new Settings();
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }
    }
}
