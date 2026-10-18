using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HappyEngine.Base;
using NetworkLib;
using Obf_CA;
using Phonest.ClientForUnity;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Stand-in for the client's Tencent IM adapter (Obf_CA.Obf_ei): the Lua chat code speaks Tencent.IM protobuf
    /// (GM_Chat.proto) with a command id, and this adapter tunnels those bytes through the game connection instead of
    /// Tencent's cloud. Frames both ways: uint32 LE cmd ‖ int32 LE errorCode ‖ protobuf.
    /// Up: game message 77010; down: game message 77011.
    /// Its per-frame pump and receive hook also carry the voice relay ticket (77016) and pump (VoiceClient).
    /// </summary>
    public sealed class ServerImAdapter : Obf_ci
    {
        public const uint UpMsgId = 77010;
        public const uint DownMsgId = 77011;

        private const uint StatusLoginSucc = 9;
        private const uint StatusLogoutSucc = 11;
        private const int MaxPerFrame = 20;

        private readonly ConcurrentQueue<Frame> _inbound = new ConcurrentQueue<Frame>();
        private readonly ConcurrentQueue<uint> _status = new ConcurrentQueue<uint>();
        private readonly ConcurrentQueue<string> _diagnostics = new ConcurrentQueue<string>();
        private readonly Dictionary<uint, Action> _statusCallbacks = new Dictionary<uint, Action>();
        private Action _requestCallback;
        private LuaArrAccess _requestArray;
        private HappyEngineClient _hooked;
        private OnGameMsgReceived _hook;

        private struct Frame
        {
            public uint Cmd;
            public int Error;
            public byte[] Body;
        }

        /// <summary>IM login: the server already knows who we are; report success.</summary>
        public void Obf_JkD(string userId, string userSig)
        {
            Debug.Log("[Ro3Emu.Chat] login as " + userId);
            _status.Enqueue(StatusLoginSucc);
        }

        /// <summary>Send: remember Lua's reply callback (all replies and pushes go through it), forward the command.</summary>
        public void Obf_kkD(uint cmd, byte[] body, Action callback, LuaArrAccess array)
        {
            _requestCallback = callback;
            _requestArray = array;
            body = body ?? new byte[0];
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(cmd).CopyTo(frame, 0);
            BitConverter.GetBytes(0).CopyTo(frame, 4);
            Buffer.BlockCopy(body, 0, frame, 8, body.Length);
            SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(UpMsgId, frame);
        }

        public void Obf_KkD(uint status, Action callback, LuaArrAccess array)
        {
            _statusCallbacks[status] = callback;
        }

        /// <summary>Pump, called every frame: status changes, then queued server frames into Lua.</summary>
        public void Obf_lkD()
        {
            AttachHook();
            int n = 0;
            while (n++ < MaxPerFrame && _status.TryDequeue(out uint status))
            {
                if (status == StatusLoginSucc) SingletonMonoBehaviour<NW_ChatNetworkManager>.Instance.Obf_OXC(true);
                else if (status == StatusLogoutSucc) SingletonMonoBehaviour<NW_ChatNetworkManager>.Instance.Obf_OXC(false);
                if (_statusCallbacks.TryGetValue(status, out var cb)) cb?.Invoke();
            }
            // Keep server frames queued until Lua has given us its callback (the first Send).
            n = 0;
            while (_requestCallback != null && n++ < MaxPerFrame && _inbound.TryDequeue(out var frame)) Deliver(frame);
            while (_diagnostics.TryDequeue(out var line)) Diagnostics.Run(line);
            VoiceClient.Pump();
            UiTweaks.Tick();
        }

        /// <summary>IM logout.</summary>
        public void Obf_LkD()
        {
            _status.Enqueue(StatusLogoutSucc);
        }

        public void Obf_mkD()
        {
            Detach();
            while (_inbound.TryDequeue(out _)) { }
            while (_status.TryDequeue(out _)) { }
            _requestCallback = null;
            _requestArray = null;
            VoiceClient.Shutdown(); // voice must not outlive the game connection (mic left open after logout)
        }

        /// <summary>Same hand-off as the Tencent adapter: cmd, length, pinned pointer, error code in the shared array.</summary>
        private unsafe void Deliver(Frame frame)
        {
            var body = frame.Body.Length > 0 ? frame.Body : new byte[1];
            fixed (byte* p = body)
            {
                try
                {
                    _requestArray.Obf_PAD(1, frame.Cmd);
                    _requestArray.Obf_OAD(2, frame.Body.Length);
                    _requestArray.Obf_PAD(3, new IntPtr(p).ToInt64());
                    _requestArray.Obf_OAD(4, frame.Error);
                    _requestCallback();
                }
                catch (Exception e)
                {
                    Debug.LogError("[Ro3Emu.Chat] deliver cmd " + frame.Cmd + " failed: " + e);
                }
            }
        }

        /// <summary>The engine client is recreated on reconnect; follow it.</summary>
        private void AttachHook()
        {
            var client = SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance?.Obf_a;
            if (client == null || ReferenceEquals(client, _hooked)) return;
            Detach();
            _hook = OnMessage;
            client.OnMsgReceived = (OnGameMsgReceived)Delegate.Combine(client.OnMsgReceived, _hook);
            _hooked = client;
            Debug.Log("[Ro3Emu.Chat] hooked HappyEngineClient.OnMsgReceived");
        }

        private void Detach()
        {
            if (_hooked != null && _hook != null) _hooked.OnMsgReceived = (OnGameMsgReceived)Delegate.Remove(_hooked.OnMsgReceived, _hook);
            _hooked = null;
        }

        private void OnMessage(uint msgId, ReadOnlyMemory<byte> memory)
        {
            if (msgId == Diagnostics.MsgId)
            {
                _diagnostics.Enqueue(System.Text.Encoding.UTF8.GetString(memory.ToArray()));
                return;
            }
            if (msgId == VoiceProtocol.TicketMsgId)
            {
                VoiceClient.OnTicket(memory.ToArray());
                return;
            }
            if (msgId != DownMsgId || memory.Length < 8) return;
            var data = memory.ToArray();
            _inbound.Enqueue(new Frame
            {
                Cmd = BitConverter.ToUInt32(data, 0),
                Error = BitConverter.ToInt32(data, 4),
                Body = data.AsSpan(8).ToArray(),
            });
        }
    }
}
