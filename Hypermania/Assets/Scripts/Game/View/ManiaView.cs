using System.Collections.Generic;
using Game.Sim;
using UnityEngine;
using Utils;

namespace Game.View
{
    public struct ManiaViewConfig
    {
        public Vector2 Center;
        public float Width;
        public float Height;
        public float Border;
        public float Gap;
        public float HitLine;
        public float ScrollSpeed;
        public float NoteHeight;
    }

    public class ManiaView : MonoBehaviour
    {
        private ManiaViewConfig _config;
        private static readonly float[] _channelGapsToCenter = { -2.5f, -1.5f, -0.5f, 0.5f, 1.5f, 2.5f };
        private List<GameObject> _activeNotes;

        public void Init(in ManiaViewConfig config)
        {
            _config = config;
            _activeNotes = new List<GameObject>();
        }

        public void DeInit()
        {
            for (int i = 0; i < _activeNotes.Count; i++)
            {
                Destroy(_activeNotes[i]);
            }
            _activeNotes = null;
        }

        public void Render(Frame frame, in ManiaState state)
        {
            int viewId = 0;
            for (int i = 0; i < state.Config.NumKeys; i++)
            {
                for (int j = 0; j < state.Channels[i].Notes.Count; j++)
                {
                    if (!RenderNote(frame, state.Config.NumKeys, i, viewId, state.Channels[i].Notes[j]))
                    {
                        break;
                    }
                    viewId++;
                }
            }

            // if we rendered fewer notes this frame compared to last, set the entity to not active
            for (int i = viewId; i < _activeNotes.Count; i++)
            {
                _activeNotes[i].SetActive(false);
                _activeNotes[i].transform.position = new Vector2(9999, 9999);
            }
        }

        private bool RenderNote(Frame frame, int numChannels, int channel, int viewId, in ManiaNote note)
        {
            // should only add a single new note to the view
            while (_activeNotes.Count <= viewId)
            {
                GameObject noteView = GameObject.CreatePrimitive(PrimitiveType.Cube);
                noteView.transform.SetParent(transform);
                _activeNotes.Add(noteView);
            }
            GameObject view = _activeNotes[viewId];
            view.SetActive(true);

            float x = ChannelX(numChannels, channel);
            float width = ChannelWidth(numChannels);
            float y =
                (note.Tick - frame) * _config.ScrollSpeed + _config.HitLine + _config.Center.y - _config.Height / 2;
            if (y < _config.Center.y - _config.Height / 2)
            {
                return true;
            }
            if (y > _config.Center.y + _config.Height / 2)
            {
                return false;
            }
            view.transform.position = new Vector2(x, y);
            view.transform.localScale = new Vector2(width, _config.NoteHeight);
            return true;
        }

        private float ChannelWidth(int numChannels) =>
            (_config.Width - _config.Gap * (numChannels + 1) - 2 * _config.Border) / numChannels;

        private float ChannelX(int numChannels, int channel) =>
            _config.Center.x + _channelGapsToCenter[channel] * (ChannelWidth(numChannels) + _config.Gap);
    }
}
