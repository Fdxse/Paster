using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    /// <summary>
    /// Which buttons have been copied, per table. The key is the table name and the set holds the
    /// rendered texts that were copied. Using the rendered text means a button becomes clickable again
    /// by itself if a variable change makes it produce different text.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> usedTextsByTable = new(StringComparer.Ordinal);

    /// <summary>The most recent parse of the four editors.</summary>
    private ParsedSetup currentSetup = new();

    /// <summary>True while the editors are being filled from a file, so that doesn't count as an edit.</summary>
    private bool isLoadingSetup;

    /// <summary>Width of the setup panel before it was hidden, so it comes back at the same size.</summary>
    private GridLength setupColumnWidthBeforeHide = new(420);

    /// <summary>Records what is done in Action mode (window list + daily log file).</summary>
    private readonly ActionLog actionLog = new();

    /// <summary>The setup file that "Save" writes to, or null for a new setup that has never been saved.</summary>
    private string? currentSetupFilePath;

    /// <summary>True when the editors changed since the setup file was last opened or saved.</summary>
    private bool hasUnsavedSetupChanges;

    /// <summary>Action mode button size of the current setup: "fixed" or "fill" (see <see cref="SetupFile.ActionButtonSize"/>).</summary>
    private string actionButtonSize = SetupFile.FixedButtonSize;

    /// <summary>Action mode button height in pixels when the size is "fixed".</summary>
    private double actionButtonHeight = 60;

    /// <summary>Action mode button text size.</summary>
    private double actionFontSize = 18;

    /// <summary>Button text size in Setup mode.</summary>
    private const double SetupModeFontSize = 13;

    /// <summary>Mode, "Always on top" and recent files, kept per computer (see <see cref="AppPreferences"/>).</summary>
    private readonly AppPreferences preferences = AppPreferences.Load();

    public MainWindow()
    {
        InitializeComponent();

        ActionLogListBox.ItemsSource = actionLog.Entries;

        ApplyPreferences();
        OpenStartupSetup();
        RebuildRecentFilesMenu();
    }

    // ------------------------------------------------------------------
    // Startup and closing
    // ------------------------------------------------------------------

    /// <summary>
    /// Restores the mode and "Always on top" from the last time Paster ran on this computer.
    /// </summary>
    private void ApplyPreferences()
    {
        isLoadingSetup = true;
        try
        {
            bool startInActionMode = string.Equals(preferences.Mode, AppPreferences.ActionModeName, StringComparison.OrdinalIgnoreCase);
            ActionModeRadioButton.IsChecked = startInActionMode;
            SetupModeRadioButton.IsChecked = !startInActionMode;
            ApplyMode(startInActionMode);

            AlwaysOnTopCheckBox.IsChecked = preferences.AlwaysOnTop;
            Topmost = preferences.AlwaysOnTop;
        }
        finally
        {
            isLoadingSetup = false;
        }
    }

    /// <summary>
    /// Picks the setup to start with: a file given on the command line (e.g. a .json dropped on Paster.exe),
    /// otherwise the most recently used file, otherwise config.json next to the EXE
    /// (created with example values if it does not exist yet).
    /// </summary>
    private void OpenStartupSetup()
    {
        string[] commandLineArguments = Environment.GetCommandLineArgs();
        string? commandLineFile = commandLineArguments.Length > 1 ? commandLineArguments[1] : null;
        if (commandLineFile is not null && File.Exists(commandLineFile) && TryOpenSetupFile(commandLineFile))
        {
            return;
        }

        string? mostRecentFile = preferences.RecentFiles.FirstOrDefault(File.Exists);
        if (mostRecentFile is not null && TryOpenSetupFile(mostRecentFile))
        {
            return;
        }

        if (!File.Exists(SetupFile.DefaultFilePath))
        {
            try
            {
                SetupFile.CreateExample().Save(SetupFile.DefaultFilePath);
            }
            catch (Exception createException)
            {
                // E.g. the EXE is in a read-only folder: show the example anyway, unsaved.
                ShowSetupInEditors(SetupFile.CreateExample(), filePath: null);
                ShowStatus($"Could not create {SetupFile.DefaultFilePath}: {createException.Message}", isError: true);
                return;
            }
        }

        if (!TryOpenSetupFile(SetupFile.DefaultFilePath))
        {
            ShowSetupInEditors(SetupFile.CreateExample(), filePath: null);
        }
    }

    /// <summary>
    /// Offers to save unsaved changes before the window closes.
    /// </summary>
    private void Window_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            eventArgs.Cancel = true;
        }
    }

    /// <summary>
    /// Splits editor text into lines for the setup file. Trailing empty lines are dropped.
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

    // ------------------------------------------------------------------
    // Live update
    // ------------------------------------------------------------------

    /// <summary>
    /// Any edit in any of the four editors: refresh everything right away and mark the setup as changed.
    /// </summary>
    private void SetupEditor_TextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (isLoadingSetup)
        {
            return;
        }

        RefreshEverything();

        if (!hasUnsavedSetupChanges)
        {
            hasUnsavedSetupChanges = true;
            UpdateTitle();
        }
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

        bool isActionMode = ActionModeRadioButton.IsChecked == true;
        ApplyMode(isActionMode);

        // The mode is a per-computer preference, not part of the setup file.
        if (!isLoadingSetup)
        {
            preferences.Mode = isActionMode ? AppPreferences.ActionModeName : AppPreferences.SetupModeName;
            preferences.Save();
        }
    }

    /// <summary>
    /// The "Always on top" switch was turned on or off: apply it to the window and save it.
    /// </summary>
    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
    {
        Topmost = AlwaysOnTopCheckBox.IsChecked == true;

        if (!isLoadingSetup)
        {
            preferences.AlwaysOnTop = Topmost;
            preferences.Save();
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

        ApplyButtonSizing();
        UpdateTitle();
    }

    // ------------------------------------------------------------------
    // Action mode button size (View > Action buttons)
    // ------------------------------------------------------------------

    /// <summary>
    /// Sets button height, text size and layout for the current mode.
    /// Setup mode: normal small buttons in a scrolling list.
    /// Action mode, fixed: every button at least <see cref="actionButtonHeight"/> high, still scrolling.
    /// Action mode, fill: no scrolling; the buttons share the available height equally.
    /// </summary>
    private void ApplyButtonSizing()
    {
        // Can be called while the window is still being built.
        if (ButtonItemsControl is null || ButtonScrollViewer is null || ActionModeRadioButton is null)
        {
            return;
        }

        bool isActionMode = ActionModeRadioButton.IsChecked == true;
        bool fillWindow = isActionMode && actionButtonSize == SetupFile.FillButtonSize;

        Resources["ClipboardButtonMinHeight"] = isActionMode && !fillWindow ? actionButtonHeight : 0.0;
        Resources["ClipboardButtonFontSize"] = isActionMode ? actionFontSize : SetupModeFontSize;

        ButtonItemsControl.ItemsPanel = (ItemsPanelTemplate)FindResource(fillWindow ? "FillButtonsPanel" : "StackedButtonsPanel");

        // "Disabled" (not "Hidden") makes the scroll viewer hand its real height to the panel, which "fill" needs.
        ButtonScrollViewer.VerticalScrollBarVisibility = fillWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        UpdateButtonSizeMenuChecks();
    }

    /// <summary>
    /// View > Action buttons > Normal / Large / Extra large / Fill the window.
    /// The choice is part of the setup, so it marks the setup as changed.
    /// </summary>
    private void ActionButtonSizeMenuItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: string presetName })
        {
            return;
        }

        (actionButtonSize, actionButtonHeight, actionFontSize) = presetName switch
        {
            "normal" => (SetupFile.FixedButtonSize, 0.0, 13.0),
            "large" => (SetupFile.FixedButtonSize, 60.0, 18.0),
            "extralarge" => (SetupFile.FixedButtonSize, 90.0, 24.0),
            _ => (SetupFile.FillButtonSize, actionButtonHeight, 20.0),
        };

        ApplyButtonSizing();

        hasUnsavedSetupChanges = true;
        UpdateTitle();

        if (ActionModeRadioButton.IsChecked != true)
        {
            ShowStatus("Button size changed. It shows in Action mode.");
        }
    }

    /// <summary>
    /// Ticks the preset in View > Action buttons that matches the current values
    /// (none, if the numbers were typed into the setup file by hand).
    /// </summary>
    private void UpdateButtonSizeMenuChecks()
    {
        bool isFixed = actionButtonSize == SetupFile.FixedButtonSize;
        NormalButtonsMenuItem.IsChecked = isFixed && actionButtonHeight == 0 && actionFontSize == 13;
        LargeButtonsMenuItem.IsChecked = isFixed && actionButtonHeight == 60 && actionFontSize == 18;
        ExtraLargeButtonsMenuItem.IsChecked = isFixed && actionButtonHeight == 90 && actionFontSize == 24;
        FillButtonsMenuItem.IsChecked = !isFixed;
    }

    /// <summary>
    /// "Paster - Action - dbt.json *": mode, setup file name, and * when there are unsaved changes.
    /// </summary>
    private void UpdateTitle()
    {
        string modeName = ActionModeRadioButton?.IsChecked == true ? "Action" : "Setup";
        string setupFileName = currentSetupFilePath is null ? "New setup" : Path.GetFileName(currentSetupFilePath);
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
    // File menu: New / Open / Open recent / Save / Save as
    // ------------------------------------------------------------------

    /// <summary>
    /// F1 = Quick guide, Ctrl+N = New, Ctrl+O = Open, Ctrl+S = Save, Ctrl+Shift+S = Save as.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs keyEventArgs)
    {
        if (keyEventArgs.Key == Key.F1)
        {
            ShowQuickGuide();
            keyEventArgs.Handled = true;
            return;
        }

        bool isControlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (!isControlHeld)
        {
            return;
        }

        switch (keyEventArgs.Key)
        {
            case Key.N:
                NewSetup();
                keyEventArgs.Handled = true;
                break;
            case Key.O:
                OpenSetupWithDialog();
                keyEventArgs.Handled = true;
                break;
            case Key.S when isShiftHeld:
                SaveSetupFileAs();
                keyEventArgs.Handled = true;
                break;
            case Key.S:
                SaveSetupFile();
                keyEventArgs.Handled = true;
                break;
        }
    }

    private void NewMenuItem_Click(object sender, RoutedEventArgs eventArgs) => NewSetup();

    private void OpenMenuItem_Click(object sender, RoutedEventArgs eventArgs) => OpenSetupWithDialog();

    private void SaveMenuItem_Click(object sender, RoutedEventArgs eventArgs) => SaveSetupFile();

    private void SaveAsMenuItem_Click(object sender, RoutedEventArgs eventArgs) => SaveSetupFileAs();

    private void ExitMenuItem_Click(object sender, RoutedEventArgs eventArgs) => Close();

    /// <summary>
    /// File > New: an empty setup that has no file yet (Save will ask for a name).
    /// </summary>
    private void NewSetup()
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        ShowSetupInEditors(SetupFile.CreateEmpty(), filePath: null);
        ShowStatus("New setup. Use File > Save to give it a name.");
    }

    /// <summary>
    /// File > Open: asks for a setup file and opens it.
    /// </summary>
    private void OpenSetupWithDialog()
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

        if (openFileDialog.ShowDialog(this) == true)
        {
            TryOpenSetupFile(openFileDialog.FileName);
        }
    }

    /// <summary>
    /// File > Open recent > (a file).
    /// </summary>
    private void RecentFileMenuItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: string recentFilePath })
        {
            return;
        }

        if (!ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        if (!File.Exists(recentFilePath))
        {
            MessageBox.Show(this, $"The file no longer exists:\n\n{recentFilePath}", "Paster",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            preferences.RecentFiles.Remove(recentFilePath);
            preferences.Save();
            RebuildRecentFilesMenu();
            return;
        }

        TryOpenSetupFile(recentFilePath);
    }

    /// <summary>
    /// File > Open recent > Clear list.
    /// </summary>
    private void ClearRecentFilesMenuItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        preferences.RecentFiles.Clear();
        preferences.Save();
        RebuildRecentFilesMenu();
    }

    /// <summary>
    /// A .json file dragged onto the window is opened like File > Open.
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs dragEventArgs)
    {
        if (dragEventArgs.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } droppedFilePaths)
        {
            return;
        }

        dragEventArgs.Handled = true;

        if (ConfirmDiscardUnsavedChanges())
        {
            TryOpenSetupFile(droppedFilePaths[0]);
        }
    }

    /// <summary>
    /// Only show the "copy" cursor when files are dragged over the window.
    /// </summary>
    private void Window_DragOver(object sender, DragEventArgs dragEventArgs)
    {
        // Leave normal text drag-and-drop inside the editors alone.
        if (!dragEventArgs.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        dragEventArgs.Effects = DragDropEffects.Copy;
        dragEventArgs.Handled = true;
    }

    /// <summary>
    /// Reads a setup file and shows it. Tells the user and returns false if the file can't be read.
    /// Does not ask about unsaved changes; callers do that first.
    /// </summary>
    private bool TryOpenSetupFile(string filePath)
    {
        SetupFile loadedSetup;
        try
        {
            loadedSetup = SetupFile.Load(filePath);
        }
        catch (Exception loadException)
        {
            MessageBox.Show(this, $"Could not open the setup:\n{filePath}\n\n{loadException.Message}", "Paster",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string fullPath = Path.GetFullPath(filePath);
        ShowSetupInEditors(loadedSetup, fullPath);
        RememberRecentFile(fullPath);
        ShowStatus($"Opened {fullPath}");
        return true;
    }

    /// <summary>
    /// Puts a setup in the four editors and makes it the current one. Every button starts clickable again.
    /// </summary>
    private void ShowSetupInEditors(SetupFile setup, string? filePath)
    {
        isLoadingSetup = true;
        try
        {
            TablesTextBox.Text = string.Join(Environment.NewLine, setup.Tables);
            VariablesTextBox.Text = string.Join(Environment.NewLine, setup.Variables);
            JoinersTextBox.Text = string.Join(Environment.NewLine, setup.Joiners);
            ButtonsTextBox.Text = string.Join(Environment.NewLine, setup.Buttons);
        }
        finally
        {
            isLoadingSetup = false;
        }

        // Action mode button size; nonsense values from a hand-edited file fall back to the defaults.
        actionButtonSize = string.Equals(setup.ActionButtonSize, SetupFile.FillButtonSize, StringComparison.OrdinalIgnoreCase)
            ? SetupFile.FillButtonSize
            : SetupFile.FixedButtonSize;
        actionButtonHeight = setup.ActionButtonHeight is >= 0 and <= 1000 ? setup.ActionButtonHeight : 60;
        actionFontSize = setup.ActionFontSize is >= 6 and <= 120 ? setup.ActionFontSize : 18;
        ApplyButtonSizing();

        usedTextsByTable.Clear();
        currentSetupFilePath = filePath;
        hasUnsavedSetupChanges = false;

        RefreshEverything();
        TableListBox.SelectedIndex = TableListBox.Items.Count > 0 ? 0 : -1;
        UpdateTitle();
    }

    /// <summary>
    /// File > Save: writes to the current file, or asks for a name for a new setup.
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
    /// File > Save as: asks for a file name, saves there and continues working in that file.
    /// Returns true if the setup was saved.
    /// </summary>
    private bool SaveSetupFileAs()
    {
        var saveFileDialog = new SaveFileDialog
        {
            Title = "Save setup as",
            Filter = "Paster setup (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
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
    /// Writes the four editors to a file and makes it the current one.
    /// </summary>
    private bool WriteSetupFile(string filePath)
    {
        var setupToSave = new SetupFile
        {
            Tables = SplitEditorLines(TablesTextBox.Text),
            Variables = SplitEditorLines(VariablesTextBox.Text),
            Joiners = SplitEditorLines(JoinersTextBox.Text),
            Buttons = SplitEditorLines(ButtonsTextBox.Text),
            ActionButtonSize = actionButtonSize,
            ActionButtonHeight = actionButtonHeight,
            ActionFontSize = actionFontSize,
        };

        try
        {
            setupToSave.Save(filePath);
        }
        catch (Exception saveException)
        {
            MessageBox.Show(this, $"Could not save the setup:\n{filePath}\n\n{saveException.Message}", "Paster",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string fullPath = Path.GetFullPath(filePath);
        currentSetupFilePath = fullPath;
        hasUnsavedSetupChanges = false;
        UpdateTitle();
        RememberRecentFile(fullPath);
        ShowStatus($"Saved {DateTime.Now:HH:mm:ss} to {fullPath}");
        return true;
    }

    /// <summary>
    /// If there are unsaved changes, asks whether to save them. Returns false if the user cancels
    /// (or chose to save and the save did not happen).
    /// </summary>
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!hasUnsavedSetupChanges)
        {
            return true;
        }

        string setupName = currentSetupFilePath is null ? "the new setup" : Path.GetFileName(currentSetupFilePath);
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Save changes to {setupName}?",
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
    /// Puts a file at the top of File > Open recent.
    /// </summary>
    private void RememberRecentFile(string filePath)
    {
        preferences.AddRecentFile(filePath);
        preferences.Save();
        RebuildRecentFilesMenu();
    }

    /// <summary>
    /// Fills File > Open recent: one item per file ("1  ssis.json" with the full path as tooltip),
    /// then "Clear list".
    /// </summary>
    private void RebuildRecentFilesMenu()
    {
        RecentFilesMenuItem.Items.Clear();

        for (int recentIndex = 0; recentIndex < preferences.RecentFiles.Count; recentIndex++)
        {
            string recentFilePath = preferences.RecentFiles[recentIndex];
            var recentFileMenuItem = new MenuItem
            {
                // The underscore makes 1-8 keyboard shortcuts inside the menu; file name underscores are doubled.
                Header = $"_{recentIndex + 1}  {Path.GetFileName(recentFilePath).Replace("_", "__")}",
                ToolTip = recentFilePath,
                Tag = recentFilePath,
            };
            recentFileMenuItem.Click += RecentFileMenuItem_Click;
            RecentFilesMenuItem.Items.Add(recentFileMenuItem);
        }

        if (preferences.RecentFiles.Count > 0)
        {
            RecentFilesMenuItem.Items.Add(new Separator());
        }

        var clearMenuItem = new MenuItem { Header = "Clear list", IsEnabled = preferences.RecentFiles.Count > 0 };
        clearMenuItem.Click += ClearRecentFilesMenuItem_Click;
        RecentFilesMenuItem.Items.Add(clearMenuItem);
    }

    /// <summary>
    /// Folder the file dialogs start in: the current setup's folder, otherwise the EXE's folder.
    /// </summary>
    private string GetDialogFolder()
    {
        string? currentFolder = currentSetupFilePath is null ? null : Path.GetDirectoryName(currentSetupFilePath);
        return currentFolder is not null && Directory.Exists(currentFolder) ? currentFolder : AppContext.BaseDirectory;
    }

    // ------------------------------------------------------------------
    // Help menu
    // ------------------------------------------------------------------

    /// <summary>The open quick guide, so F1 brings the same window forward instead of opening another.</summary>
    private HelpWindow? quickGuideWindow;

    private void QuickGuideMenuItem_Click(object sender, RoutedEventArgs eventArgs) => ShowQuickGuide();

    /// <summary>
    /// Opens the quick guide next to the main window (not modal, so it can stay open while working).
    /// </summary>
    private void ShowQuickGuide()
    {
        if (quickGuideWindow is null)
        {
            // Owned by the main window so it stays in front of it, also with "Always on top".
            quickGuideWindow = new HelpWindow { Owner = this };
            quickGuideWindow.Closed += (_, _) => quickGuideWindow = null;
            quickGuideWindow.Show();
        }
        else
        {
            quickGuideWindow.Activate();
        }
    }

    /// <summary>
    /// Help > About: version and where things are stored.
    /// </summary>
    private void AboutMenuItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        string versionText = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
        MessageBox.Show(
            this,
            $"Paster {versionText}\n\n" +
            $"Current setup: {currentSetupFilePath ?? "(not saved yet)"}\n" +
            $"Logs: {ActionLog.LogFolder}\n" +
            "Preferences: HKEY_CURRENT_USER\\Software\\Paster",
            "About Paster",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
