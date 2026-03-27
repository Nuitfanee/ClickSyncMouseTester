Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Windows
Imports System.Windows.Media

Namespace Services
    Public NotInheritable Class FontManager
        Private Const SpaceGroteskDirectory As String = "founts/SpaceGrotesk/ttf/static/"
        Private Const SpaceGroteskFamilyName As String = "Space Grotesk"
        Private Shared ReadOnly ApplicationFontBaseUri As New Uri("pack://application:,,,/")

        Private Shared ReadOnly _instance As New FontManager()
        Private Shared ReadOnly _defaultUiFallback As New FontFamily("Segoe UI")
        Private Shared ReadOnly _defaultChineseFallback As New FontFamily("Microsoft YaHei UI")

        Private _initialized As Boolean

        Private Sub New()
        End Sub

        Public Shared ReadOnly Property Instance As FontManager
            Get
                Return _instance
            End Get
        End Property

        Public Sub Initialize()
            If _initialized Then
                Return
            End If

            _initialized = True
            AddHandler LocalizationManager.Instance.LanguageChanged, AddressOf OnLanguageChanged
            ApplyCurrentLanguageFonts()
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            ApplyCurrentLanguageFonts()
        End Sub

        Private Sub ApplyCurrentLanguageFonts()
            If Application.Current Is Nothing Then
                Return
            End If

            Dim cultureName = LocalizationManager.Instance.CurrentCulture.Name
            Dim isChinese = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            Dim displaySansBase = ResolveSpaceGrotesk()
            Dim chineseUi = ResolveChineseUiFont()
            Dim displaySans = ComposeFontFamily(displaySansBase, displaySansBase, chineseUi, _defaultUiFallback)
            Dim listDisplay = displaySans
            Dim body = If(isChinese, chineseUi, displaySans)
            Dim meta = body
            Dim microUi = If(isChinese, ResolveSmallChineseUiFont(), displaySans)

            Application.Current.Resources("Font.Meta") = meta
            Application.Current.Resources("Font.MicroUi") = microUi
            Application.Current.Resources("Font.Body") = body
            Application.Current.Resources("Font.DisplaySans") = displaySans
            Application.Current.Resources("Font.ListDisplay") = listDisplay
            Application.Current.Resources("Font.Serif") = body
            Application.Current.Resources("Font.Mono") = displaySans
            Application.Current.Resources("Font.EditorialSerif") = body
            Application.Current.Resources("Font.EditorialDisplay") = displaySans

            If ShouldWriteDiagnostics() Then
                WriteDiagnostics(displaySans, listDisplay, body, meta, microUi)
            End If
        End Sub

        Private Shared Function ResolveSpaceGrotesk() As FontFamily
            Return ResolveBundledFontFamily(SpaceGroteskDirectory,
                                            SpaceGroteskFamilyName,
                                            _defaultUiFallback)
        End Function

        Private Shared Function ResolveChineseUiFont() As FontFamily
            Return _defaultChineseFallback
        End Function

        Private Shared Function ResolveSmallChineseUiFont() As FontFamily
            Return _defaultChineseFallback
        End Function

        Private Shared Function ResolveBundledFontFamily(relativeDirectory As String,
                                                         familyName As String,
                                                         fallback As FontFamily) As FontFamily
            Dim normalizedDirectory = NormalizeFontDirectory(relativeDirectory)
            If Not HasBundledFontResources(normalizedDirectory) Then
                Trace.WriteLine(String.Format(CultureInfo.InvariantCulture,
                                              "FontManager fallback: no bundled font assets found under {0}",
                                              normalizedDirectory))
                Return fallback
            End If

            Dim relativeReference = String.Format(CultureInfo.InvariantCulture,
                                                  "./{0}#{1}",
                                                  normalizedDirectory,
                                                  familyName)

            Return CreateFontFamily(New String() {relativeReference}, fallback)
        End Function

        Private Shared Function ComposeFontFamily(fallback As FontFamily,
                                                  ParamArray families As FontFamily()) As FontFamily
            If families Is Nothing OrElse families.Length = 0 Then
                Return fallback
            End If

            Dim sources =
                families.Where(Function(item) item IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(item.Source)).
                         Select(Function(item) item.Source).
                         Distinct(StringComparer.OrdinalIgnoreCase)

            Return CreateFontFamily(sources, fallback)
        End Function

        Private Shared Function CreateFontFamily(sources As IEnumerable(Of String), fallback As FontFamily) As FontFamily
            If sources Is Nothing Then
                Return fallback
            End If

            Dim normalizedSources =
                sources.Where(Function(item) Not String.IsNullOrWhiteSpace(item)).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        ToArray()

            If normalizedSources.Length = 0 Then
                Return fallback
            End If

            Dim reference = String.Join(", ", normalizedSources)

            Try
                Return New FontFamily(ApplicationFontBaseUri, reference)
            Catch ex As Exception
                Trace.WriteLine(String.Format(CultureInfo.InvariantCulture,
                                              "FontManager fallback: failed to compose font family {0}. {1}",
                                              reference,
                                              ex.Message))
                Return fallback
            End Try
        End Function

        Private Shared Function HasBundledFontResources(normalizedDirectory As String) As Boolean
            Dim directoryReference = "./" & normalizedDirectory

            Try
                Return Fonts.GetFontFamilies(ApplicationFontBaseUri, directoryReference).Any()
            Catch ex As Exception
                Trace.WriteLine(String.Format(CultureInfo.InvariantCulture,
                                              "FontManager fallback: failed to enumerate bundled fonts under {0}. {1}",
                                              normalizedDirectory,
                                              ex.Message))
                Return False
            End Try
        End Function

        Private Shared Function NormalizeFontDirectory(relativeDirectory As String) As String
            Dim normalized = relativeDirectory.Replace("\"c, "/"c).Trim("/"c)

            If Not normalized.EndsWith("/", StringComparison.Ordinal) Then
                normalized &= "/"
            End If

            Return normalized
        End Function

        Private Shared Function ShouldWriteDiagnostics() As Boolean
            Return String.Equals(Environment.GetEnvironmentVariable("CLIKSYNC_FONT_DIAGNOSTICS"),
                                 "1",
                                 StringComparison.Ordinal)
        End Function

        Private Shared Sub WriteDiagnostics(displaySans As FontFamily,
                                            listDisplay As FontFamily,
                                            body As FontFamily,
                                            meta As FontFamily,
                                            microUi As FontFamily)
            Try
                Dim lines As New List(Of String) From {
                    "Timestamp=" & DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    "Culture=" & LocalizationManager.Instance.CurrentCulture.Name
                }

                AppendDiagnostics(lines, "Font.DisplaySans", displaySans)
                AppendDiagnostics(lines, "Font.ListDisplay", listDisplay)
                AppendDiagnostics(lines, "Font.Body", body)
                AppendDiagnostics(lines, "Font.Meta", meta)
                AppendDiagnostics(lines, "Font.MicroUi", microUi)

                Dim outputPath = Path.Combine(AppContext.BaseDirectory, "font-diagnostics.txt")
                File.WriteAllLines(outputPath, lines)
            Catch ex As Exception
                Trace.WriteLine("FontManager diagnostics failed: " & ex.Message)
            End Try
        End Sub

        Private Shared Sub AppendDiagnostics(lines As ICollection(Of String), key As String, family As FontFamily)
            If lines Is Nothing Then
                Return
            End If

            If family Is Nothing Then
                lines.Add(key & ".Source=<null>")
                Return
            End If

            lines.Add(key & ".Source=" & family.Source)

            Dim typeface As New Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
            Dim glyph As GlyphTypeface = Nothing
            If typeface.TryGetGlyphTypeface(glyph) AndAlso glyph IsNot Nothing Then
                Dim glyphFamily = glyph.FamilyNames.FirstOrDefault(Function(entry) entry.Key.IetfLanguageTag = "en-US").Value
                If String.IsNullOrWhiteSpace(glyphFamily) Then
                    glyphFamily = glyph.FamilyNames.Values.FirstOrDefault()
                End If

                lines.Add(key & ".GlyphFamily=" & glyphFamily)
            Else
                lines.Add(key & ".GlyphFamily=<unresolved>")
            End If
        End Sub
    End Class
End Namespace
