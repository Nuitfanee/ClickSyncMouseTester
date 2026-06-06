#define TRACE
using ClickSyncMouseTester.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClickSyncMouseTester.Services;

[SupportedOSPlatform("windows")]
public class RawInputBroker : IRawInputBroker
{
    private const long DeviceCacheRefreshThrottleMilliseconds = 1000L;
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort KeyboardUsage = 0x06;
    private const int HiddenWindowPosition = -32000;
    private const int HiddenWindowSize = 1;
    private const int KeyNameBufferCapacity = 64;
    private const int SignedWheelDeltaThreshold = short.MaxValue + 1;
    private const int SignedWheelDeltaRange = ushort.MaxValue + 1;
    private const int MousePacketDispatcherCapacity = 65536;
    private static readonly int RawInputHeaderDwTypeOffset = (int)Marshal.OffsetOf<NativeMethods.RAWINPUTHEADER>(nameof(NativeMethods.RAWINPUTHEADER.dwType));
    private static readonly int RawInputHeaderHDeviceOffset = (int)Marshal.OffsetOf<NativeMethods.RAWINPUTHEADER>(nameof(NativeMethods.RAWINPUTHEADER.hDevice));
    private static readonly int RawInputMouseOffset = (int)Marshal.OffsetOf<NativeMethods.RAWINPUT>(nameof(NativeMethods.RAWINPUT.mouse));
    private static readonly int RawInputKeyboardOffset = (int)Marshal.OffsetOf<NativeMethods.RAWINPUTKEYBOARD>(nameof(NativeMethods.RAWINPUTKEYBOARD.keyboard));

    private readonly object _syncRoot;
    private readonly ManualResetEventSlim _ready;
    private readonly RawMouseDeviceDescriptorFactory _deviceDescriptorFactory;
    private readonly RawMouseDeviceActivityProfiler _deviceActivityProfiler;
    private readonly SerialEventDispatcher<RawMousePacketEventArgs> _mousePacketDispatcher;
    private readonly SerialEventDispatcher<RawMouseButtonInputEventArgs> _mouseButtonDispatcher;
    private readonly SerialEventDispatcher<RawMouseWheelInputEventArgs> _mouseWheelDispatcher;
    private readonly SerialEventDispatcher<RawKeyboardInputEventArgs> _keyboardDispatcher;
    private readonly SerialEventDispatcher<EventArgs> _mouseDevicesChangedDispatcher;
    private readonly Thread _thread;

    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private Exception? _initializationException;
    private bool _disposed;
    private Dictionary<nint, RawMouseEndpointState> _endpointsByHandle;
    private Dictionary<string, RawMouseDeviceInfo> _devicesById;
    private Dictionary<string, long> _captureSequencesByDevice;
    private nint _inputBuffer;
    private int _inputBufferSize;
    private long _captureSequence;
    private int _deviceRefreshPending;
    private int _mouseDevicesChangedNotificationPending;
    private long _lastDeviceCacheRefreshTick;

    public event EventHandler<RawMousePacketEventArgs> MousePacketCaptured;

    public event EventHandler<RawMouseButtonInputEventArgs> MouseButtonInput;

    public event EventHandler<RawMouseWheelInputEventArgs> MouseWheelInput;

    public event EventHandler<RawKeyboardInputEventArgs> KeyboardInput;

    public event EventHandler MouseDevicesChanged;

    private void DispatchMousePacketCaptured(RawMousePacketEventArgs args)
    {
        RecordMousePacketActivity(args?.Packet);
        MousePacketCaptured?.Invoke(this, args);
    }

    private void DispatchMouseButtonInput(RawMouseButtonInputEventArgs args)
    {
        MouseButtonInput?.Invoke(this, args);
    }

    private void DispatchMouseWheelInput(RawMouseWheelInputEventArgs args)
    {
        MouseWheelInput?.Invoke(this, args);
    }

    private void DispatchKeyboardInput(RawKeyboardInputEventArgs args)
    {
        KeyboardInput?.Invoke(this, args);
    }

    private void DispatchMouseDevicesChanged(EventArgs args)
    {
        Interlocked.Exchange(ref _mouseDevicesChangedNotificationPending, 0);
        MouseDevicesChanged?.Invoke(this, args ?? EventArgs.Empty);
    }

