using System;
using UnityEngine;
using UnityEngine.Android;

namespace Systemic.Pixels.Unity.BluetoothLE.Internal.Android
{
    public enum AndroidRequestStatus : int
    {
        // From android.bluetooth.BluetoothGatt 
        GATT_SUCCESS = 0,                   // A GATT operation completed successfully
        GATT_READ_NOT_PERMITTED = 2,        // GATT read operation is not permitted
        GATT_WRITE_NOT_PERMITTED = 3,       // GATT write operation is not permitted
        GATT_INSUFFICIENT_AUTHENTICATION =5,// Insufficient authentication for a given operation
        GATT_REQUEST_NOT_SUPPORTED = 6,     // The given request is not supported
        GATT_INVALID_OFFSET = 7,            // A read or write operation was requested with an invalid offset
        GATT_INVALID_ATTRIBUTE_LENGTH = 13, //  A write operation exceeds the maximum length of the attribute
        GATT_INSUFFICIENT_ENCRYPTION = 15,  // Insufficient encryption for a given operation
        GATT_ERROR = 133,                   // (0x85) Generic error
        GATT_CONNECTION_CONGESTED = 143,    // (0x8f) A remote device connection is congested.
        GATT_FAILURE = 257,                 // (0x101) A GATT operation failed, errors other than the above

        // From Nordic's FailCallback
        REASON_DEVICE_DISCONNECTED = -1,
        REASON_DEVICE_NOT_SUPPORTED = -2,
        REASON_NULL_ATTRIBUTE = -3,
        REASON_REQUEST_FAILED = -4,
        REASON_TIMEOUT = -5,
        REASON_VALIDATION = -6,
        REASON_CANCELLED = -7,
        REASON_BLUETOOTH_DISABLED = -100,

        // From Nordic's RequestCallback
        REASON_REQUEST_INVALID = -1000000,
    }

    public enum AndroidConnectionEventReason
    {
        REASON_UNKNOWN = -1,
        REASON_SUCCESS = 0,             // The disconnection was initiated by the user
        REASON_TERMINATE_LOCAL_HOST = 1,// The local device initiated disconnection
        REASON_TERMINATE_PEER_USER = 2, // The remote device initiated graceful disconnection
        REASON_LINK_LOSS = 3,           // This reason will only be reported when autoConnect=true,
                                        // and connection to the device was lost for any reason other than graceful disconnection initiated by peer user,
                                        // Android will try to reconnect automatically
        REASON_NOT_SUPPORTED = 4,       // The device does not have required services
        REASON_CANCELLED = 5,           // Connection attempt was canceled
        REASON_TIMEOUT = 10,            // The connection timed out
    }

    sealed class AndroidNativeInterfaceImpl : INativeInterfaceImpl
    {
        sealed class NativeBluetoothDevice : ScannedPeripheral.ISystemDevice, IDisposable
        {
            public NativeBluetoothDevice(AndroidJavaObject device) => Device = device;

            public AndroidJavaObject Device { get; private set; }

            public void Dispose() { Device = null; }
        }

        sealed class NativePeripheral : PeripheralHandle.INativePeripheral, IDisposable
        {
            public NativePeripheral(AndroidJavaObject client) => Client = client;

            public AndroidJavaObject Client { get; private set; }

            public void Dispose() { Client = null; }
        }

        readonly AndroidJavaClass _scannerClass = new AndroidJavaClass("com.systemic.pixels.Scanner");
        readonly AndroidJavaClass _peripheralClass = new AndroidJavaClass("com.systemic.pixels.Peripheral");

        public bool Initialize(NativeBluetoothEventHandler onBluetoothEvent)
        {
#if UNITY_2018_3_OR_NEWER
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                Permission.RequestUserPermission(Permission.FineLocation);
            }
#endif
            //TODO bluetooth availability events
            onBluetoothEvent((_scannerClass != null) && (_peripheralClass != null) ? BluetoothStatus.Enabled : BluetoothStatus.Disabled);
            return true;
        }

        public void Shutdown()
        {
        }

