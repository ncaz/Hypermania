using System;
using Design;
using Game.Sim;
using UnityEngine;

namespace Game.View
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Conductor))]
    public class GameView : MonoBehaviour
    {
        private Conductor _conductor;
        public FighterView[] Fighters => _fighters;

        private FighterView[] _fighters;
        private ManiaView[] _manias;
        private CharacterConfig[] _characters;

        public void Init(CharacterConfig[] characters)
        {
            _conductor = GetComponent<Conductor>();
            if (_conductor == null)
            {
                throw new InvalidOperationException(
                    "Conductor was null. Did you forget to assign a conductor component to the GameView?"
                );
            }
            _fighters = new FighterView[characters.Length];
            _manias = new ManiaView[characters.Length];
            _characters = characters;
            for (int i = 0; i < characters.Length; i++)
            {
                _fighters[i] = Instantiate(_characters[i].Prefab);
                _fighters[i].transform.SetParent(transform, true);
                _fighters[i].Init(characters[i]);

                float xPos = i - ((float)characters.Length - 1) / 2;
                GameObject maniaView = new GameObject("Mania View");
                _manias[i] = maniaView.AddComponent<ManiaView>();
                _manias[i].transform.SetParent(transform, true);
                _manias[i]
                    .Init(
                        new ManiaViewConfig
                        {
                            Center = new Vector2(8f * xPos, 0f),
                            Width = 2f,
                            Height = 6f,
                            Gap = 0.01f,
                            Border = 0.01f,
                            HitLine = 1f,
                            ScrollSpeed = 0.05f,
                            NoteHeight = 0.2f,
                        }
                    );
            }
            _conductor.Init();
        }

        public void Render(in GameState state)
        {
            for (int i = 0; i < _characters.Length; i++)
            {
                _fighters[i].Render(state.Frame, state.Fighters[i]);
                _manias[i].Render(state.Frame, state.Manias[i]);
            }
            _conductor.RequestSlice(state.Frame);
        }

        public void DeInit()
        {
            for (int i = 0; i < _characters.Length; i++)
            {
                _fighters[i].DeInit();
                Destroy(_fighters[i].gameObject);
                _manias[i].DeInit();
                Destroy(_manias[i].gameObject);
            }
            _fighters = null;
            _characters = null;
        }
    }
}
