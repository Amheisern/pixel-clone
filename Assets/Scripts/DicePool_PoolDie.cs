using Dice;
using System.Collections;
using System.Collections.Concurrent;
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

        // Queue of coroutines to run one by one, usually BLE requests for a die
        readonly ConcurrentQueue<System.Func<IEnumerator>> _coroQueue = new ConcurrentQueue<System.Func<IEnumerator>>();

        // The underlying BLE device
        Peripheral _peripheral;

        // Count how many time Connect() was called, so we only disconnect after the same number of calls to Disconnect()
        int _connectionCount;

        // Connection internal events
        ConnectionResultHandler onConnectionResult;
        ConnectionResultHandler onDisconnectionResult;

        public delegate void ConnectionResultHandler(Die die, bool success, string errorMessage);

        /// <summary>
        /// Event triggered when die got disconnected for other reasons than a call to Disconnect().
        /// Most likely the BLE device was turned off or got out of range.
        /// </summary>
        public event System.Action onDisconnectedUnexpectedly;

        public string SystemId => _peripheral?.SystemId;

        public void Setup(Peripheral peripheral)
        {
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
                    deviceId = advData.deviceId;
                    state = advData.rollState;
                    face = advData.currentFace;
                    batteryLevel = advData.batteryLevel / 255f;
                    rssi = _peripheral.Rssi;

                    // Trigger callbacks
                    OnBatteryLevelChanged?.Invoke(this, batteryLevel, charging);
                    if (appearanceChanged)
                    {
                        OnAppearanceChanged?.Invoke(this, faceCount, designAndColor);
                    }
                    if (rollStateChanged)
                    {
                        OnStateChanged?.Invoke(this, state, face);
                    }
                    OnRssiChanged?.Invoke(this, rssi);
                }
                else
                {
                    Debug.LogError($"Incorrect advertising data length {_peripheral.ManufacturerData.Count}, expected: {size}");
                }
            }
        }

        public void ResetLastError()
        {
            lastError = DieLastError.None;
        }

        public void Connect(ConnectionResultHandler onConnectionResult = null)
        {
            void IncrementConnectCount()
            {
                ++_connectionCount;
                Debug.Log($"{name}: connect => counter={_connectionCount}");
            }

            switch (connectionState)
            {
                default:
                    string errorMessage = $"Die {name} in invalid die state {connectionState} while attempting to connect";
                    Debug.LogError(errorMessage);
                    onConnectionResult?.Invoke(this, false, errorMessage);
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
            switch (connectionState)
            {
                default:
                    // We are already disconnected
                    string errorMessage = $"Die {name} in invalid die state {connectionState} while attempting to disconnect";
                    Debug.LogError(errorMessage);
                    onDisconnectionResult?.Invoke(this, true, errorMessage); // Notify as a success but with an error message
                    break;
                case DieConnectionState.Ready:
                case DieConnectionState.Connecting:
                case DieConnectionState.Identifying:
                    Debug.Assert(_connectionCount > 0);
                    _connectionCount = forceDisconnect ? 0 : Mathf.Max(0, _connectionCount - 1);

                    Debug.Log($"{name}: disconnect => counter={_connectionCount}, forceDisconnect={forceDisconnect}");

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
                _coroQueue.Enqueue(ConnectAsync);

                IEnumerator ConnectAsync()
                {
                    var request = Central.ConnectPeripheralAsync(_peripheral, (p, connected) =>
                    {
                        if (this != null)
                        {
                            Debug.Assert(_peripheral == p);
                            Debug.Log($"{name}: peripheral {(connected ? "" : "dis")}connected");

                            if ((!connected) && (connectionState != DieConnectionState.Disconnecting))
                            {
                                string errorMessage = "Disconnected unexpectedly";
                                Debug.LogError($"{name}: {errorMessage}");

                                if ((connectionState == DieConnectionState.Connecting) || (connectionState == DieConnectionState.Identifying))
                                {
                                    NotifyConnectionResult(errorMessage);
                                }

                                // Reset connection count
                                _connectionCount = 0;

                                connectionState = DieConnectionState.Available;
                                SetLastError(DieLastError.Disconnected);

                                onDisconnectedUnexpectedly?.Invoke();
                            }
                        }
                    });

                    yield return request;

                    if (connectionState == DieConnectionState.Connecting)
                    {
                        string errorMessage = null;
                        if (request.IsSuccess)
                        {
                            // Now connected to die, get characteristics and subscribe before switching to Identifying state

                            var characteristics = Central.GetPeripheralServiceCharacteristics(_peripheral, pixelService);
                            if ((characteristics != null) && characteristics.Contains(subscribeCharacteristic) && characteristics.Contains(writeCharacteristic))
                            {
                                request = Central.SubscribeCharacteristicAsync(_peripheral, pixelService, subscribeCharacteristic, data =>
                                {
                                    // Process the message coming from the actual die!
                                    var message = DieMessages.FromByteArray(data);
                                    if (message != null)
                                    {
                                        Debug.Log($"{name}: Got message of type {message.GetType()}");

                                        if (messageDelegates.TryGetValue(message.type, out MessageReceivedDelegate del))
                                        {
                                            del.Invoke(message);
                                        }
                                    }
                                });

                                yield return request;
                                if (!request.IsSuccess)
                                {
                                    errorMessage = "Subscribe request failed";
                                }
                            }
                            else
                            {
                                errorMessage = "Characteristics request failed or returned unexpected value";
                            }
                        }
                        else
                        {
                            errorMessage = "Connect request failed";
                        }

                        // Check that we are still in the right state
                        if (connectionState == DieConnectionState.Connecting)
                        {
                            // Everything ok?
                            if (errorMessage == null)
                            {
                                // Move on to identification
                                DoIdentify();
                            }
                            else
                            {
                                // Trigger callback
                                NotifyConnectionResult(errorMessage);

                                // Disconnect
                                DoDisconnect(DieLastError.ConnectionError);
                            }
                        }
                        else
                        {
                            // Wrong state, just abort without notifying
                            Debug.Log($"{name}: Connect sequence interrupted");
                        }
                    }
                    else
                    {
                        // Wrong state, just abort without notifying
                        Debug.Log($"{name}: Connect sequence interrupted");
                    }
                }
            }
        }

        void DoIdentify()
        {
            Debug.Assert(connectionState == DieConnectionState.Connecting);
            if (connectionState == DieConnectionState.Connecting)
            {
                // Remember that the die was just connected to (and trigger events)
                connectionState = DieConnectionState.Identifying;

                // Reset error
                SetLastError(DieLastError.None);

                // And have it update its info (unique Id, appearance, etc...) so it can finally be ready
                StartCoroutine(UpdateInfoCr());

                IEnumerator UpdateInfoCr()
                {
                    string errorMessage = null;

                    // Ask the die who it is!
                    yield return GetDieInfo(res => { if (!res) errorMessage = "Failed to get die info"; });

                    if (errorMessage == null)
                    {
                        // Get the die initial state
                        yield return GetDieState(res => { if (!res) errorMessage = "Failed to get die state"; });
                    }

                    // Check that we are still in the right state
                    if (connectionState == DieConnectionState.Identifying)
                    {
                        // Everything ok?
                        if (errorMessage == null)
                        {
                            // Die is finally ready, awesome!
                            connectionState = DieConnectionState.Ready;

                            // Notify success
                            NotifyConnectionResult();
                        }
                        else
                        {
                            // Trigger callback
                            NotifyConnectionResult(errorMessage);

                            // Updating info didn't work, disconnect the die
                            DoDisconnect(DieLastError.ConnectionError);
                        }
                    }
                    else
                    {
                        // Wrong state, just abort without notifying
                        Debug.Log($"{name}: Identify sequence interrupted");
                    }
                }
            }
        }

        void NotifyConnectionResult(string errorMessage = null)
        {
            if (errorMessage != null)
            {
                Debug.LogError($"{_peripheral.Name}: {errorMessage}");
            }

            var callbackCopy = onConnectionResult;
            onConnectionResult = null;
            callbackCopy?.Invoke(this, errorMessage == null, errorMessage);
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

            Debug.Assert(_connectionCount == 0);
            Debug.Assert(isConnectingOrReady);
            if (isConnectingOrReady)
            {
                connectionState = DieConnectionState.Disconnecting;

                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                //TODO ------------------------------- Clear Queue >>>>>>>>>>>>>>
                StartCoroutine(DisconnectAsync());

                IEnumerator DisconnectAsync()
                {
                    var request = Central.DisconnectPeripheralAsync(_peripheral);
                    yield return request;

                    Debug.Assert(_connectionCount == 0);
                    if (request.IsSuccess)
                    {
                        connectionState = DieConnectionState.Available;
                    }
                    else
                    {
                        // Could not disconnect the die, indicate that!
                        SetLastError(DieLastError.ConnectionError);
                    }

                    var callbackCopy = onDisconnectionResult;
                    onDisconnectionResult = null;
                    callbackCopy?.Invoke(this, request.IsSuccess, request?.ErrorMessage);
                }
            }
        }

        void SetLastError(DieLastError newError)
        {
            lastError = newError;
            if (lastError != DieLastError.None)
            {
                OnError?.Invoke(this, newError);
            }
        }

        protected override void WriteData(byte[] bytes, System.Action<Die, bool, string> onWriteResult)
        {
            _coroQueue.Enqueue(WriteAsync);

            IEnumerator WriteAsync()
            {
                var request = Central.WriteCharacteristicAsync(_peripheral, pixelService, writeCharacteristic, bytes);
                yield return request;
                onWriteResult?.Invoke(this, request.IsSuccess, request?.ErrorMessage);
            }
        }

        IEnumerator Start()
        {
            while (true)
            {
                if (_coroQueue.TryDequeue(out var coro))
                {
                    yield return coro();
                }
                else
                {
                    yield return null;
                }
            }
        }

        void OnDestroy()
        {
            Debug.LogError("OnDestroy " + name);

            onDisconnectedUnexpectedly = null;
            onConnectionResult = null;
            onDisconnectionResult = null;

            bool disconnect = isConnectingOrReady;
            _connectionCount = 0;
            connectionState = DieConnectionState.Invalid;

            if (disconnect)
            {
                Debug.Assert(_peripheral != null);
                DicePool.Instance.StartCoroutine(Central.DisconnectPeripheralAsync(_peripheral));
            }
        }
    }
}
