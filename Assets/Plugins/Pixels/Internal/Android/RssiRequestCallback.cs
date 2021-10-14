using UnityEngine;

namespace Systemic.Pixels.Unity.BluetoothLE.Internal.Android
{
    sealed class RssiRequestCallback : AndroidJavaProxy
    {
        NativeValueRequestResultHandler<int> _onRssiRead;

        public RssiRequestCallback(NativeValueRequestResultHandler<int> onRssiRead)
            : base("com.systemic.pixels.Peripheral$RssiRequestCallback")
            => _onRssiRead = onRssiRead;

        // @IntRange(from = -128, to = 20)
        void onRssiRead(AndroidJavaObject device, int rssi)
        {
            Debug.Log($"{Operation.ReadPeripheralRssi} ==> onRssiRead {rssi}");
            _onRssiRead?.Invoke(rssi, RequestStatus.Success);
        }

        void onRequestFailed(AndroidJavaObject device, int status)
        {
            Debug.LogError($"{Operation.ReadPeripheralRssi} ==> onRequestFailed: {(AndroidRequestStatus)status}");
            _onRssiRead?.Invoke(0, AndroidNativeInterfaceImpl.ToRequestStatus(status));
        }

        void onInvalidRequest()
        {
            Debug.LogError($"{Operation.ReadPeripheralRssi} ==> onInvalidRequest");
            _onRssiRead?.Invoke(0, RequestStatus.InvalidCall);
        }
    }
}