    public RawInputBroker()
    {
        _syncRoot = new object();
        _ready = new ManualResetEventSlim(initialState: false);
        _deviceDescriptorFactory = new RawMouseDeviceDescriptorFactory(new RawMouseDeviceMetadataResolver());
        _deviceActivityProfiler = new RawMouseDeviceActivityProfiler();
        _mousePacketDispatcher = new SerialEventDispatcher<RawMousePacketEventArgs>("RawMousePacketEventDispatcher", DispatchMousePacketCaptured, MousePacketDispatcherCapacity);
        _mouseButtonDispatcher = new SerialEventDispatcher<RawMouseButtonInputEventArgs>("RawMouseButtonEventDispatcher", DispatchMouseButtonInput);
        _mouseWheelDispatcher = new SerialEventDispatcher<RawMouseWheelInputEventArgs>("RawMouseWheelEventDispatcher", DispatchMouseWheelInput);
        _keyboardDispatcher = new SerialEventDispatcher<RawKeyboardInputEventArgs>("RawKeyboardEventDispatcher", DispatchKeyboardInput);
        _mouseDevicesChangedDispatcher = new SerialEventDispatcher<EventArgs>("RawMouseDevicesChangedDispatcher", DispatchMouseDevicesChanged);
        _endpointsByHandle = new Dictionary<nint, RawMouseEndpointState>();
        _devicesById = new Dictionary<string, RawMouseDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        _captureSequencesByDevice = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RawInputBrokerThread"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_initializationException != null)
        {
            throw new InvalidOperationException("Failed to initialize the Raw Input broker.", _initializationException);
        }
    }

    public IReadOnlyList<RawMouseDeviceInfo> GetMouseDevices()
    {
        _ready.Wait();
        lock (_syncRoot)
        {
            return _devicesById.Values
                .OrderBy(device => device.IsVirtual)
                .ThenBy(device => device.DisplayName)
                .ThenBy(device => device.DeviceId)
                .ToList();
        }
    }

    public IReadOnlyDictionary<string, RawMouseEndpointActivitySnapshot> GetMouseEndpointActivitySnapshots()
    {
        return _deviceActivityProfiler.CreateSnapshot();
    }

    public void RequestMouseDevicesRefresh(bool force = false)
    {
        _ready.Wait();
        QueueDeviceCacheRefresh(force);
    }

    private void QueueDeviceCacheRefresh(bool force = false)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (_disposed || _initializationException != null || dispatcher == null)
        {
            return;
        }

