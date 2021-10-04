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

        int _currentConnectionCount = 0;
        float _lastRequestDisconnectTime = 0.0f;

        public delegate void ConnectionResultHandler(Die die, bool success, string errorMessage);
        public event ConnectionResultHandler onConnectionResult;
        public event ConnectionResultHandler onDisconnectionResult;

        public Peripheral peripheral { get; private set; }

        public void Setup(Peripheral peripheral)
        {
            //bool appearanceChanged = faceCount != peripheral.faceCount || designAndColor != peripheral.designAndColor;
            if (this.peripheral == null)
            {
                SetConnectionState(DieConnectionState.Available);
            }
            this.peripheral = peripheral;
            name = peripheral.Name;
            //deviceId = peripheral.deviceId;
            //faceCount = peripheral.faceCount;
            //designAndColor = peripheral.designAndColor;
            //if (appearanceChanged)
            //{
            //    OnAppearanceChanged?.Invoke(this, faceCount, this.designAndColor);
            //}

            if (peripheral.ManufacturerData?.Count > 0)
            {
                // Marshall the data into the struct we expect
                int size = Marshal.SizeOf(typeof(PixelAdvertisingData));
                if (peripheral.ManufacturerData.Count == size)
                {
                    System.IntPtr ptr = Marshal.AllocHGlobal(size);
                    Marshal.Copy(peripheral.ManufacturerData.ToArray(), 0, ptr, size);
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
                    batteryLevel = (float)advData.batteryLevel / 255.0f;
                    rssi = peripheral.Rssi;

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
                    Debug.LogError($"Incorrect advertising data length {peripheral.ManufacturerData.Count}, expected: {size}");
                }
            }
        }

        public void ResetLastError()
        {
            lastError = DieLastError.None;
        }

        public void Connect(ConnectionResultHandler onConnectionResult)
        {
            Debug.Log($"{name}: Before request connect = {_currentConnectionCount}");
            if (_currentConnectionCount == 0)
            {
                _currentConnectionCount += 1;
            }
            else
            {
                // Keep dice connected unless specifically asked to disconnect in the pool
                // This is a bit of a hack to prevent communications errors and make the connected/disconnected
                // state work more like users expect.
                _currentConnectionCount += 2;
            }
            switch (connectionState)
            {
                default:
                    string errorMessage = $"Die {name} in invalid die state {connectionState} while attempting to connect";
                    Debug.LogError(errorMessage);
                    onConnectionResult?.Invoke(this, false, errorMessage);
                    break;
                case DieConnectionState.Available:
                    Debug.Assert(_currentConnectionCount == 1);
                    this.onConnectionResult += onConnectionResult;
                    DoConnectDie();
                    break;
                case DieConnectionState.Connecting:
                case DieConnectionState.Identifying:
                    // Already in the process of connecting, just add the callback and wait
                    this.onConnectionResult += onConnectionResult;
                    break;
                case DieConnectionState.Ready:
                    // Trigger the callback immediately
                    onConnectionResult?.Invoke(this, true, null);
                    break;
            }
            Debug.Log($"{name}: After request connect = {_currentConnectionCount}");
        }

        public void Disconnect(ConnectionResultHandler onDisconnectionResult)
        {
            Debug.Log($"{name}: Before request disconnect = {_currentConnectionCount}");
            switch (connectionState)
            {
                default:
                    string errorMessage = $"Die {name} in invalid die state {connectionState} while attempting to disconnect";
                    Debug.LogError(errorMessage);
                    onDisconnectionResult?.Invoke(this, false, errorMessage);
                    break;
                case DieConnectionState.Ready:
                case DieConnectionState.Connecting:
                case DieConnectionState.Identifying:
                    // Register to be notified when disconnection is complete
                    _currentConnectionCount--;
                    if (_currentConnectionCount == 0)
                    {
                        this.onDisconnectionResult += onDisconnectionResult;
                        _lastRequestDisconnectTime = Time.time;
                    }
                    break;
            }
            Debug.Log($"{name}: After request disconnect = {_currentConnectionCount}");
        }

        void DoConnectDie()
        {
            if (connectionState == DieConnectionState.Available)
            {
                /// <summary>
                /// Called by central when a die is properly connected to (i.e. two-way communication is working)
                /// We still need to do a bit of work before the die can be available for general us though
                /// </sumary>
                static void OnDieConnected(PoolDie poolDie, bool result, string errorMessage)
                {
                    if (result)
                    {
                        // Remember that the die was just connected to (and trigger events)
                        poolDie.SetConnectionState(DieConnectionState.Identifying);

                        // And have it update its info (unique Id, appearance, etc...) so it can finally be ready
                        poolDie.UpdateInfo();

                        // Reset error
                        poolDie.SetLastError(DieLastError.None);
                    }
                    else
                    {
                        // Remember that the die was just connected to (and trigger events)
                        poolDie.SetConnectionState(DieConnectionState.Available);

                        // Could not connect to the die, indicate that!
                        poolDie.SetLastError(DieLastError.ConnectionError);

                        // Reset connection count since it didn't succeed
                        poolDie._currentConnectionCount = 0;

                        // Trigger callback
                        var callbackCopy = poolDie.onConnectionResult;
                        poolDie.onConnectionResult = null;
                        callbackCopy?.Invoke(poolDie, false, errorMessage);
                    }
                }

                /// <summary>
                /// Called by Central when a die gets disconnected unexpectedly
                /// </sumary>
                static void OnDieDisconnectedUnexpectedly(PoolDie poolDie)
                {
                    Debug.LogError(poolDie.peripheral.Name + ": Die disconnected");

                    poolDie.SetConnectionState(DieConnectionState.Available);
                    poolDie.SetLastError(DieLastError.Disconnected);

                    // Reset connection count since it didn't succeed
                    poolDie._currentConnectionCount = 0;

                    // Forget the die for now
                    //TODO DestroyDie(this); onDisconnectionResult?.Invoke(this, false, "Disconnected unexpectedly") ?
                }

                SetConnectionState(DieConnectionState.Connecting);
                _coroQueue.Enqueue(ConnectAsync);

                IEnumerator ConnectAsync()
                {
                    var request = Central.ConnectPeripheralAsync(peripheral, (_, connected) =>
                    {
                        if (!connected)
                        {
                            if (connectionState == DieConnectionState.Disconnecting)
                            {
                                OnDieConnected(this, false, null);
                            }
                            else
                            {
                                OnDieDisconnectedUnexpectedly(this);
                            }

                            onDisconnectionResult?.Invoke(this, true, null);
                        }
                    });

                    yield return request;

                    if (request.IsSuccess)
                    {
                        var characteristics = Central.GetPeripheralServiceCharacteristics(peripheral, pixelService);
                        if ((characteristics != null) && characteristics.Contains(subscribeCharacteristic) && characteristics.Contains(writeCharacteristic))
                        {

                            void OnData(byte[] data)
                            {
                                // Process the message coming from the actual die!
                                var message = DieMessages.FromByteArray(data);
                                if (message != null)
                                {
                                    Debug.Log("Got message of type " + message.GetType());

                                    if (messageDelegates.TryGetValue(message.type, out MessageReceivedDelegate del))
                                    {
                                        del.Invoke(message);
                                    }
                                }
                            }

                            request = Central.SubscribeCharacteristicAsync(peripheral, pixelService, subscribeCharacteristic, data => OnData(data));
                            yield return request;
                            if (request.IsSuccess)
                            {
                                OnDieConnected(this, true, null);
                            }
                            else
                            {
                                OnDieConnected(this, false, "subscribe request failed"); //TODO
                            }
                        }
                        else
                        {
                            OnDieConnected(this, false, "characteristics request failed"); //TODO
                        }
                    }
                    else
                    {
                        OnDieConnected(this, false, "connect request failed"); //TODO
                    }
                }
            }
            else
            {
                Debug.LogError("Die " + name + " not in available state, instead: " + connectionState);
            }
        }

        /// <summary>
        /// Disconnects a die, doesn't remove it from the pool though
        /// </sumary>
        void DoDisconnect()
        {
            if (connectionState == DieConnectionState.Ready)
            {
                SetConnectionState(DieConnectionState.Disconnecting);
                _coroQueue.Enqueue(DisconnectAsync);

                IEnumerator DisconnectAsync()
                {
                    var request = Central.DisconnectPeripheralAsync(peripheral);
                    yield return request;

                    if (request.IsSuccess)
                    {
                        SetConnectionState(DieConnectionState.Available);

                        // Reset connection count now that nothing is connected to the die
                        _currentConnectionCount = 0;
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
            else
            {
                Debug.LogError($"Die {name} not in ready state, instead: {connectionState}");
            }
        }

        void SetConnectionState(DieConnectionState newState)
        {
            if (newState != connectionState)
            {
                var oldState = connectionState;
                connectionState = newState;
                OnConnectionStateChanged?.Invoke(this, oldState, newState);
            }
        }

        void SetLastError(DieLastError newError)
        {
            lastError = newError;
            OnError?.Invoke(this, newError);
        }

        void UpdateInfo()
        {
            if (connectionState == DieConnectionState.Identifying)
            {
                StartCoroutine(UpdateInfoCr());

                IEnumerator UpdateInfoCr()
                {
                    // Ask the die who it is!
                    yield return GetDieInfo(null);

                    // Ping the die so we know its initial state
                    Ping();
                    //TODO wait for response?

                    OnDieReady(true);
                }
            }
            else
            {
                OnDieReady(false);
            }

            /// <summary>
            /// Called by the die once it has fetched its updated information (appearance, unique Id, etc.)
            /// </sumary>
            void OnDieReady(bool ready)
            {
                if (ready)
                {
                    // Die is finally ready, awesome!
                    SetConnectionState(DieConnectionState.Ready);
                }
                else
                {
                    // Updating info didn't work, disconnect the die
                    DoDisconnect();
                }

                // Trigger callback either way
                var callbackCopy = onConnectionResult;
                onConnectionResult = null;
                callbackCopy?.Invoke(this, ready, null);
            }
        }

        protected override void WriteData(byte[] bytes, System.Action<Die, bool, string> onWriteResult)
        {
            _coroQueue.Enqueue(WriteAsync);

            IEnumerator WriteAsync()
            {
                var request = Central.WriteCharacteristicAsync(peripheral, pixelService, writeCharacteristic, bytes);
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

        void Update()
        {
            if ((connectionState == DieConnectionState.Ready) && (_currentConnectionCount == 0)
                // Die is waiting to disconnect
                && (Time.time - _lastRequestDisconnectTime > AppConstants.Instance.DicePoolDisconnectDelay))
            {
                // Go ahead and disconnect
                DoDisconnect();
            }
        }

        void OnDisable()
        {
            SetConnectionState(DieConnectionState.Invalid);
        }
    }
}
