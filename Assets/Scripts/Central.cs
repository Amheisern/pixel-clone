using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.Text;

using PixelsCentral = Systemic.Pixels.Unity.BluetoothLE.Central;
using ScannedPeripheral = Systemic.Pixels.Unity.BluetoothLE.ScannedPeripheral;

public class Central : SingletonMonoBehaviour<Central>
{
    /// <summary>
    /// What Central cares about when it comes to dice
    /// </summary>
    public interface IDie
    {
        ScannedPeripheral scannedPeripheral { get; }
        string name { get; }
        string address { get; }
    }

    /// <summary>
    /// Internal Die definition, stores connection-relevant data
    /// </summary>
    class Die
        : IDie
    {
        public enum State
        {
            Advertising = 0,
            Connecting,
            Connected,
            Subscribing,
            Ready,
            Disconnecting,
        }

        State _state = State.Advertising; // Use property to change value
        public State state
        {
            get => _state;
            set
            {
                if (value != _state)
                {
                    Debug.Log($"Die state change: {_state} => {value}");
                    _state = value;
                }
            }
        }

        public ScannedPeripheral scannedPeripheral { get; }
        public string name => scannedPeripheral.Name;
        public string address => scannedPeripheral.SystemId;

        public float startTime; // Used for timing out while looking for characteristics or subscribing to one
        public bool deviceConnected;
        public bool messageWriteCharacteristicFound;
        public bool messageReadCharacteristicFound;

        // These are set and cleared depending on what is going on
        public System.Action<IDie, int, byte[]> onCustomAdvertisingData;
        public System.Action<IDie, bool, string> onConnectionResult;
        public System.Action<IDie, byte[]> onData;
        public System.Action<IDie, bool, string> onDisconnectionResult;
        public System.Action<IDie, string> onUnexpectedDisconnection;

        public Die(ScannedPeripheral scannedPeripheral)
        {
            this.scannedPeripheral = scannedPeripheral;
            state = State.Advertising;
            startTime = float.MaxValue;
            deviceConnected = false;
            messageWriteCharacteristicFound = false;
            messageReadCharacteristicFound = false;
        }
    }

    const string serviceGUID = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";
    const string subscribeCharacteristic = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";
    const string writeCharacteristic = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E";

    const float DiscoverCharacteristicsTimeout = 5.0f; // seconds
    const float SubscribeCharacteristicsTimeout = 5.0f; // seconds

    public enum State
    {
        Uninitialized = 0,
        Initializing,
        Idle,
        Scanning,
        Connecting,
        Disconnecting,
        Error,
    }

    State _state = State.Uninitialized; // Use property to change value
    public State state
    {
        get => _state;
        private set
        {
            if (value != _state)
            {
                Debug.Log($"Central state change: {_state} => {value}");
                _state = value;
            }
        }
    }

    Dictionary<string, Die> _dice = new Dictionary<string, Die>();

    // Scanning (discovery) callbacks
    System.Action<IDie> _onDieDiscovered;
    System.Action<IDie, int, byte[]> _onCustomAdvertisingData;

    abstract class Operation
    {
        public abstract IEnumerator Process(Central central);
    }

    class OperationConnect
        : Operation
    {
        public IDie die;
        public System.Action<IDie, bool, string> connectionResultCallback;
        public System.Action<IDie, byte[]> onDataCallback;
        public System.Action<IDie, string> onUnexpectedDisconnectionCallback;

        public override IEnumerator Process(Central central)
        {
            return central.DoConnectDie(die, connectionResultCallback, onDataCallback, onUnexpectedDisconnectionCallback);
        }
    }

    class OperationDisconnect
        : Operation
    {
        public IDie die;
        public System.Action<IDie, bool, string> onDisconnectionResult;

        public override IEnumerator Process(Central central)
        {
            return central.DoDisconnectDie(die, onDisconnectionResult);
        }
    }

    class OperationWriteDie
        : Operation
    {
        public IDie die;
        public byte[] bytes;
        public System.Action<IDie, bool, string> bytesWrittenCallback;

        public override IEnumerator Process(Central central)
        {
            return central.DoWriteDie(die, bytes, bytesWrittenCallback);
        }
    }

