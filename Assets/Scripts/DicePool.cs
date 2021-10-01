using Dice;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

using Central = Systemic.Pixels.Unity.BluetoothLE.Central;
using Peripheral = Systemic.Pixels.Unity.BluetoothLE.ScannedPeripheral;

public class DicePool : SingletonMonoBehaviour<DicePool>
{
    static readonly System.Guid pixelService = new System.Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    static readonly System.Guid subscribeCharacteristic = new System.Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    static readonly System.Guid writeCharacteristic = new System.Guid("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    public delegate void BluetoothErrorEvent(string errorString);
    public delegate void DieCreationEvent(Die die);

    // A bunch of events for UI to hook onto and display pool state updates
    public event DieCreationEvent onDieDiscovered;
    public event DieCreationEvent onWillDestroyDie;

    class PoolDie
    {
        public Die die;
        public Peripheral peripheral;
        public System.Action<Die.ConnectionState> setState;
        public System.Action<Die.LastError> setError;
        public System.Action<Die, bool, string> onConnectionResult;
        public System.Action<Die, bool, string> onDisconnectionResult;

        public int currentConnectionCount = 0;
        public float lastRequestDisconnectTime = 0.0f;
    }

    readonly List<PoolDie> _pool = new List<PoolDie>();

    // Queue of coroutines to run one by one, usually BLE requests for a die
    readonly ConcurrentQueue<System.Func<IEnumerator>> _coroQueue = new ConcurrentQueue<System.Func<IEnumerator>>();

    // Multiple things may request bluetooth scanning, so we need to arbitrate when
    // we actually ask Central to scan or not. This counter will let us know
    // exactly when to start or stop asking central.
    int _scanRequestCount = 0;

    public IEnumerable<Die> allDice => _pool.Select(d => d.die);

    public void ResetDiceErrors()
    {
        foreach (var die in _pool)
        {
            die.setError(Die.LastError.None);
        }
    }
    
    /// <summary>
    /// Start scanning for new and existing dice, filling our lists in the process from
    /// events triggered by Central.
    /// </sumary>
    public void BeginScanForDice()
    {
        _scanRequestCount++;
        if (_scanRequestCount == 1)
        {
            Central.PeripheralDiscovered += OnPeripheralDiscovered;
            Central.ScanForPeripheralsWithServices(new[] { pixelService });
        }
        else
        {
            Debug.Log("Already scanning, scanRequestCount=" + _scanRequestCount);
        }
    }

    /// <summary>
    /// Stops the current scan 
    /// </sumary>
    public void StopScanForDice()
    {
        if (_scanRequestCount == 0)
        {
            Debug.LogError("Pool not currently scanning");
        }
        else
        {
            _scanRequestCount--;
            if (_scanRequestCount == 0)
            {
                Central.PeripheralDiscovered -= OnPeripheralDiscovered;
                Central.StopScan();
            }
            // Else ignore
        }
    }

    public void ClearScanList()
    {
        Debug.Log("Clearing scan list");

        var diceCopy = new List<PoolDie>(_pool);
        foreach (var die in diceCopy)
        {
            if (die.die != null && die.die.connectionState == Die.ConnectionState.Available)
            {
                DestroyDie(die);
            }
        }
        Central.ClearScannedList();
    }

    public void ConnectDie(Die die, System.Action<Die, bool, string> onConnectionResult)
    {
        var poolDie = _pool.FirstOrDefault(d => d.die == die);
        if (poolDie != null)
        {
            Debug.Log(poolDie.die.name + ": Before request connect: " + poolDie.currentConnectionCount);
            if (poolDie.currentConnectionCount == 0)
            {
                poolDie.currentConnectionCount += 1;
            }
            else
            {
                // Keep dice connected unless specifically asked to disconnect in the pool
                // This is a bit of a hack to prevent communications errors and make the connected/disconnected
                // state work more like users expect.
                poolDie.currentConnectionCount += 2;
            }
            switch (poolDie.die.connectionState)
            {
                default:
                    string errorMessage = "Die " + die.name + " in invalid die state " + poolDie.die.connectionState + " while attempting to connect";
                    Debug.LogError(errorMessage);
                    onConnectionResult?.Invoke(die, false, errorMessage);
                    break;
                case Die.ConnectionState.Available:
                    Debug.Assert(poolDie.currentConnectionCount == 1);
                    poolDie.onConnectionResult += onConnectionResult;
                    DoConnectDie(die);
                    break;
                case Die.ConnectionState.Connecting:
                case Die.ConnectionState.Identifying:
                    // Already in the process of connecting, just add the callback and wait
                    poolDie.onConnectionResult += onConnectionResult;
                    break;
                case Die.ConnectionState.Ready:
                    // Trigger the callback immediately
                    onConnectionResult?.Invoke(die, true, null);
                    break;
            }
            Debug.Log(poolDie.die.name + ": After request connect: " + poolDie.currentConnectionCount);
        }
        else
        {
            string errorMessage = "Pool attempting to connect to unknown die " + die.name;
            Debug.LogError(errorMessage);
            onConnectionResult?.Invoke(die, false, errorMessage);
        }
    }

    public void DisconnectDie(Die die, System.Action<Die, bool, string> onDisconnectionResult)
    {
        string errorMessage = null;
        var poolDie = _pool.FirstOrDefault(d => d.die == die);
        if (poolDie != null)
        {
            Debug.Log(poolDie.die.name + ": Before request disconnect: " + poolDie.currentConnectionCount);
            switch (poolDie.die.connectionState)
            {
                default:
                    errorMessage = "Die " + die.name + " in invalid die state " + poolDie.die.connectionState + " while attempting to disconnect";
                    Debug.LogError(errorMessage);
                    onDisconnectionResult?.Invoke(die, false, errorMessage);
                    break;
                case Die.ConnectionState.Ready:
                case Die.ConnectionState.Connecting:
                case Die.ConnectionState.Identifying:
                    // Register to be notified when disconnection is complete
                    poolDie.currentConnectionCount--;
                    if (poolDie.currentConnectionCount == 0)
                    {
                        poolDie.onDisconnectionResult += onDisconnectionResult;
                        poolDie.lastRequestDisconnectTime = Time.time;
                    }
                    break;
            }
            Debug.Log(poolDie.die.name + ": After request disconnect: " + poolDie.currentConnectionCount);
        }
        else
        {
            errorMessage = "Pool attempting to disconnect to unknown die " + die.name;
            Debug.LogError(errorMessage);
            onDisconnectionResult?.Invoke(die, false, errorMessage);
        }
    }

    /// <summary>
    /// Removes a die from the pool, as if we never new it.
    /// Note: We may very well 'discover' it again the next time we scan.
    /// </sumary>
    public void ForgetDie(Die die, System.Action<Die, bool, string> onForgetDieResult)
    {
        var poolDie = _pool.FirstOrDefault(d => d.die == die);
        if (poolDie != null)
        {
            switch (poolDie.die.connectionState)
            {
                default:
                    DestroyDie(poolDie);
                    onForgetDieResult?.Invoke(die, true, null);
                    break;
                case Die.ConnectionState.Ready:
                case Die.ConnectionState.Connecting:
                case Die.ConnectionState.Identifying:
                    // Disconnect!
                    DisconnectDie(die, (d, r, s) =>
                    {
                        DestroyDie(poolDie);
                        onForgetDieResult?.Invoke(d, r, s);
                    });
                    break;
            }
        }
        else
        {
            string errorMessage = "Pool attempting to forget unknown die " + die.name;
            Debug.LogError(errorMessage);
            onForgetDieResult?.Invoke(die, false, errorMessage);
        }
    }

    /// <summary>
    /// Write some data to the die
    /// </sumary>
    public void WriteDie(Die die, byte[] bytes, System.Action<Die, bool, string> onWriteResult)
    {
        var dt = _pool.First(p => p.die == die);
        _coroQueue.Enqueue(() => WriteAsync(dt, bytes, onWriteResult));

        static IEnumerator WriteAsync(PoolDie dt, byte[] bytes, System.Action<Die, bool, string> onWriteResult)
        {
            var request = Central.WriteCharacteristicAsync(dt.peripheral, pixelService, writeCharacteristic, bytes);
            yield return request;
            onWriteResult?.Invoke(dt.die, request.IsSuccess, request?.ErrorMessage);
        }
    }

    IEnumerator Start()
    {
        Central.Initialize(); //TODO handle error + user message

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
        foreach (var poolDie in _pool)
        {
            if (poolDie.die != null)
            {
                switch (poolDie.die.connectionState)
                {
                    case Die.ConnectionState.Ready:
                        if (poolDie.currentConnectionCount == 0)
                        {
                            // Die is waiting to disconnect
                            if (Time.time - poolDie.lastRequestDisconnectTime > AppConstants.Instance.DicePoolDisconnectDelay)
                            {
                                // Go ahead and disconnect
                                DoDisconnectDie(poolDie.die);
                            }
                        }
                        break;
                    default:
                        // Do nothing
                        break;
                }
            }
        }
    }

    void DoConnectDie(Die die)
    {
        if (die.connectionState == Die.ConnectionState.Available)
        {
            var dt = _pool.First(p => p.die == die);
            dt.setState.Invoke(Die.ConnectionState.Connecting);
            _coroQueue.Enqueue(() => ConnectAsync(dt, OnDieConnected, () => dt.onDisconnectionResult?.Invoke(die, true, null), OnDieDisconnectedUnexpectedly));

            static IEnumerator ConnectAsync(PoolDie dt,
                System.Action<PoolDie, bool, string> onDieConnected,
                System.Action onDieDisconnected,
                System.Action<PoolDie, string> onDieDisconnectedUnexpectedly)
            {
                var request = Central.ConnectPeripheralAsync(dt.peripheral, (_, connected) =>
                {
                    if (!connected)
                    {
                        if (dt.die.connectionState == Die.ConnectionState.Disconnecting)
                        {
                            onDieConnected(dt, false, null);
                        }
                        else
                        {
                            onDieDisconnectedUnexpectedly(dt, "disconnected"); //TODO
                        }
                        onDieDisconnected();
                    }
                });
                yield return request;
                if (request.IsSuccess)
                {
                    var characteristics = Central.GetPeripheralServiceCharacteristics(dt.peripheral, pixelService);
                    if ((characteristics != null) && characteristics.Contains(subscribeCharacteristic) && characteristics.Contains(writeCharacteristic))
                    {
                        request = Central.SubscribeCharacteristicAsync(dt.peripheral, pixelService, subscribeCharacteristic, data => dt.die.OnData(data));
                        yield return request;
                        if (request.IsSuccess)
                        {
                            onDieConnected(dt, true, null);
                        }
                        else
                        {
                            onDieConnected(dt, false, "subscribe request failed"); //TODO
                        }
                    }
                    else
                    {
                        onDieConnected(dt, false, "characteristics request failed"); //TODO
                    }
                }
                else
                {
                    onDieConnected(dt, false, "connect request failed"); //TODO
                }
            }
        }
        else
        {
            Debug.LogError("Die " + die.name + " not in available state, instead: " + die.connectionState);
        }
    }

    /// <summary>
    /// Disconnects a die, doesn't remove it from the pool though
    /// </sumary>
    void DoDisconnectDie(Die die)
    {
        if (die.connectionState == Die.ConnectionState.Ready)
        {
            // When disconnecting from a destroy, the die will have already been removed
            var dt = _pool.FirstOrDefault(p => p.die == die);
            if (dt != null)
            {
                dt.setState.Invoke(Die.ConnectionState.Disconnecting);
                _coroQueue.Enqueue(() => DisconnectAsync(dt));

                static IEnumerator DisconnectAsync(PoolDie dt)
                {
                    var request = Central.DisconnectPeripheralAsync(dt.peripheral);
                    yield return request;

                    if (request.IsSuccess)
                    {
                        dt.setState.Invoke(Die.ConnectionState.Available);

                        // Reset connection count now that nothing is connected to the die
                        dt.currentConnectionCount = 0;
                    }
                    else
                    {
                        // Could not disconnect the die, indicate that!
                        dt.setError.Invoke(Die.LastError.ConnectionError);
                    }

                    var callbackCopy = dt.onDisconnectionResult;
                    dt.onDisconnectionResult = null;
                    callbackCopy?.Invoke(dt.die, request.IsSuccess, request?.ErrorMessage);
                }
            }
        }
        else
        {
            Debug.LogError($"Die {die.name} not in ready state, instead: {die.connectionState}");
        }
    }

    /// <summary>
    /// Called by Central when a new die is discovered!
    /// </sumary>
    void OnPeripheralDiscovered(Peripheral peripheral)
    {
        Debug.Log($"Discovered dice {peripheral.Name}");

        // If the die exists, tell it that it's advertising now
        // otherwise create it (and tell it that its advertising :)
        var ourDie = _pool.FirstOrDefault(d => peripheral.SystemId == d.peripheral.SystemId);
        if (ourDie == null)
        {
            // Never seen this die before
            ourDie = CreateDie(peripheral);
            ourDie.setState.Invoke(Die.ConnectionState.Available);
            onDieDiscovered?.Invoke(ourDie.die);
        }
        else
        {
            onDieDiscovered?.Invoke(ourDie.die);

            if (ourDie.die.connectionState != Die.ConnectionState.Available)
            {
                // All other are errors
                Debug.LogError($"Die {ourDie.die.name} in invalid state: {ourDie.die.connectionState}");
                ourDie.setState(Die.ConnectionState.Available);
            }
        }

        if (peripheral.ManufacturerData?.Count > 0)
        {
            // Marshall the data into the struct we expect
            int size = Marshal.SizeOf(typeof(Die.CustomAdvertisingData));
            if (peripheral.ManufacturerData.Count == size)
            {
                System.IntPtr ptr = Marshal.AllocHGlobal(size);
                Marshal.Copy(peripheral.ManufacturerData.ToArray(), 0, ptr, size);
                var customData = Marshal.PtrToStructure<Die.CustomAdvertisingData>(ptr);
                Marshal.FreeHGlobal(ptr);

                // Update die data
                ourDie.die.UpdateAdvertisingData(peripheral.Rssi, customData);
                onDieDiscovered?.Invoke(ourDie.die);
            }
            else
            {
                Debug.LogError($"Incorrect advertising data length {peripheral.ManufacturerData.Count}, expected: {size}");
            }
        }
    }

    /// <summary>
    /// Called by central when a die is properly connected to (i.e. two-way communication is working)
    /// We still need to do a bit of work before the die can be available for general us though
    /// </sumary>
    void OnDieConnected(PoolDie dt, bool result, string errorMessage)
    {
        if (result)
        {
            // Remember that the die was just connected to (and trigger events)
            dt.setState.Invoke(Die.ConnectionState.Identifying);

            // And have it update its info (unique Id, appearance, etc...) so it can finally be ready
            dt.die.UpdateInfo(OnDieReady);

            // Reset error
            dt.setError(Die.LastError.None);
        }
        else
        {
            // Remember that the die was just connected to (and trigger events)
            dt.setState.Invoke(Die.ConnectionState.Available);

            // Could not connect to the die, indicate that!
            dt.setError(Die.LastError.ConnectionError);

            // Reset connection count since it didn't succeed
            dt.currentConnectionCount = 0;

            // Trigger callback
            var callbackCopy = dt.onConnectionResult;
            dt.onConnectionResult = null;
            callbackCopy?.Invoke(dt.die, false, errorMessage);
        }
    }

    /// <summary>
    /// Called by the die once it has fetched its updated information (appearance, unique Id, etc.)
    /// </sumary>
    void OnDieReady(Die die, bool ready)
    {
        var ourDie = _pool.FirstOrDefault(d => d.die == die);
        if (ourDie != null)
        {
            if (ready)
            {
                // Die is finally ready, awesome!
                ourDie.setState.Invoke(Die.ConnectionState.Ready);
            }
            else
            {
                // Updating info didn't work, disconnect the die
                DoDisconnectDie(die);
            }

            // Trigger callback either way
            var callbackCopy = ourDie.onConnectionResult;
            ourDie.onConnectionResult = null;
            callbackCopy?.Invoke(ourDie.die, ready, null);
        }
        else
        {
            Debug.LogError("Received Die ready notification for unknown die " + die.name);
        }
    }

    /// <summary>
    /// Called by Central when a die gets disconnected unexpectedly
    /// </sumary>
    void OnDieDisconnectedUnexpectedly(PoolDie dt, string errorMessage)
    {
        Debug.LogError(dt.peripheral.Name  + ": Die disconnected");

        dt.setState.Invoke(Die.ConnectionState.Available);
        dt.setError.Invoke(Die.LastError.Disconnected);

        // Reset connection count since it didn't succeed
        dt.currentConnectionCount = 0;

        // Forget the die for now
        DestroyDie(dt);
    }

    /// <summary>
    /// Creates a new die for the pool
    /// </sumary>
    PoolDie CreateDie(Peripheral peripheral, uint deviceId = 0, int faceCount = 0, DesignAndColor design = DesignAndColor.Unknown)
    {
        var dieObj = new GameObject(name);
        dieObj.transform.SetParent(transform);
        Die die = dieObj.AddComponent<Die>();
        System.Action<Die.ConnectionState> setStateAction;
        System.Action<Die.LastError> setLastErrorAction;
        die.Setup(peripheral.Name, deviceId, faceCount, design, out setStateAction, out setLastErrorAction);
        var ourDie = new PoolDie()
        {
            die = die,
            peripheral = peripheral,
            setState = setStateAction,
            setError = setLastErrorAction,
        };
        _pool.Add(ourDie);

        return ourDie;
    }

    /// <summary>
    /// Cleanly destroys a die, disconnecting if necessary and triggering events in the process
    /// Does not remove it from the list though
    /// </sumary>
    void DestroyDie(PoolDie ourDie)
    {
        void doDestroy()
        {
            // Trigger event
            onWillDestroyDie?.Invoke(ourDie.die);

            ourDie.setState.Invoke(Die.ConnectionState.Invalid);
            GameObject.Destroy(ourDie.die.gameObject);
            _pool.Remove(ourDie);
        }

        switch (ourDie.die.connectionState)
        {
            default:
                doDestroy();
                break;
            case Die.ConnectionState.Ready:
                // Register to be notified when disconnection is complete
                if (ourDie.currentConnectionCount == 0)
                {
                    ourDie.onDisconnectionResult += (d, r, s) => doDestroy();
                    DoDisconnectDie(ourDie.die);
                }
                break;
        }
    }

    /// <summary>
    /// Destroys all dice that fit a predicate, or all dice if predicate is null
    /// </sumary>
    void DestroyAll(System.Predicate<Die> predicate = null)
    {
        if (predicate == null)
        {
            predicate = (d) => true;
        }
        var diceCopy = new List<PoolDie>(_pool);
        foreach (var die in diceCopy)
        {
            if (predicate(die.die))
            {
                DestroyDie(die);
            }
        }
    }

}
