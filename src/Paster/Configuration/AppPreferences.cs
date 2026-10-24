using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Paster.Configuration;

/// <summary>
/// Per-computer preferences: the mode, "Always on top", "Keep buttons active", the click counter and the recent files list.
/// They are kept in the current user's registry (HKEY_CURRENT_USER\Software\Paster) instead of a file,
/// so a setup JSON file only ever holds the setup itself and can be copied anywhere.
/// </summary>
public sealed class AppPreferences
{
    /// <summary>Value of <see cref="Mode"/> when the editors are shown.</summary>
    public const string SetupModeName = "setup";

    /// <summary>Value of <see cref="Mode"/> when only the tables and buttons are shown.</summary>
    public const string ActionModeName = "action";

    /// <summary>How many files the "Open recent" menu remembers.</summary>
    public const int MaximumRecentFiles = 8;

    private const string RegistryKeyPath = @"Software\Paster";

    /// <summary>The mode the app was last in ("setup" or "action"). The app starts in this mode.</summary>
    public string Mode { get; set; } = SetupModeName;

    /// <summary>True if the window should stay above all other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// True if clicked buttons stay clickable (only Ctrl+click greys them out). False is the classic behaviour:
    /// a click copies and greys the button out.
    /// </summary>
    public bool KeepButtonsActive { get; set; }

    /// <summary>The click counter in the top bar, kept between runs.</summary>
    public int ClickCount { get; set; }

    /// <summary>Recently opened or saved setup files, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>
    /// Reads the preferences. Missing values fall back to defaults. The first time (nothing in the registry yet)
    /// mode and "Always on top" are taken over from an old-style config.json, if there is one.
    /// </summary>
    public static AppPreferences Load()
    {
        var preferences = new AppPreferences();

        try
        {
            using RegistryKey? preferencesKey = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            if (preferencesKey is null)
            {
                preferences.TakeOverFromOldConfigFile();
                return preferences;
            }

            preferences.Mode = preferencesKey.GetValue(nameof(Mode)) as string ?? SetupModeName;
            preferences.AlwaysOnTop = preferencesKey.GetValue(nameof(AlwaysOnTop)) is int alwaysOnTopValue && alwaysOnTopValue != 0;
            preferences.KeepButtonsActive = preferencesKey.GetValue(nameof(KeepButtonsActive)) is int keepActiveValue && keepActiveValue != 0;
            preferences.ClickCount = preferencesKey.GetValue(nameof(ClickCount)) is int clickCountValue ? clickCountValue : 0;
            if (preferencesKey.GetValue(nameof(RecentFiles)) is string[] recentFilePaths)
            {
                preferences.RecentFiles = recentFilePaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            }
        }
        catch (Exception)
        {
            // Preferences are a convenience: if the registry can't be read, just use the defaults.
        }

        return preferences;
    }

    /// <summary>
    /// Writes the preferences. Errors are ignored for the same reason as in <see cref="Load"/>.
    /// </summary>
    public void Save()
    {
        try
        {
            using RegistryKey preferencesKey = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            preferencesKey.SetValue(nameof(Mode), Mode, RegistryValueKind.String);
            preferencesKey.SetValue(nameof(AlwaysOnTop), AlwaysOnTop ? 1 : 0, RegistryValueKind.DWord);
            preferencesKey.SetValue(nameof(KeepButtonsActive), KeepButtonsActive ? 1 : 0, RegistryValueKind.DWord);
            preferencesKey.SetValue(nameof(ClickCount), ClickCount, RegistryValueKind.DWord);
            preferencesKey.SetValue(nameof(RecentFiles), RecentFiles.ToArray(), RegistryValueKind.MultiString);
        }
        catch (Exception)
        {
            // Not fatal: the app works the same, it just won't remember these next time.
        }
    }

    /// <summary>
    /// Moves a file to the top of the recent files list (adding it if needed) and trims the list.
    /// </summary>
    public void AddRecentFile(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        RecentFiles.RemoveAll(existingPath => string.Equals(existingPath, fullPath, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, fullPath);
        if (RecentFiles.Count > MaximumRecentFiles)
        {
            RecentFiles.RemoveRange(MaximumRecentFiles, RecentFiles.Count - MaximumRecentFiles);
        }
    }

    /// <summary>
    /// Earlier versions kept mode, "Always on top" and the last setup file inside config.json.
    /// Read them from there once. config.json itself goes first in the recent list, because it held
    /// the auto-saved working copy and so has the newest edits.
    /// </summary>
    private void TakeOverFromOldConfigFile()
    {
        try
        {
            if (!File.Exists(SetupFile.DefaultFilePath))
            {
                return;
            }

            using JsonDocument oldConfigDocument = JsonDocument.Parse(
                File.ReadAllText(SetupFile.DefaultFilePath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            JsonElement rootElement = oldConfigDocument.RootElement;
            if (rootElement.TryGetProperty("mode", out JsonElement modeElement) && modeElement.ValueKind == JsonValueKind.String)
            {
                Mode = modeElement.GetString() ?? SetupModeName;
            }

            if (rootElement.TryGetProperty("alwaysOnTop", out JsonElement alwaysOnTopElement)
                && alwaysOnTopElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                AlwaysOnTop = alwaysOnTopElement.GetBoolean();
            }

            RecentFiles.Add(Path.GetFullPath(SetupFile.DefaultFilePath));
            if (rootElement.TryGetProperty("currentSetupFile", out JsonElement setupFileElement)
                && setupFileElement.ValueKind == JsonValueKind.String
                && setupFileElement.GetString() is { Length: > 0 } oldSetupFilePath
                && File.Exists(oldSetupFilePath))
            {
                RecentFiles.Add(Path.GetFullPath(oldSetupFilePath));
            }
        }
        catch (Exception)
        {
            // A broken old file is reported when it is opened as a setup; nothing to do here.
        }
    }
}
