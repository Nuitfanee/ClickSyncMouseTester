Imports System.Runtime.InteropServices
Imports System.Text

Namespace Services
    Friend NotInheritable Class NativeMethods
        Private Sub New()
        End Sub

        Public Const WM_DISPLAYCHANGE As Integer = &H7E
        Public Const WM_DPICHANGED As Integer = &H2E0
        Public Const WM_GETMINMAXINFO As Integer = &H24
        Public Const DWMWA_USE_IMMERSIVE_DARK_MODE As Integer = 20
        Public Const DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 As Integer = 19
        Public Const DWMWA_WINDOW_CORNER_PREFERENCE As Integer = 33
        Public Const DWMWA_BORDER_COLOR As Integer = 34
        Public Const DWMWA_CAPTION_COLOR As Integer = 35
        Public Const DWMWA_TEXT_COLOR As Integer = 36
        Public Const DWMWA_SYSTEMBACKDROP_TYPE As Integer = 38
        Public Const DWMWCP_DEFAULT As Integer = 0
        Public Const DWMWCP_DONOTROUND As Integer = 1
        Public Const DWMWCP_ROUND As Integer = 2
        Public Const DWMWCP_ROUNDSMALL As Integer = 3
        Public Const DWMSBT_MAINWINDOW As Integer = 2
        Public Const GWL_EXSTYLE As Integer = -20
        Public Const WS_EX_DLGMODALFRAME As Integer = &H1
        Public Const WM_SETICON As Integer = &H80
        Public Const ICON_SMALL As Integer = 0
        Public Const ICON_BIG As Integer = 1
        Public Const ICON_SMALL2 As Integer = 2
        Public Const SWP_NOSIZE As UInteger = &H1UI
        Public Const SWP_NOMOVE As UInteger = &H2UI
        Public Const SWP_NOZORDER As UInteger = &H4UI
        Public Const SWP_NOACTIVATE As UInteger = &H10UI
        Public Const SWP_FRAMECHANGED As UInteger = &H20UI
        Public Const MONITOR_DEFAULTTONEAREST As UInteger = &H2UI
        Public Const ENUM_CURRENT_SETTINGS As Integer = -1
        Public Const CCHDEVICENAME As Integer = 32
        Public Const CCHFORMNAME As Integer = 32
        Public Const WM_INPUT As Integer = &HFF
        Public Const WM_INPUT_DEVICE_CHANGE As Integer = &HFE
        Public Const RIM_TYPEMOUSE As UInteger = 0UI
        Public Const RIM_TYPEKEYBOARD As UInteger = 1UI
        Public Const RID_INPUT As UInteger = &H10000003UI
        Public Const RIDI_DEVICENAME As UInteger = &H20000007UI
        Public Const RIDI_DEVICEINFO As UInteger = &H2000000BUI
        Public Const RIDEV_INPUTSINK As UInteger = &H100UI
        Public Const RIDEV_DEVNOTIFY As UInteger = &H2000UI
        Public Const RI_MOUSE_LEFT_BUTTON_DOWN As UShort = &H1US
        Public Const RI_MOUSE_LEFT_BUTTON_UP As UShort = &H2US
        Public Const RI_MOUSE_RIGHT_BUTTON_DOWN As UShort = &H4US
        Public Const RI_MOUSE_RIGHT_BUTTON_UP As UShort = &H8US
        Public Const RI_MOUSE_MIDDLE_BUTTON_DOWN As UShort = &H10US
        Public Const RI_MOUSE_MIDDLE_BUTTON_UP As UShort = &H20US
        Public Const RI_MOUSE_BUTTON_4_DOWN As UShort = &H40US
        Public Const RI_MOUSE_BUTTON_4_UP As UShort = &H80US
        Public Const RI_MOUSE_BUTTON_5_DOWN As UShort = &H100US
        Public Const RI_MOUSE_BUTTON_5_UP As UShort = &H200US
        Public Const RI_MOUSE_WHEEL As UShort = &H400US
        Public Const RI_MOUSE_HWHEEL As UShort = &H800US
        Public Const RI_KEY_MAKE As UShort = 0US
        Public Const RI_KEY_BREAK As UShort = &H1US
        Public Const RI_KEY_E0 As UShort = &H2US
        Public Const RI_KEY_E1 As UShort = &H4US
        Public Const KEYBOARD_OVERRUN_MAKE_CODE As UShort = &HFFUS
        Public Const MAPVK_VK_TO_VSC_EX As UInteger = 4UI
        Public Const GIDC_ARRIVAL As Integer = 1
        Public Const GIDC_REMOVAL As Integer = 2
        Public Const DIGCF_PRESENT As UInteger = &H2UI
        Public Const DIGCF_DEVICEINTERFACE As UInteger = &H10UI
        Public Const SPDRP_DEVICEDESC As UInteger = 0UI
        Public Const SPDRP_FRIENDLYNAME As UInteger = &HCUI
        Public Const ERROR_NO_MORE_ITEMS As Integer = 259
        Public Const ERROR_INSUFFICIENT_BUFFER As Integer = 122
        Public Const InvalidRawInputResult As UInteger = &HFFFFFFFFUI
        Public Const WS_POPUP As Integer = -2147483648
        Public Const WS_EX_TOOLWINDOW As Integer = &H80
        Public Const FILE_SHARE_READ As UInteger = &H1UI
        Public Const FILE_SHARE_WRITE As UInteger = &H2UI
        Public Const OPEN_EXISTING As UInteger = 3UI
        Public Const SPI_GETFONTSMOOTHING As UInteger = &H4AUI
        Public Const SPI_GETFONTSMOOTHINGTYPE As UInteger = &H200AUI
        Public Const SPI_GETFONTSMOOTHINGCONTRAST As UInteger = &H200CUI
        Public Const SPI_GETFONTSMOOTHINGORIENTATION As UInteger = &H2012UI
        Public Const FE_FONTSMOOTHINGSTANDARD As UInteger = &H1UI
        Public Const FE_FONTSMOOTHINGCLEARTYPE As UInteger = &H2UI
        Public Const FE_FONTSMOOTHINGORIENTATIONBGR As UInteger = &H0UI
        Public Const FE_FONTSMOOTHINGORIENTATIONRGB As UInteger = &H1UI

        Public Shared ReadOnly GuidDevInterfaceMouse As New Guid("378DE44C-56EF-11D1-BC8C-00A0C91405DD")
        Public Shared ReadOnly InvalidDeviceInfoSet As New IntPtr(-1)
        Public Shared ReadOnly InvalidFileHandle As New IntPtr(-1)

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWINPUTDEVICE
            Public usUsagePage As UShort
            Public usUsage As UShort
            Public dwFlags As UInteger
            Public hwndTarget As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWINPUTHEADER
            Public dwType As UInteger
            Public dwSize As UInteger
            Public hDevice As IntPtr
            Public wParam As IntPtr
        End Structure

        <StructLayout(LayoutKind.Explicit)>
        Public Structure RAWMOUSEBUTTONS
            <FieldOffset(0)>
            Public ulButtons As UInteger

            <FieldOffset(0)>
            Public usButtonFlags As UShort

            <FieldOffset(2)>
            Public usButtonData As UShort
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWMOUSE
            Public usFlags As UShort
            Public buttons As RAWMOUSEBUTTONS
            Public ulRawButtons As UInteger
            Public lLastX As Integer
            Public lLastY As Integer
            Public ulExtraInformation As UInteger
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWKEYBOARD
            Public MakeCode As UShort
            Public Flags As UShort
            Public Reserved As UShort
            Public VKey As UShort
            Public Message As UInteger
            Public ExtraInformation As UInteger
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWINPUT
            Public header As RAWINPUTHEADER
            Public mouse As RAWMOUSE
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWINPUTKEYBOARD
            Public header As RAWINPUTHEADER
            Public keyboard As RAWKEYBOARD
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RAWINPUTDEVICELIST
            Public hDevice As IntPtr
            Public dwType As UInteger
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RID_DEVICE_INFO_MOUSE
            Public dwId As UInteger
            Public dwNumberOfButtons As UInteger
            Public dwSampleRate As UInteger
            Public fHasHorizontalWheel As Boolean
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RID_DEVICE_INFO
            Public cbSize As UInteger
            Public dwType As UInteger
            Public mouse As RID_DEVICE_INFO_MOUSE
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure SP_DEVICE_INTERFACE_DATA
            Public cbSize As Integer
            Public InterfaceClassGuid As Guid
            Public Flags As Integer
            Public Reserved As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure SP_DEVINFO_DATA
            Public cbSize As Integer
            Public ClassGuid As Guid
            Public DevInst As Integer
            Public Reserved As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure POINT
            Public X As Integer
            Public Y As Integer
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure MINMAXINFO
            Public ptReserved As POINT
            Public ptMaxSize As POINT
            Public ptMaxPosition As POINT
            Public ptMinTrackSize As POINT
            Public ptMaxTrackSize As POINT
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Public Structure RECT
            Public Left As Integer
            Public Top As Integer
            Public Right As Integer
            Public Bottom As Integer
        End Structure

        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Public Structure MONITORINFOEX
            Public cbSize As Integer
            Public rcMonitor As RECT
            Public rcWork As RECT
            Public dwFlags As UInteger
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=CCHDEVICENAME)>
            Public szDevice As String
        End Structure

        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Public Structure DEVMODE
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=CCHDEVICENAME)>
            Public dmDeviceName As String
            Public dmSpecVersion As UShort
            Public dmDriverVersion As UShort
            Public dmSize As UShort
            Public dmDriverExtra As UShort
            Public dmFields As UInteger
            Public dmOrientation As Short
            Public dmPaperSize As Short
            Public dmPaperLength As Short
            Public dmPaperWidth As Short
            Public dmScale As Short
            Public dmCopies As Short
            Public dmDefaultSource As Short
            Public dmPrintQuality As Short
            Public dmColor As Short
            Public dmDuplex As Short
            Public dmYResolution As Short
            Public dmTTOption As Short
            Public dmCollate As Short
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=CCHFORMNAME)>
            Public dmFormName As String
            Public dmLogPixels As UShort
            Public dmBitsPerPel As UInteger
            Public dmPelsWidth As UInteger
            Public dmPelsHeight As UInteger
            Public dmDisplayFlags As UInteger
            Public dmDisplayFrequency As UInteger
            Public dmICMMethod As UInteger
            Public dmICMIntent As UInteger
            Public dmMediaType As UInteger
            Public dmDitherType As UInteger
            Public dmReserved1 As UInteger
            Public dmReserved2 As UInteger
            Public dmPanningWidth As UInteger
            Public dmPanningHeight As UInteger
        End Structure

        Public Delegate Function MonitorEnumProc(hMonitor As IntPtr,
                                                 hdcMonitor As IntPtr,
                                                 ByRef lprcMonitor As RECT,
                                                 dwData As IntPtr) As Boolean

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function MonitorFromWindow(hwnd As IntPtr, flags As UInteger) As IntPtr
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function EnumDisplayMonitors(hdc As IntPtr,
                                                   lprcClip As IntPtr,
                                                   callback As MonitorEnumProc,
                                                   dwData As IntPtr) As Boolean
        End Function

        <DllImport("dwmapi.dll", PreserveSig:=True)>
        Private Shared Function DwmSetWindowAttribute(hwnd As IntPtr, dwAttribute As Integer, ByRef pvAttribute As Integer, cbAttribute As Integer) As Integer
        End Function

        <DllImport("user32.dll", EntryPoint:="GetWindowLongW", SetLastError:=True)>
        Public Shared Function GetWindowLong(hwnd As IntPtr, nIndex As Integer) As Integer
        End Function

        <DllImport("user32.dll", EntryPoint:="SetWindowLongW", SetLastError:=True)>
        Public Shared Function SetWindowLong(hwnd As IntPtr, nIndex As Integer, dwNewLong As Integer) As Integer
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Public Shared Function SendMessage(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function SetWindowPos(hwnd As IntPtr, hWndInsertAfter As IntPtr, x As Integer, y As Integer, cx As Integer, cy As Integer, flags As UInteger) As Boolean
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function GetKeyNameText(lParam As Integer, lpString As StringBuilder, nSize As Integer) As Integer
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function MapVirtualKey(code As UInteger, mapType As UInteger) As UInteger
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function GetMonitorInfo(hMonitor As IntPtr, ByRef monitorInfo As MONITORINFOEX) As Boolean
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function EnumDisplaySettings(deviceName As String, modeNum As Integer, ByRef devMode As DEVMODE) As Boolean
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function SystemParametersInfo(action As UInteger, param As UInteger, ByRef value As UInteger, winIni As UInteger) As Boolean
        End Function

        Public Shared Function TrySetImmersiveDarkMode(hwnd As IntPtr, enabled As Boolean) As Boolean
            If hwnd = IntPtr.Zero Then
                Return False
            End If

            Dim value = If(enabled, 1, 0)
            If DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, value, Marshal.SizeOf(GetType(Integer))) = 0 Then
                Return True
            End If

            Return DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, value, Marshal.SizeOf(GetType(Integer))) = 0
        End Function

        Public Shared Function TrySetWindowColorAttribute(hwnd As IntPtr, attribute As Integer, colorRef As Integer) As Boolean
            If hwnd = IntPtr.Zero Then
                Return False
            End If

            Return DwmSetWindowAttribute(hwnd, attribute, colorRef, Marshal.SizeOf(GetType(Integer))) = 0
        End Function

        Public Shared Function TrySetWindowCornerPreference(hwnd As IntPtr, preference As Integer) As Boolean
            If hwnd = IntPtr.Zero Then
                Return False
            End If

            Return DwmSetWindowAttribute(hwnd,
                                         DWMWA_WINDOW_CORNER_PREFERENCE,
                                         preference,
                                         Marshal.SizeOf(GetType(Integer))) = 0
        End Function

        Public Shared Function TrySetSystemBackdropType(hwnd As IntPtr, backdropType As Integer) As Boolean
            If hwnd = IntPtr.Zero Then
                Return False
            End If

            Return DwmSetWindowAttribute(hwnd,
                                         DWMWA_SYSTEMBACKDROP_TYPE,
                                         backdropType,
                                         Marshal.SizeOf(GetType(Integer))) = 0
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function RegisterRawInputDevices(<[In]> devices() As RAWINPUTDEVICE, deviceCount As UInteger, size As UInteger) As Boolean
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function GetRawInputData(hRawInput As IntPtr, command As UInteger, data As IntPtr, ByRef size As UInteger, headerSize As UInteger) As UInteger
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function GetRawInputDeviceList(deviceList As IntPtr, ByRef deviceCount As UInteger, size As UInteger) As Integer
        End Function

        <DllImport("user32.dll", EntryPoint:="GetRawInputDeviceInfoW", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function GetRawInputDeviceInfo(deviceHandle As IntPtr, command As UInteger, data As IntPtr, ByRef size As UInteger) As UInteger
        End Function

        <DllImport("user32.dll", EntryPoint:="GetRawInputDeviceInfoW", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function GetRawInputDeviceInfo(deviceHandle As IntPtr, command As UInteger, data As StringBuilder, ByRef size As UInteger) As UInteger
        End Function

        <DllImport("setupapi.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function SetupDiGetClassDevs(ByRef classGuid As Guid, enumeratorValue As String, hwndParent As IntPtr, flags As UInteger) As IntPtr
        End Function

        <DllImport("setupapi.dll", SetLastError:=True)>
        Public Shared Function SetupDiEnumDeviceInterfaces(deviceInfoSet As IntPtr, deviceInfoData As IntPtr, ByRef interfaceClassGuid As Guid, memberIndex As UInteger, ByRef deviceInterfaceData As SP_DEVICE_INTERFACE_DATA) As Boolean
        End Function

        <DllImport("setupapi.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function SetupDiGetDeviceInterfaceDetail(deviceInfoSet As IntPtr, ByRef deviceInterfaceData As SP_DEVICE_INTERFACE_DATA, deviceInterfaceDetailData As IntPtr, deviceInterfaceDetailDataSize As UInteger, ByRef requiredSize As UInteger, ByRef deviceInfoData As SP_DEVINFO_DATA) As Boolean
        End Function

        <DllImport("setupapi.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function SetupDiGetDeviceRegistryProperty(deviceInfoSet As IntPtr, ByRef deviceInfoData As SP_DEVINFO_DATA, propertyValue As UInteger, propertyRegDataType As IntPtr, propertyBuffer As IntPtr, propertyBufferSize As UInteger, ByRef requiredSize As UInteger) As Boolean
        End Function

        <DllImport("setupapi.dll", SetLastError:=True)>
        Public Shared Function SetupDiDestroyDeviceInfoList(deviceInfoSet As IntPtr) As Boolean
        End Function

        <DllImport("kernel32.dll", EntryPoint:="CreateFileW", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Public Shared Function CreateFile(fileName As String, desiredAccess As UInteger, shareMode As UInteger, securityAttributes As IntPtr, creationDisposition As UInteger, flagsAndAttributes As UInteger, templateFile As IntPtr) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Public Shared Function CloseHandle(handle As IntPtr) As Boolean
        End Function

        <DllImport("hid.dll", SetLastError:=True)>
        Public Shared Function HidD_GetProductString(hidDeviceObject As IntPtr, <Out> buffer As Byte(), bufferLength As Integer) As Boolean
        End Function

        <DllImport("hid.dll", SetLastError:=True)>
        Public Shared Function HidD_GetManufacturerString(hidDeviceObject As IntPtr, <Out> buffer As Byte(), bufferLength As Integer) As Boolean
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function GetClipCursor(ByRef lpRect As RECT) As Boolean
        End Function

        <DllImport("user32.dll", EntryPoint:="ClipCursor", SetLastError:=True)>
        Public Shared Function SetClipCursor(ByRef lpRect As RECT) As Boolean
        End Function

        <DllImport("user32.dll", EntryPoint:="ClipCursor", SetLastError:=True)>
        Public Shared Function ClearClipCursor(lpRect As IntPtr) As Boolean
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Shared Function SetCursorPos(x As Integer, y As Integer) As Boolean
        End Function

        <DllImport("user32.dll")>
        Public Shared Function ShowCursor(show As Boolean) As Integer
        End Function

        Public Shared Function TryGetWindowDisplayRefreshRate(windowHandle As IntPtr, ByRef refreshRateHz As Double) As Boolean
            refreshRateHz = 0.0

            If windowHandle = IntPtr.Zero Then
                Return False
            End If

            Dim monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST)
            If monitorHandle = IntPtr.Zero Then
                Return False
            End If

            Dim monitorInfo As New MONITORINFOEX With {
                .cbSize = Marshal.SizeOf(GetType(MONITORINFOEX))
            }

            If Not GetMonitorInfo(monitorHandle, monitorInfo) Then
                Return False
            End If

            Dim devMode As New DEVMODE With {
                .dmSize = CUShort(Marshal.SizeOf(GetType(DEVMODE)))
            }

            If Not EnumDisplaySettings(monitorInfo.szDevice, ENUM_CURRENT_SETTINGS, devMode) Then
                Return False
            End If

            Dim frequency = CDbl(devMode.dmDisplayFrequency)
            If Double.IsNaN(frequency) OrElse Double.IsInfinity(frequency) OrElse frequency <= 1.0 Then
                Return False
            End If

            refreshRateHz = frequency
            Return True
        End Function

        Public Shared Function TryGetFontSmoothingState(ByRef enabled As Boolean,
                                                        ByRef smoothingType As UInteger,
                                                        ByRef contrast As UInteger,
                                                        ByRef orientation As UInteger) As Boolean
            enabled = False
            smoothingType = 0UI
            contrast = 0UI
            orientation = 0UI

            Dim smoothingEnabled As UInteger = 0UI
            If Not SystemParametersInfo(SPI_GETFONTSMOOTHING, 0UI, smoothingEnabled, 0UI) Then
                Return False
            End If

            enabled = smoothingEnabled <> 0UI

            If enabled Then
                SystemParametersInfo(SPI_GETFONTSMOOTHINGTYPE, 0UI, smoothingType, 0UI)
                SystemParametersInfo(SPI_GETFONTSMOOTHINGCONTRAST, 0UI, contrast, 0UI)
                SystemParametersInfo(SPI_GETFONTSMOOTHINGORIENTATION, 0UI, orientation, 0UI)
            End If

            Return True
        End Function
    End Class
End Namespace
