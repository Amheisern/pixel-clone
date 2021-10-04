using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;

using Central = Systemic.Pixels.Unity.BluetoothLE.Central;
using Peripheral = Systemic.Pixels.Unity.BluetoothLE.ScannedPeripheral;

namespace Dice
{
    public enum DesignAndColor : byte
    {
        Unknown = 0,
        Generic,
        V3_Orange,
        V4_BlackClear,
        V4_WhiteClear,
        V5_Grey,
        V5_White,
        V5_Black,
        V5_Gold,
        Onyx_Back,
        Hematite_Grey,
        Midnight_Galaxy,
        Aurora_Sky
    }

    public enum RollState : byte
    {
        Unknown = 0,
        OnFace,
        Handling,
        Rolling,
        Crooked
    };

    public enum ConnectionState
    {
        Invalid = -1,   // This is the value right after creation
        Available,      // This is a die we knew about and scanned
        Connecting,     // This die is in the process of being connected to
        Identifying,    // Getting info from the die, making sure it is valid to be used (right firmware, etc...)
        Ready,          // Die is ready for general use
        Disconnecting,  // We are currently disconnecting from this die
    }

    public partial class Die
        : MonoBehaviour
    {
        ConnectionState _connectionState = ConnectionState.Invalid; // Use property to change value
        public ConnectionState connectionState
        {
            get => _connectionState;
            protected set
            {
                if (value != _connectionState)
                {
                    Debug.Log($"Die connection state change: {_connectionState} => {value}");
                    _connectionState = value;
                }
            }
        }

        public enum LastError
        {
            None = 0,
            ConnectionError,
            Disconnected
        }

        public LastError lastError { get; protected set; } = LastError.None;

        /// <summary>
        /// This data structure mirrors the data in firmware/bluetooth/bluetooth_stack.cpp
        /// </sumary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct PixelAdvertisingData
        {
            // Die type identification
            public DesignAndColor designAndColor; // Physical look, also only 8 bits
            public byte faceCount; // Which kind of dice this is

            // Device ID
            public uint deviceId;

            // Current state
            public RollState rollState; // Indicates whether the dice is being shaken
            public byte currentFace; // Which face is currently up
            public byte batteryLevel; // 0 -> 255
        };

        // name is stored on the gameObject itself
        public int faceCount { get; protected set; } = 0;
        public DesignAndColor designAndColor { get; protected set; } = DesignAndColor.Unknown;
        public uint deviceId { get; protected set; } = 0;
        public string firmwareVersionId { get; protected set; } = "Unknown";
        public uint dataSetHash { get; protected set; } = 0;
        public uint flashSize { get; protected set; } = 0;

        public RollState state { get; protected set; } = RollState.Unknown;
        public int face { get; protected set; } = -1;

        public float? batteryLevel { get; protected set; } = null;
        public bool? charging { get; protected set; } = null;
        public int? rssi { get; protected set; } = null;

        public delegate void TelemetryEvent(Die die, AccelFrame frame);
        public TelemetryEvent _TelemetryReceived;
        public event TelemetryEvent TelemetryReceived
        {
            add
            {
                if (_TelemetryReceived == null)
                {
                    // The first time around, we make sure to request telemetry from the die
                    RequestTelemetry(true);
                }
                _TelemetryReceived += value;
            }
            remove
            {
                _TelemetryReceived -= value;
                if (_TelemetryReceived == null || _TelemetryReceived.GetInvocationList().Length == 0)
                {
                    if (connectionState == ConnectionState.Ready)
                    {
                        // Unregister from the die telemetry
                        RequestTelemetry(false);
                    }
                    // Otherwise we can't send bluetooth packets to the die, can we?
                }
            }
        }

        public delegate void StateChangedEvent(Die die, RollState newState, int newFace);
        public StateChangedEvent OnStateChanged;

        public delegate void ConnectionStateChangedEvent(Die die, ConnectionState oldState, ConnectionState newState);
        public ConnectionStateChangedEvent OnConnectionStateChanged;

        public delegate void ErrorEvent(Die die, LastError error);
        public ErrorEvent OnError;

        public delegate void SettingsChangedEvent(Die die);
        public SettingsChangedEvent OnSettingsChanged;

        public delegate void AppearanceChangedEvent(Die die, int newFaceCount, DesignAndColor newDesign);
        public AppearanceChangedEvent OnAppearanceChanged;

        public delegate void BatteryLevelChangedEvent(Die die, float? level, bool? charging);
        public BatteryLevelChangedEvent OnBatteryLevelChanged;

        public delegate void RssiChangedEvent(Die die1, int? rssi);
        public RssiChangedEvent OnRssiChanged;

        // Lock so that only one 'operation' can happen at a time on a die
        // Note: lock is not a real multithreaded lock!
        bool bluetoothOperationInProgress = false;

        // Internal delegate per message type
        delegate void MessageReceivedDelegate(IDieMessage msg);
        Dictionary<DieMessageType, MessageReceivedDelegate> messageDelegates;

        void Awake()
        {
            messageDelegates = new Dictionary<DieMessageType, MessageReceivedDelegate>();

            // Setup delegates for face and telemetry
            messageDelegates.Add(DieMessageType.State, OnStateMessage);
            messageDelegates.Add(DieMessageType.Telemetry, OnTelemetryMessage);
            messageDelegates.Add(DieMessageType.DebugLog, OnDebugLogMessage);
            messageDelegates.Add(DieMessageType.NotifyUser, OnNotifyUserMessage);
            messageDelegates.Add(DieMessageType.PlaySound, OnPlayAudioClip);
        }

        public void OnData(byte[] data)
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
    }
}