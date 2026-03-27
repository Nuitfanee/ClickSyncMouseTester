Imports WpfApp1.Models

Namespace Services
    Friend Module MouseDeviceFiltering
        Public Function FilterPhysicalDevices(devices As IEnumerable(Of RawMouseDeviceInfo)) As IReadOnlyList(Of RawMouseDeviceInfo)
            If devices Is Nothing Then
                Return Array.Empty(Of RawMouseDeviceInfo)()
            End If

            Return devices.
                Where(Function(device) device IsNot Nothing AndAlso Not device.IsVirtual).
                ToArray()
        End Function
    End Module
End Namespace
