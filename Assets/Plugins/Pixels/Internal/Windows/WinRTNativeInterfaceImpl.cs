using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Systemic.Pixels.Unity.BluetoothLE.Internal.Windows
{
    //TODO catch exceptions in callback
    internal sealed class WinRTNativeInterfaceImpl : INativeInterfaceImpl
    {
        sealed class NativeScannedPeripheral : ScannedPeripheral.ISystemDevice
        {
            public NativeScannedPeripheral(ulong peripheralId) => PeripheralId = peripheralId;

            public ulong PeripheralId { get; }
        }

        sealed class NativePeripheral : PeripheralHandle.INativePeripheral
        {
            PeripheralConnectionEventHandler _onPeripheralConnectionStatusChanged;
            List<RequestStatusHandler> _onRequestStatusHandlers = new List<RequestStatusHandler>();
            Dictionary<string, ValueChangedHandler> _onValueChangedHandlers = new Dictionary<string, ValueChangedHandler>();

            static List<NativePeripheral> _releasedPeripherals = new List<NativePeripheral>();

            public NativePeripheral(ulong peripheralId, PeripheralConnectionEventHandler onPeripheralConnectionStatusChanged)
                => (PeripheralId, _onPeripheralConnectionStatusChanged) = (peripheralId, onPeripheralConnectionStatusChanged);

            public ulong PeripheralId { get; }

            public void Keep(RequestStatusHandler onRequestStatus)
            {
                _onRequestStatusHandlers.Add(onRequestStatus);
            }

            public void Forget(RequestStatusHandler onRequestStatus)
            {
                Debug.Assert(_onRequestStatusHandlers.Contains(onRequestStatus));
                _onRequestStatusHandlers.Remove(onRequestStatus);
                CheckReleased();
            }

            public void Keep(string serviceUuid, string characteristicUuid, uint instanceIndex, ValueChangedHandler onValueChanged)
            {
                _onValueChangedHandlers[$"{serviceUuid}:{characteristicUuid}#{instanceIndex}"] = onValueChanged;
            }

            public void Forget(string serviceUuid, string characteristicUuid, uint instanceIndex)
            {
                string key = $"{serviceUuid}:{characteristicUuid}#{instanceIndex}";
                Debug.Assert(_onValueChangedHandlers.ContainsKey(key));
                _onValueChangedHandlers.Remove(key);
                CheckReleased();
            }

            public void Release()
            {
                if (_onRequestStatusHandlers.Count > 0)
                {
                    // Keep a reference to ourselves until all handlers have been cleared out
                    _releasedPeripherals.Add(this);
                }
            }

            void CheckReleased()
            {
                if (_onRequestStatusHandlers.Count == 0)
                {
                    _releasedPeripherals.Remove(this);
                }
            }
        }

        const string _libName =
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            "LibWinRTBle";
#else
            "unsupported";
#endif
        //TODO use on all platforms
        enum BleRequestStatus : int
        {
            Success,
            InvalidCall,
            InvalidParameters,
            NotSupported,
            Unreachable,
            ProtocolError,
            AccessDenied,
            Error,
            Canceled,
        };

        delegate void CentralStateUpdateHandler(bool isAvailable);
        delegate void DiscoveredPeripheralHandler([MarshalAs(UnmanagedType.LPStr)] string advertisementDataJson);
        delegate void RequestStatusHandler(BleRequestStatus errorCode);
        delegate void PeripheralConnectionEventHandler(ulong peripheralId, int connectionEvent, int reason);
        delegate void ValueChangedHandler(IntPtr data, UIntPtr length, BleRequestStatus errorCode);

        [DllImport(_libName)]
        private static extern bool pxBleInitialize(bool apartmentSingleThreaded, CentralStateUpdateHandler onCentralStateUpdate);

        [DllImport(_libName)]
        private static extern void pxBleShutdown();

        [DllImport(_libName)]
        private static extern bool pxBleStartScan(string requiredServicesUuids, DiscoveredPeripheralHandler onDiscoveredPeripheral);

        [DllImport(_libName)]
        private static extern void pxBleStopScan();

        [DllImport(_libName)]
        private static extern bool pxBleCreatePeripheral(ulong peripheralId, PeripheralConnectionEventHandler onConnectionEvent);

        [DllImport(_libName)]
        private static extern void pxBleReleasePeripheral(ulong peripheralId);

        [DllImport(_libName)]
        private static extern void pxBleConnectPeripheral(ulong peripheralId, string requiredServicesUuids, bool autoConnect, RequestStatusHandler onRequestStatus);

        [DllImport(_libName)]
        private static extern void pxBleDisconnectPeripheral(ulong peripheralId, RequestStatusHandler onRequestStatus);

        [DllImport(_libName)]
        private static extern string pxBleGetPeripheralName(ulong peripheralId);

        [DllImport(_libName)]
        private static extern int pxBleGetPeripheralMtu(ulong peripheralId);

        [DllImport(_libName)]
        private static extern string pxBleGetPeripheralDiscoveredServices(ulong peripheralId);

        [DllImport(_libName)]
        private static extern string pxBleGetPeripheralServiceCharacteristics(ulong peripheralId, string serviceUuid);

        [DllImport(_libName)]
        private static extern ulong pxBleGetCharacteristicProperties(ulong peripheralId, string serviceUuid, string characteristicUuid, uint instanceIndex);

        [DllImport(_libName)]
        private static extern void pxBleReadCharacteristicValue(ulong peripheralId, string serviceUuid, string characteristicUuid, uint instanceIndex, ValueChangedHandler onValueChanged, RequestStatusHandler onRequestStatus);

        [DllImport(_libName)]
        private static extern void pxBleWriteCharacteristicValue(ulong peripheralId, string serviceUuid, string characteristicUuid, uint instanceIndex, IntPtr data, UIntPtr length, bool withoutResponse, RequestStatusHandler onRequestStatus);

        [DllImport(_libName)]
        private static extern void pxBleSetNotifyCharacteristic(ulong peripheralId, string serviceUuid, string characteristicUuid, uint instanceIndex, ValueChangedHandler onValueChanged, RequestStatusHandler onRequestStatus);

        static CentralStateUpdateHandler _onCentralStateUpdate;
        static DiscoveredPeripheralHandler _onDiscoveredPeripheral;

        public bool Initialize(NativeBluetoothEventHandler onBluetoothEvent)
        {
            CentralStateUpdateHandler onCentralStateUpdate = available => onBluetoothEvent(available);
            bool success = pxBleInitialize(true, onCentralStateUpdate);
            if (success)
            {
                _onCentralStateUpdate = onCentralStateUpdate;
            }
            return success;
        }

        public void Shutdown()
        {
            pxBleShutdown();
            // Keep callback _onCentralStateUpdate
        }

        public bool StartScan(string requiredServiceUuids, Action<ScannedPeripheral> onScannedPeripheral)
        {
            DiscoveredPeripheralHandler onDiscoveredPeripheral = jsonStr =>
            {
                var adv = JsonUtility.FromJson<AdvertisementDataJson>(jsonStr);
                onScannedPeripheral(new ScannedPeripheral(new NativeScannedPeripheral(adv.address), adv));
            };
            // Starts a new scan if on is already in progress
            bool success = pxBleStartScan(requiredServiceUuids, onDiscoveredPeripheral);
            if (success)
            {
                // Store callback now that scan is started
                _onDiscoveredPeripheral = onDiscoveredPeripheral;
            }
            return success;
        }

        public void StopScan()
        {
            pxBleStopScan();
            _onDiscoveredPeripheral = null;
        }

        public PeripheralHandle CreatePeripheral(ulong bluetoothAddress, NativePeripheralConnectionEventHandler onConnectionEvent)
        {
            PeripheralConnectionEventHandler peripheralConnectionEventHandler = (ulong peripheralId, int connectionEvent, int reason) =>
            {
                try
                {
                    onConnectionEvent((ConnectionEvent)connectionEvent, (ConnectionEventReason)reason);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };

            bool success = pxBleCreatePeripheral(bluetoothAddress, peripheralConnectionEventHandler);
            return new PeripheralHandle(success ? new NativePeripheral(bluetoothAddress, peripheralConnectionEventHandler) : null);
        }

        public PeripheralHandle CreatePeripheral(ScannedPeripheral peripheral, NativePeripheralConnectionEventHandler onConnectionEvent)
        {
            return CreatePeripheral(GetPeripheralId(peripheral), onConnectionEvent);
        }

        public void ReleasePeripheral(PeripheralHandle peripheral)
        {
            var periph = (NativePeripheral)peripheral.SystemClient;
            periph.Release();
            pxBleReleasePeripheral(GetPeripheralId(peripheral));
        }

        public void ConnectPeripheral(PeripheralHandle peripheral, string requiredServicesUuids, bool autoConnect, NativeRequestResultHandler onResult)
        {
            pxBleConnectPeripheral(GetPeripheralId(peripheral), requiredServicesUuids, autoConnect,
                GetRequestStatusHandler(Operation.ConnectPeripheral, peripheral, onResult));
        }

        public void DisconnectPeripheral(PeripheralHandle peripheral, NativeRequestResultHandler onResult)
        {
            pxBleDisconnectPeripheral(GetPeripheralId(peripheral),
                GetRequestStatusHandler(Operation.DisconnectPeripheral, peripheral, onResult));
        }

        public string GetPeripheralName(PeripheralHandle peripheral)
        {
            return pxBleGetPeripheralName(GetPeripheralId(peripheral));
        }

        public int GetPeripheralMtu(PeripheralHandle peripheral)
        {
            return pxBleGetPeripheralMtu(GetPeripheralId(peripheral));
        }

        public void RequestPeripheralMtu(PeripheralHandle peripheral, int mtu, NativeValueRequestResultHandler<int> onMtuResult)
        {
            // No support for MTU request with WinRT Bluetooth, we just return the automatically negotiated MTU
            onMtuResult(GetPeripheralMtu(peripheral), new NativeError((int)Error.NotSupported));
        }

        public void ReadPeripheralRssi(PeripheralHandle peripheral, NativeValueRequestResultHandler<int> onRssiRead)
        {
            // No support for reading RSSI of connected device with WinRT Bluetooth
            onRssiRead(0, new NativeError((int)Error.NotSupported));
        }

        public string GetPeripheralDiscoveredServices(PeripheralHandle peripheral)
        {
            return pxBleGetPeripheralDiscoveredServices(GetPeripheralId(peripheral));
        }

        public string GetPeripheralServiceCharacteristics(PeripheralHandle peripheral, string serviceUuid)
        {
            return pxBleGetPeripheralServiceCharacteristics(GetPeripheralId(peripheral), serviceUuid);
        }

        public CharacteristicProperties GetCharacteristicProperties(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex)
        {
            return (CharacteristicProperties)pxBleGetCharacteristicProperties(GetPeripheralId(peripheral), serviceUuid, characteristicUuid, instanceIndex);
        }

        public void ReadCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, NativeValueChangedHandler onValueChanged, NativeRequestResultHandler onResult)
        {
            var valueChangedHandler = GetValueChangedHandler(peripheral, onValueChanged);
            var periph = (NativePeripheral)peripheral.SystemClient;
            periph.Keep(serviceUuid, characteristicUuid, instanceIndex, valueChangedHandler);
            pxBleReadCharacteristicValue(GetPeripheralId(peripheral), serviceUuid, characteristicUuid, instanceIndex, valueChangedHandler,
                GetRequestStatusHandler(Operation.ReadCharacteristic, peripheral, onResult));
        }

        public void WriteCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, byte[] data, bool withoutResponse, NativeRequestResultHandler onResult)
        {
            var ptr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
                pxBleWriteCharacteristicValue(GetPeripheralId(peripheral), serviceUuid, characteristicUuid, instanceIndex, ptr, (UIntPtr)data.Length, withoutResponse,
                    GetRequestStatusHandler(Operation.WriteCharacteristic, peripheral, onResult));
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void SubscribeCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, NativeValueChangedHandler onValueChanged, NativeRequestResultHandler onResult)
        {
            var valueChangedHandler = GetValueChangedHandler(peripheral, onValueChanged);
            var periph = (NativePeripheral)peripheral.SystemClient;
            periph.Keep(serviceUuid, characteristicUuid, instanceIndex, valueChangedHandler);
            pxBleSetNotifyCharacteristic(GetPeripheralId(peripheral), serviceUuid, characteristicUuid, instanceIndex, valueChangedHandler,
                GetRequestStatusHandler(Operation.SubscribeCharacteristic, peripheral, onResult));
        }

        public void UnsubscribeCharacteristic(PeripheralHandle peripheral, string serviceUuid, string characteristicUuid, uint instanceIndex, NativeRequestResultHandler onResult)
        {
            pxBleSetNotifyCharacteristic(GetPeripheralId(peripheral), serviceUuid, characteristicUuid, instanceIndex, null,
                GetRequestStatusHandler(Operation.UnsubscribeCharacteristic, peripheral, onResult));
            var periph = (NativePeripheral)peripheral.SystemClient;
            periph.Forget(serviceUuid, characteristicUuid, instanceIndex);
        }

        private ulong GetPeripheralId(ScannedPeripheral scannedPeripheral) => ((NativeScannedPeripheral)scannedPeripheral.SystemDevice).PeripheralId;

        private ulong GetPeripheralId(PeripheralHandle peripheralHandle) => ((NativePeripheral)peripheralHandle.SystemClient).PeripheralId;

        private RequestStatusHandler GetRequestStatusHandler(Operation operation, PeripheralHandle peripheral, NativeRequestResultHandler onResult)
        {
            var periph = (NativePeripheral)peripheral.SystemClient;
            RequestStatusHandler onRequestStatus = null;
            onRequestStatus = errorCode =>
            {
                try
                {
                    if (errorCode == BleRequestStatus.Success)
                    {
                        Debug.Log($"{operation} ==> Request successful");
                    }
                    else
                    {
                        Debug.LogError($"{operation} ==> Request failed: {errorCode}");
                    }
                    periph.Forget(onRequestStatus);
                    onResult(new NativeError((int)errorCode));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };
            periph.Keep(onRequestStatus);
            return onRequestStatus;
        }

        private ValueChangedHandler GetValueChangedHandler(PeripheralHandle peripheral, NativeValueChangedHandler onValueChanged)
        {
            var periph = (NativePeripheral)peripheral.SystemClient;
            ValueChangedHandler valueChangedHandler = (IntPtr data, UIntPtr length, BleRequestStatus errorCode) =>
            {
                try
                {
                    var array = new byte[(int)length];
                    if (data != IntPtr.Zero)
                    {
                        Marshal.Copy(data, array, 0, array.Length);
                    }
                    onValueChanged(array, new NativeError((int)errorCode));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };

            return valueChangedHandler;
        }
    }
}
