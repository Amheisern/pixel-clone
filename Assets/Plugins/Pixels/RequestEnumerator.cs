using System;
using System.Collections;
using UnityEngine;

namespace Systemic.Pixels.Unity.BluetoothLE
{
    public enum Operation
    {
        ConnectPeripheral,
        DisconnectPeripheral,
        ReadPeripheralRssi,
        RequestPeripheralMtu,
        ReadCharacteristic,
        WriteCharacteristic,
        SubscribeCharacteristic,
        UnsubscribeCharacteristic,
    }

    public class RequestEnumerator : IEnumerator
    {
        readonly double _timeout;
        RequestStatus? _status;

        public Operation Operation { get; }

        public bool IsDone => _status.HasValue;

        public bool IsSuccess => _status.HasValue && (_status.Value == RequestStatus.Success);

        public bool IsTimeout { get; private set; }

        public RequestStatus RequestStatus => _status.HasValue ? _status.Value : RequestStatus.InProgress;

        public string ErrorMessage => RequestStatus switch
        {
            RequestStatus.Success => null,
            RequestStatus.InProgress => "Operation in progress",
            RequestStatus.Canceled => "Operation canceled",
            RequestStatus.InvalidCall => "Invalid operation",
            RequestStatus.InvalidParameters => "Invalid parameters",
            RequestStatus.NotSupported => "Operation not supported",
            RequestStatus.ProtocolError => "GATT protocol error",
            RequestStatus.AccessDenied => "Access denied",
            RequestStatus.Timeout => "Timeout",
            _ => "Unknown error",
        };

        public object Current => null;

        internal RequestEnumerator(Operation operation, float timeoutSec, Action<NativeRequestResultHandler> action)
        {
            Operation = operation;
            _timeout = timeoutSec == 0 ? 0 : Time.realtimeSinceStartupAsDouble + timeoutSec;
            action?.Invoke(SetResult);
        }

        protected void SetResult(RequestStatus status)
        {
            // Only keep first error
            if (!_status.HasValue)
            {
                _status = status;
            }
        }

        public virtual bool MoveNext()
        {
            if ((!_status.HasValue) && (_timeout > 0))
            {
                // Update timeout
                if (Time.realtimeSinceStartupAsDouble > _timeout)
                {
                    IsTimeout = true;
                    _status = RequestStatus.Timeout;
                }
            }

            return !_status.HasValue;
        }

        public void Reset()
        {
            // Not supported
        }
    }

    public class ValueRequestEnumerator<T> : RequestEnumerator
    {
        public T Value { get; private set; }

        public ValueRequestEnumerator(Operation operation, float timeoutSec, Action<NativeValueRequestResultHandler<T>> action)
            : base(operation, timeoutSec, null)
        {
            action((value, error) =>
            {
                Value = value;
                SetResult(error);
            });
        }
    }

    public class ConnectRequestEnumerator : RequestEnumerator
    {
        PeripheralHandle _peripheral;
        DisconnectRequestEnumerator _disconnect;

        public ConnectRequestEnumerator(PeripheralHandle peripheral, float timeoutSec, Action<NativeRequestResultHandler> action)
            : base(Operation.ConnectPeripheral, timeoutSec, action)
        {
            _peripheral = peripheral;
        }

        public override bool MoveNext()
        {
            bool done;

            if (_disconnect == null)
            {
                done = !base.MoveNext();

                // Did we fail with a timeout?
                if (done && IsTimeout && _peripheral.IsValid)
                {
                    // Cancel connection attempt
                    _disconnect = new DisconnectRequestEnumerator(_peripheral);
                    done = !_disconnect.MoveNext();
                }
            }
            else
            {
                done = !_disconnect.MoveNext();
            }

            return !done;
        }
    }

    public class DisconnectRequestEnumerator : RequestEnumerator
    {
        PeripheralHandle _peripheral;

        public DisconnectRequestEnumerator(PeripheralHandle peripheral)
            : base(Operation.DisconnectPeripheral, 0, null)
        {
            _peripheral = peripheral;
            NativeInterface.DisconnectPeripheral(peripheral, SetResult);
        }

        public override bool MoveNext()
        {
            bool done = !base.MoveNext();

            // Are we done with the disconnect?
            if (done && _peripheral.IsValid)
            {
                // Release peripheral no matter what
                NativeInterface.ReleasePeripheral(_peripheral);
                _peripheral = new PeripheralHandle();
            }

            return !done;
        }
    }
}
