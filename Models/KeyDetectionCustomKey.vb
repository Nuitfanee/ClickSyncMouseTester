Namespace Models
    Public NotInheritable Class KeyDetectionCustomKey
        Private ReadOnly _virtualKey As Integer
        Private ReadOnly _scanCode As Integer
        Private ReadOnly _isExtendedKey As Boolean
        Private ReadOnly _displayName As String

        Public Sub New(virtualKey As Integer, scanCode As Integer, isExtendedKey As Boolean, displayName As String)
            _virtualKey = virtualKey
            _scanCode = scanCode
            _isExtendedKey = isExtendedKey
            _displayName = If(displayName, String.Empty)
        End Sub

        Public ReadOnly Property VirtualKey As Integer
            Get
                Return _virtualKey
            End Get
        End Property

        Public ReadOnly Property ScanCode As Integer
            Get
                Return _scanCode
            End Get
        End Property

        Public ReadOnly Property IsExtendedKey As Boolean
            Get
                Return _isExtendedKey
            End Get
        End Property

        Public ReadOnly Property DisplayName As String
            Get
                Return _displayName
            End Get
        End Property

        Public Function Matches(input As RawKeyboardInput) As Boolean
            If input Is Nothing Then
                Return False
            End If

            Return input.VirtualKey = VirtualKey AndAlso
                   input.ScanCode = ScanCode AndAlso
                   input.IsExtendedKey = IsExtendedKey
        End Function

        Public Shared Function FromInput(input As RawKeyboardInput) As KeyDetectionCustomKey
            If input Is Nothing Then
                Return Nothing
            End If

            Return New KeyDetectionCustomKey(input.VirtualKey,
                                             input.ScanCode,
                                             input.IsExtendedKey,
                                             input.DisplayName)
        End Function
    End Class
End Namespace
