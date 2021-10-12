using Dice;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

using Central = Systemic.Pixels.Unity.BluetoothLE.Central;
using Peripheral = Systemic.Pixels.Unity.BluetoothLE.ScannedPeripheral;

partial class DicePool
{
    sealed class PoolDie : Die
    {
        /// <summary>
        /// This data structure mirrors the data in firmware/bluetooth/bluetooth_stack.cpp
        /// </sumary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct PixelAdvertisingData
        {
            // Die type identification
            public DieDesignAndColor designAndColor; // Physical look, also only 8 bits
            public byte faceCount; // Which kind of dice this is

            // Device ID
            public uint deviceId;

            // Current state
            public DieRollState rollState; // Indicates whether the dice is being shaken
            public byte currentFace; // Which face is currently up
            public byte batteryLevel; // 0 -> 255
        };

        // The underlying BLE device
        Peripheral _peripheral;

        // Count how many time Connect() was called, so we only disconnect after the same number of calls to Disconnect()
        int _connectionCount;

        // Connection internal events
        ConnectionResultHandler onConnectionResult;
        ConnectionResultHandler onDisconnectionResult;

        public delegate void ConnectionResultHandler(Die die, bool success, string error);

        /// <summary>
        /// Event triggered when die got disconnected for other reasons than a call to Disconnect().
        /// Most likely the BLE device was turned off or got out of range.
        /// </summary>
        public event System.Action onDisconnectedUnexpectedly;

        public string SystemId => _peripheral?.SystemId;

        public void Setup(Peripheral peripheral)
        {
            EnsureRunningOnMainThread();

            if (peripheral == null) throw new System.ArgumentNullException(nameof(peripheral));

            if (_peripheral == null)
            {
                Debug.Assert(connectionState == DieConnectionState.Invalid);
                connectionState = DieConnectionState.Available;
            }
            else if (_peripheral.SystemId != peripheral.SystemId)
            {
                throw new System.InvalidOperationException("Trying to assign another peripheral to Die");
            }

            _peripheral = peripheral;
            systemId = _peripheral.SystemId;
            name = _peripheral.Name;

            if (_peripheral.ManufacturerData?.Count > 0)
            {
                // Marshall the data into the struct we expect
                int size = Marshal.SizeOf(typeof(PixelAdvertisingData));
                if (_peripheral.ManufacturerData.Count == size)
                {
                    System.IntPtr ptr = Marshal.AllocHGlobal(size);
                    Marshal.Copy(_peripheral.ManufacturerData.ToArray(), 0, ptr, size);
                    var advData = Marshal.PtrToStructure<PixelAdvertisingData>(ptr);
                    Marshal.FreeHGlobal(ptr);

                    // Update die data
                    bool appearanceChanged = faceCount != advData.faceCount || designAndColor != advData.designAndColor;
                    bool rollStateChanged = state != advData.rollState || face != advData.currentFace;
                    faceCount = advData.faceCount;
                    designAndColor = advData.designAndColor;
                    state = advData.rollState;
                    face = advData.currentFace;
                    batteryLevel = advData.batteryLevel / 255f;
                    rssi = _peripheral.Rssi;

                    // Trigger callbacks
                    BatteryLevelChanged?.Invoke(this, batteryLevel, charging);
                    if (appearanceChanged)
                    {
                        AppearanceChanged?.Invoke(this, faceCount, designAndColor);
                    }
                    if (rollStateChanged)
                    {
                        StateChanged?.Invoke(this, state, face);
                    }
                    RssiChanged?.Invoke(this, rssi);
                }
                else
                {
                    Debug.LogError($"Die {name}: incorrect advertising data length {_peripheral.ManufacturerData.Count}, expected: {size}");
                }
            }
        }

        public void ResetLastError()
        {
            EnsureRunningOnMainThread();

            lastError = DieLastError.None;
        }

        public void Connect(ConnectionResultHandler onConnectionResult = null)
        {
            EnsureRunningOnMainThread();

            void IncrementConnectCount()
            {
                ++_connectionCount;
                Debug.Log($"Die {name}: connecting, counter={_connectionCount}");
            }

            switch (connectionState)
            {
                default:
                    string error = $"Invalid die state {connectionState} while attempting to connect";
                    Debug.LogError($"Die {name}: {error}");
                    onConnectionResult?.Invoke(this, false, error);
                    break;
                case DieConnectionState.Available:
                    IncrementConnectCount();
                    Debug.Assert(_connectionCount == 1);
                    this.onConnectionResult += onConnectionResult;
                    DoConnect();
                    break;
                case DieConnectionState.Connecting:
                case DieConnectionState.Identifying:
                    // Already in the process of connecting, just add the callback and wait
                    IncrementConnectCount();
                    this.onConnectionResult += onConnectionResult;
                    break;
                case DieConnectionState.Ready:
                    // Trigger the callback immediately
                    IncrementConnectCount();
                    onConnectionResult?.Invoke(this, true, null);
                    break;
            }
        }

