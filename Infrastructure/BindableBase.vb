Imports System.ComponentModel
Imports System.Runtime.CompilerServices

Namespace Infrastructure
    Public MustInherit Class BindableBase
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Protected Function SetProperty(Of T)(ByRef storage As T, value As T, <CallerMemberName> Optional propertyName As String = Nothing) As Boolean
            If EqualityComparer(Of T).Default.Equals(storage, value) Then
                Return False
            End If

            storage = value
            RaisePropertyChanged(propertyName)
            Return True
        End Function

        Protected Sub RaisePropertyChanged(<CallerMemberName> Optional propertyName As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class
End Namespace
