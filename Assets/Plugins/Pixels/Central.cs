using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Systemic.Pixels.Unity.BluetoothLE
{
    public sealed class Central : MonoBehaviour
    {
        #region MonoBehaviour

        static Central _instance;
        static bool _autoDestroy;
        ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

        static void EnqueueAction(Action action)
        {
            if (_instance != null)
            {
                _instance._actionQueue.Enqueue(action);
            }
        }

        static void CreateBehaviour()
        {
            _autoDestroy = false;
            if (!_instance)
            {
                var go = new GameObject("PixelsBleCentral");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<Central>();
            }
        }

        static void ScheduleDestroy()
        {
            _autoDestroy = true;
        }

        void Start()
        {
            // Safeguard
            if (_instance != this)
            {
                Debug.LogError("A second instance of Central got spawned, now destroying it");
                Destroy(this);
            }
        }

        void Update()
        {
            while (_actionQueue.TryDequeue(out Action act))
            {
                act?.Invoke();
            }
            if (_autoDestroy)
            {
                Destroy(_instance);
                _instance = null;
            }
        }

        void OnDestroy()
        {
            if (!_autoDestroy)
            {
                Central.Shutdown();
            }
        }

        #endregion

        class PeripheralState
        {
            public ScannedPeripheral ScannedPeripheral;
            public PeripheralHandle PeripheralHandle;
            public Guid[] RequiredServices;
            public bool IsReady;
        }

        public static int RequestTimeout { get; set; } = 5;

        // Dictionary key is peripheral SystemId
        static Dictionary<string, PeripheralState> _peripherals = new Dictionary<string, PeripheralState>();

        public static bool IsReady { get; private set; }

        public static bool IsScanning { get; private set; }

        public static event Action<ScannedPeripheral> PeripheralDiscovered;

        public static ScannedPeripheral[] ScannedPeripherals
        {
            get
            {
                lock (_peripherals)
                {
                    return _peripherals.Values
                        .Select(ps => ps.ScannedPeripheral)
                        .ToArray();
                }
            }
        }

        public static ScannedPeripheral[] ConnectedPeripherals
        {
            get
            {
                lock (_peripherals)
                {
                    return _peripherals.Values
                        .Where(ps => ps.IsReady)
                        .Select(ps => ps.ScannedPeripheral)
                        .ToArray();
                }
            }
        }

        public static bool Initialize()
        {
            CreateBehaviour();

            IsReady = false;

            bool success = NativeInterface.Initialize(available =>
            {
                Debug.Log($"[BLE] Bluetooth status: {(available ? "" : "not")} available");
                IsReady = available;
                IsScanning = IsScanning && IsReady;
            });

            if (!success)
            {
                Debug.LogError("[BLE] Failed to initialize");
            }

            return success;
        }

        public static void Shutdown()
        {
            Debug.Log("[BLE] Shutting down");

            NativeInterface.Shutdown();
            ScheduleDestroy();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="serviceUuids">Can be null (but may use more battery life)</param>
        /// <returns></returns>
        /// <remarks>If a scan is already in progress, it will replace it by the new one</remarks>
        public static bool ScanForPeripheralsWithServices(IEnumerable<Guid> serviceUuids = null)
        {
            if (!IsReady)
            {
                Debug.LogError("[BLE] Central not ready for scanning");
                return false;
            }

            var requiredServices = serviceUuids?.ToArray() ?? Array.Empty<Guid>();
            IsScanning = NativeInterface.StartScan(serviceUuids, scannedPeripheral =>
            {
                Debug.Log($"[BLE] Discovered peripheral {scannedPeripheral.Name}, rssi={scannedPeripheral.Rssi}, addr={scannedPeripheral.BluetoothAddress})");

                lock (_peripherals)
                {
                    if (!_peripherals.TryGetValue(scannedPeripheral.SystemId, out PeripheralState ps))
                    {
                        _peripherals[scannedPeripheral.SystemId] = ps = new PeripheralState();
                    }
                    ps.ScannedPeripheral = scannedPeripheral;
                    ps.RequiredServices = requiredServices;
                }

                // Notify
                EnqueueAction(() => PeripheralDiscovered?.Invoke(scannedPeripheral));
            });

            if (IsScanning)
            {
                Debug.Log($"[BLE] Starting scan for BLE peripherals with services {serviceUuids?.Select(g => g.ToString()).Aggregate((a, b) => a + ", " + b)}");
            }
            else
            {
                Debug.LogError("[BLE] Failed to start scanning for peripherals");
            }

            return IsScanning;
        }

        public static void StopScan()
        {
            Debug.Log($"[BLE] Stopping scan");

            NativeInterface.StopScan();
            IsScanning = false;
        }

        public static RequestEnumerator ConnectPeripheralAsync(ScannedPeripheral peripheral, Action<ScannedPeripheral, bool> onConnectionEvent)
        {
            PeripheralState ps = GetPeripheralState(peripheral);
            return new RequestEnumerator(Operation.ConnectPeripheral, 0,
                onResult =>
                {
                    if (ps.PeripheralHandle.IsEmpty)
                    {
                        ps.IsReady = false;
                        ps.PeripheralHandle = NativeInterface.CreatePeripheral(peripheral,
                            (connectionEvent, reason) => EnqueueAction(() => OnPeripheralConnectionEvent(ps, onConnectionEvent, connectionEvent, reason)));
                    }

                    if (ps.PeripheralHandle.IsEmpty)
                    {
                        Debug.LogError($"[BLE:{ps.ScannedPeripheral.Name}] Failed to create peripheral");
                        onResult(new NativeError((int)Error.Unknown));
                    }
                    else //TODO if ((!ps.IsConnecting) && (!ps.IsReady))
                    {
                        Connect(ps, onResult);

                        static void Connect(PeripheralState ps, NativeRequestResultHandler onResult)
                        {
                            Debug.Log($"[BLE:{ps.ScannedPeripheral.Name}] Connecting...");

                            NativeInterface.ConnectPeripheral(
                                ps.PeripheralHandle,
                                ps.RequiredServices,
                                error =>
                                {
                                    if ((error.Code == (int)RequestStatus.Unreachable) && (!ps.PeripheralHandle.IsEmpty))
                                    {
                                        Debug.LogError("UNREACHABLE");
                                        // Try again
                                        Connect(ps, onResult);
                                    }
                                    else
                                    {
                                        onResult(error);
                                    }
                                });
                        }
                    }
                });
        }

        static void OnPeripheralConnectionEvent(PeripheralState ps, Action<ScannedPeripheral, bool> onConnectionEvent, ConnectionEvent connectionEvent, ConnectionEventReason reason)
        {
            Debug.Log($"[BLE:{ps.ScannedPeripheral.Name}] ConnectionEvent => {connectionEvent}{(reason == ConnectionEventReason.Success ? "" : $", reason: { reason}")}");

            bool ready = connectionEvent == ConnectionEvent.Ready;
            bool disconnected = connectionEvent == ConnectionEvent.Disconnected || connectionEvent == ConnectionEvent.FailedToConnect;

            if (!disconnected && !ready)
            {
                // Nothing to do
                return;
            }

            if (ready)
            {
                // Change MTU to maximum (note: MTU can only be set once)
                NativeInterface.RequestPeripheralMtu(ps.PeripheralHandle, NativeInterface.MaxMtu,
                    (mtu, error) =>
                    {
                        Debug.Log($"[BLE:{ps.ScannedPeripheral.Name}] MTU {(error.IsEmpty ? "changed to" : "is")}: {mtu}");
                        if (error.HasError && (error.Code != (int)Error.NotSupported))
                        {
                            Debug.LogError($"[BLE:{ps.ScannedPeripheral.Name}] error changing MTU: {error}");
                        }

                        Debug.Assert(!ps.IsReady);
                        ps.IsReady = true;

                        onConnectionEvent(ps.ScannedPeripheral, true);
                    });
            }
            else if (ps.IsReady)
            {
                // We got disconnected
                Debug.Log($"[BLE:{ps.ScannedPeripheral.Name}] Disconnected");

                // We were previously connected
                ps.IsReady = false;

                onConnectionEvent(ps.ScannedPeripheral, false);
            }
        }

        public static RequestEnumerator DisconnectPeripheralAsync(ScannedPeripheral peripheral)
        {
            var ps = GetPeripheralState(peripheral);
            var nativePeripheral = ps.PeripheralHandle;
            ps.IsReady = false;
            ps.PeripheralHandle = new PeripheralHandle();

            //TODO make sure that disconnect always calls onResult (infinite timeout)
            return new RequestEnumerator(Operation.DisconnectPeripheral, 0,
                onResult => NativeInterface.DisconnectPeripheral(nativePeripheral, onResult),
                postAction: () => NativeInterface.ReleasePeripheral(nativePeripheral));
        }

        public static string GetPeripheralName(ScannedPeripheral peripheral)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return NativeInterface.GetPeripheralName(nativePeripheral);
        }

        public static int GetPeripheralMtu(ScannedPeripheral peripheral)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return NativeInterface.GetPeripheralMtu(nativePeripheral);
        }

        public static ValueRequestEnumerator<int> ReadPeripheralRssi(ScannedPeripheral peripheral)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return new ValueRequestEnumerator<int>(Operation.ReadPeripheralRssi, RequestTimeout,
                onResult => NativeInterface.ReadPeripheralRssi(nativePeripheral, onResult));
        }

        public static Guid[] GetPeripheralDiscoveredServices(ScannedPeripheral peripheral)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return NativeInterface.GetPeripheralDiscoveredServices(nativePeripheral);
        }

        public static Guid[] GetPeripheralServiceCharacteristics(ScannedPeripheral peripheral, Guid serviceUuid)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return NativeInterface.GetPeripheralServiceCharacteristics(nativePeripheral, serviceUuid);
        }

        public static CharacteristicProperties GetCharacteristicProperties(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, uint instanceIndex = 0)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return NativeInterface.GetCharacteristicProperties(nativePeripheral, serviceUuid, characteristicUuid, instanceIndex);
        }

        public static RequestEnumerator ReadCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, Action<byte[]> onValueChanged)
        {
            return ReadCharacteristicAsync(peripheral, serviceUuid, characteristicUuid, 0, onValueChanged);
        }

        public static RequestEnumerator ReadCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, uint instanceIndex, Action<byte[]> onValueChanged)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return new RequestEnumerator(Operation.ReadCharacteristic, RequestTimeout,
                onResult => NativeInterface.ReadCharacteristic(
                    nativePeripheral, serviceUuid, characteristicUuid, instanceIndex,
                    onValueChanged: GetNativeHandler(onValueChanged, onResult),
                    onResult: onResult));
        }

        public static RequestEnumerator WriteCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withoutResponse = false)
        {
            return WriteCharacteristicAsync(peripheral, serviceUuid, characteristicUuid, 0, data, withoutResponse);
        }

        public static RequestEnumerator WriteCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, uint instanceIndex, byte[] data, bool withoutResponse = false)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return new RequestEnumerator(Operation.WriteCharacteristic, RequestTimeout,
                onResult => NativeInterface.WriteCharacteristic(
                    nativePeripheral, serviceUuid, characteristicUuid, instanceIndex, data, withoutResponse, onResult));
        }

        public static RequestEnumerator SubscribeCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, Action<byte[]> onValueChanged)
        {
            return SubscribeCharacteristicAsync(peripheral, serviceUuid, characteristicUuid, 0, onValueChanged);
        }

        public static RequestEnumerator SubscribeCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, uint instanceIndex, Action<byte[]> onValueChanged)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return new RequestEnumerator(Operation.SubscribeCharacteristic, RequestTimeout,
                onResult => NativeInterface.SubscribeCharacteristic(
                    nativePeripheral, serviceUuid, characteristicUuid, instanceIndex,
                    onValueChanged: GetNativeHandler(onValueChanged, onResult),
                    onResult: onResult));
        }

        public static RequestEnumerator UnsubscribeCharacteristicAsync(ScannedPeripheral peripheral, Guid serviceUuid, Guid characteristicUuid, uint instanceIndex = 0)
        {
            var nativePeripheral = GetPeripheralState(peripheral).PeripheralHandle;
            return new RequestEnumerator(Operation.UnsubscribeCharacteristic, RequestTimeout,
                onResult => NativeInterface.UnsubscribeCharacteristic(
                    nativePeripheral, serviceUuid, characteristicUuid, instanceIndex, onResult));
        }

        static PeripheralState GetPeripheralState(ScannedPeripheral scannedPeripheral)
        {
            lock (_peripherals)
            {
                _peripherals.TryGetValue(scannedPeripheral.SystemId, out PeripheralState ps);
                return ps ?? throw new ArgumentException(nameof(scannedPeripheral), $"No peripheral found with SystemId={scannedPeripheral.SystemId}");
            }
        }

        static NativeValueChangedHandler GetNativeHandler(Action<byte[]> onValueChanged, NativeRequestResultHandler onResult)
        {
            return (data, error) =>
            {
                try
                {
                    if (error.IsEmpty)
                    {
                        EnqueueAction(() => onValueChanged(data));
                    }
                    else
                    {
                        onResult(error);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };
        }
    }
}
