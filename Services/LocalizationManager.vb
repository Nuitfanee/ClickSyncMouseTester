Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports WpfApp1.Models

Namespace Services
    Public NotInheritable Class LocalizationManager
        Private Const DefaultCultureName As String = "en-US"
        Private Const ChineseCultureName As String = "zh-CN"

        Private Shared ReadOnly _instance As New LocalizationManager()

        Private ReadOnly _availableLanguages As ReadOnlyCollection(Of LanguageOption)
        Private _activeDictionary As ResourceDictionary
        Private _currentLanguage As LanguageOption
        Private _initialized As Boolean

        Private Sub New()
            _availableLanguages =
                New ReadOnlyCollection(Of LanguageOption)(
                    New List(Of LanguageOption) From {
                        New LanguageOption("zh-CN", "简体中文", "Chinese"),
                        New LanguageOption("en-US", "English", "English")
                    })
        End Sub

        Public Shared ReadOnly Property Instance As LocalizationManager
            Get
                Return _instance
            End Get
        End Property

        Public Event LanguageChanged As EventHandler

        Public ReadOnly Property AvailableLanguages As ReadOnlyCollection(Of LanguageOption)
            Get
                Return _availableLanguages
            End Get
        End Property

        Public ReadOnly Property CurrentLanguage As LanguageOption
            Get
                Return _currentLanguage
            End Get
        End Property

        Public ReadOnly Property CurrentCulture As CultureInfo
            Get
                If _currentLanguage Is Nothing Then
                    Return CultureInfo.GetCultureInfo(DefaultCultureName)
                End If

                Return CultureInfo.GetCultureInfo(_currentLanguage.CultureName)
            End Get
        End Property

        Public Sub Initialize()
            If _initialized Then
                Return
            End If

            EnsureWpfResourceContext()
            _initialized = True
            SetLanguage(ResolveCultureName(CultureInfo.CurrentUICulture.Name), False)
        End Sub

        Public Function SetLanguage(cultureName As String, Optional raiseChanged As Boolean = True) As Boolean
            EnsureWpfResourceContext()

            Dim resolvedLanguage = FindLanguage(cultureName)
            If resolvedLanguage Is Nothing Then
                resolvedLanguage = FindLanguage(DefaultCultureName)
            End If

            Dim requiresReload =
                _activeDictionary Is Nothing OrElse
                _currentLanguage Is Nothing OrElse
                Not String.Equals(_currentLanguage.CultureName, resolvedLanguage.CultureName, StringComparison.OrdinalIgnoreCase)

            If requiresReload Then
                Dim dictionary = LoadLanguageDictionary(resolvedLanguage.CultureName)
                ReplaceLanguageDictionary(dictionary)
                _currentLanguage = resolvedLanguage
            End If

            Dim targetCulture = CultureInfo.GetCultureInfo(_currentLanguage.CultureName)
            Thread.CurrentThread.CurrentCulture = targetCulture
            Thread.CurrentThread.CurrentUICulture = targetCulture

            If raiseChanged Then
                RaiseEvent LanguageChanged(Me, EventArgs.Empty)
            End If

            Return requiresReload
        End Function

        Public Function GetString(key As String, ParamArray args() As Object) As String
            If String.IsNullOrWhiteSpace(key) Then
                Return String.Empty
            End If

            If Not _initialized Then
                Initialize()
            End If

            Dim value = ResolveResourceValue(key)
            If String.IsNullOrWhiteSpace(value) Then
                Return key
            End If

            If args Is Nothing OrElse args.Length = 0 Then
                Return value
            End If

            Return String.Format(CurrentCulture, value, args)
        End Function

        Private Function ResolveResourceValue(key As String) As String
            If _activeDictionary IsNot Nothing AndAlso _activeDictionary.Contains(key) Then
                Return TryCast(_activeDictionary(key), String)
            End If

            If Application.Current IsNot Nothing AndAlso Application.Current.Resources.Contains(key) Then
                Return TryCast(Application.Current.Resources(key), String)
            End If

            Return Nothing
        End Function

        Private Function LoadLanguageDictionary(cultureName As String) As ResourceDictionary
            Dim languageDictionary As New ResourceDictionary()
            languageDictionary.MergedDictionaries.Add(New ResourceDictionary With {
                .Source = BuildComponentUri(String.Format(CultureInfo.InvariantCulture,
                                                          "Resources/Typography/Typography.{0}.xaml",
                                                          cultureName))
            })
            languageDictionary.MergedDictionaries.Add(New ResourceDictionary With {
                .Source = BuildComponentUri(String.Format(CultureInfo.InvariantCulture,
                                                          "Resources/Localization/Strings.{0}.xaml",
                                                          cultureName))
            })
            Return languageDictionary
        End Function

        Private Sub ReplaceLanguageDictionary(dictionary As ResourceDictionary)
            If dictionary Is Nothing OrElse Application.Current Is Nothing Then
                _activeDictionary = dictionary
                Return
            End If

            If _activeDictionary IsNot Nothing Then
                Application.Current.Resources.MergedDictionaries.Remove(_activeDictionary)
            End If

            _activeDictionary = dictionary
            Application.Current.Resources.MergedDictionaries.Add(_activeDictionary)
        End Sub

        Private Function FindLanguage(cultureName As String) As LanguageOption
            If String.IsNullOrWhiteSpace(cultureName) Then
                Return Nothing
            End If

            Dim exact = _availableLanguages.FirstOrDefault(Function(item) String.Equals(item.CultureName, cultureName, StringComparison.OrdinalIgnoreCase))
            If exact IsNot Nothing Then
                Return exact
            End If

            Dim languagePrefix = cultureName.Split("-"c)(0)
            Return _availableLanguages.FirstOrDefault(Function(item) item.CultureName.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function ResolveCultureName(cultureName As String) As String
            If IsChineseSystemCulture(cultureName) Then
                Return ChineseCultureName
            End If

            Return DefaultCultureName
        End Function

        Private Shared Function IsChineseSystemCulture(cultureName As String) As Boolean
            If String.IsNullOrWhiteSpace(cultureName) Then
                Return False
            End If

            Dim normalizedCultureName = cultureName.Trim()

            If normalizedCultureName.Equals("zh", StringComparison.OrdinalIgnoreCase) Then
                Return True
            End If

            If normalizedCultureName.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) OrElse
               normalizedCultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) OrElse
               normalizedCultureName.Equals("zh-CHS", StringComparison.OrdinalIgnoreCase) OrElse
               normalizedCultureName.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase) Then
                Return True
            End If

            Try
                Dim culture = CultureInfo.GetCultureInfo(normalizedCultureName)
                If Not String.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase) Then
                    Return False
                End If

                Dim regionName = culture.Name.Split("-"c).LastOrDefault()
                Return String.Equals(regionName, "CN", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(regionName, "SG", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(regionName, "TW", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(regionName, "HK", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(regionName, "MO", StringComparison.OrdinalIgnoreCase)
            Catch ex As CultureNotFoundException
                Return False
            End Try
        End Function

        Private Shared Function BuildComponentUri(relativePath As String) As Uri
            Dim assemblyName = GetType(LocalizationManager).Assembly.GetName().Name
            Dim source = String.Format(CultureInfo.InvariantCulture,
                                       "pack://application:,,,/{0};component/{1}",
                                       assemblyName,
                                       relativePath.Replace("\"c, "/"c))
            Return New Uri(source, UriKind.Absolute)
        End Function

        Private Shared Sub EnsureWpfResourceContext()
            If Application.Current IsNot Nothing Then
                Dim applicationDispatcher As Dispatcher = Application.Current.Dispatcher
                Return
            End If

            Dim threadDispatcher As Dispatcher = Dispatcher.CurrentDispatcher
        End Sub
    End Class
End Namespace
