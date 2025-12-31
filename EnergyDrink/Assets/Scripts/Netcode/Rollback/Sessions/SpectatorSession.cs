using System;
using System.Collections.Generic;
using Netcode.Rollback.Network;
using UnityEngine.Assertions;

namespace Netcode.Rollback.Sessions
{
    public static class SpectatorConstants
    {
        public const uint SPECTATOR_BUFFER_SIZE = 60;
    }

    public class SpectatorSession<TState, TInput, TAddress>
        where TState : struct
        where TInput : IInput<TInput>
    {
        const uint NORMAL_SPEED = 1;

        private SessionState _state;
        private int _numPlayers;
        private PlayerInput<TInput>[][] _inputs;
        private ConnectionStatus[] _hostConnectStatus;
        private INonBlockingSocket<TAddress> _socket;
        private UdpProtocol<TInput, TAddress> _host;
        private Deque<RollbackEvent<TInput, TAddress>> _eventQueue;
        private Frame _currentFrame;
        private Frame _lastRecvFrame;
        private uint _maxFramesBehind;
        private uint _catchupSpeed;

        public SpectatorSession(int numPlayers, INonBlockingSocket<TAddress> socket, UdpProtocol<TInput, TAddress> host, uint maxFramesBehind, uint catchupSpeed)
        {
            _hostConnectStatus = new ConnectionStatus[numPlayers];
            for (int i = 0; i < numPlayers; i++) { _hostConnectStatus[i] = ConnectionStatus.Default; }

            _state = SessionState.Synchronizing;
            _numPlayers = numPlayers;
            _inputs = new PlayerInput<TInput>[SpectatorConstants.SPECTATOR_BUFFER_SIZE][];
            for (int i = 0; i < SpectatorConstants.SPECTATOR_BUFFER_SIZE; i++)
            {
                _inputs[i] = new PlayerInput<TInput>[numPlayers];
                for (int j = 0; j < numPlayers; j++)
                {
                    _inputs[i][j] = PlayerInput<TInput>.BlankInput(Frame.NullFrame);
                }
            }
            _socket = socket;
            _host = host;
            _eventQueue = new Deque<RollbackEvent<TInput, TAddress>>();
            _currentFrame = Frame.NullFrame;
            _lastRecvFrame = Frame.NullFrame;
            _maxFramesBehind = maxFramesBehind;
            _catchupSpeed = catchupSpeed;
        }

        public SessionState CurrentState => _state;
        public uint FramesBehindHost => (uint)(_lastRecvFrame - _currentFrame);
        public NetworkStats NetworkStats() => _host.NetworkStats();
        public IEnumerable<RollbackEvent<TInput, TAddress>> DrainEvents()
        {
            while (_eventQueue.Count > 0) { yield return _eventQueue.PopFront(); }
        }
        public List<RollbackRequest<TState, TInput>> AdvanceFrame()
        {
            PollRemoteClients();
            if (_state != SessionState.Running) { throw new InvalidOperationException("not synchronized yet"); }

            List<RollbackRequest<TState, TInput>> requests = new List<RollbackRequest<TState, TInput>>();
            uint framesToAdvance = FramesBehindHost > _maxFramesBehind ? _catchupSpeed : NORMAL_SPEED;

            for (int i = 0; i < framesToAdvance; i++)
            {
                Frame frameToGrab = _currentFrame + 1;
                (TInput input, InputStatus status)[] syncedInputs = InputsAtFrame(frameToGrab);

                requests.Add(RollbackRequest<TState, TInput>.From(new RollbackRequest<TState, TInput>.AdvanceFrame
                {
                    Inputs = syncedInputs,
                }));

                _currentFrame += 1;
            }
            return requests;
        }

        public void PollRemoteClients()
        {
            foreach ((TAddress from, Message msg) in _socket.ReceiveAllMessages())
            {
                if (_host.IsHandlingMessage(from)) { _host.HandleMessage(msg); }
            }

            Deque<Event<TInput>> events = new Deque<Event<TInput>>();
            TAddress addr = _host.PeerAddr;
            foreach (Event<TInput> ev in _host.Poll(_hostConnectStatus)) { events.PushBack(ev); }
            while (events.Count > 0) { HandleEvent(events.PopFront(), addr); }

            _host.SendAllMessages(_socket);
        }

        public Frame CurrentFrame => _currentFrame;
        public int NumPlayers => _numPlayers;

        public (TInput input, InputStatus status)[] InputsAtFrame(Frame frameToGrab)
        {
            Assert.IsTrue(frameToGrab != Frame.NullFrame);
            PlayerInput<TInput>[] inputs = _inputs[frameToGrab.No % SpectatorConstants.SPECTATOR_BUFFER_SIZE];

            if (inputs[0].Frame < frameToGrab)
            {
                throw new InvalidOperationException("have not received input from host yet");
            }
            if (inputs[0].Frame > frameToGrab)
            {
                throw new InvalidOperationException("spectator way too far behind");
            }
            (TInput input, InputStatus status)[] res = new (TInput input, InputStatus status)[_numPlayers];
            for (int i = 0; i < _numPlayers; i++)
            {
                if (_hostConnectStatus[i].Disconnected && _hostConnectStatus[i].LastFrame < frameToGrab) { res[i] = (inputs[i].Input, InputStatus.Disconnected); }
                else { res[i] = (inputs[i].Input, InputStatus.Confirmed); }
            }
            return res;
        }

        public void HandleEvent(Event<TInput> ev, in TAddress addr)
        {
            switch (ev.Kind)
            {
                case EventKind.Synchronizing:
                    Event<TInput>.Synchronizing synchronizing = ev.GetSynchronizing();
                    _eventQueue.PushBack(RollbackEvent<TInput, TAddress>.From(new RollbackEvent<TInput, TAddress>.Synchronizing
                    {
                        Addr = addr,
                        Total = synchronizing.Total,
                        Count = synchronizing.Count,
                    }));
                    break;
                case EventKind.Synchronized:
                    _state = SessionState.Running;
                    _eventQueue.PushBack(RollbackEvent<TInput, TAddress>.From(new RollbackEvent<TInput, TAddress>.Synchronized
                    {
                        Addr = addr,
                    }));
                    break;
                case EventKind.NetworkInterrupted:
                    Event<TInput>.NetworkInterrupted networkInterrupted = ev.GetNetworkInterrupted();
                    _eventQueue.PushBack(RollbackEvent<TInput, TAddress>.From(new RollbackEvent<TInput, TAddress>.NetworkInterrupted
                    {
                        Addr = addr,
                        DisconnectTimeout = networkInterrupted.DisconnectTimeout
                    }));
                    break;
                case EventKind.NetworkResumed:
                    _eventQueue.PushBack(RollbackEvent<TInput, TAddress>.From(new RollbackEvent<TInput, TAddress>.NetworkResumed
                    {
                        Addr = addr,
                    }));
                    break;
                case EventKind.Disconnected:
                    _eventQueue.PushBack(RollbackEvent<TInput, TAddress>.From(new RollbackEvent<TInput, TAddress>.Disconnected
                    {
                        Addr = addr,
                    }));
                    break;
                case EventKind.Input:
                    Event<TInput>.Input input = ev.GetInput();
                    Assert.IsTrue(input.Data.Frame != Frame.NullFrame);
                    _inputs[input.Data.Frame.No % SpectatorConstants.SPECTATOR_BUFFER_SIZE][input.Player.Id] = input.Data;
                    Assert.IsTrue(input.Data.Frame >= _lastRecvFrame);
                    _lastRecvFrame = input.Data.Frame;

                    _host.UpdateLocalFrameAdvantage(input.Data.Frame);

                    for (int i = 0; i < _numPlayers; i++) { _hostConnectStatus[i] = _host.PeerConnectStatus(new PlayerHandle(i)); }
                    break;
            }

            while (_eventQueue.Count > SessionConstants.MAX_EVENT_QUEUE_SIZE) { _eventQueue.PopFront(); }
        }
    }
}