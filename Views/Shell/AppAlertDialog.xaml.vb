Imports System.Windows

Namespace Views.Shell
    Public Class AppAlertDialog
        Public Sub New(titleText As String, messageText As String, confirmText As String)
            InitializeComponent()

            TitleText = If(titleText, String.Empty)
            MessageText = If(messageText, String.Empty)
            ConfirmText = If(confirmText, String.Empty)

            DataContext = Me
        End Sub

        Public ReadOnly Property TitleText As String

        Public ReadOnly Property MessageText As String

        Public ReadOnly Property ConfirmText As String

        Private Sub ConfirmButton_Click(sender As Object, e As RoutedEventArgs)
            DialogResult = True
            Close()
        End Sub
    End Class
End Namespace