        long currentTick = Environment.TickCount64;
        long lastRefreshTick = Volatile.Read(ref _lastDeviceCacheRefreshTick);
        bool throttled = !force && lastRefreshTick > 0 && currentTick - lastRefreshTick < DeviceCacheRefreshThrottleMilliseconds;
        if (throttled || Interlocked.CompareExchange(ref _deviceRefreshPending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    RefreshDeviceCache();
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"Raw input broker device refresh failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _deviceRefreshPending, 0);
                }
            }), DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _deviceRefreshPending, 0);
            Trace.TraceError($"Raw input broker failed to queue device refresh: {ex}");
        }
    }

    private void QueueMouseDevicesChangedNotification()
    {
        if (_disposed || Interlocked.CompareExchange(ref _mouseDevicesChangedNotificationPending, 1, 0) != 0)
        {
            return;
        }

        _mouseDevicesChangedDispatcher.Enqueue(EventArgs.Empty);
    }

    private void RecordMousePacketActivity(RawMousePacket packet)
    {
        if (packet == null)
        {
            return;
        }

        RawMouseDeviceActivityProfiler.EndpointActivity activity = _deviceActivityProfiler.GetOrCreateActivity(packet.DeviceId);
        if (activity.RecordPacket(packet.RawCaptureMs, packet.DeltaX, packet.DeltaY, packet.ButtonFlags))
        {
            QueueMouseDevicesChangedNotification();
        }
    }

    private void ThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            HwndSourceParameters parameters = new HwndSourceParameters("RawInputBroker")
            {
                Width = HiddenWindowSize,
                Height = HiddenWindowSize,
                PositionX = HiddenWindowPosition,
                PositionY = HiddenWindowPosition,
                WindowStyle = NativeMethods.WS_POPUP,
                ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            RegisterForRawInput(_source.Handle);
            RefreshDeviceCache(notifyDevicesChanged: false);
        }
        catch (Exception ex)
        {
            _initializationException = ex;
        }
        finally
        {
            _ready.Set();
        }

        if (_initializationException == null)
        {
            Dispatcher.Run();
        }
    }

    private static void RegisterForRawInput(nint targetHandle)
    {
        NativeMethods.RAWINPUTDEVICE[] rawInputDevices = new NativeMethods.RAWINPUTDEVICE[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = GenericDesktopUsagePage,
                usUsage = MouseUsage,
                dwFlags = NativeMethods.RIDEV_INPUTSINK | NativeMethods.RIDEV_DEVNOTIFY,
                hwndTarget = targetHandle
            },
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = GenericDesktopUsagePage,
                usUsage = KeyboardUsage,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = targetHandle
            }
        };
        uint deviceCount = (uint)rawInputDevices.Length;
        uint deviceSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>();
        if (!NativeMethods.RegisterRawInputDevices(rawInputDevices, deviceCount, deviceSize))
        {
            throw new InvalidOperationException("RegisterRawInputDevices failed for the Raw Input broker.");
        }
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WM_INPUT:
                HandleInput(lParam, Stopwatch.GetTimestamp());
                break;
            case NativeMethods.WM_INPUT_DEVICE_CHANGE:
                RefreshDeviceCache();
                break;
        }
        return IntPtr.Zero;
    }

    private void HandleInput(nint rawInputHandle, long timestampTicks)
    {
        uint rawInputSize = 0u;
        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        NativeMethods.GetRawInputData(rawInputHandle, NativeMethods.RID_INPUT, IntPtr.Zero, ref rawInputSize, headerSize);
        if (rawInputSize == 0)
        {
            return;
        }

        nint rawInputBuffer = EnsureInputBuffer((int)rawInputSize);
        if (rawInputBuffer == IntPtr.Zero)
        {
            return;
        }

        uint readSize = rawInputSize;
        if (NativeMethods.GetRawInputData(rawInputHandle, NativeMethods.RID_INPUT, rawInputBuffer, ref readSize, headerSize) == NativeMethods.InvalidRawInputResult)
        {
            return;
        }

        uint inputType = (uint)Marshal.ReadInt32(rawInputBuffer, RawInputHeaderDwTypeOffset);
        nint deviceHandle = Marshal.ReadIntPtr(rawInputBuffer, RawInputHeaderHDeviceOffset);
        double timestampMs = (double)timestampTicks * 1000.0 / Stopwatch.Frequency;
        switch (inputType)
        {
            case NativeMethods.RIM_TYPEMOUSE:
                NativeMethods.RAWMOUSE mouseInput = Marshal.PtrToStructure<NativeMethods.RAWMOUSE>(IntPtr.Add(rawInputBuffer, RawInputMouseOffset));
                HandleMouseInput(deviceHandle, mouseInput, timestampTicks, timestampMs);
                break;
            case NativeMethods.RIM_TYPEKEYBOARD:
                NativeMethods.RAWKEYBOARD keyboardInput = Marshal.PtrToStructure<NativeMethods.RAWKEYBOARD>(IntPtr.Add(rawInputBuffer, RawInputKeyboardOffset));
                HandleKeyboardInput(keyboardInput, timestampMs);
                break;
        }
    }

    private void HandleMouseInput(nint deviceHandle, NativeMethods.RAWMOUSE rawMouse, long timestampTicks, double timestampMs)
    {
        RawMouseEndpointState endpoint = ResolveEndpoint(deviceHandle);
        RawMouseDeviceInfo deviceInfo = endpoint.DeviceInfo;
        ushort buttonFlags = rawMouse.buttons.usButtonFlags;
        long captureSequence = Interlocked.Increment(ref _captureSequence);
        long timingSequence = NextDeviceCaptureSequence(deviceInfo.DeviceId);
        RawMousePacket packet = new RawMousePacket(
            deviceInfo.DeviceId,
            timestampTicks,
            timestampMs,
            captureSequence,
            rawMouse.lLastX,
            rawMouse.lLastY,
            buttonFlags,
            rawMouse.usFlags,
            rawMouse.buttons.usButtonData,
            rawMouse.ulExtraInformation,
            timingSequence);
        _mousePacketDispatcher.Enqueue(new RawMousePacketEventArgs(packet));

        if (buttonFlags == 0)
        {
            return;
        }

        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN, MouseButtonKind.LeftButton, isButtonDown: true, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_LEFT_BUTTON_UP, MouseButtonKind.LeftButton, isButtonDown: false, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN, MouseButtonKind.MiddleButton, isButtonDown: true, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_MIDDLE_BUTTON_UP, MouseButtonKind.MiddleButton, isButtonDown: false, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN, MouseButtonKind.RightButton, isButtonDown: true, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_RIGHT_BUTTON_UP, MouseButtonKind.RightButton, isButtonDown: false, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_BUTTON_5_DOWN, MouseButtonKind.ForwardButton, isButtonDown: true, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_BUTTON_5_UP, MouseButtonKind.ForwardButton, isButtonDown: false, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_BUTTON_4_DOWN, MouseButtonKind.BackButton, isButtonDown: true, timestampMs);
        RaiseMouseButtonIfNeeded(buttonFlags, NativeMethods.RI_MOUSE_BUTTON_4_UP, MouseButtonKind.BackButton, isButtonDown: false, timestampMs);

        if ((buttonFlags & NativeMethods.RI_MOUSE_WHEEL) == NativeMethods.RI_MOUSE_WHEEL)
        {
            int wheelDelta = DecodeSignedWheelDelta(rawMouse.buttons.usButtonData);
            _mouseWheelDispatcher.Enqueue(new RawMouseWheelInputEventArgs(new RawMouseWheelInput(wheelDelta, timestampMs)));
        }
    }

    private long NextDeviceCaptureSequence(string deviceId)
    {
        string key = deviceId ?? string.Empty;
        long nextSequence = 1L;
        if (_captureSequencesByDevice.TryGetValue(key, out long previousSequence))
        {
            nextSequence = previousSequence + 1L;
        }
        _captureSequencesByDevice[key] = nextSequence;
        return nextSequence;
    }

    private static int DecodeSignedWheelDelta(ushort rawButtonData)
    {
        return rawButtonData >= SignedWheelDeltaThreshold ? rawButtonData - SignedWheelDeltaRange : rawButtonData;
    }

    private void RaiseMouseButtonIfNeeded(ushort buttonFlags, ushort targetFlag, MouseButtonKind buttonKind, bool isButtonDown, double timestampMs)
    {
        if ((buttonFlags & targetFlag) == targetFlag)
        {
            _mouseButtonDispatcher.Enqueue(new RawMouseButtonInputEventArgs(new RawMouseButtonInput(buttonKind, isButtonDown, timestampMs)));
        }
    }

    private void HandleKeyboardInput(NativeMethods.RAWKEYBOARD rawKeyboard, double timestampMs)
    {
        if (rawKeyboard.MakeCode == NativeMethods.KEYBOARD_OVERRUN_MAKE_CODE)
        {
            return;
        }

        bool isKeyUp = (rawKeyboard.Flags & NativeMethods.RI_KEY_BREAK) == NativeMethods.RI_KEY_BREAK;
        bool isExtendedKey = (rawKeyboard.Flags & NativeMethods.RI_KEY_E0) == NativeMethods.RI_KEY_E0 || (rawKeyboard.Flags & NativeMethods.RI_KEY_E1) == NativeMethods.RI_KEY_E1;
        int scanCode = rawKeyboard.MakeCode;
        if (scanCode == 0)
        {
            uint mappedScanCode = NativeMethods.MapVirtualKey(rawKeyboard.VKey, NativeMethods.MAPVK_VK_TO_VSC_EX);
            scanCode = (int)(mappedScanCode & 0xFF);
            isExtendedKey = isExtendedKey || (mappedScanCode & 0xFF00) != 0;
        }

        string displayName = ResolveKeyDisplayName(rawKeyboard.VKey, scanCode, isExtendedKey);
        RawKeyboardInput input = new RawKeyboardInput(rawKeyboard.VKey, scanCode, isExtendedKey, !isKeyUp, timestampMs, displayName);
        _keyboardDispatcher.Enqueue(new RawKeyboardInputEventArgs(input));
    }

    private static string ResolveKeyDisplayName(int virtualKey, int scanCode, bool isExtendedKey)
    {
        int resolvedScanCode = scanCode;
        if (resolvedScanCode == 0)
        {
            uint mappedScanCode = NativeMethods.MapVirtualKey((uint)virtualKey, NativeMethods.MAPVK_VK_TO_VSC_EX);
            resolvedScanCode = (int)(mappedScanCode & 0xFF);
            isExtendedKey = isExtendedKey || (mappedScanCode & 0xFF00) != 0;
        }

        if (resolvedScanCode != 0)
        {
            int keyNameTextParameter = (resolvedScanCode & 0xFF) << 16;
            if (isExtendedKey)
            {
                keyNameTextParameter |= 0x1000000;
            }

            StringBuilder keyName = new StringBuilder(KeyNameBufferCapacity);
            if (NativeMethods.GetKeyNameText(keyNameTextParameter, keyName, keyName.Capacity) > 0)
            {
                return keyName.ToString();
            }
        }

        return string.Format(CultureInfo.InvariantCulture, "VK_{0:X4}", virtualKey);
    }

    private nint EnsureInputBuffer(int requiredSize)
    {
        if (requiredSize <= 0)
        {
            return IntPtr.Zero;
        }
        if (_inputBuffer != IntPtr.Zero && _inputBufferSize >= requiredSize)
        {
            return _inputBuffer;
        }

        ReleaseInputBuffer();
        _inputBuffer = Marshal.AllocHGlobal(requiredSize);
        _inputBufferSize = requiredSize;
        return _inputBuffer;
    }

    private void ReleaseInputBuffer()
    {
        if (_inputBuffer == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeHGlobal(_inputBuffer);
        _inputBuffer = IntPtr.Zero;
        _inputBufferSize = 0;
    }

    private RawMouseEndpointState ResolveEndpoint(nint deviceHandle)
    {
        if (deviceHandle != IntPtr.Zero && _endpointsByHandle.TryGetValue(deviceHandle, out RawMouseEndpointState cachedEndpoint))
        {
            return cachedEndpoint;
        }

        RawMouseDeviceInfo? resolvedDevice = deviceHandle == IntPtr.Zero ? null : BuildDeviceInfo(deviceHandle);
        resolvedDevice ??= BuildFallbackDeviceInfo(deviceHandle);
        RawMouseEndpointState resolvedEndpoint;

        lock (_syncRoot)
        {
            if (!_devicesById.TryGetValue(resolvedDevice.DeviceId, out RawMouseDeviceInfo canonicalDevice))
            {
                canonicalDevice = resolvedDevice;
                _devicesById[resolvedDevice.DeviceId] = canonicalDevice;
            }
            resolvedEndpoint = CreateEndpointState(canonicalDevice);
            if (deviceHandle != IntPtr.Zero)
            {
                _endpointsByHandle[deviceHandle] = resolvedEndpoint;
            }
            return resolvedEndpoint;
        }
    }

    private void RefreshDeviceCache(bool notifyDevicesChanged = true)
    {
        try
        {
            Dictionary<nint, RawMouseEndpointState> endpointsByHandle = new Dictionary<nint, RawMouseEndpointState>();
            Dictionary<string, RawMouseDeviceInfo> devicesById = new Dictionary<string, RawMouseDeviceInfo>(StringComparer.OrdinalIgnoreCase);
            ReadRawMouseDevices(endpointsByHandle, devicesById);

            bool devicesChanged;
            lock (_syncRoot)
            {
                devicesChanged = !AreDeviceMapsEquivalent(_devicesById, devicesById);
                _endpointsByHandle = endpointsByHandle;
                _devicesById = devicesById;
            }

            if (notifyDevicesChanged && devicesChanged)
            {
                QueueMouseDevicesChangedNotification();
            }
        }
        finally
        {
            Volatile.Write(ref _lastDeviceCacheRefreshTick, Environment.TickCount64);
        }
    }

    private void ReadRawMouseDevices(IDictionary<nint, RawMouseEndpointState> endpointsByHandle, IDictionary<string, RawMouseDeviceInfo> devicesById)
    {
        int deviceListItemSize = Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();
        uint deviceCount = 0u;
        if (NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref deviceCount, (uint)deviceListItemSize) != 0 || deviceCount == 0)
        {
            return;
        }

        if (deviceCount > int.MaxValue / (uint)deviceListItemSize)
        {
            Trace.TraceWarning($"Raw input device list is too large to allocate safely: {deviceCount} devices.");
            return;
        }

        int deviceListBytes = (int)deviceCount * deviceListItemSize;
        nint deviceListBuffer = Marshal.AllocHGlobal(deviceListBytes);
        try
        {
            if (NativeMethods.GetRawInputDeviceList(deviceListBuffer, ref deviceCount, (uint)deviceListItemSize) < 0)
            {
                return;
            }

            for (int deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                nint deviceListItemAddress = IntPtr.Add(deviceListBuffer, deviceIndex * deviceListItemSize);
                NativeMethods.RAWINPUTDEVICELIST deviceListItem = Marshal.PtrToStructure<NativeMethods.RAWINPUTDEVICELIST>(deviceListItemAddress);
                if (deviceListItem.dwType != NativeMethods.RIM_TYPEMOUSE)
                {
                    continue;
                }

                RawMouseDeviceInfo? deviceInfo = BuildDeviceInfo(deviceListItem.hDevice);
                if (deviceInfo == null)
                {
                    continue;
                }

                if (!devicesById.ContainsKey(deviceInfo.DeviceId))
                {
                    devicesById[deviceInfo.DeviceId] = deviceInfo;
                }
                endpointsByHandle[deviceListItem.hDevice] = CreateEndpointState(devicesById[deviceInfo.DeviceId]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(deviceListBuffer);
        }
    }

    private RawMouseEndpointState CreateEndpointState(RawMouseDeviceInfo deviceInfo)
    {
        return new RawMouseEndpointState(deviceInfo);
    }

    private static bool AreDeviceMapsEquivalent(IReadOnlyDictionary<string, RawMouseDeviceInfo>? current, IReadOnlyDictionary<string, RawMouseDeviceInfo>? candidate)
    {
        int currentCount = current?.Count ?? 0;
        int candidateCount = candidate?.Count ?? 0;
        if (currentCount != candidateCount)
        {
            return false;
        }
        if (candidate == null || candidate.Count == 0)
        {
            return true;
        }

        foreach (KeyValuePair<string, RawMouseDeviceInfo> candidateDevice in candidate)
        {
            if (current == null || !current.TryGetValue(candidateDevice.Key, out RawMouseDeviceInfo currentDevice))
            {
                return false;
            }
            if (!AreEquivalentDeviceInfo(currentDevice, candidateDevice.Value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool AreEquivalentDeviceInfo(RawMouseDeviceInfo left, RawMouseDeviceInfo right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }

        return string.Equals(left.DeviceId, right.DeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
            && string.Equals(left.PhysicalDeviceKey, right.PhysicalDeviceKey, StringComparison.OrdinalIgnoreCase)
            && Nullable.Equals(left.VendorId, right.VendorId)
            && Nullable.Equals(left.ProductId, right.ProductId)
            && left.ButtonCount == right.ButtonCount
            && left.IsVirtual == right.IsVirtual;
    }

    private RawMouseDeviceInfo? BuildDeviceInfo(nint deviceHandle)
    {
        return _deviceDescriptorFactory.BuildDeviceInfo(deviceHandle);
    }

    private static RawMouseDeviceInfo BuildFallbackDeviceInfo(nint deviceHandle)
    {
        string deviceId = deviceHandle == IntPtr.Zero
            ? "rawinput://mouse/unknown"
            : string.Format(CultureInfo.InvariantCulture, "rawinput://mouse/handle/{0:X}", ((IntPtr)deviceHandle).ToInt64());
        string displayName = deviceHandle == IntPtr.Zero ? "Raw Input Mouse" : "Raw Input Mouse (Unresolved)";
        return new RawMouseDeviceInfo(deviceId, displayName, null, null, 0, isVirtual: false);
    }

    private void ShutdownOnHostThread()
    {
        ReleaseInputBuffer();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
        if (_dispatcher != null)
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            _dispatcher = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher != null)
        {
            try
            {
                dispatcher.Invoke((Action)ShutdownOnHostThread);
            }
            catch (Exception)
            {
            }
        }
        if (_thread.IsAlive)
        {
            _thread.Join(1000);
        }

        _mousePacketDispatcher.Dispose();
        _mouseButtonDispatcher.Dispose();
        _mouseWheelDispatcher.Dispose();
        _keyboardDispatcher.Dispose();
        _mouseDevicesChangedDispatcher.Dispose();
        ReleaseInputBuffer();
        _ready.Dispose();
    }

    void IDisposable.Dispose()
    {
        Dispose();
    }
}
