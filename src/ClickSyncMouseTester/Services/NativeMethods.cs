using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClickSyncMouseTester.Services;

internal sealed class NativeMethods
{
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;

        public ushort usUsage;

        public uint dwFlags;

        public nint hwndTarget;
    }

    public struct RAWINPUTHEADER
    {
        public uint dwType;

        public uint dwSize;

        public nint hDevice;

        public nint wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RAWMOUSEBUTTONS
    {
        [FieldOffset(0)]
        public uint ulButtons;

        [FieldOffset(0)]
        public ushort usButtonFlags;

        [FieldOffset(2)]
        public ushort usButtonData;
    }

    public struct RAWMOUSE
    {
        public ushort usFlags;

        public RAWMOUSEBUTTONS buttons;

        public uint ulRawButtons;

        public int lLastX;

        public int lLastY;

        public uint ulExtraInformation;
    }

    public struct RAWKEYBOARD
    {
        public ushort MakeCode;

        public ushort Flags;

        public ushort Reserved;

        public ushort VKey;

        public uint Message;

        public uint ExtraInformation;
    }

    public struct RAWINPUT
    {
        public RAWINPUTHEADER header;

        public RAWMOUSE mouse;
    }

    public struct RAWINPUTKEYBOARD
    {
        public RAWINPUTHEADER header;

        public RAWKEYBOARD keyboard;
    }

    public struct RAWINPUTDEVICELIST
    {
        public nint hDevice;

        public uint dwType;
    }

    public struct RID_DEVICE_INFO_MOUSE
    {
        public uint dwId;

        public uint dwNumberOfButtons;

        public uint dwSampleRate;

        public bool fHasHorizontalWheel;
    }

    public struct RID_DEVICE_INFO
    {
        public uint cbSize;

        public uint dwType;

        public RID_DEVICE_INFO_MOUSE mouse;
    }

    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;

        public Guid InterfaceClassGuid;

        public int Flags;

        public nint Reserved;
    }

    public struct SP_DEVINFO_DATA
    {
        public int cbSize;

        public Guid ClassGuid;

        public int DevInst;

        public nint Reserved;
    }

    public struct DEVPROPKEY
    {
        public Guid fmtid;

        public uint pid;
    }

    public struct POINT
    {
        public int X;

        public int Y;
    }

    public struct MINMAXINFO
    {
        public POINT ptReserved;

        public POINT ptMaxSize;

        public POINT ptMaxPosition;

        public POINT ptMinTrackSize;

        public POINT ptMaxTrackSize;
    }

    public struct RECT
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;

        public RECT rcMonitor;

        public RECT rcWork;

        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;

        public ushort dmDriverVersion;

        public ushort dmSize;

        public ushort dmDriverExtra;

        public uint dmFields;

        public short dmOrientation;

        public short dmPaperSize;

        public short dmPaperLength;

        public short dmPaperWidth;

        public short dmScale;

        public short dmCopies;

        public short dmDefaultSource;

        public short dmPrintQuality;

        public short dmColor;

        public short dmDuplex;

        public short dmYResolution;

        public short dmTTOption;

        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;

        public uint dmBitsPerPel;

        public uint dmPelsWidth;

        public uint dmPelsHeight;

        public uint dmDisplayFlags;

        public uint dmDisplayFrequency;

        public uint dmICMMethod;

        public uint dmICMIntent;

        public uint dmMediaType;

        public uint dmDitherType;

        public uint dmReserved1;

        public uint dmReserved2;

        public uint dmPanningWidth;

        public uint dmPanningHeight;
    }

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    public const int WM_DISPLAYCHANGE = 126;

    public const int WM_DPICHANGED = 736;

    public const int WM_GETMINMAXINFO = 36;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public const int DWMWA_BORDER_COLOR = 34;

    public const int DWMWA_CAPTION_COLOR = 35;

    public const int DWMWA_TEXT_COLOR = 36;

    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_DEFAULT = 0;

    public const int DWMWCP_DONOTROUND = 1;

    public const int DWMWCP_ROUND = 2;

    public const int DWMWCP_ROUNDSMALL = 3;

    public const int DWMSBT_MAINWINDOW = 2;

    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_DLGMODALFRAME = 1;

    public const int WM_SETICON = 128;

    public const int ICON_SMALL = 0;

    public const int ICON_BIG = 1;

    public const int ICON_SMALL2 = 2;

    public const uint SWP_NOSIZE = 1u;

    public const uint SWP_NOMOVE = 2u;

    public const uint SWP_NOZORDER = 4u;

    public const uint SWP_NOACTIVATE = 16u;

    public const uint SWP_FRAMECHANGED = 32u;

    public const uint MONITOR_DEFAULTTONEAREST = 2u;

    public const int ENUM_CURRENT_SETTINGS = -1;

    public const int CCHDEVICENAME = 32;

    public const int CCHFORMNAME = 32;

    public const int WM_INPUT = 255;

    public const int WM_INPUT_DEVICE_CHANGE = 254;

    public const uint RIM_TYPEMOUSE = 0u;

    public const uint RIM_TYPEKEYBOARD = 1u;

    public const uint RID_INPUT = 268435459u;

    public const uint RIDI_DEVICENAME = 536870919u;

    public const uint RIDI_DEVICEINFO = 536870923u;

    public const uint RIDEV_INPUTSINK = 256u;

    public const uint RIDEV_DEVNOTIFY = 8192u;

    public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 1;

    public const ushort RI_MOUSE_LEFT_BUTTON_UP = 2;

    public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 4;

    public const ushort RI_MOUSE_RIGHT_BUTTON_UP = 8;

    public const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 16;

    public const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 32;

    public const ushort RI_MOUSE_BUTTON_4_DOWN = 64;

    public const ushort RI_MOUSE_BUTTON_4_UP = 128;

    public const ushort RI_MOUSE_BUTTON_5_DOWN = 256;

    public const ushort RI_MOUSE_BUTTON_5_UP = 512;

    public const ushort RI_MOUSE_WHEEL = 1024;

    public const ushort RI_MOUSE_HWHEEL = 2048;

    public const ushort RI_KEY_MAKE = 0;

    public const ushort RI_KEY_BREAK = 1;

    public const ushort RI_KEY_E0 = 2;

    public const ushort RI_KEY_E1 = 4;

    public const ushort KEYBOARD_OVERRUN_MAKE_CODE = 255;

    public const uint MAPVK_VK_TO_VSC_EX = 4u;

    public const int GIDC_ARRIVAL = 1;

    public const int GIDC_REMOVAL = 2;

    public const uint DIGCF_PRESENT = 2u;

    public const uint DIGCF_DEVICEINTERFACE = 16u;

    public const uint SPDRP_DEVICEDESC = 0u;

    public const uint SPDRP_FRIENDLYNAME = 12u;

    public const int ERROR_NO_MORE_ITEMS = 259;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint InvalidRawInputResult = uint.MaxValue;

    public const int WS_POPUP = int.MinValue;

    public const int WS_EX_TOOLWINDOW = 128;

    public const uint FILE_SHARE_READ = 1u;

    public const uint FILE_SHARE_WRITE = 2u;

    public const uint OPEN_EXISTING = 3u;

    public const uint SPI_GETFONTSMOOTHING = 74u;

    public const uint SPI_GETFONTSMOOTHINGTYPE = 8202u;

    public const uint SPI_GETFONTSMOOTHINGCONTRAST = 8204u;

    public const uint SPI_GETFONTSMOOTHINGORIENTATION = 8210u;

    public const uint FE_FONTSMOOTHINGSTANDARD = 1u;

    public const uint FE_FONTSMOOTHINGCLEARTYPE = 2u;

    public const uint FE_FONTSMOOTHINGORIENTATIONBGR = 0u;

    public const uint FE_FONTSMOOTHINGORIENTATIONRGB = 1u;

    public static readonly Guid GuidDevInterfaceMouse = new Guid("378DE44C-56EF-11D1-BC8C-00A0C91405DD");

    public static readonly nint InvalidDeviceInfoSet = new IntPtr(-1);

    public static readonly nint InvalidFileHandle = new IntPtr(-1);

    private NativeMethods()
    {
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc callback, nint dwData);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static extern int GetWindowLong(nint hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static extern int SetWindowLong(nint hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hwnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetKeyNameText(int lParam, StringBuilder lpString, int nSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);

    public static bool TrySetImmersiveDarkMode(nint hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        int pvAttribute = (enabled ? 1 : 0);
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref pvAttribute, Marshal.SizeOf(typeof(int))) == 0)
        {
            return true;
        }
        return DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref pvAttribute, Marshal.SizeOf(typeof(int))) == 0;
    }

    public static bool TrySetWindowColorAttribute(nint hwnd, int attribute, int colorRef)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        return DwmSetWindowAttribute(hwnd, attribute, ref colorRef, Marshal.SizeOf(typeof(int))) == 0;
    }

    public static bool TrySetWindowCornerPreference(nint hwnd, int preference)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, Marshal.SizeOf(typeof(int))) == 0;
    }

    public static bool TrySetSystemBackdropType(nint hwnd, int backdropType)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, Marshal.SizeOf(typeof(int))) == 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint deviceCount, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(nint hRawInput, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetRawInputDeviceList(nint deviceList, ref uint deviceCount, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    public static extern uint GetRawInputDeviceInfo(nint deviceHandle, uint command, nint data, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    public static extern uint GetRawInputDeviceInfo(nint deviceHandle, uint command, StringBuilder data, ref uint size);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SetupDiGetClassDevs(ref Guid classGuid, string enumeratorValue, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, nint deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceRegistryProperty(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint propertyValue, nint propertyRegDataType, nint propertyBuffer, uint propertyBufferSize, ref uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceProperty(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey, ref uint propertyType, nint propertyBuffer, uint propertyBufferSize, ref uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    public static extern nint CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetProductString(nint hidDeviceObject, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(nint hidDeviceObject, [Out] byte[] buffer, int bufferLength);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClipCursor(ref RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    public static extern bool SetClipCursor(ref RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    public static extern bool ClearClipCursor(nint lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern int ShowCursor(bool show);

    public static bool TryGetWindowDisplayRefreshRate(nint windowHandle, ref double refreshRateHz)
    {
        refreshRateHz = 0.0;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }
        nint monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }
        MONITORINFOEX monitorInfo = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf(typeof(MONITORINFOEX))
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }
        DEVMODE devMode = new DEVMODE
        {
            dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE))
        };
        if (!EnumDisplaySettings(monitorInfo.szDevice, ENUM_CURRENT_SETTINGS, ref devMode))
        {
            return false;
        }
        double displayRefreshRateHz = devMode.dmDisplayFrequency;
        if (double.IsNaN(displayRefreshRateHz) || double.IsInfinity(displayRefreshRateHz) || displayRefreshRateHz <= 1.0)
        {
            return false;
        }
        refreshRateHz = displayRefreshRateHz;
        return true;
    }

    public static bool TryGetFontSmoothingState(ref bool enabled, ref uint smoothingType, ref uint contrast, ref uint orientation)
    {
        enabled = false;
        smoothingType = 0u;
        contrast = 0u;
        orientation = 0u;
        uint value = 0u;
        if (!SystemParametersInfo(SPI_GETFONTSMOOTHING, 0u, ref value, 0u))
        {
            return false;
        }
        enabled = value != 0;
        if (enabled)
        {
            SystemParametersInfo(SPI_GETFONTSMOOTHINGTYPE, 0u, ref smoothingType, 0u);
            SystemParametersInfo(SPI_GETFONTSMOOTHINGCONTRAST, 0u, ref contrast, 0u);
            SystemParametersInfo(SPI_GETFONTSMOOTHINGORIENTATION, 0u, ref orientation, 0u);
        }
        return true;
    }
}