        public bool StartScan(string requiredServiceUuids, Action<ScannedPeripheral> onScannedPeripheral)
        {
            var callback = new ScannerCallback((device, scanResult)
                => onScannedPeripheral(new ScannedPeripheral(new NativeBluetoothDevice(device), scanResult)));
                //TODO try/catch?

            _scannerClass.CallStatic(
                "startScan",
                requiredServiceUuids,
                callback);

            return true;
        }

        public void StopScan()
        {
            _scannerClass.CallStatic("stopScan");
        }

        public PeripheralHandle CreatePeripheral(ulong bluetoothAddress, NativePeripheralConnectionEventHandler onConnectionEvent)
        {
            var device = _peripheralClass.CallStatic<AndroidJavaObject>(
                "getDeviceFromAddress",
                (long)bluetoothAddress);
            if (device == null)
            {
                return new PeripheralHandle();
            }
            else
            {
                var client = new AndroidJavaObject(
                    "com.systemic.pixels.Peripheral",
                    device,
                    new ConnectionObserver(onConnectionEvent));
                return new PeripheralHandle(new NativePeripheral(client));
            }
        }

        public PeripheralHandle CreatePeripheral(ScannedPeripheral peripheral, NativePeripheralConnectionEventHandler onConnectionEvent)
        {
            var client = new AndroidJavaObject(
                "com.systemic.pixels.Peripheral",
                GetDevice(peripheral),
                new ConnectionObserver(onConnectionEvent));
            return new PeripheralHandle(new NativePeripheral(client));
        }

        public void ReleasePeripheral(PeripheralHandle peripheral)
        {
            ((NativePeripheral)peripheral.SystemClient).Dispose();
        }

        public void ConnectPeripheral(PeripheralHandle peripheral, string requiredServicesUuids, bool autoConnect, NativeRequestResultHandler onResult)
        {
            GetClient(peripheral).Call(
                "connect",
                requiredServicesUuids,
                autoConnect,
                new RequestCallback(Operation.ConnectPeripheral, onResult));
        }

        public void DisconnectPeripheral(PeripheralHandle peripheral, NativeRequestResultHandler onResult)
        {
            GetClient(peripheral).Call(
                "disconnect",
                new RequestCallback(Operation.DisconnectPeripheral, onResult));
        }

        public string GetPeripheralName(PeripheralHandle peripheral)
        {
            return GetClient(peripheral).Call<string>("getName");
        }

        public int GetPeripheralMtu(PeripheralHandle peripheral)
        {
            return GetClient(peripheral).Call<int>("getMtu");
        }

        public void RequestPeripheralMtu(PeripheralHandle peripheral, int mtu, NativeValueRequestResultHandler<int> onMtuResult)
        {
            GetClient(peripheral).Call(
                "requestMtu",
                mtu,
                new MtuRequestCallback(onMtuResult));
        }

        public void ReadPeripheralRssi(PeripheralHandle peripheral, NativeValueRequestResultHandler<int> onRssiRead)
        {
            GetClient(peripheral).Call(
                "readRssi",
                new RssiRequestCallback(onRssiRead));
        }

        public string GetPeripheralDiscoveredServices(PeripheralHandle peripheral)
        {
            return GetClient(peripheral).Call<string>("getDiscoveredServices");
        }

        public string GetPeripheralServiceCharacteristics(PeripheralHandle peripheral, string serviceUuid)
        {
            return GetClient(peripheral).Call<string>(
                "getServiceCharacteristics",
                serviceUuid);
        }

        public CharacteristicProperties GetCharacteristicProperties(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex)
        {
            return (CharacteristicProperties)GetClient(peripheral).Call<int>(
                "getCharacteristicProperties",
                serviceUuid,
                characteristicUuid,
                (int)instanceIndex);
        }

