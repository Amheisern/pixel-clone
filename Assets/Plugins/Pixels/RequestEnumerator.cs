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
        bool _isTimedOut;
        NativeError? _error;

        public Operation Operation { get; }

        public bool IsDone => _error.HasValue;

        public bool IsSuccess => _error.HasValue && _error.Value.IsEmpty;

        public bool IsTimeOut => _isTimedOut = _isTimedOut || ((_timeout > 0) && (Time.realtimeSinceStartupAsDouble > _timeout));

        public int ErrorCode => _error?.Code ?? 0;

        public string ErrorMessage => _error?.Message;

        public object Current => null;

        internal RequestEnumerator(Operation operation, float timeoutSec, Action<NativeRequestResultHandler> action)
        {
            Operation = operation;
            _timeout = timeoutSec == 0 ? 0 : Time.realtimeSinceStartupAsDouble + timeoutSec;
            action?.Invoke(SetResult);
        }

        protected void SetResult(NativeError error)
        {
            _error = error;
        }

        public virtual bool MoveNext()
        {
            bool done = _error.HasValue || IsTimeOut;
            if (done && IsTimeOut && (!_error.HasValue))
            {
                _error = new NativeError((int)Error.Timeout, "Timeout");
            }
            return !done;
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
                if (done && IsTimeOut && _peripheral.IsValid)
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
