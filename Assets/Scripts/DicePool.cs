using Dice;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

using Central = Systemic.Pixels.Unity.BluetoothLE.Central;
using Peripheral = Systemic.Pixels.Unity.BluetoothLE.ScannedPeripheral;

public sealed partial class DicePool : SingletonMonoBehaviour<DicePool>
{
    static readonly System.Guid pixelService = new System.Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    static readonly System.Guid subscribeCharacteristic = new System.Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    static readonly System.Guid writeCharacteristic = new System.Guid("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    // Multiple things may request bluetooth scanning, so we need to arbitrate when
    // we actually ask Central to scan or not. This counter will let us know
    // exactly when to start or stop asking central.
    int _scanRequestCount = 0;

    readonly List<PoolDie> _pool = new List<PoolDie>();

    public Die[] scannedDice => _pool.ToArray();

    public delegate void DieDiscoveredHandler(Die die);
    public static event DieDiscoveredHandler onDieDiscovered;

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
        foreach (var poolDie in diceCopy)
        {
            if (poolDie.connectionState == DieConnectionState.Available)
            {
                DestroyDie(poolDie);
            }
        }
        Central.ClearScannedList();
    }

    #endregion

    #region EditDie management

    Dictionary<EditDie, PoolDie> _editDice = new Dictionary<EditDie, PoolDie>();
    Dictionary<EditDie, PoolDie> _disconnectingEditDice = new Dictionary<EditDie, PoolDie>();

    public IEnumerable<EditDie> allDice => _editDice.Keys.ToArray();

    public delegate void DieEventHandler(EditDie editDie);
    public static event DieEventHandler onDieAdded;
    public static event DieEventHandler onWillRemoveDie;

    public static event DieEventHandler onDieFound; // onDieConnected;
    public static event DieEventHandler onDieWillBeLost; // onDieDisconnected;

    public void ResetDiceErrors()
    {
        foreach (var die in _pool)
        {
            die.ResetLastError();
        }
    }

