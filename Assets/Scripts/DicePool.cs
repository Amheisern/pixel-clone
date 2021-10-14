﻿using Dice;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    }

    /// <summary>
    /// Called by Central when a new die is discovered!
    /// </sumary>
    void OnPeripheralDiscovered(Peripheral peripheral)
    {
        Debug.Log($"Discovered dice {peripheral.Name}");

        // If the die exists, tell it that it's advertising now
        // otherwise create it (and tell it that its advertising :)
        var poolDie = _pool.FirstOrDefault(d => peripheral.SystemId == d.SystemId);
        if (poolDie == null)
        {
            // Never seen this die before
            var dieObj = new GameObject(name);
            dieObj.transform.SetParent(transform);

            poolDie = dieObj.AddComponent<PoolDie>();
            poolDie.onDisconnectedUnexpectedly += () => DestroyDie(poolDie);
            _pool.Add(poolDie);
        }

        poolDie.Setup(peripheral);

        var editDie = _editDice.Keys.FirstOrDefault(d => d.systemId == poolDie.SystemId);
        if (editDie != null)
        {
            SetDieForEditDie(editDie, poolDie);
            Debug.Log($"Pairing discovered Die: {poolDie.SystemId} - {poolDie.name}");
        }
        else
        {
            Debug.Log($"Discovered Die is unpaired: {poolDie.SystemId} - {poolDie.name}");
        }

        if (poolDie.connectionState != DieConnectionState.Available)
        {
            // All other are errors
            Debug.LogError($"Discovered Die {poolDie.name} in invalid state: {poolDie.connectionState}");
            //TODO poolDie.SetConnectionState(DieConnectionState.Available);
        }

        onDieDiscovered?.Invoke(poolDie);
    }

    #endregion

    #region Die management

    Dictionary<EditDie, PoolDie> _editDice = new Dictionary<EditDie, PoolDie>();
    Dictionary<EditDie, PoolDie> _editDiceToDestroy = new Dictionary<EditDie, PoolDie>();

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

            for (int i = 0; i < discoveredDice.Count; ++i)
            {
                var die = discoveredDice[i];

                var poolDie = die as PoolDie;
                if ((poolDie == null) || (!_pool.Contains(die)))
                {
                    Debug.LogError("Attempting to add unknown die " + die.name);
                    continue;
                }

                PixelsApp.Instance.UpdateProgrammingBox((float)(i + 1) / discoveredDice.Count, $"Adding {poolDie.name} to the pool");

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
                    SetDieForEditDie(editDie, poolDie);

                    return editDie;
                }

                if (!string.IsNullOrEmpty(poolDie.systemId))
                {
                    AddNewDie();
                }
                else
                {
                    Debug.LogError($"Die {poolDie.name} doesn't have a system id");
                }
            }
            PixelsApp.Instance.HideProgrammingBox();
        }
    }

    public Coroutine ConnectDice(IEnumerable<EditDie> diceList, System.Func<bool> requestCancelFunc, System.Action<EditDie, bool, string> dieReadyCallback = null)
    {
        if (diceList == null) throw new System.ArgumentNullException(nameof(diceList));
        if (requestCancelFunc == null) throw new System.ArgumentNullException(nameof(requestCancelFunc));

         var dice = diceList.ToArray();
        if (!dice.All(d => _editDice.ContainsKey(d)))
        {
            Debug.LogError("some dice not valid");
            return null;
        }
        else
        {
            return StartCoroutine(ConnectDiceListCr());

            IEnumerator ConnectDiceListCr()
            {
                // requestCancelFunc() only need to return true once to cancel the operation
                bool isCancelled = false;
                bool UpdateIsCancelledOrTimeout() => isCancelled |= requestCancelFunc();

                if (dice.Any(ed => ed.die == null))
                {
                    BeginScanForDice();

                    Debug.Log($"Scanning for dice {string.Join(", ", dice.Select(d => d.name))} with timeout of {AppConstants.Instance.ScanTimeout}s");

                    // Wait for all dice to be scanned, or timeout
                    float scanTimeout = Time.realtimeSinceStartup + AppConstants.Instance.ScanTimeout;
                    yield return new WaitUntil(() => dice.All(ed => ed.die != null) || (Time.realtimeSinceStartup > scanTimeout) || UpdateIsCancelledOrTimeout());

                    StopScanForDice();
                }

                // Array of error message for each dice connect attempt
                // - if null: still connecting
                // - if empty string: successfully connected
                var results = new string[dice.Length];
                for (int i = 0; i < dice.Length; ++i)
                {
                    var editDie = dice[i];
                    if (editDie.die != null)
                    {
                        Debug.Assert(_pool.Contains(editDie.die));
                        var poolDie = (PoolDie)editDie.die;

                        // We found the die, try to connect
                        int index = i; // Capture the current value of i
                        poolDie.Connect((_, res, error) => results[index] = res ? "" : error);
                    }
                    else
                    {
                        results[i] = ConnectTimeoutErrorMessage;
                    }
                }

                // Wait for all dice to connect
                yield return new WaitUntil(() => results.All(msg => msg != null) || UpdateIsCancelledOrTimeout());

                if (isCancelled)
                {
                    // Disconnect any die that just successfully connected or that is still connecting
                    for (int i = 0; i < dice.Length; ++i)
                    {
                        if (string.IsNullOrEmpty(results[i]))
                        {
                            var poolDie = (PoolDie)dice[i].die;
                            poolDie?.Disconnect();
                        }
                        dieReadyCallback?.Invoke(dice[i], false, "Connection to Die canceled by application");
                    }
                }
                else if (dieReadyCallback != null)
                {
                    // Report connection result(s)
                    for (int i = 0; i < dice.Length; ++i)
                    {
                        bool connected = results[i] == "";
                        dieReadyCallback.Invoke(dice[i], connected, connected ? null : results[i]);
                    }
                }
            }
        }
    }

    public Coroutine DisconnectDie(EditDie editDie, bool forceDisconnect = false)
    {
        return StartCoroutine(DisconnectDieCr());

        IEnumerator DisconnectDieCr()
        {
            if (!_editDice.ContainsKey(editDie))
            {
                Debug.LogError($"Trying to disconnect unknown edit die {editDie.name}");
            }
            else if (editDie.die != null)
            {
                if (!_pool.Contains(editDie.die))
                {
                    Debug.LogError($"Trying attempting to disconnect unknown pool die {editDie.name}");
                }
                else
                {
                    var poolDie = (PoolDie)editDie.die;

                    bool? res = null;
                    poolDie.Disconnect((d, r, s) => res = r, forceDisconnect);

                    yield return new WaitUntil(() => res.HasValue);
                }
            }
        }
    }

    public void ForgetDie(EditDie editDie)
    {
        if (!_editDice.ContainsKey(editDie))
        {
            Debug.LogError($"Trying to forget unknown edit die {editDie.name}");
        }
        else
        {
            onWillRemoveDie?.Invoke(editDie);

            if (editDie.die != null)
            {
                Debug.Assert(_pool.Contains(editDie.die));
                var poolDie = (PoolDie)editDie.die;

                _editDiceToDestroy.Add(editDie, poolDie);
                if (poolDie.isConnectingOrReady)
                {
                    poolDie.Disconnect((d, r, s) => DestroyDie(poolDie), forceDisconnect: true);
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
            _editDiceToDestroy.TryGetValue(editDie, out die);
        }
        return die;
    }

    void SetDieForEditDie(EditDie editDie, PoolDie poolDie)
    {
        if (poolDie != editDie.die)
        {
            Debug.Assert((poolDie == null) || _pool.Contains(poolDie));
            if (poolDie == null)
            {
                onDieWillBeLost?.Invoke(editDie);
            }
            if (_editDice.ContainsKey(editDie))
            {
                _editDice[editDie] = poolDie;
            }
            else
            {
                Debug.Assert(poolDie == null);
            }
            if (poolDie != null)
            {
                onDieFound?.Invoke(editDie);
            }
        }
    }

    /// <summary>
    /// Cleanly destroys a die, disconnecting if necessary and triggering events in the process
    /// Does not remove it from the list though
    /// </sumary>
    void DestroyDie(PoolDie poolDie)
    {
        SetDieForEditDie(_editDice.FirstOrDefault(kv => kv.Value == poolDie).Key, null);
        GameObject.Destroy(poolDie.gameObject);
        _pool.Remove(poolDie);
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

    void Update()
    {
        List<PoolDie> destroyNow = null;
        foreach (var kv in _editDiceToDestroy)
        {
            if (!kv.Value.isConnectingOrReady)
            {
                if (destroyNow == null)
                {
                    destroyNow = new List<PoolDie>();
                }
                destroyNow.Add(kv.Value);
            }
        }
        if (destroyNow != null)
        {
            foreach (var poolDie in destroyNow)
            {
                DestroyDie(poolDie);
            }
        }
    }

    #endregion
}