        public void ReadCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, NativeValueChangedHandler onValueChanged, NativeRequestResultHandler onResult)
        {
            GetClient(peripheral).Call(
                "readCharacteristic",
                serviceUuid,
                characteristicUuid,
                (int)instanceIndex,
                new DataReceivedCallback(onValueChanged),
                new RequestCallback(Operation.ReadCharacteristic, onResult));
        }

        public void WriteCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, byte[] data, bool withoutResponse, NativeRequestResultHandler onResult)
        {
            GetClient(peripheral).Call(
                "writeCharacteristic",
                serviceUuid,
                characteristicUuid,
                (int)instanceIndex,
                JavaUtils.ToSignedArray(data),
                withoutResponse,
                new RequestCallback(Operation.WriteCharacteristic, onResult));
        }

        // No notification with error on Android
        public void SubscribeCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, NativeValueChangedHandler onValueChanged, NativeRequestResultHandler onResult)
        {
            GetClient(peripheral).Call(
                "subscribeCharacteristic",
                serviceUuid,
                characteristicUuid,
                (int)instanceIndex,
                new DataReceivedCallback(onValueChanged),
                new RequestCallback(Operation.SubscribeCharacteristic, onResult));
        }

        public void UnsubscribeCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, NativeRequestResultHandler onResult)
        {
            GetClient(peripheral).Call(
                "unsubscribeCharacteristic",
                serviceUuid,
                characteristicUuid,
                (int)instanceIndex,
                new RequestCallback(Operation.UnsubscribeCharacteristic, onResult));
        }

        AndroidJavaObject GetDevice(ScannedPeripheral scannedPeripheral) => ((NativeBluetoothDevice)scannedPeripheral.SystemDevice).Device;

        AndroidJavaObject GetClient(PeripheralHandle peripheralHandle) => ((NativePeripheral)peripheralHandle.SystemClient).Client;

        public static RequestStatus ToRequestStatus(int androidStatus)
        {
            return androidStatus switch
            {
                (int)AndroidRequestStatus.GATT_SUCCESS => RequestStatus.Success,
                (int)AndroidRequestStatus.GATT_INSUFFICIENT_AUTHENTICATION => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_READ_NOT_PERMITTED => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_WRITE_NOT_PERMITTED => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_REQUEST_NOT_SUPPORTED => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_INVALID_OFFSET => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_INVALID_ATTRIBUTE_LENGTH => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_INSUFFICIENT_ENCRYPTION => RequestStatus.ProtocolError,
                (int)AndroidRequestStatus.GATT_ERROR => RequestStatus.Error,
                (int)AndroidRequestStatus.GATT_CONNECTION_CONGESTED => RequestStatus.Error,
                (int)AndroidRequestStatus.GATT_FAILURE => RequestStatus.Error,
                (int)AndroidRequestStatus.REASON_DEVICE_DISCONNECTED => RequestStatus.InvalidCall,
                (int)AndroidRequestStatus.REASON_DEVICE_NOT_SUPPORTED => RequestStatus.Error,
                (int)AndroidRequestStatus.REASON_NULL_ATTRIBUTE => RequestStatus.InvalidParameters,
                (int)AndroidRequestStatus.REASON_REQUEST_FAILED => RequestStatus.Error,
                (int)AndroidRequestStatus.REASON_TIMEOUT => RequestStatus.Timeout,
                (int)AndroidRequestStatus.REASON_VALIDATION => RequestStatus.Error,
                (int)AndroidRequestStatus.REASON_CANCELLED => RequestStatus.Canceled,
                (int)AndroidRequestStatus.REASON_BLUETOOTH_DISABLED => RequestStatus.InvalidCall,
                (int)AndroidRequestStatus.REASON_REQUEST_INVALID => RequestStatus.InvalidCall,
                _ => RequestStatus.Error
            };
        }

        public static ConnectionEventReason ToConnectionEventReason(int androidReason)
        {
            return androidReason switch
            {
                (int)AndroidConnectionEventReason.REASON_SUCCESS => ConnectionEventReason.Unknown,
                (int)AndroidConnectionEventReason.REASON_TERMINATE_LOCAL_HOST => ConnectionEventReason.AdpaterOff,
                (int)AndroidConnectionEventReason.REASON_TERMINATE_PEER_USER => ConnectionEventReason.Peripheral,
                (int)AndroidConnectionEventReason.REASON_LINK_LOSS => ConnectionEventReason.LinkLoss,
                (int)AndroidConnectionEventReason.REASON_NOT_SUPPORTED => ConnectionEventReason.NotSupported,
                (int)AndroidConnectionEventReason.REASON_CANCELLED => ConnectionEventReason.Canceled,
                (int)AndroidConnectionEventReason.REASON_TIMEOUT => ConnectionEventReason.Timeout,
                _ => ConnectionEventReason.Unknown,
            };
        }
    }
}