    // The thread-safe queue is used only as an extra precaution
    // as operations should always be pushed on the main thread
    readonly ConcurrentQueue<Operation> _operations = new ConcurrentQueue<Operation>();

    /// <summary>
    /// Initiates a bluetooth scan
    /// </summary>
    public bool BeginScanForDice(System.Action<IDie> onDieDiscovered, System.Action<IDie, int, byte[]> onCustomAdvertisingData)
    {
        if (state != State.Idle)
        {
            Debug.LogError("Central not ready to start scanning, state: " + state);
            return false;
        }

        Debug.Log("start scan");

        // Begin scanning
        state = State.Scanning;

        // Notify of all the already known advertising dice
        foreach (var die in _dice.Values)
        {
            if (die.state == Die.State.Advertising)
            {
                die.onCustomAdvertisingData = onCustomAdvertisingData;
                onDieDiscovered?.Invoke(die);
            }
        }

        _onDieDiscovered = onDieDiscovered;
        _onCustomAdvertisingData = onCustomAdvertisingData;

        // Remove any previous subscription to avoid receiving event twice
        PixelsCentral.PeripheralDiscovered -= OnPeripheralDiscovered;
        PixelsCentral.PeripheralDiscovered += OnPeripheralDiscovered;
        PixelsCentral.ScanForPeripheralsWithServices(new[] { new System.Guid(serviceGUID) });
        //BluetoothLEHardwareInterface.ScanForPeripheralsWithServices(
        //    new string[] { serviceGUID },
        //    (a, n) => OnDeviceDiscovered(a, n, onDieDiscovered, onCustomAdvertisingData),
        //    OnDeviceAdvertisingInfo, false, false);

        return true;
    }

    /// <summary>
    /// Stops scanning for new bluetooth devices
    /// </summary>
    public bool StopScanForDice()
    {
        if (state != State.Scanning)
        {
            Debug.LogError("Die Manager not scanning, so can't stop scanning");
            return false;
        }

        Debug.Log("stop scan");

        // Stop scanning
        PixelsCentral.StopScan();
        //BluetoothLEHardwareInterface.StopScan();
        state = State.Idle;
        return true;
    }

    public void ClearScanList()
    {
        var diceCopy = new List<Die>(_dice.Values);
        foreach (var die in diceCopy)
        {
            if (die.state == Die.State.Advertising)
            {
                _dice.Remove(die.address);
            }
        }
    }

    public void ConnectDie(
        IDie die,
        System.Action<IDie, bool, string> connectionResultCallback,
        System.Action<IDie, byte[]> onDataCallback,
        System.Action<IDie, string> onUnexpectedDisconnectionCallback)
    {
        _operations.Enqueue(new OperationConnect() { die = die, connectionResultCallback = connectionResultCallback, onDataCallback = onDataCallback, onUnexpectedDisconnectionCallback = onUnexpectedDisconnectionCallback });
        TryPerformOneOperation();
    }

    public void DisconnectDie(IDie die, System.Action<IDie, bool, string> onDisconnectionResult)
    {
        _operations.Enqueue(new OperationDisconnect() { die = die, onDisconnectionResult = onDisconnectionResult });
        TryPerformOneOperation();
    }

    /// <summary>
    /// Writes data to a connected die
    /// </summary>
    /// <param name="die">The die to write to</param>
    /// <param name="bytes">The data to write</param>
    /// <param name="bytesWrittenCallback">Callback for when the data is written</param>
    public void WriteDie(IDie die, byte[] bytes, System.Action<IDie, bool, string> bytesWrittenCallback)
    {
        _operations.Enqueue(new OperationWriteDie() { die = die, bytes = bytes, bytesWrittenCallback = bytesWrittenCallback });
        TryPerformOneOperation();
    }

