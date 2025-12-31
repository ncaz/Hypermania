using System;
using System.Collections.Generic;
using Netcode.Rollback.Network;
using UnityEngine.Assertions;

namespace Netcode.Rollback
{
    public struct GameStateCell<TState> where TState : struct
    {
        // TODO: lock?
        public Arc<GameState<TState>> State;
        public void Save(Frame frame, in TState? data, ulong? checksum)
        {
            State.Value.Frame = frame;
            State.Value.Data = data;
            State.Value.Checksum = checksum;
        }
    }

    public struct SavedStates<TState> where TState : struct
    {
        public List<GameStateCell<TState>> States;
        public SavedStates(uint maxPrediction)
        {
            int numCells = (int)maxPrediction + 1;
            List<GameStateCell<TState>> states = new List<GameStateCell<TState>>();
            for (int i = 0; i < numCells; i++)
            {
                states.Add(new GameStateCell<TState>
                {
                    State = new Arc<GameState<TState>>(new GameState<TState>
                    {
                        Frame = Frame.NullFrame,
                        Data = null,
                        Checksum = null
                    })
                });
            }
            States = states;
        }

        public GameStateCell<TState> GetCell(Frame frame)
        {
            Assert.IsTrue(frame != Frame.NullFrame);
            int pos = frame.No % States.Count;
            return States[pos];
        }
    }

    public class SyncLayer<TState, TInput>
        where TState : struct
        where TInput : struct, IInput<TInput>
    {
        private int _numPlayers;
        private uint _maxPrediction;
        private SavedStates<TState> _savedStates;
        private Frame _lastConfirmedFrame;
        private Frame _lastSavedFrame;
        private Frame _currentFrame;
        private InputQueue<TInput>[] _inputQueues;

        public SyncLayer(int numPlayers, uint maxPrediction)
        {
            _numPlayers = numPlayers;
            _maxPrediction = maxPrediction;
            _lastConfirmedFrame = Frame.NullFrame;
            _lastSavedFrame = Frame.NullFrame;
            _currentFrame = Frame.FirstFrame;
            _savedStates = new SavedStates<TState>(maxPrediction);
            _inputQueues = new InputQueue<TInput>[numPlayers];
            for (int i = 0; i < numPlayers; i++)
            {
                _inputQueues[i] = new InputQueue<TInput>();
            }
        }

        public Frame CurrentFrame => _currentFrame;
        public void AdvanceFrame() => _currentFrame += 1;

        public RollbackRequest<TState, TInput> SaveCurrentState()
        {
            _lastSavedFrame = _currentFrame;
            return RollbackRequest<TState, TInput>.From(new RollbackRequest<TState, TInput>.SaveGameState
            {
                Cell = _savedStates.GetCell(_currentFrame),
                Frame = CurrentFrame,
            });
        }

        public void SetFrameDelay(PlayerHandle playerHandle, uint delay)
        {
            Assert.IsTrue(playerHandle.Id < _numPlayers);
            _inputQueues[playerHandle.Id].SetFrameDelay(delay);
        }

        public void ResetPrediction()
        {
            for (int i = 0; i < _numPlayers; i++) { _inputQueues[i].ResetPrediction(); }
        }

        public RollbackRequest<TState, TInput> LoadFrame(Frame frameToLoad)
        {
            Assert.IsTrue(frameToLoad != Frame.NullFrame, "cannot load null frame");
            Assert.IsTrue(frameToLoad < _currentFrame, $"must load frame in past (frame to load is {frameToLoad}, current frame is {_currentFrame})");
            Assert.IsTrue(frameToLoad >= _currentFrame - (int)_maxPrediction, "cannot load frame outside of prediction window");

            GameStateCell<TState> cell = _savedStates.GetCell(frameToLoad);
            Assert.IsTrue(cell.State.Value.Frame == frameToLoad);
            _currentFrame = frameToLoad;

            return RollbackRequest<TState, TInput>.From(new RollbackRequest<TState, TInput>.LoadGameState
            {
                Cell = cell,
                Frame = frameToLoad,
            });
        }

        public Frame AddLocalInput(PlayerHandle playerHandle, in PlayerInput<TInput> input)
        {
            Assert.IsTrue(input.Frame == _currentFrame);
            return _inputQueues[playerHandle.Id].AddInput(input);
        }

        public void AddRemoteInput(PlayerHandle playerHandle, in PlayerInput<TInput> input)
        {
            _inputQueues[playerHandle.Id].AddInput(input);
        }

        public (TInput input, InputStatus status)[] SynchronizedInputs(ConnectionStatus[] connectStatus)
        {
            (TInput input, InputStatus status)[] inputs = new (TInput input, InputStatus status)[connectStatus.Length];
            for (int i = 0; i < connectStatus.Length; i++)
            {
                if (connectStatus[i].Disconnected && connectStatus[i].LastFrame < _currentFrame) { inputs[i] = (default, InputStatus.Disconnected); }
                else { inputs[i] = _inputQueues[i].Input(_currentFrame); }
            }
            return inputs;
        }

        public PlayerInput<TInput>[] ConfirmedInputs(Frame frame, ConnectionStatus[] connectStatus)
        {
            PlayerInput<TInput>[] inputs = new PlayerInput<TInput>[connectStatus.Length];
            for (int i = 0; i < connectStatus.Length; i++)
            {
                if (connectStatus[i].Disconnected && connectStatus[i].LastFrame < frame)
                {
                    inputs[i] = PlayerInput<TInput>.BlankInput(Frame.NullFrame);
                }
                else { inputs[i] = _inputQueues[i].ConfirmedInput(frame); }
            }
            return inputs;
        }

        public void SetLastConfirmedFrame(Frame frame, bool SparseSaving)
        {
            Frame firstIncorrect = Frame.NullFrame;
            for (int i = 0; i < _numPlayers; i++)
            {
                firstIncorrect = Frame.Max(firstIncorrect, _inputQueues[i].FirstIncorrectFrame);
            }

            if (SparseSaving) { frame = Frame.Min(frame, _lastSavedFrame); }
            frame = Frame.Min(frame, _currentFrame);

            Assert.IsTrue(firstIncorrect == Frame.NullFrame || firstIncorrect >= frame);

            _lastConfirmedFrame = frame;
            if (_lastConfirmedFrame > Frame.FirstFrame)
            {
                for (int i = 0; i < _numPlayers; i++)
                {
                    _inputQueues[i].DiscardConfirmedFrames(frame - 1);
                }
            }
        }

        public Frame CheckSimulationConsistency(Frame firstIncorrect)
        {
            for (int i = 0; i < _numPlayers; i++)
            {
                Frame incorrect = _inputQueues[i].FirstIncorrectFrame;
                if (incorrect != Frame.NullFrame && (firstIncorrect == Frame.NullFrame || incorrect < firstIncorrect))
                {
                    firstIncorrect = incorrect;
                }
            }
            return firstIncorrect;
        }

        public GameStateCell<TState> SavedStateByFrame(Frame frame)
        {
            GameStateCell<TState> cell = _savedStates.GetCell(frame);
            if (cell.State.Value.Frame == frame) { return cell; }
            throw new InvalidOperationException($"state missing for frame {frame}");
        }

        public Frame LastSavedFrame => _lastSavedFrame;
        public Frame LastConfirmedFrame => _lastConfirmedFrame;
    }
}