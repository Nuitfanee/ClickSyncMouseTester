Namespace Models
    Public Class LanguageOption
        Private ReadOnly _cultureName As String
        Private ReadOnly _nativeName As String
        Private ReadOnly _englishName As String

        Public Sub New(cultureName As String, nativeName As String, englishName As String)
            _cultureName = If(cultureName, String.Empty)
            _nativeName = If(nativeName, String.Empty)
            _englishName = If(englishName, String.Empty)
        End Sub

        Public ReadOnly Property CultureName As String
            Get
                Return _cultureName
            End Get
        End Property

        Public ReadOnly Property NativeName As String
            Get
                Return _nativeName
            End Get
        End Property

        Public ReadOnly Property EnglishName As String
            Get
                Return _englishName
            End Get
        End Property

        Public ReadOnly Property DisplayName As String
            Get
                If String.IsNullOrWhiteSpace(NativeName) Then
                    Return EnglishName
                End If

                If String.IsNullOrWhiteSpace(EnglishName) OrElse String.Equals(NativeName, EnglishName, StringComparison.OrdinalIgnoreCase) Then
                    Return NativeName
                End If

                Return NativeName & " / " & EnglishName
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return DisplayName
        End Function
    End Class
End Namespace
