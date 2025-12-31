using UnityEngine.Assertions;

namespace Netcode.Rollback
{
    public struct TimeSync
    {
        const int FRAME_WINDOW_SIZE = 30;
        private unsafe fixed int _local[FRAME_WINDOW_SIZE];
        private unsafe fixed int _remote[FRAME_WINDOW_SIZE];

        public unsafe void AdvanceFrame(Frame frame, int localAdv, int remoteAdv)
        {
            Assert.IsTrue(frame != Frame.NullFrame);
            _local[frame.No % FRAME_WINDOW_SIZE] = localAdv;
            _remote[frame.No % FRAME_WINDOW_SIZE] = remoteAdv;
        }

        public unsafe int AverageFrameAdvantage()
        {
            int localSum = 0;
            int remoteSum = 0;
            for (int i = 0; i < FRAME_WINDOW_SIZE; i++)
            {
                localSum += _local[i];
                remoteSum += _remote[i];
            }
            float localAvg = (float)localSum / FRAME_WINDOW_SIZE;
            float remoteAvg = (float)remoteSum / FRAME_WINDOW_SIZE;
            return (int)((remoteAvg - localAvg) / 2.0f);
        }
    }
}