using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Paster;

/// <summary>
/// Minimal INotifyPropertyChanged helper so the UI updates when a property changes.
/// (Written by hand to avoid pulling in an MVVM package.)
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets the backing field and raises PropertyChanged if the value actually changed.
    /// </summary>
    protected void SetProperty<TValue>(ref TValue backingField, TValue newValue, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<TValue>.Default.Equals(backingField, newValue))
        {
            return;
        }

        backingField = newValue;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// One row in the table list on the left.
/// </summary>
public sealed class TableListItem : ObservableObject
{
    private string progressText = string.Empty;

    /// <summary>The table name as shown in the list.</summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>True if the table line has a parse error (row is shown light red).</summary>
    public bool HasError { get; init; }

    /// <summary>Tooltip text: the errors on this line, or null when there are none.</summary>
    public string? ErrorText { get; init; }

    /// <summary>"2/5": how many of the buttons have been copied for this table.</summary>
    public string ProgressText
    {
        get => progressText;
        set => SetProperty(ref progressText, value);
    }
}

/// <summary>
/// One clipboard button on the right.
/// </summary>
public sealed class ClipboardButtonItem : ObservableObject
{
    private bool isUsed;

    /// <summary>The finished text that is copied to the clipboard.</summary>
    public string RenderedText { get; init; } = string.Empty;

    /// <summary>The template the text came from, shown in the tooltip.</summary>
    public string Template { get; init; } = string.Empty;

    /// <summary>True if rendering failed somewhere (button is shown light red).</summary>
    public bool HasError { get; init; }

    /// <summary>Tooltip text: template plus any errors.</summary>
    public string ToolTipText { get; init; } = string.Empty;

    /// <summary>
    /// True after the button has been clicked (it looks disabled). Ctrl+click sets it back to false.
    /// </summary>
    public bool IsUsed
    {
        get => isUsed;
        set => SetProperty(ref isUsed, value);
    }
}
