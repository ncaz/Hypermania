using System;
using UnityEngine.Assertions;

namespace Netcode.Rollback
{
    public class InputQueue<TInput> where TInput : struct, IInput<TInput>
    {
        const int INPUT_QUEUE_LENGTH = 128;
        private int _head;
        private int _tail;
        private int _length;
        private bool _firstFrame;
        private Frame _lastAddedFrame;
        private Frame _firstIncorrectFrame;
        private Frame _lastRequestedFrame;

        private uint _frameDelay;
        private PlayerInput<TInput>[] _inputs;
        private PlayerInput<TInput> _prediction;

        public InputQueue()
        {
            _head = 0;
            _tail = 0;
            _length = 0;
            _frameDelay = 0;
            _firstFrame = true;
            _lastAddedFrame = Frame.NullFrame;
            _firstIncorrectFrame = Frame.NullFrame;
            _lastRequestedFrame = Frame.NullFrame;
            _prediction = PlayerInput<TInput>.BlankInput(Frame.NullFrame);
            _inputs = new PlayerInput<TInput>[INPUT_QUEUE_LENGTH];
            for (int i = 0; i < INPUT_QUEUE_LENGTH; i++)
                _inputs[i] = PlayerInput<TInput>.BlankInput(Frame.NullFrame);
        }

        public Frame FirstIncorrectFrame => _firstIncorrectFrame;

        public void SetFrameDelay(uint delay) { _frameDelay = delay; }

        public void ResetPrediction()
        {
            _prediction.Frame = Frame.NullFrame;
            _firstIncorrectFrame = Frame.NullFrame;
            _lastRequestedFrame = Frame.NullFrame;
        }

        public PlayerInput<TInput> ConfirmedInput(Frame requestedFrame)
        {
            int offset = requestedFrame.No % INPUT_QUEUE_LENGTH;
            if (_inputs[offset].Frame == requestedFrame) { return _inputs[offset]; }
            throw new InvalidOperationException("should not have asked for known incorrect frame");
        }

        public void DiscardConfirmedFrames(Frame frame)
        {
            if (_lastAddedFrame == Frame.NullFrame) return; // nothing real to discard
            if (_lastRequestedFrame != Frame.NullFrame) { frame = Frame.Min(frame, _lastRequestedFrame); }
            // move tail to delete inputs
            if (frame >= _lastAddedFrame)
            {
                // delete all but most recent
                _tail = _head;
                _length = 1;
            }
            else if (frame <= _inputs[_tail].Frame) { }
            else
            {
                int offset = frame - _inputs[_tail].Frame;
                _tail = (_tail + offset) % INPUT_QUEUE_LENGTH;
                _length -= offset;
            }
        }

        public (TInput input, InputStatus status) Input(Frame requestedFrame)
        {
            Assert.IsTrue(_firstIncorrectFrame == Frame.NullFrame);
            _lastRequestedFrame = requestedFrame;
            Assert.IsTrue(requestedFrame >= _inputs[_tail].Frame);

            if (_prediction.Frame == Frame.NullFrame)
            {
                int offset = requestedFrame - _inputs[_tail].Frame;
                if (offset < _length)
                {
                    offset = (offset + _tail) % INPUT_QUEUE_LENGTH;
                    Assert.IsTrue(_inputs[offset].Frame == requestedFrame);
                    return (_inputs[offset].Input, InputStatus.Confirmed);
                }

                if (requestedFrame == Frame.FirstFrame || _lastAddedFrame == Frame.NullFrame) { _prediction = PlayerInput<TInput>.BlankInput(_prediction.Frame); }
                else
                {
                    int previousPosition = _head == 0 ? INPUT_QUEUE_LENGTH - 1 : _head - 1;
                    _prediction = _inputs[previousPosition];
                }
                _prediction.Frame += 1;
            }

            Assert.IsTrue(_prediction.Frame != Frame.NullFrame);
            return (_prediction.Input, InputStatus.Predicted);
        }

        public Frame AddInput(PlayerInput<TInput> input)
        {
            if (_lastAddedFrame != Frame.NullFrame && input.Frame + (int)_frameDelay != _lastAddedFrame + 1) { return Frame.NullFrame; }

            Frame newFrame = AdvanceQueueHead(input.Frame);
            if (newFrame != Frame.NullFrame) { AddInputByFrame(input, newFrame); }
            return newFrame;
        }

        public void AddInputByFrame(PlayerInput<TInput> input, Frame frame)
        {
            int previousPosition = _head == 0 ? INPUT_QUEUE_LENGTH - 1 : _head - 1;
            Assert.IsTrue(_lastAddedFrame == Frame.NullFrame || frame == _lastAddedFrame + 1);
            Assert.IsTrue(frame == Frame.FirstFrame || _inputs[previousPosition].Frame == frame - 1);

            _inputs[_head] = input;
            _inputs[_head].Frame = frame;
            _head = (_head + 1) % INPUT_QUEUE_LENGTH;
            _length += 1;
            Assert.IsTrue(_length <= INPUT_QUEUE_LENGTH);
            _firstFrame = false;
            _lastAddedFrame = frame;

            if (_prediction.Frame != Frame.NullFrame)
            {
                Assert.IsTrue(frame == _prediction.Frame);
                if (_firstIncorrectFrame == Frame.NullFrame && !_prediction.Equal(input, true)) { _firstIncorrectFrame = frame; }

                if (_prediction.Frame == _lastRequestedFrame && _firstIncorrectFrame == Frame.NullFrame) { _prediction.Frame = Frame.NullFrame; }
                else { _prediction.Frame += 1; }
            }
        }

        private Frame AdvanceQueueHead(Frame inputFrame)
        {
            int previousPosition = _head == 0 ? INPUT_QUEUE_LENGTH - 1 : _head - 1;
            Frame expectedFrame = _firstFrame ? Frame.FirstFrame : _inputs[previousPosition].Frame + 1;
            inputFrame += (int)_frameDelay;
            if (expectedFrame > inputFrame) { return Frame.NullFrame; }

            while (expectedFrame < inputFrame)
            {
                PlayerInput<TInput> inputToRepl = _inputs[previousPosition];
                AddInputByFrame(inputToRepl, expectedFrame);
                expectedFrame += 1;
            }

            previousPosition = _head == 0 ? INPUT_QUEUE_LENGTH - 1 : _head - 1;
            Assert.IsTrue(inputFrame == Frame.FirstFrame || inputFrame == _inputs[previousPosition].Frame + 1);
            return inputFrame;
        }
    }
}