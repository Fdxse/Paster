using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Paster;

/// <summary>
/// A tiny "type a whole number" dialog (WPF has no built-in input box). Built in code, since it is just
/// a question, a text box and OK / Cancel.
/// </summary>
public sealed class NumberPromptWindow : Window
{
    private readonly TextBox numberTextBox;
    private readonly TextBlock problemTextBlock;

    /// <summary>The number typed, once OK was pressed with a valid number.</summary>
    public int EnteredNumber { get; private set; }

    private NumberPromptWindow(string title, string question, int initialNumber)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var questionTextBlock = new TextBlock { Text = question, Margin = new Thickness(0, 0, 0, 6) };

        numberTextBox = new TextBox
        {
            Text = initialNumber.ToString(),
            MinWidth = 220,
            FontSize = 14,
            Padding = new Thickness(2),
        };

        problemTextBlock = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("SoftErrorTextBrush"),
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        // IsDefault makes Enter press OK, IsCancel makes Esc press Cancel.
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(6, 2, 6, 2) };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
        okButton.Click += OkButton_Click;

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);

        var layoutPanel = new StackPanel { Margin = new Thickness(14) };
        layoutPanel.Children.Add(questionTextBlock);
        layoutPanel.Children.Add(numberTextBox);
        layoutPanel.Children.Add(problemTextBlock);
        layoutPanel.Children.Add(buttonRow);
        Content = layoutPanel;

        // Start with the number selected, so typing replaces it.
        Loaded += (_, _) =>
        {
            numberTextBox.Focus();
            numberTextBox.SelectAll();
        };
    }

    /// <summary>
    /// Asks for a whole number. Returns null if the user cancels.
    /// </summary>
    public static int? Ask(Window owner, string title, string question, int initialNumber)
    {
        var promptWindow = new NumberPromptWindow(title, question, initialNumber) { Owner = owner };
        return promptWindow.ShowDialog() == true ? promptWindow.EnteredNumber : null;
    }

    /// <summary>
    /// Only closes when the text is a whole number; otherwise says what is wrong and stays open.
    /// </summary>
    private void OkButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(numberTextBox.Text.Trim(), out int typedNumber))
        {
            problemTextBlock.Text = "Please type a whole number, e.g. 0 or 25.";
            problemTextBlock.Visibility = Visibility.Visible;
            numberTextBox.Focus();
            numberTextBox.SelectAll();
            return;
        }

        EnteredNumber = typedNumber;
        DialogResult = true;
    }
}
