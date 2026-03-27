Imports System.Windows.Input

Namespace Infrastructure
    Public Class DelegateCommand
        Implements ICommand

        Private ReadOnly _execute As Action(Of Object)
        Private ReadOnly _canExecute As Predicate(Of Object)

        Public Sub New(execute As Action)
            Me.New(Sub(parameter) execute(), Nothing)
        End Sub

        Public Sub New(execute As Action, canExecute As Func(Of Boolean))
            Me.New(Sub(parameter) execute(), If(canExecute Is Nothing, Nothing, Function(parameter) canExecute()))
        End Sub

        Public Sub New(execute As Action(Of Object), canExecute As Predicate(Of Object))
            If execute Is Nothing Then
                Throw New ArgumentNullException("execute")
            End If

            _execute = execute
            _canExecute = canExecute
        End Sub

        Public Event CanExecuteChanged As EventHandler Implements ICommand.CanExecuteChanged

        Public Function CanExecute(parameter As Object) As Boolean Implements ICommand.CanExecute
            If _canExecute Is Nothing Then
                Return True
            End If

            Return _canExecute(parameter)
        End Function

        Public Sub Execute(parameter As Object) Implements ICommand.Execute
            _execute(parameter)
        End Sub

        Public Sub RaiseCanExecuteChanged()
            RaiseEvent CanExecuteChanged(Me, EventArgs.Empty)
        End Sub
    End Class
End Namespace
