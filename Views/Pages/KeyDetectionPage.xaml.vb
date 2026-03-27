Imports System.Runtime.Versioning
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports WpfApp1.ViewModels.Pages

Namespace Views.Pages
    <SupportedOSPlatform("windows")>
    Partial Public Class KeyDetectionPage
        Private _hostWindow As Window
        Private _activeTextEntryCount As Integer

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub KeyDetectionPage_Loaded(sender As Object, e As RoutedEventArgs)
            AttachWindowHandlers()
            SyncViewModelState()
        End Sub

        Private Sub KeyDetectionPage_Unloaded(sender As Object, e As RoutedEventArgs)
            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetPageActive(False)
                viewModel.SetWindowActive(False)
                viewModel.SetTextEntryActive(False)
            End If

            _activeTextEntryCount = 0
            DetachWindowHandlers()
        End Sub

        Private Sub KeyDetectionPage_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub KeyDetectionPage_DataContextChanged(sender As Object, e As DependencyPropertyChangedEventArgs)
            SyncViewModelState()
        End Sub

        Private Sub AttachWindowHandlers()
            Dim nextWindow = Window.GetWindow(Me)
            If ReferenceEquals(_hostWindow, nextWindow) Then
                Return
            End If

            DetachWindowHandlers()
            _hostWindow = nextWindow

            If _hostWindow IsNot Nothing Then
                AddHandler _hostWindow.Activated, AddressOf HostWindow_Activated
                AddHandler _hostWindow.Deactivated, AddressOf HostWindow_Deactivated
            End If
        End Sub

        Private Sub DetachWindowHandlers()
            If _hostWindow Is Nothing Then
                Return
            End If

            RemoveHandler _hostWindow.Activated, AddressOf HostWindow_Activated
            RemoveHandler _hostWindow.Deactivated, AddressOf HostWindow_Deactivated
            _hostWindow = Nothing
        End Sub

        Private Sub HostWindow_Activated(sender As Object, e As EventArgs)
            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            viewModel.SetWindowActive(True)
        End Sub

        Private Sub HostWindow_Deactivated(sender As Object, e As EventArgs)
            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            viewModel.SetWindowActive(False)
        End Sub

        Private Sub SyncViewModelState()
            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            AttachWindowHandlers()
            viewModel.SetPageActive(IsLoaded AndAlso IsVisible)
            viewModel.SetWindowActive(_hostWindow Is Nothing OrElse _hostWindow.IsActive)
            viewModel.SetTextEntryActive(_activeTextEntryCount > 0)
        End Sub

        Private Sub RootScrollViewer_PreviewMouseWheel(sender As Object, e As MouseWheelEventArgs)
            e.Handled = True
        End Sub

        Private Sub RootScrollViewer_PreviewMouseDown(sender As Object, e As MouseButtonEventArgs)
            Dim activeThresholdTextBox = TryCast(Keyboard.FocusedElement, TextBox)
            If activeThresholdTextBox Is Nothing OrElse Not IsThresholdTextBox(activeThresholdTextBox) Then
                Return
            End If

            Dim source = TryCast(e.OriginalSource, DependencyObject)
            If source Is Nothing Then
                Return
            End If

            If FindAncestorOrSelf(Of TextBox)(source) IsNot Nothing Then
                Return
            End If

            CommitThresholdTextBox(activeThresholdTextBox)

            If FindAncestorOrSelf(Of Button)(source) Is Nothing Then
                FocusThresholdFallbackSurface()
            End If
        End Sub

        Private Sub ThresholdTextBox_PreviewKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then
                Return
            End If

            Dim textBox = TryCast(sender, TextBox)
            CommitThresholdTextBox(textBox)
            FocusThresholdFallbackSurface()
            e.Handled = True
        End Sub

        Private Sub ThresholdTextBox_LostFocus(sender As Object, e As RoutedEventArgs)
            CommitThresholdTextBox(TryCast(sender, TextBox))
        End Sub

        Private Sub ThresholdTextBox_GotKeyboardFocus(sender As Object, e As KeyboardFocusChangedEventArgs)
            _activeTextEntryCount += 1

            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetTextEntryActive(True)
            End If
        End Sub

        Private Sub ThresholdTextBox_LostKeyboardFocus(sender As Object, e As KeyboardFocusChangedEventArgs)
            _activeTextEntryCount = Math.Max(0, _activeTextEntryCount - 1)

            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel IsNot Nothing Then
                viewModel.SetTextEntryActive(_activeTextEntryCount > 0)
            End If
        End Sub

        Private Sub CommitThresholdTextBox(textBox As TextBox)
            If textBox Is Nothing Then
                Return
            End If

            Dim bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty)
            If bindingExpression IsNot Nothing Then
                bindingExpression.UpdateSource()
            End If

            Dim viewModel = TryCast(DataContext, KeyDetectionPageViewModel)
            If viewModel Is Nothing Then
                Return
            End If

            If ReferenceEquals(textBox, MouseThresholdTextBox) Then
                viewModel.CommitMouseDoubleClickThresholdInput()
                Return
            End If

            If ReferenceEquals(textBox, CustomKeyThresholdTextBox) Then
                viewModel.CommitKeyDoubleClickThresholdInput()
            End If
        End Sub

        Private Sub FocusThresholdFallbackSurface()
            RootScrollViewer.Focus()
            Keyboard.Focus(RootScrollViewer)
        End Sub

        Private Shared Function IsThresholdTextBox(textBox As TextBox) As Boolean
            If textBox Is Nothing Then
                Return False
            End If

            Return textBox.Name = NameOf(MouseThresholdTextBox) OrElse
                   textBox.Name = NameOf(CustomKeyThresholdTextBox)
        End Function

        Private Shared Function FindAncestorOrSelf(Of T As DependencyObject)(source As DependencyObject) As T
            Dim current = source

            Do While current IsNot Nothing
                If TypeOf current Is T Then
                    Return DirectCast(current, T)
                End If

                Dim visual = TryCast(current, Visual)
                If visual IsNot Nothing Then
                    current = VisualTreeHelper.GetParent(visual)
                    Continue Do
                End If

                Dim contentElement = TryCast(current, FrameworkContentElement)
                If contentElement IsNot Nothing Then
                    current = contentElement.Parent
                    Continue Do
                End If

                current = Nothing
            Loop

            Return Nothing
        End Function
    End Class
End Namespace
