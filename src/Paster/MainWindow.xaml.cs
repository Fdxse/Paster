using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Paster.Configuration;
using Paster.Engine;

namespace Paster;

/// <summary>
/// The main (and only) window: table list on the left, clipboard buttons in the middle,
/// setup editors on the right (only in Setup mode). Every edit re-parses everything and refreshes the buttons at once.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>How long to wait after the last keystroke before writing config.json.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>Waits for typing to pause, then saves. Restarted on every edit.</summary>
    private readonly DispatcherTimer saveTimer;

    /// <summary>
    /// Which buttons have been copied, per table. The key is the table name and the set holds the
    /// rendered texts that were copied. Using the rendered text means a button becomes clickable again
    /// by itself if a variable change makes it produce different text.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> usedTextsByTable = new(StringComparer.Ordinal);

    /// <summary>The most recent parse of the four editors.</summary>
    private ParsedSetup currentSetup = new();

    /// <summary>True while the editors are being filled from config.json, so that doesn't trigger a save.</summary>
    private bool isLoadingConfig;

    /// <summary>Width of the setup panel before it was hidden, so it comes back at the same size.</summary>
    private GridLength setupColumnWidthBeforeHide = new(420);

    /// <summary>Records what is done in Action mode (window list + daily log file).</summary>
    private readonly ActionLog actionLog = new();

    /// <summary>The setup file that "Save" writes to, or null if the setup has not been saved to a file yet.</summary>
    private string? currentSetupFilePath;

    /// <summary>True when the editors changed since the setup file was last opened or saved.</summary>
    private bool hasUnsavedSetupChanges;

    public MainWindow()
    {
        InitializeComponent();

        saveTimer = new DispatcherTimer { Interval = SaveDelay };
        saveTimer.Tick += SaveTimer_Tick;

        ActionLogListBox.ItemsSource = actionLog.Entries;

        LoadConfigIntoEditors();
        RefreshEverything();

        // Start with the first table selected so there are buttons to click right away.
        if (TableListBox.Items.Count > 0)
        {
            TableListBox.SelectedIndex = 0;
        }
    }

    // ------------------------------------------------------------------
    // Config loading / saving
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads config.json (creating it with dummy values if missing) and puts each section in its editor.
    /// A broken file is renamed out of the way so the user's content is not lost.
    /// </summary>
    private void LoadConfigIntoEditors()
    {
        PasterConfig loadedConfig;
        try
        {
            loadedConfig = ConfigStore.LoadOrCreate();
            ShowStatus($"Config: {ConfigStore.ConfigFilePath}");
        }
        catch (Exception loadException)
        {
            string backupFilePath = ConfigStore.ConfigFilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(ConfigStore.ConfigFilePath, backupFilePath);
            }
            catch (Exception)
            {
                // If even the rename fails we still continue with defaults; the message below explains.
            }

            MessageBox.Show(
                this,
                $"config.json could not be read:\n\n{loadException.Message}\n\n" +
                $"It was renamed to:\n{backupFilePath}\n\nPaster starts with the example setup instead.",
                "Paster",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            loadedConfig = PasterConfig.CreateDefault();
        }

        isLoadingConfig = true;
        try
        {
            TablesTextBox.Text = string.Join(Environment.NewLine, loadedConfig.Tables);
            VariablesTextBox.Text = string.Join(Environment.NewLine, loadedConfig.Variables);
            JoinersTextBox.Text = string.Join(Environment.NewLine, loadedConfig.Joiners);
            ButtonsTextBox.Text = string.Join(Environment.NewLine, loadedConfig.Buttons);

            // Start in the mode the app was last closed in.
            bool startInActionMode = string.Equals(loadedConfig.Mode, PasterConfig.ActionModeName, StringComparison.OrdinalIgnoreCase);
            ActionModeRadioButton.IsChecked = startInActionMode;
            SetupModeRadioButton.IsChecked = !startInActionMode;
            ApplyMode(startInActionMode);

            // Restore "Always on top" from the last session.
            AlwaysOnTopCheckBox.IsChecked = loadedConfig.AlwaysOnTop;
            Topmost = loadedConfig.AlwaysOnTop;

            // Remember which setup file "Save" writes to.
            currentSetupFilePath = loadedConfig.CurrentSetupFile;
        }
        finally
        {
            isLoadingConfig = false;
        }

        UpdateTitle();
    }

    /// <summary>
    /// Writes the current editor contents to config.json.
    /// </summary>
    private void SaveConfigNow()
    {
        saveTimer.Stop();

        var configToSave = new PasterConfig
        {
            Tables = SplitEditorLines(TablesTextBox.Text),
            Variables = SplitEditorLines(VariablesTextBox.Text),
            Joiners = SplitEditorLines(JoinersTextBox.Text),
            Buttons = SplitEditorLines(ButtonsTextBox.Text),
            Mode = ActionModeRadioButton.IsChecked == true ? PasterConfig.ActionModeName : PasterConfig.SetupModeName,
            AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true,
            CurrentSetupFile = currentSetupFilePath,
        };

        try
        {
            ConfigStore.Save(configToSave);
            ShowStatus($"Saved {DateTime.Now:HH:mm:ss} to {ConfigStore.ConfigFilePath}");
        }
        catch (Exception saveException)
        {
            ShowStatus($"Could not save config.json: {saveException.Message}", isError: true);
        }
    }

    /// <summary>
    /// Splits editor text into lines for config.json. Trailing empty lines are dropped.
    /// </summary>
    private static List<string> SplitEditorLines(string editorText)
    {
        List<string> lines = editorText.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private void SaveTimer_Tick(object? sender, EventArgs eventArgs)
    {
        SaveConfigNow();
    }

    private void Window_Closing(object? sender, CancelEventArgs eventArgs)
    {
        // Make sure the last edits are written even if the save timer has not fired yet.
        if (saveTimer.IsEnabled)
        {
            SaveConfigNow();
        }
    }

    // ------------------------------------------------------------------
    // Live update
    // ------------------------------------------------------------------

    /// <summary>
    /// Any edit in any of the four editors: refresh everything right away and schedule a save.
    /// </summary>
    private void SetupEditor_TextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (isLoadingConfig)
        {
            return;
        }

        RefreshEverything();

        if (!hasUnsavedSetupChanges)
        {
            hasUnsavedSetupChanges = true;
            UpdateTitle();
        }

        // Restart the timer so we save once the user pauses typing.
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>
    /// Re-parses the editors, marks soft errors, rebuilds the table list (keeping the selection)
    /// and rebuilds the buttons for the selected table.
    /// </summary>
    private void RefreshEverything()
    {
        // TextChanged can fire during InitializeComponent before all controls exist.
        if (TablesTextBox is null || VariablesTextBox is null || JoinersTextBox is null || ButtonsTextBox is null)
        {
            return;
        }

        currentSetup = SetupParser.Parse(TablesTextBox.Text, VariablesTextBox.Text, JoinersTextBox.Text, ButtonsTextBox.Text);

        ShowSoftErrors(TablesTextBox, TablesErrorText, currentSetup.TableErrors);
        ShowSoftErrors(VariablesTextBox, VariablesErrorText, currentSetup.VariableErrors);
        ShowSoftErrors(JoinersTextBox, JoinersErrorText, currentSetup.JoinerErrors);
        ShowSoftErrors(ButtonsTextBox, ButtonsErrorText, currentSetup.ButtonErrors);

        RebuildTableList();
        RebuildButtons();
    }

    /// <summary>
    /// Colors an editor light red and lists its errors underneath, or clears both when there are none.
    /// </summary>
    private void ShowSoftErrors(TextBox editorTextBox, TextBlock errorTextBlock, List<string> errors)
    {
        if (errors.Count == 0)
        {
            editorTextBox.ClearValue(BackgroundProperty);
            editorTextBox.ToolTip = null;
            errorTextBlock.Text = string.Empty;
            errorTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        string allErrors = string.Join(Environment.NewLine, errors);
        editorTextBox.Background = (Brush)FindResource("SoftErrorBrush");
        editorTextBox.ToolTip = allErrors;
        errorTextBlock.Text = allErrors;
        errorTextBlock.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Rebuilds the table list from the parsed setup. The selection is kept by position,
    /// so typing in a table name does not make the selection jump.
    /// </summary>
    private void RebuildTableList()
    {
        int previouslySelectedIndex = TableListBox.SelectedIndex;

        var tableListItems = new List<TableListItem>();
        foreach (TableEntry tableEntry in currentSetup.Tables)
        {
            tableListItems.Add(new TableListItem
            {
                TableName = tableEntry.Name.Length > 0 ? tableEntry.Name : "(no name)",
                HasError = tableEntry.Errors.Count > 0,
                ErrorText = tableEntry.Errors.Count > 0 ? string.Join(Environment.NewLine, tableEntry.Errors) : null,
                ProgressText = BuildProgressText(tableEntry),
            });
        }

        TableListBox.ItemsSource = tableListItems;

        if (tableListItems.Count > 0)
        {
            TableListBox.SelectedIndex = Math.Clamp(previouslySelectedIndex, 0, tableListItems.Count - 1);
        }
    }

    /// <summary>
    /// Builds the buttons for the selected table.
    /// </summary>
    private void RebuildButtons()
    {
        TableEntry? selectedTable = GetSelectedTable();
        if (selectedTable is null)
        {
            SelectedTableHeader.Text = "Buttons";
            ButtonItemsControl.ItemsSource = null;
            return;
        }

        SelectedTableHeader.Text = $"Buttons for {selectedTable.Name}";

        HashSet<string> usedTexts = GetUsedTexts(selectedTable.Name);
        var templateRenderer = new TemplateRenderer(currentSetup, selectedTable);
        var buttonItems = new List<ClipboardButtonItem>();

        foreach (ButtonTemplate buttonTemplate in currentSetup.ButtonTemplates)
        {
            RenderResult renderResult = templateRenderer.Render(buttonTemplate.Template);

            string toolTipText = buttonTemplate.Template;
            if (renderResult.HasErrors)
            {
                toolTipText += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, renderResult.Errors);
            }

            buttonItems.Add(new ClipboardButtonItem
            {
                RenderedText = renderResult.Text,
                Template = buttonTemplate.Template,
                HasError = renderResult.HasErrors,
                ToolTipText = toolTipText,
                IsUsed = usedTexts.Contains(renderResult.Text),
            });
        }

        ButtonItemsControl.ItemsSource = buttonItems;
    }

    /// <summary>
    /// The parsed table that matches the list selection, or null when nothing is selected.
    /// </summary>
    private TableEntry? GetSelectedTable()
    {
        int selectedIndex = TableListBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= currentSetup.Tables.Count)
        {
            return null;
        }

        return currentSetup.Tables[selectedIndex];
    }

    private void TableListBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        RebuildButtons();
    }

    // ------------------------------------------------------------------
    // Clicking buttons
    // ------------------------------------------------------------------

    /// <summary>
    /// Click: copy the text and grey the button out.
    /// Ctrl+click on a greyed-out button: make it clickable again (without copying).
    /// Click on a greyed-out button without Ctrl: nothing is copied, a hint is shown.
    /// </summary>
    private void ClipboardButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ClipboardButtonItem buttonItem })
        {
            return;
        }

        TableEntry? selectedTable = GetSelectedTable();
        if (selectedTable is null)
        {
            return;
        }

        HashSet<string> usedTexts = GetUsedTexts(selectedTable.Name);
        bool isControlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (buttonItem.IsUsed)
        {
            if (isControlHeld)
            {
                buttonItem.IsUsed = false;
                usedTexts.Remove(buttonItem.RenderedText);
                UpdateSelectedTableProgress(selectedTable);
                ShowStatus("Button enabled again.");
                RecordAction("Re-enabled", selectedTable.Name, buttonItem.RenderedText);
            }
            else
            {
                ShowStatus("Already copied. Ctrl+click the button to enable it again.");
            }

            return;
        }

        if (!TryCopyToClipboard(buttonItem.RenderedText, out string? copyError))
        {
            ShowStatus($"Could not copy to the clipboard: {copyError}", isError: true);
            return;
        }

        buttonItem.IsUsed = true;
        usedTexts.Add(buttonItem.RenderedText);
        UpdateSelectedTableProgress(selectedTable);
        ShowStatus($"Copied: {FirstLine(buttonItem.RenderedText)}");
        RecordAction("Copied", selectedTable.Name, buttonItem.RenderedText);
    }

    /// <summary>
    /// Makes every button for the selected table clickable again.
    /// </summary>
    private void ResetUsedButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        TableEntry? selectedTable = GetSelectedTable();
        if (selectedTable is null)
        {
            return;
        }

        usedTextsByTable.Remove(selectedTable.Name);
        RebuildButtons();
        UpdateSelectedTableProgress(selectedTable);
        ShowStatus($"All buttons for {selectedTable.Name} are enabled again.");
        RecordAction("Reset", selectedTable.Name, string.Empty);
    }

    /// <summary>
    /// Puts text on the clipboard. The clipboard can be briefly locked by another program,
    /// so a few retries are made before giving up.
    /// </summary>
    private static bool TryCopyToClipboard(string text, out string? errorMessage)
    {
        const int maximumAttempts = 5;
        for (int attemptNumber = 1; attemptNumber <= maximumAttempts; attemptNumber++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                errorMessage = null;
                return true;
            }
            catch (ExternalException) when (attemptNumber < maximumAttempts)
            {
                // Another app has the clipboard open; wait a moment and try again.
                Thread.Sleep(50);
            }
            catch (ExternalException clipboardException)
            {
                errorMessage = clipboardException.Message;
                return false;
            }
        }

        errorMessage = "The clipboard is busy.";
        return false;
    }

    // ------------------------------------------------------------------
    // Used-state bookkeeping
    // ------------------------------------------------------------------

    /// <summary>
    /// The set of copied texts for a table, created on first use.
    /// </summary>
    private HashSet<string> GetUsedTexts(string tableName)
    {
        if (!usedTextsByTable.TryGetValue(tableName, out HashSet<string>? usedTexts))
        {
            usedTexts = new HashSet<string>(StringComparer.Ordinal);
            usedTextsByTable[tableName] = usedTexts;
        }

        return usedTexts;
    }

    /// <summary>
    /// "2/5" for a table: copied buttons out of all buttons. Empty when nothing is copied yet.
    /// </summary>
    private string BuildProgressText(TableEntry tableEntry)
    {
        if (!usedTextsByTable.TryGetValue(tableEntry.Name, out HashSet<string>? usedTexts) || usedTexts.Count == 0)
        {
            return string.Empty;
        }

        var templateRenderer = new TemplateRenderer(currentSetup, tableEntry);
        int totalButtonCount = currentSetup.ButtonTemplates.Count;
        int usedButtonCount = currentSetup.ButtonTemplates
            .Count(buttonTemplate => usedTexts.Contains(templateRenderer.Render(buttonTemplate.Template).Text));

        return $"{usedButtonCount}/{totalButtonCount}";
    }

    /// <summary>
    /// Updates the "2/5" counter of the selected row without rebuilding the whole list.
    /// </summary>
    private void UpdateSelectedTableProgress(TableEntry selectedTable)
    {
        if (TableListBox.SelectedItem is TableListItem selectedListItem)
        {
            selectedListItem.ProgressText = BuildProgressText(selectedTable);
        }
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// The user picked Setup or Action in the top bar.
    /// </summary>
    private void ModeRadioButton_Checked(object sender, RoutedEventArgs eventArgs)
    {
        // Checked can fire during InitializeComponent before the columns exist.
        if (SetupColumn is null || SetupPanel is null || SetupGridSplitter is null || ActionModeRadioButton is null)
        {
            return;
        }

        ApplyMode(isActionMode: ActionModeRadioButton.IsChecked == true);

        // The mode is remembered in config.json, so save it like any other change.
        if (!isLoadingConfig)
        {
            saveTimer.Stop();
            saveTimer.Start();
        }
    }

    /// <summary>
    /// The "Always on top" switch was turned on or off: apply it to the window and save it.
    /// </summary>
    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
    {
        Topmost = AlwaysOnTopCheckBox.IsChecked == true;

        if (!isLoadingConfig)
        {
            saveTimer.Stop();
            saveTimer.Start();
        }
    }

    /// <summary>
    /// Setup mode: the editors are shown on the right (the buttons still work, as a live preview).
    /// Action mode: the editors are hidden so only the table list and the buttons are left.
    /// </summary>
    private void ApplyMode(bool isActionMode)
    {
        if (isActionMode)
        {
            // Remember the width the user dragged the setup panel to, so it comes back the same.
            if (SetupColumn.Width.Value > 0)
            {
                setupColumnWidthBeforeHide = SetupColumn.Width;
            }

            SetupColumn.Width = new GridLength(0);
            SetupColumn.MinWidth = 0;
            SetupSplitterColumn.Width = new GridLength(0);
            SetupPanel.Visibility = Visibility.Collapsed;
            SetupGridSplitter.Visibility = Visibility.Collapsed;
            ActionLogPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SetupColumn.Width = setupColumnWidthBeforeHide;
            SetupColumn.MinWidth = 200;
            SetupSplitterColumn.Width = new GridLength(6);
            SetupPanel.Visibility = Visibility.Visible;
            SetupGridSplitter.Visibility = Visibility.Visible;
            ActionLogPanel.Visibility = Visibility.Collapsed;
        }

        UpdateTitle();
    }

    /// <summary>
    /// "Paster - Action - dbt.json *": mode, setup file name, and * when there are unsaved changes.
    /// </summary>
    private void UpdateTitle()
    {
        string modeName = ActionModeRadioButton?.IsChecked == true ? "Action" : "Setup";
        string setupFileName = currentSetupFilePath is null ? "unsaved setup" : Path.GetFileName(currentSetupFilePath);
        string unsavedMarker = hasUnsavedSetupChanges ? " *" : string.Empty;
        Title = $"Paster - {modeName} - {setupFileName}{unsavedMarker}";
    }

    // ------------------------------------------------------------------
    // Action log
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds an entry to the action log. Only done in Action mode, since Setup mode is for trying things out.
    /// </summary>
    private void RecordAction(string action, string tableName, string text)
    {
        if (ActionModeRadioButton.IsChecked != true)
        {
            return;
        }

        string? logFileError = actionLog.Record(action, tableName, text);
        if (logFileError is not null)
        {
            ShowStatus($"Could not write the log file: {logFileError}", isError: true);
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        actionLog.ClearDisplayedEntries();
        ShowStatus("Log list cleared. The log files are kept in the logs folder.");
    }

    /// <summary>
    /// Opens the logs folder in Explorer so older days can be looked at.
    /// </summary>
    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Directory.CreateDirectory(ActionLog.LogFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ActionLog.LogFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception openException)
        {
            ShowStatus($"Could not open the logs folder: {openException.Message}", isError: true);
        }
    }

    // ------------------------------------------------------------------
    // Setup files (Open / Save / Save as)
    // ------------------------------------------------------------------

    /// <summary>
    /// Ctrl+O = Open, Ctrl+S = Save, Ctrl+Shift+S = Save as.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs keyEventArgs)
    {
        bool isControlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (!isControlHeld)
        {
            return;
        }

        if (keyEventArgs.Key == Key.O)
        {
            OpenSetupFile();
            keyEventArgs.Handled = true;
        }
        else if (keyEventArgs.Key == Key.S)
        {
            if (isShiftHeld)
            {
                SaveSetupFileAs();
            }
            else
            {
                SaveSetupFile();
            }

            keyEventArgs.Handled = true;
        }
    }

    private void OpenSetupButton_Click(object sender, RoutedEventArgs eventArgs) => OpenSetupFile();

    private void SaveSetupButton_Click(object sender, RoutedEventArgs eventArgs) => SaveSetupFile();

    private void SaveSetupAsButton_Click(object sender, RoutedEventArgs eventArgs) => SaveSetupFileAs();

    /// <summary>
    /// Asks for a setup file and loads it into the editors. Offers to save unsaved changes first.
    /// </summary>
    private void OpenSetupFile()
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        var openFileDialog = new OpenFileDialog
        {
            Title = "Open setup",
            Filter = "Paster setup (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = GetDialogFolder(),
        };

        if (openFileDialog.ShowDialog(this) != true)
        {
            return;
        }

        SetupFile loadedSetup;
        try
        {
            loadedSetup = SetupFile.Load(openFileDialog.FileName);
        }
        catch (Exception loadException)
        {
            MessageBox.Show(this, $"Could not open the setup:\n\n{loadException.Message}", "Paster",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        isLoadingConfig = true;
        try
        {
            TablesTextBox.Text = string.Join(Environment.NewLine, loadedSetup.Tables);
            VariablesTextBox.Text = string.Join(Environment.NewLine, loadedSetup.Variables);
            JoinersTextBox.Text = string.Join(Environment.NewLine, loadedSetup.Joiners);
            ButtonsTextBox.Text = string.Join(Environment.NewLine, loadedSetup.Buttons);
        }
        finally
        {
            isLoadingConfig = false;
        }

        // A new setup starts with every button clickable.
        usedTextsByTable.Clear();
        currentSetupFilePath = openFileDialog.FileName;
        hasUnsavedSetupChanges = false;

        RefreshEverything();
        TableListBox.SelectedIndex = TableListBox.Items.Count > 0 ? 0 : -1;
        UpdateTitle();

        // Remember the opened setup as the working copy in config.json too.
        SaveConfigNow();
        ShowStatus($"Opened {currentSetupFilePath}");
    }

    /// <summary>
    /// Saves to the current setup file, or asks for a file name if there is none yet.
    /// Returns true if the setup was saved.
    /// </summary>
    private bool SaveSetupFile()
    {
        if (currentSetupFilePath is null)
        {
            return SaveSetupFileAs();
        }

        return WriteSetupFile(currentSetupFilePath);
    }

    /// <summary>
    /// Asks for a file name and saves the setup there. Returns true if the setup was saved.
    /// </summary>
    private bool SaveSetupFileAs()
    {
        var saveFileDialog = new SaveFileDialog
        {
            Title = "Save setup as",
            Filter = "Paster setup (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            InitialDirectory = GetDialogFolder(),
            FileName = currentSetupFilePath is null ? "setup.json" : Path.GetFileName(currentSetupFilePath),
        };

        if (saveFileDialog.ShowDialog(this) != true)
        {
            return false;
        }

        return WriteSetupFile(saveFileDialog.FileName);
    }

    /// <summary>
    /// Writes the four editors to a setup file and makes it the current one.
    /// </summary>
    private bool WriteSetupFile(string filePath)
    {
        var setupToSave = new SetupFile
        {
            Tables = SplitEditorLines(TablesTextBox.Text),
            Variables = SplitEditorLines(VariablesTextBox.Text),
            Joiners = SplitEditorLines(JoinersTextBox.Text),
            Buttons = SplitEditorLines(ButtonsTextBox.Text),
        };

        try
        {
            setupToSave.Save(filePath);
        }
        catch (Exception saveException)
        {
            ShowStatus($"Could not save the setup: {saveException.Message}", isError: true);
            return false;
        }

        currentSetupFilePath = filePath;
        hasUnsavedSetupChanges = false;
        UpdateTitle();
        SaveConfigNow();
        ShowStatus($"Saved setup to {filePath}");
        return true;
    }

    /// <summary>
    /// If there are unsaved changes, asks whether to save them. Returns false if the user cancels.
    /// (The editors are always kept in config.json, so "No" only skips writing the setup file.)
    /// </summary>
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!hasUnsavedSetupChanges)
        {
            return true;
        }

        string setupName = currentSetupFilePath is null ? "the current setup" : Path.GetFileName(currentSetupFilePath);
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Save changes to {setupName} first?",
            "Paster",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => SaveSetupFile(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>
    /// Folder the file dialogs start in: the current setup's folder, otherwise the "setups" folder next to the EXE.
    /// </summary>
    private string GetDialogFolder()
    {
        string? currentFolder = currentSetupFilePath is null ? null : Path.GetDirectoryName(currentSetupFilePath);
        if (currentFolder is not null && Directory.Exists(currentFolder))
        {
            return currentFolder;
        }

        try
        {
            Directory.CreateDirectory(SetupFile.DefaultFolder);
        }
        catch (Exception)
        {
            // Not fatal: the dialog just starts somewhere else.
        }

        return SetupFile.DefaultFolder;
    }

    /// <summary>
    /// Shows a message in the top bar, in red for errors.
    /// </summary>
    private void ShowStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? (Brush)FindResource("SoftErrorTextBrush")
            : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
    }

    /// <summary>
    /// First line of a text, used to keep the status bar to one line.
    /// </summary>
    private static string FirstLine(string text)
    {
        int newLineIndex = text.IndexOfAny(new[] { '\r', '\n' });
        return newLineIndex < 0 ? text : text[..newLineIndex] + " ...";
    }
}
