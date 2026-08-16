using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoClicker.Input;

namespace AutoClicker.Core;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public double ClicksPerSecond { get; set; } = 10;

    /// <summary>Half-width of the random rate window, in CPS. 0 = perfectly regular.</summary>
    public double VarianceCps { get; set; } = 2;

    /// <summary>When true the variance is derived from the rate instead of using <see cref="VarianceCps"/>.</summary>
    public bool HumanVariance { get; set; } = true;

    public bool HoldEnabled { get; set; } = true;

    public InputKey HoldTrigger { get; set; } = InputKey.FromMouse(MouseCode.X1);

    public InputKey HoldAction { get; set; } = InputKey.LeftClick;

    public bool ToggleEnabled { get; set; } = true;

    public InputKey ToggleTrigger { get; set; } = InputKey.FromKeyboard(0x76); // F7

    public InputKey ToggleAction { get; set; } = InputKey.LeftClick;

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutoClicker",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.ClicksPerSecond = Math.Clamp(loaded.ClicksPerSecond, 1, 1000);
                    loaded.VarianceCps = Math.Clamp(loaded.VarianceCps, 0, 1000);
                    loaded.HoldTrigger ??= InputKey.None;
                    loaded.HoldAction ??= InputKey.None;
                    loaded.ToggleTrigger ??= InputKey.None;
                    loaded.ToggleAction ??= InputKey.None;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings - fall back to defaults rather than failing to start.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting settings is best-effort; never interrupt the user over it.
        }
    }
}
