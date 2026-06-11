using System.Windows;

namespace ClickSyncMouseTester.Views.Shell;

public partial class AppAlertDialog : Window
{
    public AppAlertDialog(string titleText, string messageText, string confirmText, string secondaryText = null)
    {
        InitializeComponent();
        string dialogTitle = titleText ?? string.Empty;
        string dialogMessage = messageText ?? string.Empty;
        string confirmButtonText = confirmText ?? string.Empty;
        string secondaryButtonText = secondaryText ?? string.Empty;
        bool hasSecondaryButton = !string.IsNullOrWhiteSpace(secondaryButtonText);

        TitleTextBlock.Text = dialogTitle;
        MessageTextBlock.Text = dialogMessage;
        ConfirmButton.Content = confirmButtonText;
        ConfirmButton.IsCancel = !hasSecondaryButton;
        SecondaryButton.Content = secondaryButtonText;
        SecondaryButton.Visibility = hasSecondaryButton ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.IsCancel = hasSecondaryButton;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
