Imports WpfApp1.Models

Namespace Services
    Public Interface IRawInputBroker
        Inherits IDisposable

        Event MousePacketCaptured As EventHandler(Of RawMousePacketEventArgs)
        Event MouseButtonInput As EventHandler(Of RawMouseButtonInputEventArgs)
        Event MouseWheelInput As EventHandler(Of RawMouseWheelInputEventArgs)
        Event KeyboardInput As EventHandler(Of RawKeyboardInputEventArgs)
        Event MouseDevicesChanged As EventHandler

        Function GetMouseDevices() As IReadOnlyList(Of RawMouseDeviceInfo)
    End Interface
End Namespace