        public void Disconnect(ConnectionResultHandler onDisconnectionResult = null, bool forceDisconnect = false)
        {
            EnsureRunningOnMainThread();

            switch (connectionState)
            {
                default:
                    // Die not connected
                    onDisconnectionResult?.Invoke(this, true, null);
                    break;
                case DieConnectionState.Ready:
                case DieConnectionState.Connecting:
                case DieConnectionState.Identifying:
                    Debug.Assert(_connectionCount > 0);
                    _connectionCount = forceDisconnect ? 0 : Mathf.Max(0, _connectionCount - 1);

                    Debug.Log($"Die {name}: disconnecting, counter={_connectionCount}, forceDisconnect={forceDisconnect}");

                    if (_connectionCount == 0)
                    {
                        // Register to be notified when disconnection is complete
                        this.onDisconnectionResult += onDisconnectionResult;
                        DoDisconnect();
                    }
                    else
                    {
                        // Trigger the callback immediately
                        onDisconnectionResult(this, true, null);
                    }
                    break;
            }
        }

        void DoConnect()
        {
            Debug.Assert(connectionState == DieConnectionState.Available);
            if (connectionState == DieConnectionState.Available)
            {
                connectionState = DieConnectionState.Connecting;
                StartCoroutine(ConnectAsync());

                IEnumerator ConnectAsync()
                {
                    Systemic.Pixels.Unity.BluetoothLE.RequestEnumerator request = null;
                    request = Central.ConnectPeripheralAsync(_peripheral, (p, connected) =>
                    {
                        // Is Unity behavior still valid?
                        if ((this != null) && (!request.IsTimeOut))
                        {
                            Debug.Assert(_peripheral.SystemId == p.SystemId);
                            Debug.Log($"Die {name}: {(connected ? "" : "dis")}connected");

                            if ((!connected) && (connectionState != DieConnectionState.Disconnecting))
                            {
                                if ((connectionState == DieConnectionState.Connecting) || (connectionState == DieConnectionState.Identifying))
                                {
                                    NotifyConnectionResult("Disconnected unexpectedly");
                                }
                                else
                                {
                                    Debug.LogError($"{_peripheral.Name}: Got disconnected unexpectedly while in state {connectionState}");
                                }

                                // Reset connection count
                                _connectionCount = 0;

                                connectionState = DieConnectionState.Available;
                                SetLastError(DieLastError.Disconnected);

                                onDisconnectedUnexpectedly?.Invoke();
                            }
                        }
                    }, AppConstants.Instance.ConnectionTimeout);

                    yield return request;

                    bool canceled = connectionState != DieConnectionState.Connecting;
                    if (!canceled)
                    {
                        string error = null;
                        if (request.IsSuccess)
                        {
                            // Now connected to die, get characteristics and subscribe before switching to Identifying state

                            var characteristics = Central.GetPeripheralServiceCharacteristics(_peripheral, pixelService);
                            if ((characteristics != null) && characteristics.Contains(subscribeCharacteristic) && characteristics.Contains(writeCharacteristic))
                            {
                                request = Central.SubscribeCharacteristicAsync(_peripheral, pixelService, subscribeCharacteristic, data =>
                                {
                                    // Is Unity behavior still valid?
                                    if (this != null)
                                    {
                                        // Process the message coming from the actual die!
                                        var message = DieMessages.FromByteArray(data);
                                        if (message != null)
                                        {
                                            Debug.Log($"Die {name}: received message of type {message.GetType()}");

                                            if (messageDelegates.TryGetValue(message.type, out MessageReceivedDelegate del))
                                            {
                                                del.Invoke(message);
                                            }
                                        }
                                    }
                                });

                                yield return request;
                                if (!request.IsSuccess)
                                {
                                    error = $"Subscribe request failed: {request.ErrorMessage}";
                                }
                            }
                            else if (characteristics == null)
                            {
                                error = $"Characteristics request failed: {request.ErrorMessage}";
                            }
                            else
                            {
                                error = "Missing required characteristics";
                            }
                        }
                        else if (request.IsTimeOut)
                        {
                            error = "Timeout trying to connect to Die, is it too far, turned off or discharged?";
                        }
                        else
                        {
                            error = $"Connection failed: {request.ErrorMessage}";
                        }

                        // Check that we are still in the right state
                        canceled = connectionState != DieConnectionState.Connecting;
                        if ((!canceled) && (error == null))
                        {
                            // Move on to identification
                            yield return DoIdentifyAsync(err => error = err);

                            canceled = connectionState != DieConnectionState.Identifying;
                        }

                        if (!canceled)
                        {
                            if (error == null)
                            {
                                // Die is finally ready, awesome!
                                connectionState = DieConnectionState.Ready;

                                // Notify success
                                NotifyConnectionResult();
                            }
                            else
                            {
                                // Trigger callback
                                NotifyConnectionResult(error);

                                // Updating info didn't work, disconnect the die
                                DoDisconnect(DieLastError.ConnectionError);
                            }
                        }
                    }

                    if (canceled)
                    {
                        // Wrong state => we got canceled, just abort without notifying
                        Debug.Log($"Die {name}: connect sequence interrupted");
                    }
                }
            }
        }