    public Coroutine AddDiscoveredDice(List<Die> discoveredDice)
    {
        return StartCoroutine(AddDiscoveredDiceCr());

        IEnumerator AddDiscoveredDiceCr()
        {
            PixelsApp.Instance.ShowProgrammingBox("Adding Dice to the Dice Bag");

            //while (state != State.Idle) yield return null;
            //state = State.AddingDiscoveredDie;

            for (int i = 0; i < discoveredDice.Count; ++i)
            {
                var die = discoveredDice[i];

                var poolDie = die as PoolDie;
                if ((poolDie == null) || (!_pool.Contains(die)))
                {
                    Debug.LogError("Attempting to add unknown die " + die.name);
                    continue;
                }

                PixelsApp.Instance.UpdateProgrammingBox((float)(i + 1) / discoveredDice.Count, "Adding " + poolDie.name + " to the pool");

                // Here we wait a couple frames to give the programming box a chance to show up
                // on PC at least the attempt to connect can freeze the app
                yield return null;
                yield return null;

                EditDie AddNewDie()
                {
                    // Add a new entry in the dataset
                    var editDie = AppDataSet.Instance.AddNewDie(poolDie);
                    AppDataSet.Instance.SaveData();

                    // And in our map
                    _editDice.Add(editDie, null);
                    onDieAdded?.Invoke(editDie);
                    SetDieForEditDie(poolDie, editDie);

                    return editDie;
                }

                if (poolDie.deviceId != 0)
                {
                    AddNewDie();
                }
                else
                {
                    bool? res = null;
                    poolDie.Connect((d, r, s) => res = r);
                    yield return new WaitUntil(() => res.HasValue);

                    if (poolDie.connectionState == DieConnectionState.Ready)
                    {
                        if (poolDie.deviceId == 0)
                        {
                            Debug.LogError("Die " + poolDie.name + " was connected to but doesn't have a proper device id");
                            bool acknowledge = false;
                            PixelsApp.Instance.ShowDialogBox("Identification Error", $"Die {poolDie.name} was connected to but doesn't have a proper device id", "Ok", null, (_) => acknowledge = true);
                            yield return new WaitUntil(() => acknowledge);
                        }
                        else
                        {
                            var editDie = AddNewDie();

                            // Fetch battery level
                            bool battLevelReceived = false;
                            editDie.die.GetBatteryLevel((d, f) => battLevelReceived = true);
                            yield return new WaitUntil(() => battLevelReceived == true);

                            // Fetch RSSI
                            bool rssiReceived = false;
                            editDie.die.GetRssi((d, r) => rssiReceived = true);
                            yield return new WaitUntil(() => rssiReceived == true);

                        }

                        poolDie.Disconnect(null);
                    }
                    else
                    {
                        bool acknowledge = false;
                        PixelsApp.Instance.ShowDialogBox("Connection error", $"Could not connect to {poolDie.name} to add it to the dice bag.", "Ok", null, (_) => acknowledge = true);
                        yield return new WaitUntil(() => acknowledge);
                    }
                }
            }
            PixelsApp.Instance.HideProgrammingBox();
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
                        Debug.Assert(_pool.Contains(editDie.die));
                        var poolDie = (PoolDie)editDie.die;

                        // We found the die, try to connect
                        bool? res = null;
                        poolDie.Connect((d, r, s) => res = r);
                        yield return new WaitUntil(() => res.HasValue);

                        if (editDie.die.connectionState == DieConnectionState.Ready)
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
                    Debug.Assert(_pool.Contains(editDie.die));
                    var poolDie = (PoolDie)editDie.die;

                    // We already know what die matches the edit die, connect to it!
                    bool? res = null;
                    poolDie.Connect((d, r, s) => res = r);
                    yield return new WaitUntil(() => res.HasValue);

                    if (editDie.die.connectionState == DieConnectionState.Ready)
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
            else if (editDie.die.connectionState != DieConnectionState.Ready)
            {
                Debug.LogError($"Trying to disconnect die that isn't connected {editDie.name}, current state {editDie.die.connectionState}");
            }
            else if (!_pool.Contains(editDie.die))
            {
                Debug.LogError("Trying attempting to disconnect unknown pool die " + editDie.name);
            }
            else
            {
                var poolDie = (PoolDie)editDie.die;

                bool? res = null;
                poolDie.Disconnect((d, r, s) => res = r);

                yield return new WaitUntil(() => res.HasValue);
                dieDisconnectedCallback?.Invoke(editDie, res.Value, res.Value ? null : $"Could not disconnect die {editDie.name}. Communication Error");
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

                foreach (var editDie in editDiceList)
                {
                    if (editDie.die != null)
                    {
                        Debug.Assert(_pool.Contains(editDie.die));
                        var poolDie = (PoolDie)editDie.die;

                        bool? res = null;
                        poolDie.Connect((d, r, s) => res = r);
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
                Debug.Assert(_pool.Contains(editDie.die));
                var poolDie = (PoolDie)editDie.die;

                switch (poolDie.connectionState)
                {
                    default:
                        DestroyDie(poolDie);
                        break;
                    case DieConnectionState.Ready:
                    case DieConnectionState.Connecting:
                    case DieConnectionState.Identifying:
                        // Disconnect!
                        _disconnectingEditDice.Add(editDie, poolDie);
                        poolDie.Disconnect((d, r, s) => DestroyDie(poolDie));
                        break;
                }
            }
            AppDataSet.Instance.DeleteDie(editDie);
            _editDice.Remove(editDie);
            AppDataSet.Instance.SaveData();
        }
    }

    public Die GetDieForEditDie(EditDie editDie)
    {
        if (!_editDice.TryGetValue(editDie, out PoolDie die))
        {
            _disconnectingEditDice.TryGetValue(editDie, out die);
        }
        return die;
    }

    void SetDieForEditDie(PoolDie poolDie, EditDie editDie)
    {
        if ((editDie != null) && (poolDie != editDie.die))
        {
            Debug.Assert(_editDice.ContainsKey(editDie));
            Debug.Assert((poolDie == null) || _pool.Contains(poolDie));
            if (poolDie == null)
            {
                onDieWillBeLost?.Invoke(editDie);
            }
            _editDice[editDie] = poolDie;
            if (poolDie != null)
            {
                onDieFound?.Invoke(editDie);
            }
        }
    }

    #endregion

    #region Unity messages

    void Start()
    {
        Central.Initialize(); //TODO handle error + user message

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
    }

    #endregion

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
            var dieObj = new GameObject(name);
            dieObj.transform.SetParent(transform);

            poolDie = dieObj.AddComponent<PoolDie>();
            _pool.Add(poolDie);
        }

        poolDie.Setup(peripheral);

        var editDie = _editDice.Keys.FirstOrDefault(d => AppConstants.FindDiceByDeviceId ? d.deviceId == poolDie.deviceId : d.name == poolDie.name);
        SetDieForEditDie(poolDie, editDie);
        Debug.Log($"{(editDie != null ? "Pairing discovered die" : "Discovered die is unpaired")} : {poolDie.deviceId} - {poolDie.name}");

        if (poolDie.connectionState != DieConnectionState.Available)
        {
            // All other are errors
            Debug.LogError($"Die {poolDie.name} in invalid state: {poolDie.connectionState}");
            //TODO poolDie.SetConnectionState(DieConnectionState.Available);
        }

        onDieDiscovered?.Invoke(poolDie);
    }

    /// <summary>
    /// Cleanly destroys a die, disconnecting if necessary and triggering events in the process
    /// Does not remove it from the list though
    /// </sumary>
    void DestroyDie(PoolDie poolDie)
    {
        void doDestroy()
        {
            SetDieForEditDie(null, _editDice.FirstOrDefault(kv => kv.Value == poolDie).Key);
            GameObject.Destroy(poolDie.gameObject);
            _pool.Remove(poolDie);
            var editDie = _disconnectingEditDice.FirstOrDefault(kv => kv.Value == poolDie).Key;
            if (editDie != null)
            {
                _disconnectingEditDice.Remove(editDie);
            }
        }

        switch (poolDie.connectionState)
        {
            default:
                doDestroy();
                break;
            case DieConnectionState.Ready:
                // Register to be notified when disconnection is complete
                //if (poolDie._currentConnectionCount == 0)
                //{
                //    poolDie.onDisconnectionResult += (d, r, s) => doDestroy();
                //    poolDie.DoDisconnect();
                //}
                //break;
                throw new System.NotImplementedException("Don't know how we can end up here..."); //TODO
        }
    }
}