    /// <summary>
    /// Connect to a die
    /// </summary>
    IEnumerator DoConnectDie(
        IDie die,
        System.Action<IDie, bool, string> connectionResultCallback,
        System.Action<IDie, byte[]> onDataCallback,
        System.Action<IDie, string> onUnexpectedDisconnectionCallback)
    {
        if (_dice.TryGetValue(die.address, out Die ddie))
        {
            if (ddie.state != Die.State.Advertising)
            {
                Debug.LogError("Die " + die.name + " in invalid state " + ddie.state);
                yield break;
            }

            ddie.state = Die.State.Connecting;
            ddie.startTime = Time.time;
            ddie.deviceConnected = false;
            ddie.messageReadCharacteristicFound = false;
            ddie.messageWriteCharacteristicFound = false;
            ddie.onConnectionResult = connectionResultCallback;
            ddie.onData = onDataCallback;
            ddie.onUnexpectedDisconnection = onUnexpectedDisconnectionCallback;

            Debug.Log("Connecting to die " + ddie.name);

            state = State.Connecting;

            // And kick off the connection!
            yield return PixelsCentral.ConnectPeripheralAsync(die.scannedPeripheral, OnPeripheralConnectionEvent);
            //BluetoothLEHardwareInterface.ConnectToPeripheral(die.address, OnDeviceConnected, OnServiceDiscovered, OnCharacteristicDiscovered, OnDeviceDisconnected);
        }
        else
        {
            string errorMessage = "Trying to connect to unknown die " + die.name;
            Debug.LogError(errorMessage);
            connectionResultCallback?.Invoke(die, false, errorMessage);
        }
    }

    /// <summary>
    /// Disconnect from a given die
    /// </summary>
    IEnumerator DoDisconnectDie(IDie die, System.Action<IDie, bool, string> onDisconnectionResult)
    {
        if (_dice.TryGetValue(die.address, out Die ddie))
        {
            Debug.Log("Disconnecting die " + die.name);
            if (ddie.state == Die.State.Advertising)
            {
                Debug.LogError("Die " + die.name + " in invalid state " + ddie.state);
                yield break;
            }

            state = State.Disconnecting;

            // And kick off the disconnection!
            ddie.state = Die.State.Disconnecting;
            ddie.onDisconnectionResult = onDisconnectionResult;
            yield return PixelsCentral.DisconnectPeripheralAsync(die.scannedPeripheral);
            //BluetoothLEHardwareInterface.DisconnectPeripheral(die.address, null); // <-- we don't use this callback, we already have one
        }
        else
        {
            Debug.LogError("Trying to disconnect unknown die " + die.name);
        }
    }

    IEnumerator DoWriteDie(IDie die, byte[] bytes, System.Action<IDie, bool, string> bytesWrittenCallback)
    {
        if (_dice.TryGetValue(die.address, out Die ddie))
        {
            if (ddie.state != Die.State.Ready)
            {
                Debug.LogError("Die " + die.name + " in invalid state " + ddie.state);
                yield break;
            }

            Debug.Log($"Writing data for {die.address} of size = {bytes.Length} with first byte = {bytes.FirstOrDefault()}");

            // Write the data!
            yield return PixelsCentral.WriteCharacteristicAsync(
                die.scannedPeripheral, new System.Guid(serviceGUID), new System.Guid(writeCharacteristic), bytes);
            //BluetoothLEHardwareInterface.WriteCharacteristic(die.address, serviceGUID, writeCharacteristic, bytes, length, false, null);
            bytesWrittenCallback?.Invoke(die, true, null);
        }
        else
        {
            Debug.LogError("Unknown die " + die.name + " received data!");
        }
    }


    // Start is called before the first frame update
    void Awake()
    {
        state = State.Uninitialized;
    }

