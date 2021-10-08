using System;
using UnityEngine;

namespace Systemic.Pixels.Unity.BluetoothLE.Internal.Android
{
    sealed class RequestCallback : AndroidJavaProxy
    {
        Operation _operation;
        Action<int> _onRequestDone;

        public RequestCallback(Operation operation, NativeRequestResultHandler onResult)
            : base("com.systemic.pixels.Peripheral$RequestCallback")
            => (_operation, _onRequestDone) = (operation, errorCode => onResult(new NativeError(errorCode, "Android error")));

        void onRequestCompleted(AndroidJavaObject device)
        {
            Debug.Log($"{_operation} ==> onRequestCompleted");
            _onRequestDone?.Invoke(0); //RequestStatus.GATT_SUCCESS
        }

        void onRequestFailed(AndroidJavaObject device, int status)
        {
            Debug.LogError($"{_operation} ==> onRequestFailed: {(AndroidRequestStatus)status}");
            _onRequestDone?.Invoke(status);
        }

        void onInvalidRequest()
        {
            Debug.LogError($"{_operation} ==> onInvalidRequest");
            _onRequestDone?.Invoke((int)AndroidRequestStatus.REASON_REQUEST_INVALID);
        }
    }
}
