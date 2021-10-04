using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Dice;
/*
public class DiceManager : SingletonMonoBehaviour<DiceManager>
{
    List<EditDie> dice = new List<EditDie>();
    List<Die> _addingDice = new List<Die>();

    public delegate void DieAddedRemovedEvent(EditDie editDie);
    public event DieAddedRemovedEvent onDieAdded;
    public event DieAddedRemovedEvent onWillRemoveDie;

    public IEnumerable<EditDie> allDice => dice;

    public enum State
    {
        Idle = 0,
        AddingDiscoveredDie,
        ConnectingDie,
        RefreshingPool,
    }

    State _state = State.Idle; // Use property to change value
    public State state
    {
        get => _state;
        private set
        {
            if (value != _state)
            {
                Debug.Log($"DiceManager state change: {_state} => {value}");
                _state = value;
            }
        }
    }

    public Coroutine AddDiscoveredDice(List<Die> discoveredDice)
    {
        return StartCoroutine(AddDiscoveredDiceCr());

        IEnumerator AddDiscoveredDiceCr()
        {
            PixelsApp.Instance.ShowProgrammingBox("Adding Dice to the Dice Bag");
            _addingDice.AddRange(discoveredDice);
            while (state != State.Idle) yield return null;
            state = State.AddingDiscoveredDie;
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
                    dice.Add(editDie);
                    onDieAdded?.Invoke(editDie);
                    editDie.SetDieFromManager(die);
                    return editDie;
                }

                if (die.deviceId != 0)
                {
                    AddNewDie(die);
                }
                else
                {
                    bool? res = null;
                    DicePool.Instance.ConnectDie(die, (d, r, s) => res = r);
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
                        DicePool.Instance.DisconnectDie(die, null);
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
            state = State.Idle;
        }
    }

    public Coroutine ConnectDie(EditDie editDie, System.Action<EditDie, bool, string> dieReadyCallback)
    {
        var ourDie = dice.FirstOrDefault(d => d == editDie);
        if (ourDie == null)
        {
            Debug.LogError("Die " + editDie.name + " not in Dice Manager");
            dieReadyCallback?.Invoke(editDie, false, "Edit Die not in Dice Manager");
            return null;
        }
        else
        {
            return StartCoroutine(ConnectDieCr());

            IEnumerator ConnectDieCr()
            {
                while (state != State.Idle) yield return null;

                state = State.ConnectingDie;

                if (editDie.die == null)
                {
                    DicePool.Instance.BeginScanForDice();

                    float startScanTime = Time.time;
                    yield return new WaitUntil(() => Time.time > startScanTime + 3.0f || editDie.die != null);

                    DicePool.Instance.StopScanForDice();

                    if (editDie.die != null)
                    {
                        // We found the die, try to connect
                        bool? res = null;
                        DicePool.Instance.ConnectDie(editDie.die, (d, r, s) => res = r);
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
                    DicePool.Instance.ConnectDie(editDie.die, (d, r, s) => res = r);
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

                state = State.Idle;
            }
        }
    }

    public Coroutine DisconnectDie(EditDie editDie, System.Action<EditDie, bool, string> dieDisconnectedCallback)
    {
        return StartCoroutine(DisconnectDieCr());

        IEnumerator DisconnectDieCr()
        {
            while (state != State.Idle) yield return null;

            var dt = dice.First(p => p == editDie);
            if (dt == null)
            {
                Debug.LogError("Trying to disconnect unknown edit die " + editDie.name);
            }
            else if (dt.die == null)
            {
                Debug.LogError("Trying to disconnect unknown die " + editDie.name);
            }
            else if (dt.die.connectionState != ConnectionState.Ready)
            {
                Debug.LogError("Trying to disconnect die that isn't connected " + editDie.name + ", current state " + dt.die.connectionState);
            }
            else
            {
                bool? res = null;
                DicePool.Instance.DisconnectDie(dt.die, (d, r, s) => res = r);
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

    public Coroutine ConnectDiceList(List<EditDie> editDice, System.Action callback)
    {
        bool allDiceValid = dice.All(d => dice.Any(d2 => d2 == d));
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
                while (state != State.Idle) yield return null;

                state = State.ConnectingDie;

                if (editDice.Any(ed => ed.die == null))
                {
                    DicePool.Instance.BeginScanForDice();
                    float startScanTime = Time.time;
                    yield return new WaitUntil(() => Time.time > startScanTime + 3.0f || editDice.All(ed => ed.die != null));
                    DicePool.Instance.StopScanForDice();
                }

                foreach (var ed in editDice)
                {
                    if (ed.die != null)
                    {
                        bool? res = null;
                        DicePool.Instance.ConnectDie(ed.die, (d, r, s) => res = r);
                        yield return new WaitUntil(() => res.HasValue);
                    }
                }

                callback?.Invoke();

                state = State.Idle;
            }
        }
    }

    public Coroutine ForgetDie(EditDie editDie)
    {
        return StartCoroutine(ForgetDieCr());

        IEnumerator ForgetDieCr()
        {
            while (state != State.Idle) yield return null;

            var dt = dice.First(p => p == editDie);
            if (dt == null)
            {
                Debug.LogError("Trying to forget unknown edit die " + editDie.name);
            }
            else
            {
                onWillRemoveDie?.Invoke(editDie);
                if (dt.die != null)
                {
                    DicePool.Instance.ForgetDie(dt.die, null);
                }
                AppDataSet.Instance.DeleteDie(editDie);
                dice.Remove(dt);
                AppDataSet.Instance.SaveData();
            }
        }
    }

    void Awake()
    {
        DicePool.Instance.onDieDiscovered += OnDieDiscovered;
        DicePool.Instance.onWillDestroyDie += OnWillDestroyDie;
    }

    void Start()
    {
        // Load our pool from JSON!
        if (AppDataSet.Instance.dice != null)
        {
            foreach (var ddie in AppDataSet.Instance.dice)
            {
                // Create a disconnected die
                dice.Add(ddie);
                onDieAdded?.Invoke(ddie);
            }
        }
    }

    void OnDieDiscovered(Die die)
    {
        var ourDie = dice.FirstOrDefault(d => AppConstants.FindDiceByDeviceId ? d.deviceId == die.deviceId : d.name == die.name);
        ourDie?.SetDieFromManager(die);
        Debug.Log($"{(ourDie != null ? "Pairing discovered die" : "Discovered die is unpaired")} : {die.deviceId} - {die.name}");
    }

    void OnWillDestroyDie(Die die)
    {
        dice.FirstOrDefault(d => d.die == die)?.SetDieFromManager(die);
    }

}
*/