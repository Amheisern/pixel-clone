using System.Collections;
using UnityEngine;

namespace Dice
{
    partial class Die
    {
        protected interface IOperationEnumerator : IEnumerator, System.IDisposable
        {
            bool IsDone { get; }

            bool IsSuccess { get; }

            bool IsTimeOut { get; }

            string Error { get; }
        }

        class WaitForMessageEnumerator<T> : IOperationEnumerator
            where T : IDieMessage, new()
        {
            readonly DieMessageType _msgType;
            readonly float _timeout;
            bool _isTimedOut;
            bool _isStarted;

            public bool IsDone => IsSuccess || (Error != null) || IsTimeOut;

            public bool IsSuccess => Message != null;

            public string Error { get; protected set; }

            public bool IsTimeOut => _isTimedOut = _isTimedOut || ((_timeout > 0) && (Time.realtimeSinceStartupAsDouble > _timeout));

            public T Message { get; private set; }

            public object Current => null;

            protected Die Die { get; }

            protected bool IsDisposed { get; private set; }

            public WaitForMessageEnumerator(Die die, float timeoutSec = AckMessageTimeout)
            {
                if (timeoutSec <= 0) throw new System.ArgumentException("Timeout value must be greater than zero", nameof(timeoutSec));

                Die = die ?? throw new System.ArgumentNullException(nameof(die));
                _timeout = Time.realtimeSinceStartup + timeoutSec;
                _msgType = DieMessages.GetMessageType<T>();
            }

            public virtual bool MoveNext()
            {
                if (IsDisposed) throw new System.ObjectDisposedException(nameof(WaitForMessageEnumerator<T>));

                if (!_isStarted)
                {
                    _isStarted = true;
                    Die.AddMessageHandler(_msgType, OnMessage);
                }

                // ErrorMessage might be set by child class
                bool done = IsSuccess || IsTimeOut || (Error != null);
                if (done)
                {
                    Die.RemoveMessageHandler(_msgType, OnMessage);

                    if (IsSuccess)
                    {
                        if (Error != null)
                        {
                            // Some error occurred, we might have got an old message
                            Message = default;
                        }
                    }
                    else if (Error == null)
                    {
                        // Operation failed
                        Error = $"{(IsTimeOut ? "Timeout on" : "Unknown error")} waiting for message of type {typeof(T)}";
                    }
                }
                return !done;
            }

            public void Reset()
            {
                // Not supported
            }

            public virtual void Dispose()
            {
                IsDisposed = true;
                Die.RemoveMessageHandler(_msgType, OnMessage);
            }

            void OnMessage(IDieMessage msg)
            {
                Debug.Assert(msg is T);
                Message = (T)msg;
                Die.RemoveMessageHandler(_msgType, OnMessage);
            }
        }

        class SendMessageAndWaitForResponseEnumerator<TMsg, TResp> : WaitForMessageEnumerator<TResp>
            where TMsg : IDieMessage, new()
            where TResp : IDieMessage, new()
        {
            IOperationEnumerator _sendMessage;
            readonly System.Type _msgType;

            public SendMessageAndWaitForResponseEnumerator(Die die, TMsg message, float timeoutSec = AckMessageTimeout)
                : base(die, timeoutSec)
            {
                if (message == null) throw new System.ArgumentNullException(nameof(message));

                _msgType = message.GetType();
                _sendMessage = Die.WriteDataAsync(DieMessages.ToByteArray(message), timeoutSec);
            }

            public SendMessageAndWaitForResponseEnumerator(Die die, float timeoutSec = AckMessageTimeout)
                : this(die, new TMsg(), timeoutSec)
            {
            }

            public override bool MoveNext()
            {
                if (IsDisposed) throw new System.ObjectDisposedException(nameof(SendMessageAndWaitForResponseEnumerator<TMsg, TResp>));

                if ((_sendMessage != null) && (!_sendMessage.MoveNext()))
                {
                    if (!_sendMessage.IsSuccess)
                    {
                        // Done sending message
                        Error = $"Failed to send message of type {typeof(TMsg)}, {_sendMessage.Error}";
                    }
                    _sendMessage = null;
                }

                return base.MoveNext();
            }

            public override void Dispose()
            {
                base.Dispose();
                _sendMessage.Dispose();
            }
        }

        class SendMessageAndProcessResponseEnumerator<TMsg, TResp> : SendMessageAndWaitForResponseEnumerator<TMsg, TResp>
            where TMsg : IDieMessage, new()
            where TResp : IDieMessage, new()
        {
            System.Action<TResp> _onResponse;

            public SendMessageAndProcessResponseEnumerator(Die die, TMsg message, System.Action<TResp> onResponse, float timeoutSec = AckMessageTimeout)
               : base(die, message, timeoutSec)
            {
                _onResponse = onResponse ?? throw new System.ArgumentNullException(nameof(onResponse));
            }

            public SendMessageAndProcessResponseEnumerator(Die die, System.Action<TResp> onResponse, float timeoutSec = AckMessageTimeout)
               : this(die, new TMsg(), onResponse, timeoutSec)
            {
            }

            public override bool MoveNext()
            {
                bool result = base.MoveNext();
                if (IsSuccess)
                {
                    _onResponse(Message);
                }
                return result;
            }
        }

        class SendMessageAndProcessResponseWithValue<TMsg, TResp, TValue> : SendMessageAndWaitForResponseEnumerator<TMsg, TResp>
            where TMsg : IDieMessage, new()
            where TResp : IDieMessage, new()
        {
            System.Func<TResp, TValue> _onResponse;

            public TValue Value { get; private set; }

            public SendMessageAndProcessResponseWithValue(Die die, TMsg message, System.Func<TResp, TValue> onResponse, float timeoutSec = AckMessageTimeout)
               : base(die, message, timeoutSec)
            {
                _onResponse = onResponse ?? throw new System.ArgumentNullException(nameof(onResponse)); ;
            }

            public SendMessageAndProcessResponseWithValue(Die die, System.Func<TResp, TValue> onResponse, float timeoutSec = AckMessageTimeout)
                : this(die, new TMsg(), onResponse, timeoutSec)
            {
            }

            public override bool MoveNext()
            {
                bool result = base.MoveNext();
                if (IsSuccess)
                {
                    Value = _onResponse(Message);
                }
                return result;
            }
        }
    }
}
