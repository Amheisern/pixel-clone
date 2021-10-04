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
    //class PoolDie
    //{
    //    public Die die;
    //    public Peripheral peripheral;
    //    public System.Action<ConnectionState> setState;
    //    public System.Action<Die.LastError> setError;
    //    public System.Action<Die, bool, string> onConnectionResult;
    //    public System.Action<Die, bool, string> onDisconnectionResult;

    //    public int currentConnectionCount = 0;
    //    public float lastRequestDisconnectTime = 0.0f;
    //}

    sealed class PoolDie : Die
    {
        public Peripheral peripheral { get; private set; }

        public int currentConnectionCount = 0;
        public float lastRequestDisconnectTime = 0.0f;
        public System.Action<Die, bool, string> onConnectionResult;
        public System.Action<Die, bool, string> onDisconnectionResult;

        public void Setup(Peripheral peripheral)
        {
            //bool appearanceChanged = faceCount != peripheral.faceCount || designAndColor != peripheral.designAndColor;
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
                int size = Marshal.SizeOf(typeof(Die.PixelAdvertisingData));
                if (peripheral.ManufacturerData.Count == size)
                {
                    System.IntPtr ptr = Marshal.AllocHGlobal(size);
                    Marshal.Copy(peripheral.ManufacturerData.ToArray(), 0, ptr, size);
                    var customData = Marshal.PtrToStructure<Die.PixelAdvertisingData>(ptr);
                    Marshal.FreeHGlobal(ptr);

                    // Update die data
                    UpdateAdvertisingData(peripheral.Rssi, customData);
                }
                else
                {
                    Debug.LogError($"Incorrect advertising data length {peripheral.ManufacturerData.Count}, expected: {size}");
                }
            }
        }

        void UpdateAdvertisingData(int rssi, PixelAdvertisingData newData)
        {
            bool appearanceChanged = faceCount != newData.faceCount || designAndColor != newData.designAndColor;
            bool rollStateChanged = state != newData.rollState || face != newData.currentFace;
            faceCount = newData.faceCount;
            designAndColor = newData.designAndColor;
            deviceId = newData.deviceId;
            state = newData.rollState;
            face = newData.currentFace;
            batteryLevel = (float)newData.batteryLevel / 255.0f;
            this.rssi = rssi;

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

        public void UpdateInfo(System.Action<Die, bool> onInfoUpdatedCallback)
        {
            if (connectionState == ConnectionState.Identifying)
            {
                StartCoroutine(UpdateInfoCr());

                IEnumerator UpdateInfoCr()
                {
                    // Ask the die who it is!
                    yield return GetDieInfo(null);

                    // Ping the die so we know its initial state
                    yield return Ping();

                    onInfoUpdatedCallback?.Invoke(this, true);
                }
            }
            else
            {
                onInfoUpdatedCallback?.Invoke(this, false);
            }
        }

        public void SetConnectionState(ConnectionState newState)
        {
            if (newState != connectionState)
            {
                var oldState = connectionState;
                connectionState = newState;
                OnConnectionStateChanged?.Invoke(this, oldState, newState);
            }
        }

        public void SetLastError(LastError newError)
        {
            lastError = newError;
            OnError?.Invoke(this, newError);
        }
    }
}
