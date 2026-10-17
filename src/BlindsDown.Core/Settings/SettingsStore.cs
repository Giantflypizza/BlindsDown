using System.Text.Json;
using BlindsDown.Core.IO;
using BlindsDown.Core.Json;

namespace BlindsDown.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string path)
{
    /// <summary>Loads settings; a missing or damaged file gives the defaults.</summary>
    public AppSettings Load()
    {
        foreach (string text in AtomicFile.ReadCandidates(path))
        {
            try
            {
                AppSettings? settings = JsonSerializer.Deserialize(text, CoreJsonContext.Default.AppSettings);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings));

    /// <summary>A deep copy, so a settings dialog can edit without touching the live settings until "Save".</summary>
    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings), CoreJsonContext.Default.AppSettings)!;
}
