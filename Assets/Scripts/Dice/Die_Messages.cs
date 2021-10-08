using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Dice
{
    partial class Die
    {
        const float AckMessageTimeout = 5;

        #region Message Infrastructure

        void AddMessageHandler(DieMessageType msgType, MessageReceivedDelegate newDel)
        {
            if (messageDelegates.TryGetValue(msgType, out MessageReceivedDelegate del))
            {
                del += newDel;
                messageDelegates[msgType] = del;
            }
            else
            {
                messageDelegates.Add(msgType, newDel);
            }
        }

        void RemoveMessageHandler(DieMessageType msgType, MessageReceivedDelegate newDel)
        {
            if (messageDelegates.TryGetValue(msgType, out MessageReceivedDelegate del))
            {
                del -= newDel;
                if (del == null)
                {
                    messageDelegates.Remove(msgType);
                }
                else
                {
                    messageDelegates[msgType] = del;
                }
            }
        }

        void PostMessage<T>(T message)
            where T : IDieMessage
        {
            EnsureRunningOnMainThread();

            Debug.Log($"Posting message of type {message.GetType()}");

            WriteData(DieMessages.ToByteArray(message), null);
        }

        IEnumerator WaitForMessageCr(DieMessageType msgType, System.Action<IDieMessage> msgReceivedCallback)
        {
            bool msgReceived = false;
            IDieMessage msg = default;
            void callback(IDieMessage ackMsg)
            {
                msgReceived = true;
                msg = ackMsg;
            }

            AddMessageHandler(msgType, callback);
            yield return new WaitUntil(() => msgReceived);
            RemoveMessageHandler(msgType, callback);
            if (msgReceivedCallback != null)
            {
                msgReceivedCallback.Invoke(msg);
            }
        }

        IEnumerator SendMessageWithAckOrTimeoutCr<T>(T message, DieMessageType ackType, System.Action<IDieMessage> ackAction, System.Action timeoutAction)
            where T : IDieMessage
        {
            Debug.Log($"Sending message of type {typeof(T)} with ACK of type {message.GetType()}");

            IDieMessage ackMessage = null;
            float timeout = Time.realtimeSinceStartup + AckMessageTimeout;
            void callback(IDieMessage ackMsg) => ackMessage = ackMsg;

            AddMessageHandler(ackType, callback);
            WriteData(DieMessages.ToByteArray(message), null);
            while (ackMessage == null && Time.realtimeSinceStartup < timeout)
            {
                yield return null;
            }
            RemoveMessageHandler(ackType, callback);

            if (ackMessage != null)
            {
                ackAction?.Invoke(ackMessage);
            }
            else
            {
                Debug.LogError($"Timeout on sending message of type {message.GetType()}");
                timeoutAction?.Invoke();
            }
        }

        IEnumerator SendMessageWithAckRetryCr<T>(T message, DieMessageType ackType, int retryCount, System.Action<IDieMessage> ackAction, System.Action timeoutAction)
            where T : IDieMessage
        {
            bool msgReceived = false;
            void msgAction(IDieMessage msg)
            {
                msgReceived = true;
                ackAction?.Invoke(msg);
            }

            while ((!msgReceived) && (retryCount >= 0))
            {
                // Retry every half second if necessary
                yield return StartCoroutine(SendMessageWithAckOrTimeoutCr(message, ackType, msgAction, timeoutAction));
                --retryCount;
            }
        }

        #endregion

        public void PlayAnimation(int animationIndex)
        {
            PostMessage(new DieMessagePlayAnim() { index = (byte)animationIndex });
        }

        public void PlayAnimation(int animationIndex, int remapFace, bool loop)
        {
            PostMessage(new DieMessagePlayAnim()
            {
                index = (byte)animationIndex,
                remapFace = (byte)remapFace,
                loop = loop ? (byte)1 : (byte)0
            });
        }

        public void StopAnimation(int animationIndex, int remapIndex)
        {
            PostMessage(new DieMessageStopAnim()
            {
                index = (byte)animationIndex,
                remapFace = (byte)remapIndex,
            });
        }

        public void StartAttractMode()
        {
            PostMessage(new DieMessageAttractMode());
        }

        public Coroutine GetDieState(System.Action<bool> callback)
        {
            var whoAreYouMsg = new DieMessageRequestState();
            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                whoAreYouMsg,
                DieMessageType.State,
                _ => callback?.Invoke(true),
                () => callback?.Invoke(false)));
        }

        public Coroutine GetDieInfo(System.Action<bool> callback)
        {
            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                new DieMessageWhoAreYou(),
                DieMessageType.IAmADie,
                msg =>
                {
                    var idMsg = (DieMessageIAmADie)msg;
                    bool appearanceChanged = faceCount != idMsg.faceCount || designAndColor != idMsg.designAndColor;
                    faceCount = idMsg.faceCount;
                    designAndColor = idMsg.designAndColor;
                    dataSetHash = idMsg.dataSetHash;
                    flashSize = idMsg.flashSize;
                    firmwareVersionId = idMsg.versionInfo;
                    Debug.Log($"Die {name} has {flashSize} bytes available for data, current dataset hash {dataSetHash:X08}, firmware version is {firmwareVersionId}");
                    if (appearanceChanged)
                    {
                        AppearanceChanged?.Invoke(this, faceCount, designAndColor);
                    }
                    callback?.Invoke(true);
                },
                () => callback?.Invoke(false)));
        }

        public void RequestTelemetry(bool on)
        {
            PostMessage(new DieMessageRequestTelemetry() { telemetry = on ? (byte)1 : (byte)0 });
        }

        public void RequestBulkData()
        {
            PostMessage(new DieMessageTestBulkSend());
        }

        public void PrepareBulkData()
        {
            PostMessage(new DieMessageTestBulkReceive());
        }

        public void SetLEDsToRandomColor()
        {
            var msg = new DieMessageSetAllLEDsToColor();
            uint r = (byte)Random.Range(0, 256);
            uint g = (byte)Random.Range(0, 256);
            uint b = (byte)Random.Range(0, 256);
            msg.color = (r << 16) + (g << 8) + b;
            PostMessage(msg);
        }

        public void SetLEDsToColor(Color color)
        {
            Color32 color32 = color;
            PostMessage(new DieMessageSetAllLEDsToColor
            {
                color = (uint)((color32.r << 16) + (color32.g << 8) + color32.b)
            });
        }

        public Coroutine GetBatteryLevel(System.Action<Die, float?> outLevelAction)
        {
            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                new DieMessageRequestBatteryLevel(),
                DieMessageType.BatteryLevel,
                msg =>
                {
                    var lvlMsg = (DieMessageBatteryLevel)msg;
                    batteryLevel = lvlMsg.level;
                    charging = lvlMsg.charging != 0;
                    BatteryLevelChanged?.Invoke(this, lvlMsg.level, lvlMsg.charging != 0);
                    outLevelAction?.Invoke(this, lvlMsg.level);
                },
                () => outLevelAction?.Invoke(this, null)));
        }

        public Coroutine GetRssi(System.Action<Die, int?> outRssiAction)
        {
            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                new DieMessageRequestRssi(),
                DieMessageType.Rssi,
                msg =>
                {
                    var rssiMsg = (DieMessageRssi)msg;
                    rssi = rssiMsg.rssi;
                    RssiChanged?.Invoke(this, rssiMsg.rssi);
                    outRssiAction?.Invoke(this, rssiMsg.rssi);
                },
                () => outRssiAction?.Invoke(this, null)));
        }

        public Coroutine SetCurrentDesignAndColor(DieDesignAndColor design, System.Action<bool> callback)
        {
            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                new DieMessageSetDesignAndColor() { designAndColor = design },
                DieMessageType.SetDesignAndColorAck,
                _ =>
                {
                    designAndColor = design;
                    AppearanceChanged?.Invoke(this, faceCount, designAndColor);
                    callback?.Invoke(true);
                },
                () => callback?.Invoke(false)));
        }

        public Coroutine RenameDie(string newName, System.Action<bool> callback)
        {
            Debug.Log("Renaming to " + newName);

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(newName + "\0");
            byte[] nameByte10 = new byte[10]; // 10 is the declared size in DieMessageSetName. There is probably a better way to do this...
            System.Array.Copy(nameBytes, nameByte10, nameBytes.Length);

            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                new DieMessageSetName { name = nameByte10 },
                DieMessageType.SetNameAck,
                _ => callback?.Invoke(true),
                () => callback?.Invoke(false)));
        }

        public Coroutine Flash(Color color, int count, System.Action<bool> callback)
        {
            Color32 color32 = color;
            var msg = new DieMessageFlash
            {
                color = (uint)((color32.r << 16) + (color32.g << 8) + color32.b),
                flashCount = (byte)count,
            };
            return StartCoroutine(SendMessageWithAckOrTimeoutCr(
                msg,
                DieMessageType.FlashFinished,
                _ => callback?.Invoke(true),
                () => callback?.Invoke(false)));
        }

        public void StartHardwareTest()
        {
            PostMessage(new DieMessageTestHardware());
        }

        public void StartCalibration()
        {
            PostMessage(new DieMessageCalibrate());
        }

        public void CalibrateFace(int face)
        {
            PostMessage(new DieMessageCalibrateFace() { face = (byte)face });
        }

        public void SetStandardMode()
        {
            PostMessage(new DieMessageSetStandardState());
        }

        public void SetLEDAnimatorMode()
        {
            PostMessage(new DieMessageSetLEDAnimState());
        }

        public void SetBattleMode()
        {
            PostMessage(new DieMessageSetBattleState());
        }

        public void DebugAnimController()
        {
            PostMessage(new DieMessageDebugAnimController());
        }

        public Coroutine PrintNormals()
        {
            return StartCoroutine(PrintNormalsCr());

            IEnumerator PrintNormalsCr()
            {
                for (int i = 0; i < 20; ++i)
                {
                    var msg = new DieMessagePrintNormals { face = (byte)i };
                    PostMessage(msg);
                    yield return new WaitForSeconds(0.5f);
                }
            }
        }

        public void ResetParams()
        {
            PostMessage(new DieMessageProgramDefaultParameters());
        }

        #region MessageHandlers

        void OnStateMessage(IDieMessage message)
        {
            // Handle the message
            var stateMsg = (DieMessageState)message;
            Debug.Log($"State: {stateMsg.state}, {stateMsg.face}");

            var newState = (DieRollState)stateMsg.state;
            var newFace = stateMsg.face;
            if (newState != state || newFace != face)
            {
                state = newState;
                face = newFace;

                // Notify anyone who cares
                StateChanged?.Invoke(this, state, face);
            }
        }

        void OnTelemetryMessage(IDieMessage message)
        {
            // Don't bother doing anything with the message if we don't have
            // anybody interested in telemetry data.
            if (_TelemetryReceived != null)
            {
                // Notify anyone who cares
                var telem = (DieMessageAcc)message;
                _TelemetryReceived.Invoke(this, telem.data);
            }
        }

        void OnDebugLogMessage(IDieMessage message)
        {
            var dlm = (DieMessageDebugLog)message;
            string text = System.Text.Encoding.UTF8.GetString(dlm.data, 0, dlm.data.Length);
            Debug.Log(name + ": " + text);
        }

        void OnNotifyUserMessage(IDieMessage message)
        {
            var notifyUserMsg = (DieMessageNotifyUser)message;
            bool ok = notifyUserMsg.ok != 0;
            bool cancel = notifyUserMsg.cancel != 0;
            float timeout = (float)notifyUserMsg.timeout_s;
            string text = System.Text.Encoding.UTF8.GetString(notifyUserMsg.data, 0, notifyUserMsg.data.Length);
            PixelsApp.Instance.ShowDialogBox("Message from " + name, text, "Ok", cancel ? "Cancel" : null, (res) =>
            {
                PostMessage(new DieMessageNotifyUserAck() { okCancel = (byte)(res ? 1 : 0) });
            });
        }

        void OnPlayAudioClip(IDieMessage message)
        {
            var playClipMessage = (DieMessagePlaySound)message;
            AudioClipManager.Instance.PlayAudioClip((uint)playClipMessage.clipId);
        }

        #endregion
    }
}
