using System.Collections.ObjectModel;
using System.IO;

namespace Paster;

/// <summary>
/// One thing that happened in Action mode, e.g. a button was copied.
/// </summary>
public sealed class ActionLogEntry
{
    public DateTime Time { get; init; }

    /// <summary>"Copied", "Re-enabled" or "Reset".</summary>
    public string Action { get; init; } = string.Empty;

    public string TableName { get; init; } = string.Empty;

    /// <summary>The text that was copied (or re-enabled). Empty for a reset.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>One line for the log list in the window.</summary>
    public string DisplayText => $"{Time:HH:mm:ss}   {Action,-10}  {TableName}   {Text}";
}

/// <summary>
/// Records what you do in Action mode: shown in the log panel and appended to a daily log file
/// in a "logs" folder next to Paster.exe (logs\paster-2026-10-23.log).
/// </summary>
public sealed class ActionLog
{
    /// <summary>The entries shown in the window, newest first.</summary>
    public ObservableCollection<ActionLogEntry> Entries { get; } = new();

    /// <summary>Folder holding the daily log files.</summary>
    public static string LogFolder => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>Today's log file.</summary>
    public static string TodaysLogFilePath => Path.Combine(LogFolder, $"paster-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>
    /// Adds an entry to the window and the log file. Returns an error message if the file
    /// could not be written (the entry is still shown in the window), otherwise null.
    /// </summary>
    public string? Record(string action, string tableName, string text)
    {
        var logEntry = new ActionLogEntry
        {
            Time = DateTime.Now,
            Action = action,
            TableName = tableName,
            // Keep each entry on one line, both in the window and in the file.
            Text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '),
        };

        Entries.Insert(0, logEntry);

        try
        {
            Directory.CreateDirectory(LogFolder);
            string fileLine = $"{logEntry.Time:yyyy-MM-dd HH:mm:ss}\t{logEntry.Action}\t{logEntry.TableName}\t{logEntry.Text}{Environment.NewLine}";
            File.AppendAllText(TodaysLogFilePath, fileLine);
            return null;
        }
        catch (Exception writeException)
        {
            return writeException.Message;
        }
    }

    /// <summary>
    /// Clears the list in the window. The log files on disk are kept.
    /// </summary>
    public void ClearDisplayedEntries()
    {
        Entries.Clear();
    }
}
