using Dice;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

using Central = Systemic.Pixels.Unity.BluetoothLE.Central;
using Peripheral = Systemic.Pixels.Unity.BluetoothLE.ScannedPeripheral;

public partial class DicePool : SingletonMonoBehaviour<DicePool>
{
    static readonly System.Guid pixelService = new System.Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    static readonly System.Guid subscribeCharacteristic = new System.Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    static readonly System.Guid writeCharacteristic = new System.Guid("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    readonly List<PoolDie> _pool = new List<PoolDie>();

    // Queue of coroutines to run one by one, usually BLE requests for a die
    readonly ConcurrentQueue<System.Func<IEnumerator>> _coroQueue = new ConcurrentQueue<System.Func<IEnumerator>>();

    // Multiple things may request bluetooth scanning, so we need to arbitrate when
    // we actually ask Central to scan or not. This counter will let us know
    // exactly when to start or stop asking central.
    int _scanRequestCount = 0;

    public delegate void BluetoothErrorEvent(string errorString);
    public delegate void DieCreationEvent(Die die);

    // A bunch of events for UI to hook onto and display pool state updates
    public static event DieCreationEvent onDieDiscovered;

    public Die[] allConnectedDice => _pool.ToArray();

    public void ResetDiceErrors()
    {
        foreach (var die in _pool)
        {
            die.SetLastError(Die.LastError.None);
        }
    }
    
    #region Scanning

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
            if (die.connectionState == ConnectionState.Available)
            {
                DestroyDie(die);
            }
        }
        Central.ClearScannedList();
    }

    #endregion

    #region EditDie manager

    Dictionary<EditDie, Die> _editDice = new Dictionary<EditDie, Die>();
    List<Die> _addingDice = new List<Die>();

    public delegate void DieEventHandler(EditDie editDie);
    public static event DieEventHandler onDieAdded;
    public static event DieEventHandler onWillRemoveDie;

    public static event DieEventHandler onDieFound; // onDieConnected;
    public static event DieEventHandler onDieWillBeLost; // onDieDisconnected;

    public IEnumerable<EditDie> allDice => _editDice.Keys.ToArray();

    //public enum State
    //{
    //    Idle = 0,
    //    AddingDiscoveredDie,
    //    ConnectingDie,
    //    RefreshingPool,
    //}

    //State _state = State.Idle; // Use property to change value
    //public State state
    //{
    //    get => _state;
    //    private set
    //    {
    //        if (value != _state)
    //        {
    //            Debug.Log($"DiceManager state change: {_state} => {value}");
    //            _state = value;
    //        }
    //    }
    //}

    public Coroutine AddDiscoveredDice(List<Die> discoveredDice)
    {
        return StartCoroutine(AddDiscoveredDiceCr());

        IEnumerator AddDiscoveredDiceCr()
        {
            PixelsApp.Instance.ShowProgrammingBox("Adding Dice to the Dice Bag");
            _addingDice.AddRange(discoveredDice);

            //while (state != State.Idle) yield return null;
            //state = State.AddingDiscoveredDie;

            for (int i = 0; i < discoveredDice.Count; ++i)
            {
                var die = discoveredDice[i];
                PixelsApp.Instance.UpdateProgrammingBox((float)(i + 1) / discoveredDice.Count, "Adding " + die.name + " to the pool");

                // Here we wait a frame to give the programming box a chance to show up
                // on PC at least the attempt to connect can freeze the app
                yield return null;
                yield return null;

                EditDie AddNewDie(Die die)
                {
                    // Add a new entry in the dataset
                    var editDie = AppDataSet.Instance.AddNewDie(die);
                    AppDataSet.Instance.SaveData();
                    // And in our map
                    _editDice.Add(editDie, null);
                    onDieAdded?.Invoke(editDie);
                    SetDieForEditDie(die, editDie);
                    return editDie;
                }

                if (die.deviceId != 0)
                {
                    AddNewDie(die);
                }
                else
                {
                    bool? res = null;
                    ConnectDie(die, (d, r, s) => res = r);
                    yield return new WaitUntil(() => res.HasValue);

                    if (die.connectionState == ConnectionState.Ready)
                    {
                        if (die.deviceId == 0)
                        {
                            Debug.LogError("Die " + die.name + " was connected to but doesn't have a proper device id");
                            bool acknowledge = false;
                            PixelsApp.Instance.ShowDialogBox("Identification Error", "Die " + die.name + " was connected to but doesn't have a proper device id", "Ok", null, (_) => acknowledge = true);
                            yield return new WaitUntil(() => acknowledge);
                        }
                        else
                        {
                            var editDie = AddNewDie(die);

                            // Fetch battery level
                            bool battLevelReceived = false;
                            editDie.die.GetBatteryLevel((d, f) => battLevelReceived = true);
                            yield return new WaitUntil(() => battLevelReceived == true);

                            // Fetch RSSI
                            bool rssiReceived = false;
                            editDie.die.GetRssi((d, r) => rssiReceived = true);
                            yield return new WaitUntil(() => rssiReceived == true);

                        }
                        DisconnectDie(die, null);
                    }
                    else
                    {
                        bool acknowledge = false;
                        PixelsApp.Instance.ShowDialogBox("Connection error", "Could not connect to " + die.name + " to add it to the dice bag.", "Ok", null, (_) => acknowledge = true);
                        yield return new WaitUntil(() => acknowledge);
                    }
                }
            }
            PixelsApp.Instance.HideProgrammingBox();
            _addingDice.Clear();
            //state = State.Idle;
        }
    }

    public Coroutine ConnectDie(EditDie editDie, System.Action<EditDie, bool, string> dieReadyCallback)
    {
        if (!_editDice.ContainsKey(editDie))
        {
            Debug.LogError("Die " + editDie.name + " not in DicePool");
            dieReadyCallback?.Invoke(editDie, false, "Edit Die not in DicePool");
            return null;
        }
        else
        {
            return StartCoroutine(ConnectDieCr());

            IEnumerator ConnectDieCr()
            {
                //while (state != State.Idle) yield return null;
                //state = State.ConnectingDie;

                if (editDie.die == null)
                {
                    BeginScanForDice();

                    float startScanTime = Time.time;
                    yield return new WaitUntil(() => Time.time > startScanTime + 3.0f || editDie.die != null);

                    StopScanForDice();

                    if (editDie.die != null)
                    {
                        // We found the die, try to connect
                        bool? res = null;
                        ConnectDie(editDie.die, (d, r, s) => res = r);
                        yield return new WaitUntil(() => res.HasValue);

                        if (editDie.die.connectionState == ConnectionState.Ready)
                        {
                            dieReadyCallback?.Invoke(editDie, true, null);
                        }
                        else
                        {
                            dieReadyCallback?.Invoke(editDie, false, "Could not connect to Die " + editDie.name + ". Communication Error");
                        }
                    }
                    else
                    {
                        dieReadyCallback?.Invoke(editDie, false, "Could not find Die " + editDie.name + ".");
                    }
                }
                else
                {
                    // We already know what die matches the edit die, connect to it!
                    bool? res = null;
                    ConnectDie(editDie.die, (d, r, s) => res = r);
                    yield return new WaitUntil(() => res.HasValue);

                    if (editDie.die.connectionState == ConnectionState.Ready)
                    {
                        dieReadyCallback?.Invoke(editDie, true, null);
                    }
                    else
                    {
                        dieReadyCallback?.Invoke(editDie, false, "Could not connect to Die " + editDie.name + ". Communication Error");
                    }
                }

                //state = State.Idle;
            }
        }
    }

    public Coroutine DisconnectDie(EditDie editDie, System.Action<EditDie, bool, string> dieDisconnectedCallback)
    {
        return StartCoroutine(DisconnectDieCr());

        IEnumerator DisconnectDieCr()
        {
            //while (state != State.Idle) yield return null;

            if (!_editDice.ContainsKey(editDie))
            {
                Debug.LogError("Trying to disconnect unknown edit die " + editDie.name);
            }
            else if (editDie.die == null)
            {
                Debug.LogError("Trying to disconnect unknown die " + editDie.name);
            }
            else if (editDie.die.connectionState != ConnectionState.Ready)
            {
                Debug.LogError("Trying to disconnect die that isn't connected " + editDie.name + ", current state " + editDie.die.connectionState);
            }
            else
            {
                bool? res = null;
                DisconnectDie(editDie.die, (d, r, s) => res = r);
                yield return new WaitUntil(() => res.HasValue);

                if (res.Value)
                {
                    dieDisconnectedCallback?.Invoke(editDie, true, null);
                }
                else
                {
                    dieDisconnectedCallback?.Invoke(editDie, false, "Could not disconnect to Die " + editDie.name + ". Communication Error");
                }
            }
        }
    }

    public Coroutine ConnectDiceList(List<EditDie> editDiceList, System.Action callback)
    {
        bool allDiceValid = editDiceList.All(d => _editDice.ContainsKey(d));
        if (!allDiceValid)
        {
            Debug.LogError("some dice not valid");
            callback?.Invoke();
            return null;
        }
        else
        {
            return StartCoroutine(ConnectDiceListCr());

            IEnumerator ConnectDiceListCr()
            {
                //while (state != State.Idle) yield return null;
                //state = State.ConnectingDie;

                if (editDiceList.Any(ed => ed.die == null))
                {
                    BeginScanForDice();
                    float startScanTime = Time.time;
                    yield return new WaitUntil(() => Time.time > startScanTime + 3.0f || editDiceList.All(ed => ed.die != null));
                    StopScanForDice();
                }

                foreach (var editDice in editDiceList)
                {
                    if (editDice.die != null)
                    {
                        bool? res = null;
                        ConnectDie(editDice.die, (d, r, s) => res = r);
                        yield return new WaitUntil(() => res.HasValue);
                    }
                }

                callback?.Invoke();

                //state = State.Idle;
            }
        }
    }

    public void ForgetDie(EditDie editDie)
    {
        //while (state != State.Idle) yield return null;

        if (!_editDice.ContainsKey(editDie))
        {
            Debug.LogError("Trying to forget unknown edit die " + editDie.name);
        }
        else
        {
            onWillRemoveDie?.Invoke(editDie);
            if (editDie.die != null)
            {
                ForgetDie(editDie.die, null);
            }
            AppDataSet.Instance.DeleteDie(editDie);
            _editDice.Remove(editDie);
            AppDataSet.Instance.SaveData();
        }
    }

    public Die GetDieForEditDie(EditDie editDie)
    {
        _editDice.TryGetValue(editDie, out Die die);
        return die;
    }

    void OnDieDiscovered(Die die)
    {

        var editDie = _editDice.Keys.FirstOrDefault(d => AppConstants.FindDiceByDeviceId ? d.deviceId == die.deviceId : d.name == die.name);
        SetDieForEditDie(die, editDie);
        Debug.Log($"{(editDie != null ? "Pairing discovered die" : "Discovered die is unpaired")} : {die.deviceId} - {die.name}");
    }

    void SetDieForEditDie(Die die, EditDie editDie)
    {
        if ((editDie != null) && (die != editDie.die))
        {
            Debug.Assert(_editDice.ContainsKey(editDie));
            if (die == null)
            {
                onDieWillBeLost?.Invoke(editDie);
            }
            _editDice[editDie] = die;
            if (die != null)
            {
                onDieFound?.Invoke(editDie);
            }
        }
    }

    #endregion

    //TODO
    public void RawDisconnectDie(Die die) => DisconnectDie(die, null);

    //TODO
    public void RawWriteDie(Die die, byte[] bytes) => WriteDie(die, bytes, null);

    #region Unity messages

    IEnumerator Start()
    {
        Central.Initialize(); //TODO handle error + user message

        onDieDiscovered += OnDieDiscovered;

        // Load our pool from JSON!
        if (AppDataSet.Instance.dice != null)
        {
            foreach (var editDie in AppDataSet.Instance.dice)
            {
                // Create a disconnected die
                _editDice.Add(editDie, null);
                onDieAdded?.Invoke(editDie);
            }
        }

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
            switch (poolDie.connectionState)
            {
                case ConnectionState.Ready:
                    if (poolDie.currentConnectionCount == 0)
                    {
                        // Die is waiting to disconnect
                        if (Time.time - poolDie.lastRequestDisconnectTime > AppConstants.Instance.DicePoolDisconnectDelay)
                        {
                            // Go ahead and disconnect
                            DoDisconnectDie(poolDie);
                        }
                    }
                    break;
                default:
                    // Do nothing
                    break;
            }
        }
    }

    #endregion

    void ConnectDie(Die die, System.Action<Die, bool, string> onConnectionResult)
    {
        if (_pool.Contains(die))
        {
            var poolDie = (PoolDie)die;
            Debug.Log($"{poolDie.name}: Before request connect = {poolDie.currentConnectionCount}");
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
            switch (poolDie.connectionState)
            {
                default:
                    string errorMessage = $"Die {die.name} in invalid die state {poolDie.connectionState} while attempting to connect";
                    Debug.LogError(errorMessage);
                    onConnectionResult?.Invoke(die, false, errorMessage);
                    break;
                case ConnectionState.Available:
                    Debug.Assert(poolDie.currentConnectionCount == 1);
                    poolDie.onConnectionResult += onConnectionResult;
                    DoConnectDie(poolDie);
                    break;
                case ConnectionState.Connecting:
                case ConnectionState.Identifying:
                    // Already in the process of connecting, just add the callback and wait
                    poolDie.onConnectionResult += onConnectionResult;
                    break;
                case ConnectionState.Ready:
                    // Trigger the callback immediately
                    onConnectionResult?.Invoke(die, true, null);
                    break;
            }
            Debug.Log($"{poolDie.name}: After request connect = {poolDie.currentConnectionCount}");
        }
        else
        {
            string errorMessage = "Pool attempting to connect to unknown die " + die.name;
            Debug.LogError(errorMessage);
            onConnectionResult?.Invoke(die, false, errorMessage);
        }
    }

    void DisconnectDie(Die die, System.Action<Die, bool, string> onDisconnectionResult)
    {
        if (_pool.Contains(die))
        {
            var poolDie = (PoolDie)die;
            Debug.Log($"{poolDie.name}: Before request disconnect = {poolDie.currentConnectionCount}");
            switch (poolDie.connectionState)
            {
                default:
                    string errorMessage = "Die " + die.name + " in invalid die state " + poolDie.connectionState + " while attempting to disconnect";
                    Debug.LogError(errorMessage);
                    onDisconnectionResult?.Invoke(die, false, errorMessage);
                    break;
                case ConnectionState.Ready:
                case ConnectionState.Connecting:
                case ConnectionState.Identifying:
                    // Register to be notified when disconnection is complete
                    poolDie.currentConnectionCount--;
                    if (poolDie.currentConnectionCount == 0)
                    {
                        poolDie.onDisconnectionResult += onDisconnectionResult;
                        poolDie.lastRequestDisconnectTime = Time.time;
                    }
                    break;
            }
            Debug.Log($"{poolDie.name}: After request disconnect = {poolDie.currentConnectionCount}");
        }
        else
        {
            string errorMessage = "Pool attempting to disconnect to unknown die " + die.name;
            Debug.LogError(errorMessage);
            onDisconnectionResult?.Invoke(die, false, errorMessage);
        }
    }

    /// <summary>
    /// Removes a die from the pool, as if we never new it.
    /// Note: We may very well 'discover' it again the next time we scan.
    /// </sumary>
    void ForgetDie(Die die, System.Action<Die, bool, string> onForgetDieResult)
    {
        if (_pool.Contains(die))
        {
            var poolDie = (PoolDie)die;
            switch (poolDie.connectionState)
            {
                default:
                    DestroyDie(poolDie);
                    onForgetDieResult?.Invoke(die, true, null);
                    break;
                case ConnectionState.Ready:
                case ConnectionState.Connecting:
                case ConnectionState.Identifying:
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
    void WriteDie(Die die, byte[] bytes, System.Action<Die, bool, string> onWriteResult)
    {
        if (_pool.Contains(die))
        {
            var poolDie = (PoolDie)die;
            _coroQueue.Enqueue(() => WriteAsync());

            IEnumerator WriteAsync()
            {
                var request = Central.WriteCharacteristicAsync(poolDie.peripheral, pixelService, writeCharacteristic, bytes);
                yield return request;
                onWriteResult?.Invoke(die, request.IsSuccess, request?.ErrorMessage);
            }
        }
    }

    void DoConnectDie(PoolDie poolDie)
    {
        if (poolDie.connectionState == ConnectionState.Available)
        {
            poolDie.SetConnectionState(ConnectionState.Connecting);
            _coroQueue.Enqueue(() => ConnectAsync(poolDie, OnDieConnected, () => poolDie.onDisconnectionResult?.Invoke(poolDie, true, null), OnDieDisconnectedUnexpectedly));

            static IEnumerator ConnectAsync(PoolDie poolDie,
                System.Action<PoolDie, bool, string> onDieConnected,
                System.Action onDieDisconnected,
                System.Action<PoolDie, string> onDieDisconnectedUnexpectedly)
            {
                var request = Central.ConnectPeripheralAsync(poolDie.peripheral, (_, connected) =>
                {
                    if (!connected)
                    {
                        if (poolDie.connectionState == ConnectionState.Disconnecting)
                        {
                            onDieConnected(poolDie, false, null);
                        }
                        else
                        {
                            onDieDisconnectedUnexpectedly(poolDie, "disconnected"); //TODO
                        }
                        onDieDisconnected();
                    }
                });
                yield return request;
                if (request.IsSuccess)
                {
                    var characteristics = Central.GetPeripheralServiceCharacteristics(poolDie.peripheral, pixelService);
                    if ((characteristics != null) && characteristics.Contains(subscribeCharacteristic) && characteristics.Contains(writeCharacteristic))
                    {
                        request = Central.SubscribeCharacteristicAsync(poolDie.peripheral, pixelService, subscribeCharacteristic, data => poolDie.OnData(data));
                        yield return request;
                        if (request.IsSuccess)
                        {
                            onDieConnected(poolDie, true, null);
                        }
                        else
                        {
                            onDieConnected(poolDie, false, "subscribe request failed"); //TODO
                        }
                    }
                    else
                    {
                        onDieConnected(poolDie, false, "characteristics request failed"); //TODO
                    }
                }
                else
                {
                    onDieConnected(poolDie, false, "connect request failed"); //TODO
                }
            }
        }
        else
        {
            Debug.LogError("Die " + poolDie.name + " not in available state, instead: " + poolDie.connectionState);
        }
    }

    /// <summary>
    /// Disconnects a die, doesn't remove it from the pool though
    /// </sumary>
    void DoDisconnectDie(Die die)
    {
        if (die.connectionState == ConnectionState.Ready)
        {
            // When disconnecting from a destroy, the die will have already been removed
            if (_pool.Contains(die))
            {
                var poolDie = (PoolDie)die;
                poolDie.SetConnectionState(ConnectionState.Disconnecting);
                _coroQueue.Enqueue(() => DisconnectAsync(poolDie));

                static IEnumerator DisconnectAsync(PoolDie poolDie)
                {
                    var request = Central.DisconnectPeripheralAsync(poolDie.peripheral);
                    yield return request;

                    if (request.IsSuccess)
                    {
                        poolDie.SetConnectionState(ConnectionState.Available);

                        // Reset connection count now that nothing is connected to the die
                        poolDie.currentConnectionCount = 0;
                    }
                    else
                    {
                        // Could not disconnect the die, indicate that!
                        poolDie.SetLastError(Die.LastError.ConnectionError);
                    }

                    var callbackCopy = poolDie.onDisconnectionResult;
                    poolDie.onDisconnectionResult = null;
                    callbackCopy?.Invoke(poolDie, request.IsSuccess, request?.ErrorMessage);
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
        var poolDie = _pool.FirstOrDefault(d => peripheral.SystemId == d.peripheral.SystemId);
        if (poolDie == null)
        {
            // Never seen this die before
            poolDie = CreateDie(peripheral);
            poolDie.SetConnectionState(ConnectionState.Available);
        }
        else
        {
            poolDie.Setup(peripheral);
        }

        onDieDiscovered?.Invoke(poolDie);

        if (poolDie.connectionState != ConnectionState.Available)
        {
            // All other are errors
            Debug.LogError($"Die {poolDie.name} in invalid state: {poolDie.connectionState}");
            poolDie.SetConnectionState(ConnectionState.Available);
        }
    }

    /// <summary>
    /// Called by central when a die is properly connected to (i.e. two-way communication is working)
    /// We still need to do a bit of work before the die can be available for general us though
    /// </sumary>
    void OnDieConnected(PoolDie poolDie, bool result, string errorMessage)
    {
        if (result)
        {
            // Remember that the die was just connected to (and trigger events)
            poolDie.SetConnectionState(ConnectionState.Identifying);

            // And have it update its info (unique Id, appearance, etc...) so it can finally be ready
            poolDie.UpdateInfo(OnDieReady);

            // Reset error
            poolDie.SetLastError(Die.LastError.None);
        }
        else
        {
            // Remember that the die was just connected to (and trigger events)
            poolDie.SetConnectionState(ConnectionState.Available);

            // Could not connect to the die, indicate that!
            poolDie.SetLastError(Die.LastError.ConnectionError);

            // Reset connection count since it didn't succeed
            poolDie.currentConnectionCount = 0;

            // Trigger callback
            var callbackCopy = poolDie.onConnectionResult;
            poolDie.onConnectionResult = null;
            callbackCopy?.Invoke(poolDie, false, errorMessage);
        }
    }

    /// <summary>
    /// Called by the die once it has fetched its updated information (appearance, unique Id, etc.)
    /// </sumary>
    void OnDieReady(Die die, bool ready)
    {
        if (_pool.Contains(die))
        {
            var poolDie = (PoolDie)die;

            if (ready)
            {
                // Die is finally ready, awesome!
                poolDie.SetConnectionState(ConnectionState.Ready);
            }
            else
            {
                // Updating info didn't work, disconnect the die
                DoDisconnectDie(die);
            }

            // Trigger callback either way
            var callbackCopy = poolDie.onConnectionResult;
            poolDie.onConnectionResult = null;
            callbackCopy?.Invoke(poolDie, ready, null);
        }
        else
        {
            Debug.LogError("Received Die ready notification for unknown die " + die.name);
        }
    }

    /// <summary>
    /// Called by Central when a die gets disconnected unexpectedly
    /// </sumary>
    void OnDieDisconnectedUnexpectedly(PoolDie poolDie, string errorMessage)
    {
        Debug.LogError(poolDie.peripheral.Name  + ": Die disconnected");

        poolDie.SetConnectionState(ConnectionState.Available);
        poolDie.SetLastError(Die.LastError.Disconnected);

        // Reset connection count since it didn't succeed
        poolDie.currentConnectionCount = 0;

        // Forget the die for now
        DestroyDie(poolDie);
    }

    /// <summary>
    /// Creates a new die for the pool
    /// </sumary>
    PoolDie CreateDie(Peripheral peripheral)
    {
        var dieObj = new GameObject(name);
        dieObj.transform.SetParent(transform);

        var poolDie = dieObj.AddComponent<PoolDie>();
        poolDie.Setup(peripheral);
        _pool.Add(poolDie);

        return poolDie;
    }

    /// <summary>
    /// Cleanly destroys a die, disconnecting if necessary and triggering events in the process
    /// Does not remove it from the list though
    /// </sumary>
    void DestroyDie(PoolDie poolDie)
    {
        void doDestroy()
        {
            SetDieForEditDie(poolDie, _editDice.FirstOrDefault(kv => kv.Value == poolDie).Key);

            poolDie.SetConnectionState(ConnectionState.Invalid);
            GameObject.Destroy(poolDie.gameObject);
            _pool.Remove(poolDie);
        }

        switch (poolDie.connectionState)
        {
            default:
                doDestroy();
                break;
            case ConnectionState.Ready:
                // Register to be notified when disconnection is complete
                if (poolDie.currentConnectionCount == 0)
                {
                    poolDie.onDisconnectionResult += (d, r, s) => doDestroy();
                    DoDisconnectDie(poolDie);
                }
                break;
        }
    }
}
