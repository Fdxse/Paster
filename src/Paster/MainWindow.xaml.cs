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
/// The main (and only) window: tables (or table · layer steps) on the left, clipboard buttons in the middle,
/// setup editors on the right (only in Setup mode). Every edit re-parses everything and refreshes the buttons at once.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Which buttons have been copied, per step. The key is <see cref="WorkStep.Key"/> (table, plus layer
    /// when layers are used) and the set holds the rendered texts that were copied. Using the rendered text
    /// means a button becomes clickable again by itself if a variable change makes it produce different text.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> usedTextsByStep = new(StringComparer.Ordinal);

    /// <summary>The most recent parse of the five editors.</summary>
    private ParsedSetup currentSetup = new();

    /// <summary>The rows of the list on the left, in walking order (one per table, or per table · layer).</summary>
    private List<WorkStep> currentSteps = new();

    /// <summary>Walk order of the current setup (only matters when it has layers).</summary>
    private WalkOrder walkOrder = WalkOrder.ByLayer;

    /// <summary>Short pause before Action mode jumps to the next step, so the last copied button is seen greying out.</summary>
    private static readonly TimeSpan AutoAdvanceDelay = TimeSpan.FromMilliseconds(350);

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
    /// Any edit in any of the five editors: refresh everything right away and mark the setup as changed.
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
    /// Re-parses the editors, marks soft errors, rebuilds the list on the left (keeping the selection)
    /// and rebuilds the buttons for the selected step.
    /// </summary>
    private void RefreshEverything()
    {
        // TextChanged can fire during InitializeComponent before all controls exist.
        if (TablesTextBox is null || LayersTextBox is null || VariablesTextBox is null || JoinersTextBox is null || ButtonsTextBox is null)
        {
            return;
        }

        currentSetup = SetupParser.Parse(TablesTextBox.Text, LayersTextBox.Text, VariablesTextBox.Text, JoinersTextBox.Text, ButtonsTextBox.Text);
        currentSteps = WorkPlanner.BuildSteps(currentSetup, walkOrder);

        ShowSoftErrors(TablesTab, "Tables", TablesTextBox, TablesErrorText, currentSetup.TableErrors);
        ShowSoftErrors(LayersTab, "Layers", LayersTextBox, LayersErrorText, currentSetup.LayerErrors);
        ShowSoftErrors(VariablesTab, "Variables", VariablesTextBox, VariablesErrorText, currentSetup.VariableErrors);
        ShowSoftErrors(JoinersTab, "Joiners", JoinersTextBox, JoinersErrorText, currentSetup.JoinerErrors);
        ShowSoftErrors(ButtonsTab, "Buttons", ButtonsTextBox, ButtonsErrorText, currentSetup.ButtonErrors);
        UpdateInsertExampleButtons();

        // The walk order switch and the "step" wording only make sense with layers.
        bool usesLayers = currentSetup.Layers.Count > 0;
        WalkOrderPanel.Visibility = usesLayers ? Visibility.Visible : Visibility.Collapsed;
        StepListHeader.Text = usesLayers ? "Steps   (table · layer)" : "Tables";
        ResetStepButton.Content = usesLayers ? "Reset step" : "Reset table";

        RebuildStepList();
        RebuildButtons();
        UpdateOverallProgress();
    }

    /// <summary>
    /// Colors an editor light red and lists its errors underneath, or clears both when there are none.
    /// The tab header turns red with the error count too, so problems in a hidden tab are not missed.
    /// </summary>
    private void ShowSoftErrors(TabItem editorTab, string tabName, TextBox editorTextBox, TextBlock errorTextBlock, List<string> errors)
    {
        editorTab.Header = errors.Count == 0 ? tabName : $"{tabName} ({errors.Count})";
        if (errors.Count == 0)
        {
            editorTab.ClearValue(ForegroundProperty);
        }
        else
        {
            editorTab.Foreground = (Brush)FindResource("SoftErrorTextBrush");
        }

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
    /// Rebuilds the list on the left from <see cref="currentSteps"/>. The selection follows the same
    /// step (so switching the walk order keeps your place); if that step is gone, the selection is kept
    /// by position, so typing in a table name does not make the selection jump.
    /// </summary>
    private void RebuildStepList()
    {
        int previouslySelectedIndex = StepListBox.SelectedIndex;
        string? previouslySelectedKey = (StepListBox.SelectedItem as StepListItem)?.StepKey;

        var stepListItems = new List<StepListItem>();
        foreach (WorkStep workStep in currentSteps)
        {
            TableEntry tableEntry = workStep.Table;
            stepListItems.Add(new StepListItem
            {
                StepKey = workStep.Key,
                StepName = workStep.DisplayName,
                HasError = tableEntry.Errors.Count > 0,
                ErrorText = tableEntry.Errors.Count > 0 ? string.Join(Environment.NewLine, tableEntry.Errors) : null,
                ProgressText = BuildProgressText(workStep),
                IsComplete = IsStepComplete(workStep),
            });
        }

        StepListBox.ItemsSource = stepListItems;

        if (stepListItems.Count == 0)
        {
            return;
        }

        int sameStepIndex = stepListItems.FindIndex(stepListItem => stepListItem.StepKey == previouslySelectedKey);
        StepListBox.SelectedIndex = sameStepIndex >= 0
            ? sameStepIndex
            : Math.Clamp(previouslySelectedIndex, 0, stepListItems.Count - 1);
        StepListBox.ScrollIntoView(StepListBox.SelectedItem);
    }

    /// <summary>
    /// Builds the buttons for the selected step.
    /// </summary>
    private void RebuildButtons()
    {
        WorkStep? selectedStep = GetSelectedStep();
        if (selectedStep is null)
        {
            SelectedTableHeader.Text = "Buttons";
            ButtonItemsControl.ItemsSource = null;
            return;
        }

        SelectedTableHeader.Text = $"Buttons for {selectedStep.DisplayName}";

        HashSet<string> usedTexts = GetUsedTexts(selectedStep);
        var templateRenderer = new TemplateRenderer(currentSetup, selectedStep);
        var buttonItems = new List<ClipboardButtonItem>();

        foreach (ButtonTemplate buttonTemplate in WorkPlanner.ButtonsFor(currentSetup, selectedStep))
        {
            RenderResult renderResult = templateRenderer.Render(buttonTemplate.Template);

            // A labelled block shows its (rendered) label on the button; the full text is in the tooltip.
            RenderResult? labelResult = buttonTemplate.Label is null ? null : templateRenderer.Render(buttonTemplate.Label);
            List<string> allErrors = renderResult.Errors.Concat(labelResult?.Errors ?? new List<string>()).ToList();

            string toolTipText = renderResult.Text;
            if (allErrors.Count > 0)
            {
                toolTipText += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, allErrors);
            }

            buttonItems.Add(new ClipboardButtonItem
            {
                RenderedText = renderResult.Text,
                DisplayText = labelResult?.Text ?? renderResult.Text,
                Template = buttonTemplate.Template,
                HasError = allErrors.Count > 0,
                ToolTipText = toolTipText,
                IsUsed = usedTexts.Contains(renderResult.Text),
            });
        }

        ButtonItemsControl.ItemsSource = buttonItems;
    }

    /// <summary>
    /// The step that matches the list selection, or null when nothing is selected.
    /// </summary>
    private WorkStep? GetSelectedStep()
    {
        int selectedIndex = StepListBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= currentSteps.Count)
        {
            return null;
        }

        return currentSteps[selectedIndex];
    }

    private void StepListBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
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

        WorkStep? selectedStep = GetSelectedStep();
        if (selectedStep is null)
        {
            return;
        }

        HashSet<string> usedTexts = GetUsedTexts(selectedStep);
        bool isControlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (buttonItem.IsUsed)
        {
            if (isControlHeld)
            {
                buttonItem.IsUsed = false;
                usedTexts.Remove(buttonItem.RenderedText);
                UpdateSelectedStepProgress(selectedStep);
                UpdateOverallProgress();
                ShowStatus("Button enabled again.");
                RecordAction("Re-enabled", selectedStep.DisplayName, buttonItem.RenderedText);
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
        UpdateSelectedStepProgress(selectedStep);
        UpdateOverallProgress();
        ShowStatus($"Copied: {FirstLine(buttonItem.RenderedText)}");
        RecordAction("Copied", selectedStep.DisplayName, buttonItem.RenderedText);

        // In Action mode, the last button of a step moves on to the next step by itself.
        if (ActionModeRadioButton.IsChecked == true && IsStepComplete(selectedStep))
        {
            RecordAction("Done", selectedStep.DisplayName, string.Empty);
            ScheduleAdvanceFrom(selectedStep);
        }
    }

    /// <summary>
    /// After a short pause, selects the next step that still has buttons left (wrapping around to the top).
    /// Nothing happens if another step was picked or a button was re-enabled in the meantime.
    /// </summary>
    private void ScheduleAdvanceFrom(WorkStep finishedStep)
    {
        var advanceTimer = new DispatcherTimer { Interval = AutoAdvanceDelay };
        advanceTimer.Tick += (_, _) =>
        {
            advanceTimer.Stop();

            // The editors may have been changed meanwhile, so look the step up again by its key.
            int finishedIndex = currentSteps.FindIndex(workStep => workStep.Key == finishedStep.Key);
            if (finishedIndex < 0 || StepListBox.SelectedIndex != finishedIndex || !IsStepComplete(currentSteps[finishedIndex]))
            {
                return;
            }

            for (int offset = 1; offset < currentSteps.Count; offset++)
            {
                int candidateIndex = (finishedIndex + offset) % currentSteps.Count;
                if (!IsStepComplete(currentSteps[candidateIndex]))
                {
                    StepListBox.SelectedIndex = candidateIndex;
                    StepListBox.ScrollIntoView(StepListBox.SelectedItem);
                    ShowStatus($"{finishedStep.DisplayName} done. Next: {currentSteps[candidateIndex].DisplayName}");
                    return;
                }
            }

            ShowStatus("Everything is done! Reset all to start over.");
        };
        advanceTimer.Start();
    }

    /// <summary>
    /// Makes every button for the selected step clickable again.
    /// </summary>
    private void ResetStepButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        WorkStep? selectedStep = GetSelectedStep();
        if (selectedStep is null)
        {
            return;
        }

        usedTextsByStep.Remove(selectedStep.Key);
        RebuildButtons();
        UpdateSelectedStepProgress(selectedStep);
        UpdateOverallProgress();
        ShowStatus($"All buttons for {selectedStep.DisplayName} are enabled again.");
        RecordAction("Reset", selectedStep.DisplayName, string.Empty);
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
    /// The set of copied texts for a step, created on first use.
    /// </summary>
    private HashSet<string> GetUsedTexts(WorkStep workStep)
    {
        if (!usedTextsByStep.TryGetValue(workStep.Key, out HashSet<string>? usedTexts))
        {
            usedTexts = new HashSet<string>(StringComparer.Ordinal);
            usedTextsByStep[workStep.Key] = usedTexts;
        }

        return usedTexts;
    }

    /// <summary>
    /// "2/5" for a step: copied buttons out of its buttons, with a tick when all are done. Empty when nothing is copied yet.
    /// </summary>
    private string BuildProgressText(WorkStep workStep)
    {
        int usedButtonCount = CountUsedButtons(workStep);
        if (usedButtonCount == 0)
        {
            return string.Empty;
        }

        int buttonCount = WorkPlanner.ButtonsFor(currentSetup, workStep).Count;
        string doneMarker = usedButtonCount >= buttonCount ? " ✓" : string.Empty;
        return $"{usedButtonCount}/{buttonCount}{doneMarker}";
    }

    /// <summary>
    /// How many of a step's buttons have been copied (and not re-enabled since).
    /// </summary>
    private int CountUsedButtons(WorkStep workStep)
    {
        if (!usedTextsByStep.TryGetValue(workStep.Key, out HashSet<string>? usedTexts) || usedTexts.Count == 0)
        {
            return 0;
        }

        var templateRenderer = new TemplateRenderer(currentSetup, workStep);
        return WorkPlanner.ButtonsFor(currentSetup, workStep)
            .Count(buttonTemplate => usedTexts.Contains(templateRenderer.Render(buttonTemplate.Template).Text));
    }

    /// <summary>
    /// True when every button of the step has been copied. A step without buttons counts as done,
    /// so moving on to the next step skips it.
    /// </summary>
    private bool IsStepComplete(WorkStep workStep)
    {
        return CountUsedButtons(workStep) >= WorkPlanner.ButtonsFor(currentSetup, workStep).Count;
    }

    /// <summary>
    /// Updates the progress bar above the buttons: copied buttons out of all buttons, over every step.
    /// </summary>
    private void UpdateOverallProgress()
    {
        // Can be called while the window is still being built.
        if (OverallProgressBar is null || OverallProgressText is null)
        {
            return;
        }

        int totalButtonCount = currentSteps.Sum(workStep => WorkPlanner.ButtonsFor(currentSetup, workStep).Count);
        int usedButtonCount = currentSteps.Sum(CountUsedButtons);

        OverallProgressBar.Maximum = Math.Max(totalButtonCount, 1);
        OverallProgressBar.Value = usedButtonCount;

        int percentDone = totalButtonCount == 0 ? 0 : (int)Math.Round(100.0 * usedButtonCount / totalButtonCount);
        OverallProgressText.Text = $"{usedButtonCount} / {totalButtonCount}  ({percentDone}%)";
    }

    /// <summary>
    /// Makes every button everywhere clickable again (asks first, since it can't be undone).
    /// </summary>
    private void ResetAllButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (usedTextsByStep.Values.All(usedTexts => usedTexts.Count == 0))
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(this, "Make every button everywhere clickable again?", "Paster",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        usedTextsByStep.Clear();
        RefreshEverything();
        ShowStatus("All buttons are enabled again.");
        RecordAction("Reset all", string.Empty, string.Empty);
    }

    /// <summary>
    /// Updates the "2/5" counter and the greyed-out look of the selected row without rebuilding the whole list.
    /// </summary>
    private void UpdateSelectedStepProgress(WorkStep selectedStep)
    {
        if (StepListBox.SelectedItem is StepListItem selectedListItem)
        {
            selectedListItem.ProgressText = BuildProgressText(selectedStep);
            selectedListItem.IsComplete = IsStepComplete(selectedStep);
        }
    }

    // ------------------------------------------------------------------
    // Setup tabs: Insert example
    // ------------------------------------------------------------------

    /// <summary>
    /// "Insert example" in one of the setup tabs: fills that box with the lines from the example setup.
    /// Only allowed while the box holds nothing but blank lines and // comments, so real work is never overwritten.
    /// </summary>
    private void InsertExampleButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        SetupFile exampleSetup = SetupFile.CreateExample();

        (TextBox editorTextBox, List<string> exampleLines) = sender switch
        {
            _ when sender == TablesExampleButton => (TablesTextBox, exampleSetup.Tables),
            _ when sender == LayersExampleButton => (LayersTextBox, exampleSetup.Layers),
            _ when sender == VariablesExampleButton => (VariablesTextBox, exampleSetup.Variables),
            _ when sender == JoinersExampleButton => (JoinersTextBox, exampleSetup.Joiners),
            _ => (ButtonsTextBox, exampleSetup.Buttons),
        };

        if (HasMeaningfulLines(editorTextBox.Text))
        {
            ShowStatus("Insert example only works on a box without lines of its own (comments are fine).");
            return;
        }

        // Replacing the selection (instead of setting Text) keeps Ctrl+Z working. It counts as an edit,
        // so everything refreshes and the setup is marked as changed.
        editorTextBox.Focus();
        editorTextBox.SelectAll();
        editorTextBox.SelectedText = string.Join(Environment.NewLine, exampleLines);
        editorTextBox.CaretIndex = editorTextBox.Text.Length;
        ShowStatus("Example inserted. Change it to fit, or Ctrl+Z to take it back.");
    }

    /// <summary>
    /// Enables each "Insert example" button only while its box has nothing but blank lines and comments.
    /// </summary>
    private void UpdateInsertExampleButtons()
    {
        TablesExampleButton.IsEnabled = !HasMeaningfulLines(TablesTextBox.Text);
        LayersExampleButton.IsEnabled = !HasMeaningfulLines(LayersTextBox.Text);
        VariablesExampleButton.IsEnabled = !HasMeaningfulLines(VariablesTextBox.Text);
        JoinersExampleButton.IsEnabled = !HasMeaningfulLines(JoinersTextBox.Text);
        ButtonsExampleButton.IsEnabled = !HasMeaningfulLines(ButtonsTextBox.Text);
    }

    /// <summary>
    /// True if the text has at least one line that is not blank and not a // comment.
    /// </summary>
    private static bool HasMeaningfulLines(string editorText)
    {
        return editorText
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Any(trimmedLine => trimmedLine.Length > 0 && !trimmedLine.StartsWith("//", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Walk order (only shown when the setup has layers)
    // ------------------------------------------------------------------

    /// <summary>
    /// "By layer" or "By table" was picked. The steps stay the same, only their order changes,
    /// so everything already copied is kept and the selected step stays selected.
    /// The choice is part of the setup, so it marks the setup as changed.
    /// </summary>
    private void WalkOrderRadioButton_Checked(object sender, RoutedEventArgs eventArgs)
    {
        // Checked can fire during InitializeComponent before everything exists.
        if (ByTableRadioButton is null || StepListBox is null)
        {
            return;
        }

        WalkOrder pickedWalkOrder = ByTableRadioButton.IsChecked == true ? WalkOrder.ByTable : WalkOrder.ByLayer;
        if (pickedWalkOrder == walkOrder)
        {
            return;
        }

        walkOrder = pickedWalkOrder;
        RefreshEverything();

        if (!isLoadingSetup)
        {
            hasUnsavedSetupChanges = true;
            UpdateTitle();
        }
    }

    /// <summary>
    /// Ticks the walk order button that matches <see cref="walkOrder"/>.
    /// </summary>
    private void ShowWalkOrder()
    {
        ByLayerRadioButton.IsChecked = walkOrder == WalkOrder.ByLayer;
        ByTableRadioButton.IsChecked = walkOrder == WalkOrder.ByTable;
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

    /// <summary>
    /// Saves the entries in the log list (oldest first) to a tab-separated text file.
    /// </summary>
    private void ExportLogButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (actionLog.Entries.Count == 0)
        {
            ShowStatus("The log list is empty, nothing to export.");
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Title = "Export action log",
            Filter = "Text file, tab-separated (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"paster-log-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };

        if (saveFileDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            actionLog.ExportDisplayedEntries(saveFileDialog.FileName);
            ShowStatus($"Exported {actionLog.Entries.Count} log entries to {saveFileDialog.FileName}");
        }
        catch (Exception exportException)
        {
            ShowStatus($"Could not export the log: {exportException.Message}", isError: true);
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
    /// Puts a setup in the five editors and makes it the current one. Every button starts clickable again.
    /// </summary>
    private void ShowSetupInEditors(SetupFile setup, string? filePath)
    {
        isLoadingSetup = true;
        try
        {
            TablesTextBox.Text = string.Join(Environment.NewLine, setup.Tables);
            LayersTextBox.Text = string.Join(Environment.NewLine, setup.Layers);
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

        // Walk order; anything other than "table" means by layer.
        walkOrder = string.Equals(setup.WalkOrder, SetupFile.ByTableWalkOrder, StringComparison.OrdinalIgnoreCase)
            ? WalkOrder.ByTable
            : WalkOrder.ByLayer;
        isLoadingSetup = true;
        try
        {
            ShowWalkOrder();
        }
        finally
        {
            isLoadingSetup = false;
        }

        usedTextsByStep.Clear();
        currentSetupFilePath = filePath;
        hasUnsavedSetupChanges = false;

        RefreshEverything();
        StepListBox.SelectedIndex = StepListBox.Items.Count > 0 ? 0 : -1;
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
    /// Writes the five editors to a file and makes it the current one.
    /// </summary>
    private bool WriteSetupFile(string filePath)
    {
        var setupToSave = new SetupFile
        {
            Tables = SplitEditorLines(TablesTextBox.Text),
            Layers = SplitEditorLines(LayersTextBox.Text),
            WalkOrder = walkOrder == WalkOrder.ByTable ? SetupFile.ByTableWalkOrder : SetupFile.ByLayerWalkOrder,
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
