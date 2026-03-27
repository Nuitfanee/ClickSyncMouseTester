Imports WpfApp1.Infrastructure

Namespace Navigation
    Public NotInheritable Class AppPageDescriptor
        Inherits BindableBase

        Private _displayTitle As String
        Private _displaySummary As String
        Private _displayMenuTitle As String
        Private _displayMenuMeta As String
        Private _menuIndexText As String
        Private _isCurrentPage As Boolean

        Public Sub New(pageKey As AppPageKey,
                       titleResourceKey As String,
                       fallbackTitle As String,
                       summaryResourceKey As String,
                       fallbackSummary As String,
                       menuMetaResourceKey As String,
                       fallbackMenuMeta As String,
                       pageViewModel As Object)
            Me.PageKey = pageKey
            Me.TitleResourceKey = titleResourceKey
            Me.FallbackTitle = fallbackTitle
            Me.SummaryResourceKey = summaryResourceKey
            Me.FallbackSummary = fallbackSummary
            Me.MenuMetaResourceKey = menuMetaResourceKey
            Me.FallbackMenuMeta = fallbackMenuMeta
            Me.PageViewModel = pageViewModel

            _displayTitle = fallbackTitle
            _displaySummary = fallbackSummary
            _displayMenuTitle = fallbackTitle
            _displayMenuMeta = fallbackMenuMeta
            _menuIndexText = String.Empty
        End Sub

        Public ReadOnly Property PageKey As AppPageKey

        Public ReadOnly Property TitleResourceKey As String

        Public ReadOnly Property FallbackTitle As String

        Public ReadOnly Property SummaryResourceKey As String

        Public ReadOnly Property FallbackSummary As String

        Public ReadOnly Property MenuMetaResourceKey As String

        Public ReadOnly Property FallbackMenuMeta As String

        Public ReadOnly Property PageViewModel As Object

        Public Property DisplayTitle As String
            Get
                Return _displayTitle
            End Get
            Private Set(value As String)
                SetProperty(_displayTitle, value)
            End Set
        End Property

        Public Property DisplaySummary As String
            Get
                Return _displaySummary
            End Get
            Private Set(value As String)
                SetProperty(_displaySummary, value)
            End Set
        End Property

        Public Property DisplayMenuTitle As String
            Get
                Return _displayMenuTitle
            End Get
            Private Set(value As String)
                SetProperty(_displayMenuTitle, value)
            End Set
        End Property

        Public Property DisplayMenuMeta As String
            Get
                Return _displayMenuMeta
            End Get
            Private Set(value As String)
                SetProperty(_displayMenuMeta, value)
            End Set
        End Property

        Public Property MenuIndexText As String
            Get
                Return _menuIndexText
            End Get
            Private Set(value As String)
                SetProperty(_menuIndexText, value)
            End Set
        End Property

        Public Property IsCurrentPage As Boolean
            Get
                Return _isCurrentPage
            End Get
            Private Set(value As Boolean)
                SetProperty(_isCurrentPage, value)
            End Set
        End Property

        Public Sub SetDisplayText(title As String, summary As String)
            DisplayTitle = title
            DisplaySummary = summary
        End Sub

        Public Sub SetMenuDisplayText(menuTitle As String, menuMeta As String, menuIndexText As String)
            DisplayMenuTitle = menuTitle
            DisplayMenuMeta = menuMeta
            MenuIndexText = menuIndexText
        End Sub

        Public Sub SetCurrentPageState(isCurrentPage As Boolean)
            IsCurrentPage = isCurrentPage
        End Sub
    End Class
End Namespace
