using TMPro;
using UnityEngine;

namespace Game.View
{
    public class InfoOverlayView : MonoBehaviour
    {
        public struct PerSecondCounter
        {
            private float LastCallTs;
            private float Accum;
            private int Ticks;
            public int Tps { get; private set; }

            public void Call()
            {
                float now = Time.realtimeSinceStartup;

                Ticks++;
                Accum += now - LastCallTs;
                if (Accum >= 1f)
                {
                    Tps = Mathf.RoundToInt(Ticks / Accum);
                    Accum = 0f;
                    Ticks = 0;
                }

                LastCallTs = now;
            }
        }

        private PerSecondCounter _fps;
        private PerSecondCounter _tps;

        public void Render(InfoOverlayDetails details)
        {
            _tps.Call();
            string detailsString = "FPS: " + _fps.Tps + "  TPS: " + _tps.Tps;
            if (details.HasPing)
            {
                detailsString += "  Ping: " + details.Ping + "ms";
            }
            GetComponent<TMP_Text>().SetText(detailsString);
        }

        public void Update()
        {
            _fps.Call();
        }
    }

    public struct InfoOverlayDetails
    {
        public bool HasPing;
        public ulong Ping;
    }
}