        IEnumerator DoIdentifyAsync(System.Action<string> onResult)
        {
            string error = null;

            Debug.Assert(connectionState == DieConnectionState.Connecting);
            if (connectionState == DieConnectionState.Connecting)
            {
                // Remember that the die was just connected to (and trigger events)
                connectionState = DieConnectionState.Identifying;

                // Reset error
                SetLastError(DieLastError.None);

                // And have it update its info (unique Id, appearance, etc...) so it can finally be ready

                // Ask the die who it is!
                bool success = false;
                if (connectionState == DieConnectionState.Identifying)
                {
                    yield return GetDieInfoAsync((res, err) => (success, error) = (res, err));

                    if (success && (connectionState == DieConnectionState.Identifying))
                    {
                        // Get the die initial state
                        yield return GetDieState((res, err) => (success, error) = (res, err));
                    }
                }

                Debug.Assert(success == (error == null));
            }
            else
            {
                // Wrong state, just abort without notifying
                Debug.Log($"Die {name}: connect sequence interrupted");
            }

            onResult(error);
        }

        void NotifyConnectionResult(string error = null)
        {
            if (error != null)
            {
                Debug.LogError($"Die {name}: {error}");
            }

            var callbackCopy = onConnectionResult;
            onConnectionResult = null;
            callbackCopy?.Invoke(this, error == null, error);
        }

        /// <summary>
        /// Disconnects a die, doesn't remove it from the pool though
        /// </sumary>
        void DoDisconnect(DieLastError error = DieLastError.None)
        {
            if (error != DieLastError.None)
            {
                // We're disconnecting because of an error
                SetLastError(error);
            }

            Debug.Assert(isConnectingOrReady);
            if (isConnectingOrReady)
            {
                _connectionCount = 0;
                connectionState = DieConnectionState.Disconnecting;
                StartCoroutine(DisconnectAsync());

                IEnumerator DisconnectAsync()
                {
                    yield return Central.DisconnectPeripheralAsync(_peripheral);

                    Debug.Assert(_connectionCount == 0);
                    connectionState = DieConnectionState.Available;

                    var callbackCopy = onDisconnectionResult;
                    onDisconnectionResult = null;
                    callbackCopy?.Invoke(this, true, null); // Always return a success
                }
            }
        }

        void SetLastError(DieLastError newError)
        {
            lastError = newError;
            if (lastError != DieLastError.None)
            {
                GotError?.Invoke(this, newError);
            }
        }

        class WriteDataEnumerator : IOperationEnumerator
        {
            readonly Systemic.Pixels.Unity.BluetoothLE.RequestEnumerator _request;

            public bool IsDone => _request.IsDone;

            public bool IsSuccess => _request.IsSuccess;

            public bool IsTimeOut => _request.IsTimeOut;

            public string Error => _request.ErrorMessage;

            public object Current => null;

            public WriteDataEnumerator(Peripheral peripheral, byte[] bytes, float timeout)
            {
                _request = Central.WriteCharacteristicAsync(peripheral, pixelService, writeCharacteristic, bytes, timeout);
            }

            public bool MoveNext()
            {
                return _request.MoveNext();
            }

            public void Reset()
            {
                _request.Reset();
            }

            public void Dispose()
            {
            }
        }

        protected override IOperationEnumerator WriteDataAsync(byte[] bytes, float timeout = 0)
        {
            EnsureRunningOnMainThread();

            return new WriteDataEnumerator(_peripheral, bytes, timeout);

        }

        void OnDestroy()
        {
            onDisconnectedUnexpectedly = null;
            onConnectionResult = null;
            onDisconnectionResult = null;

            bool disconnect = isConnectingOrReady;
            _connectionCount = 0;
            connectionState = DieConnectionState.Invalid;

            Debug.Log($"Die {name}: destroyed (was connecting or connected: {disconnect})");

            if (disconnect)
            {
                Debug.Assert(_peripheral != null);
                var pool = DicePool.Instance;
                if (pool && pool.gameObject.activeInHierarchy)
                {
                    DicePool.Instance.StartCoroutine(Central.DisconnectPeripheralAsync(_peripheral));
                }
            }
        }
    }
}
