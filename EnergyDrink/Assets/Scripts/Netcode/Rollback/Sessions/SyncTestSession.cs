using System;
using System.Collections.Generic;
using Netcode.Rollback.Network;
using UnityEngine.Assertions;

namespace Netcode.Rollback.Sessions
{
    public class SyncTestSession<TState, TInput, TAddress>
        where TInput : struct, IInput<TInput>
        where TState : struct
    {
        private int _numPlayers;
        private uint _maxPrediction;
        private uint _checkDistance;
        private SyncLayer<TState, TInput> _syncLayer;
        private ConnectionStatus[] _dummyConnectStatus;
        private Dictionary<Frame, ulong?> _checksumHistory;
        private Dictionary<PlayerHandle, PlayerInput<TInput>> _localInputs;

        public SyncTestSession(int numPlayers, uint maxPrediction, uint checkDistance, uint inputDelay)
        {
            _dummyConnectStatus = new ConnectionStatus[numPlayers];
            for (int i = 0; i < numPlayers; i++) { _dummyConnectStatus[i] = ConnectionStatus.Default; }

            _syncLayer = new SyncLayer<TState, TInput>(numPlayers, maxPrediction);
            for (int i = 0; i < numPlayers; i++) { _syncLayer.SetFrameDelay(new PlayerHandle(i), inputDelay); }

            _numPlayers = numPlayers;
            _maxPrediction = maxPrediction;
            _checkDistance = checkDistance;
            _checksumHistory = new Dictionary<Frame, ulong?>();
            _localInputs = new Dictionary<PlayerHandle, PlayerInput<TInput>>();
        }

        public void AddLocalInput(PlayerHandle handle, in TInput input)
        {
            if (handle.Id >= _numPlayers) { throw new InvalidOperationException("player handle is invalid"); }

            PlayerInput<TInput> playerInput = new PlayerInput<TInput> { Frame = _syncLayer.CurrentFrame, Input = input };
            _localInputs.Add(handle, playerInput);
        }

        public List<RollbackRequest<TState, TInput>> AdvanceFrame()
        {
            List<RollbackRequest<TState, TInput>> requests = new List<RollbackRequest<TState, TInput>>();

            Frame currentFrame = _syncLayer.CurrentFrame;
            if (_checkDistance > 0 && currentFrame > new Frame { No = (int)_checkDistance })
            {
                Frame oldestFrameToCheck = currentFrame - (int)_checkDistance;
                List<Frame> mismatchedFrames = new List<Frame>();
                for (Frame i = oldestFrameToCheck; i <= currentFrame; i += 1)
                {
                    if (ChecksumsConsistent(i)) { continue; }
                    mismatchedFrames.Add(i);
                }

                if (mismatchedFrames.Count > 0) { throw new InvalidOperationException("Invalid checksum"); }

                Frame frameTo = _syncLayer.CurrentFrame - (int)_checkDistance;
                AdjustGameState(frameTo, requests);
            }

            if (_numPlayers != _localInputs.Count) { throw new InvalidOperationException("missing local inputs"); }
            foreach ((PlayerHandle handle, PlayerInput<TInput> input) in _localInputs) { _syncLayer.AddLocalInput(handle, input); }
            _localInputs.Clear();

            if (_checkDistance > 0) { requests.Add(_syncLayer.SaveCurrentState()); }

            (TInput input, InputStatus status)[] inputs = _syncLayer.SynchronizedInputs(_dummyConnectStatus);
            requests.Add(RollbackRequest<TState, TInput>.From(new RollbackRequest<TState, TInput>.AdvanceFrame
            {
                Inputs = inputs
            }));
            _syncLayer.AdvanceFrame();

            Frame safeFrame = _syncLayer.CurrentFrame - (int)_checkDistance;
            _syncLayer.SetLastConfirmedFrame(safeFrame, false);

            for (int i = 0; i < _numPlayers; i++) { _dummyConnectStatus[i].LastFrame = _syncLayer.CurrentFrame; }
            return requests;
        }

        public Frame CurrentFrame => _syncLayer.CurrentFrame;
        public int NumPlayers => _numPlayers;
        public uint MaxPrediction => _maxPrediction;
        public uint CheckDistance => _checkDistance;

        private bool ChecksumsConsistent(Frame frameToCheck)
        {
            Frame oldestAllowedFrame = _syncLayer.CurrentFrame - (int)_checkDistance;
            List<Frame> framesToRemove = new List<Frame>();
            foreach (Frame frame in _checksumHistory.Keys)
            {
                if (frame < oldestAllowedFrame) { framesToRemove.Add(frame); }
            }
            foreach (Frame frame in framesToRemove) { _checksumHistory.Remove(frame); }

            try
            {
                GameStateCell<TState> cell = _syncLayer.SavedStateByFrame(frameToCheck);
                if (_checksumHistory.TryGetValue(cell.State.Value.Frame, out var cs)) { return cs == cell.State.Value.Checksum; }
                else
                {
                    _checksumHistory.Add(cell.State.Value.Frame, cell.State.Value.Checksum);
                    return true;
                }
            }
            catch (InvalidOperationException) { return true; }
        }

        private void AdjustGameState(Frame frameTo, List<RollbackRequest<TState, TInput>> requests)
        {
            Frame startFrame = _syncLayer.CurrentFrame;
            int count = startFrame - frameTo;

            requests.Add(_syncLayer.LoadFrame(frameTo));
            _syncLayer.ResetPrediction();
            Assert.IsTrue(_syncLayer.CurrentFrame == frameTo);

            for (int i = 0; i < count; i++)
            {
                (TInput input, InputStatus status)[] inputs = _syncLayer.SynchronizedInputs(_dummyConnectStatus);
                if (i > 0) { requests.Add(_syncLayer.SaveCurrentState()); }
                _syncLayer.AdvanceFrame();
                requests.Add(RollbackRequest<TState, TInput>.From(new RollbackRequest<TState, TInput>.AdvanceFrame
                {
                    Inputs = inputs
                }));
            }
            Assert.IsTrue(_syncLayer.CurrentFrame == startFrame);
        }
    }
}