    void Start()
    {
        state = State.Initializing;
        if (PixelsCentral.Initialize())
        {
            //TODO support for disabled BLE, notify user on error
            OnBluetoothInitComplete();
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        PixelsCentral.Shutdown();
    }

    // Update is called once per frame
    void Update()
    {
        foreach (var die in _dice.Values)
        {
            if (die.state == Die.State.Connecting)
            {
                CheckDieCharacteristics(die);
            }
            else if (die.state == Die.State.Subscribing)
            {
                CheckSubscriptionState(die);
            }
        }

        while (TryPerformOneOperation())
            ;
    }

    bool TryPerformOneOperation()
    {
        Operation op = null;
        bool res = (state == State.Idle) && _operations.TryDequeue(out op);
        if (res)
        {
            StartCoroutine(op.Process(this));
        }
        return res;
    }

    void OnBluetoothInitComplete()
    {
        // We're ready!
        state = State.Idle;
    }

    //TODO error handling
    //void OnError(string error)
    //{
    //    bool errorAttributed = false;
    //    var addressesToRemove = new List<string>();
    //    foreach (var die in _dice.Values)
    //    {
    //        switch (die.state)
    //        {
    //            case Die.State.Disconnecting:
    //                Debug.Assert(state == State.Disconnecting);
    //                state = State.Idle;
    //                Debug.LogError($"Error while disconnecting from {die.name} (connected={die.deviceConnected}): {error}");

    //                // We got an error while this die was disconnecting,
    //                // Just indicate it
    //                die.onDisconnectionResult?.Invoke(die, false, error);
    //                die.onDisconnectionResult = null;
    //                addressesToRemove.Add(die.address);
    //                errorAttributed = true;
    //                break;
    //            case Die.State.Advertising:
    //                // Ignore this die
    //                break;
    //            case Die.State.Connecting:
    //                Debug.Assert(state == State.Connecting);
    //                state = State.Idle;

    //                Debug.LogError($"Error while connecting to {die.name} (connected={die.deviceConnected}): {error}");
    //                die.onConnectionResult?.Invoke(die, false, error);
    //                die.onConnectionResult = null;
    //                die.onUnexpectedDisconnection = null;
    //                die.onData = null;

    //                // Temporarily add the die to the connected list to avoid an error message during the disconnect
    //                // And force a disconnect
    //                if (die.deviceConnected)
    //                {
    //                    die.state = Die.State.Disconnecting;
    //                    state = State.Disconnecting;
    //                    BluetoothLEHardwareInterface.DisconnectPeripheral(die.address, null);
    //                }
    //                else
    //                {
    //                    state = State.Idle;
    //                    addressesToRemove.Add(die.address);
    //                }
    //                errorAttributed = true;
    //                break;
    //            case Die.State.Connected:
    //                // Ignore this die
    //                break;
    //            case Die.State.Subscribing:
    //                {
    //                    Debug.Assert(state == State.Connecting);
    //                    state = State.Idle;
    //                    Debug.LogError($"Characteristic error with {die.name} (connected={die.deviceConnected}): {error}");

    //                    die.onConnectionResult?.Invoke(die, false, error);
    //                    die.onConnectionResult = null;
    //                    die.onUnexpectedDisconnection = null;
    //                    die.onData = null;

    //                    // Temporarily add the die to the connected list to avoid an error message during the disconnect
    //                    // And force a disconnect
    //                    die.state = Die.State.Disconnecting;
    //                    state = State.Disconnecting;
    //                    BluetoothLEHardwareInterface.DisconnectPeripheral(die.address, null);
    //                    errorAttributed = true;

    //                    // Only kick off the next subscription IF this was the 'current' subscription attempt
    //                    // Otherwise there will still be a subscription success/fail event and we'll trigger
    //                    // the next one then.
    //                    StartNextSubscribeToCharacteristic();
    //                }
    //                break;
    //            case Die.State.Ready:
    //            default:
    //                // ignore this die
    //                break;
    //        }
    //    }

    //    // Remove all the dice that errored out!
    //    // In most every case this is only one die
    //    foreach (var add in addressesToRemove)
    //    {
    //        _dice.Remove(add);
    //    }

    //    // Print something!
    //    if (!errorAttributed)
    //    {
    //        Debug.LogError("Bluetooth error: " + error);
    //    }
    //}

    void OnPeripheralDiscovered(ScannedPeripheral scannedPeripheral)
    {
        OnDeviceDiscovered(scannedPeripheral, _onDieDiscovered, _onCustomAdvertisingData);

        if (scannedPeripheral.ManufacturerData?.Count > 0)
        {
            OnDeviceAdvertisingInfo(scannedPeripheral.SystemId, scannedPeripheral.Name, scannedPeripheral.Rssi, scannedPeripheral.ManufacturerData.ToArray());
        }
    }

    void OnDeviceDiscovered(
        //string address,
        //string name,
        ScannedPeripheral scannedPeripheral,
        System.Action<IDie> onDieDiscovered,
        System.Action<IDie, int, byte[]> onCustomAdvertisingData)
    {
        string address = scannedPeripheral.SystemId;
        if (_dice.TryGetValue(address, out Die die))
        {
            switch (die.state)
            {
            case Die.State.Advertising:
                // We already know about this die, just update the advertising data handler
                die.onCustomAdvertisingData = onCustomAdvertisingData;
                break;
            case Die.State.Connecting:
                // We're about to make a connection, ignore the die
                break;
            default:
                Debug.LogError("Advertising die " + die.name + " in incorrect state " + die.state);
                _dice.Remove(address);
                break;
            }
        }
        else
        {
            // We didn't know this die before, create it
            die = new Die(scannedPeripheral);
            _dice.Add(address, die);

            Debug.Log($"Discovered new die {address} - {name}");

            // Notify die!
            die.state = Die.State.Advertising; // <-- this is the default value, but it doesn't hurt to be explicit
            die.onCustomAdvertisingData = onCustomAdvertisingData;
            onDieDiscovered?.Invoke(die);
        }
    }

    void OnDeviceAdvertisingInfo(string address, string name, int rssi, byte[] data) 
    {
        if (_dice.TryGetValue(address, out Die d))
        {
            d.onCustomAdvertisingData?.Invoke(d, rssi, data);
        }
        else 
        {
            Debug.LogError("Received advertising data for unknown die address" + address);
        }
    }

    void OnPeripheralConnectionEvent(ScannedPeripheral scannedPeripheral, bool connected)
    {
        if (connected)
        {
            OnDeviceConnected(scannedPeripheral.SystemId);
            var services = PixelsCentral.GetPeripheralDiscoveredServices(scannedPeripheral);
            if (services?.Contains(new System.Guid(serviceGUID)) ?? false)
            {
                OnServiceDiscovered(scannedPeripheral.SystemId, serviceGUID);
                var characteristics = PixelsCentral.GetPeripheralServiceCharacteristics(scannedPeripheral, new System.Guid(serviceGUID));
                if (characteristics != null)
                {
                    foreach (var uuid in characteristics)
                    {
                        OnCharacteristicDiscovered(scannedPeripheral.SystemId, serviceGUID, uuid.ToString());
                    }
                }
            }
            //TODO and if service or characteristics not found?
        }
        else
        {
            OnDeviceDisconnected(scannedPeripheral.SystemId);
        }
    }

    void OnDeviceConnected(string address)
    {
        if (_dice.TryGetValue(address, out Die die))
        {
            if (die.state != Die.State.Connecting)
            {
                Debug.LogError("Advertising die " + die.name + " in incorrect state " + die.state);
                return;
            }

            // This die received notification that it was connected to, but not necessarily found the characteristics
            die.deviceConnected = true;
            die.onCustomAdvertisingData = null;

            // Are we ready to move onto the next phase?
            CheckDieCharacteristics(die);
        }
        else
        {
            Debug.LogError("Unknown die " + address + " connected!");
        }
    }

    void OnDeviceDisconnected(string address)
    {
        // Check that this isn't an error-triggered disconnect, if it is, skip sending messages to the die
        if (_dice.TryGetValue(address, out Die die))
        {
            switch (die.state)
            {
                case Die.State.Disconnecting:
                    Debug.Assert(state == State.Disconnecting, "Wrong state " + state.ToString());
                    state = State.Idle;
                    // This is perfectly okay
                    die.onDisconnectionResult?.Invoke(die,true, null);
                    die.onDisconnectionResult = null;
                    die.state = Die.State.Advertising;
                    Debug.Log("Disconnected " + die.name);
                    break;
                case Die.State.Advertising:
                    {
                        string errorString = "Incorrect state " + die.state;
                        die.onUnexpectedDisconnection?.Invoke(die, errorString);
                        _dice.Remove(address);
                        Debug.LogError("Disconnected " + die.name + ":" + errorString);
                    }
                    break;
                case Die.State.Connecting:
                case Die.State.Connected:
                    {
                        Debug.Assert(state == State.Connecting);
                        state = State.Idle;
                        string errorString = "Disconnected before subscribing (state = " + die.state + ")";
                        die.onConnectionResult?.Invoke(die, false, errorString);
                        _dice.Remove(address);
                        Debug.LogError("Disconnected " + die.name + ":" + errorString);
                    }
                    break;
                case Die.State.Subscribing:
                    {
                        Debug.Assert(state == State.Connecting);
                        state = State.Idle;
                        string errorString = "Disconnected while subscribing";
                        die.onConnectionResult?.Invoke(die, false, errorString);
                        _dice.Remove(address);
                        Debug.LogError("Disconnected " + die.name + ":" + errorString);

                        // Only kick off the next subscription IF this was the 'current' subscription attempt
                        // Otherwise there will still be a subscription success/fail event and we'll trigger
                        // the next one then.
                        StartNextSubscribeToCharacteristic();
                    }
                    break;
                case Die.State.Ready:
                default:
                    {
                        string errorString = "Device disconnected";
                        die.onUnexpectedDisconnection?.Invoke(die, errorString);
                        _dice.Remove(address);
                        Debug.LogWarning("Disconnected " + die.name + ":" + errorString);
                    }
                    break;
            }
        }
        else
        {
            Debug.LogError("Unknown die " + address + " disconnected!");
        }
    }

    void OnServiceDiscovered(string address, string service)
    {
        // Nothing to do for now
    }

    void OnCharacteristicDiscovered(string address, string service, string characteristic)
    {
        if (_dice.TryGetValue(address, out Die die))
        {
            Debug.Log("Found characteristic " + characteristic.ToLower());
            // We are looking for 2 characteristics, a generic read and a generic write!
            if (string.Compare(service.ToLower(), serviceGUID.ToLower()) == 0)
            {
                if (die.state != Die.State.Connecting)
                {
                    Debug.LogError("Die " + die.name + " in invalid state " + die.state);
                    return;
                }

                if (string.Compare(characteristic.ToLower(), subscribeCharacteristic.ToLower()) == 0)
                    die.messageReadCharacteristicFound = true;
                else if (string.Compare(characteristic.ToLower(), writeCharacteristic.ToLower()) == 0)
                    die.messageWriteCharacteristicFound = true;

                // Are we ready to move onto the next step?
                CheckDieCharacteristics(die);
            }
            // Else ignore this characteristic
        }
        else
        {
            Debug.LogError("Unknown die " + address + " discovered characteristic!");
        }
    }

    void CheckDieCharacteristics(Die die)
    {
        // Check that the die has the read and write characteristics
        if (die.deviceConnected &&
            die.messageReadCharacteristicFound &&
            die.messageWriteCharacteristicFound)
        {
            die.state = Die.State.Connected;

            // Subscribe, but only subscribe to one characteristic at a time
            StartNextSubscribeToCharacteristic();
        }
        else
        {
            // Check timeout!
            if (Time.time - die.startTime > DiscoverCharacteristicsTimeout)
            {
                Debug.Assert(state == State.Connecting);
                state = State.Idle;
                // Wrong characteristics, we can't talk to this die!
                string errorString = "Timeout looking for characteristics on Die";
                die.onConnectionResult?.Invoke(die, false, errorString);
                die.onConnectionResult = null;
                die.onUnexpectedDisconnection = null;
                die.onData = null;
                Debug.LogError("Characteristic Error: " + die.name + ": " + errorString);

                // Temporarily add the die to the connected list to avoid an error message during the disconnect
                // And force a disconnect
                die.state = Die.State.Disconnecting;
                state = State.Disconnecting;
                StartCoroutine(PixelsCentral.DisconnectPeripheralAsync(die.scannedPeripheral));
                //BluetoothLEHardwareInterface.DisconnectPeripheral(die.address, null);
            }
            // Else just keep waiting
        }
    }

    void StartNextSubscribeToCharacteristic()
    {
        Die nextToSub = _dice.Values.FirstOrDefault(d => d.state == Die.State.Connected);
        if (nextToSub != null)
        {
            nextToSub.state = Die.State.Subscribing;

            // Set timeout...
            nextToSub.startTime = Time.time;

            // And subscribe!
            StartCoroutine(SubscribeCharacteristicAsync(nextToSub.scannedPeripheral));
            //BluetoothLEHardwareInterface.SubscribeCharacteristic(
            //    nextToSub.address,
            //    serviceGUID,
            //    subscribeCharacteristic,
            //    OnCharacteristicSubscriptionChanged,
            //    (charac, data) => OnCharacteristicData(nextToSub.address, data));
        }
        // Else no more subscription pending
    }

    IEnumerator SubscribeCharacteristicAsync(ScannedPeripheral scannedPeripheral)
    {
        string address = scannedPeripheral.SystemId;
        var request = PixelsCentral.SubscribeCharacteristicAsync(
            scannedPeripheral, new System.Guid(serviceGUID), new System.Guid(subscribeCharacteristic),
            data => OnCharacteristicData(address, data));
        yield return request;
        yield return new WaitForSeconds(1);
        if (request.IsSuccess)
        {
            OnCharacteristicSubscriptionChanged(subscribeCharacteristic);
        }
        else
        {
            Debug.LogError("SubscribeCharacteristicAsync failed");
        }
    }

    void OnCharacteristicSubscriptionChanged(string characteristic)
    {
        Die sub = _dice.Values.FirstOrDefault(d => d.state == Die.State.Subscribing);
        if (sub != null)
        {
            Debug.Assert(state == State.Connecting);
            state = State.Idle;
            sub.state = Die.State.Ready;
            sub.onConnectionResult?.Invoke(sub, true, null);
            sub.onConnectionResult = null;

            StartNextSubscribeToCharacteristic();
        }
        else
        {
            sub = _dice.Values.FirstOrDefault(d => d.state == Die.State.Disconnecting);
            if (sub == null)
            {
                Debug.LogError("Subscription success but no subscribing die");
            }
        }
    }

    void CheckSubscriptionState(Die die)
    {
        if (Time.time - die.startTime > SubscribeCharacteristicsTimeout)
        {
            Debug.Assert(state == State.Connecting);
            state = State.Idle;
            string errorString = "Timeout trying to subscribe to die";
            Debug.LogError("Characteristic Error: " + die.name + ": " + errorString);
            die.onConnectionResult?.Invoke(die, false, errorString);
            die.onConnectionResult = null;
            die.onUnexpectedDisconnection = null;
            die.onData = null;

            // Temporarily add the die to the connected list to avoid an error message during the disconnect
            // And force a disconnect
            die.state = Die.State.Disconnecting;
            state = State.Disconnecting;
            StartCoroutine(PixelsCentral.DisconnectPeripheralAsync(die.scannedPeripheral));
            //BluetoothLEHardwareInterface.DisconnectPeripheral(die.address, null);

            StartNextSubscribeToCharacteristic();
        }
    }

    void OnCharacteristicData(string address, byte[] data)
    {
        Debug.Log($"Got data for {address} of size = {data.Length} with first byte = {data.FirstOrDefault()}");

        if (_dice.TryGetValue(address, out Die die))
        {
            if (die.state != Die.State.Ready)
            {
                Debug.LogError("Die " + die.name + " in invalid state " + die.state);
                return;
            }

            // Pass on the data
            die.onData?.Invoke(die, data);
        }
        else
        {
            Debug.LogError("Unknown die " + address + " received data!");
        }
    }
}
