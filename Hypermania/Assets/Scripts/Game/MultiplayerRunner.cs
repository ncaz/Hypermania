
using System;
using System.Collections.Generic;
using Game.Sim;
using Netcode.P2P;
using Netcode.Rollback;
using Netcode.Rollback.Sessions;
using Steamworks;
using UnityEngine;

namespace Game
{
    public class MultiplayerRunner : GameRunner
    {
        private GameState _curState;
        private P2PSession<GameState, GameInput, SteamNetworkingIdentity> _session;

        private bool _initialized;
        private uint _waitRemaining;
        private PlayerHandle _myHandle;
        private float _time;

        void OnEnable()
        {
            _initialized = false;
            _waitRemaining = 0;
            _session = null;
            _curState = null;
            _myHandle = new PlayerHandle(-1);
            _time = 0;
        }

        void OnDisable()
        {
            _initialized = false;
            _waitRemaining = 0;
            _session = null;
            _curState = null;
            _myHandle = new PlayerHandle(-1);
            _time = 0;
        }

        public override void Init(List<(PlayerHandle playerHandle, PlayerKind playerKind, SteamNetworkingIdentity address)> players, P2PClient client)
        {
            _curState = GameState.New();
            SessionBuilder<GameInput, SteamNetworkingIdentity> builder = new SessionBuilder<GameInput, SteamNetworkingIdentity>().WithNumPlayers(players.Count).WithFps(64);
            foreach ((PlayerHandle playerHandle, PlayerKind playerKind, SteamNetworkingIdentity address) in players)
            {
                if (playerKind == PlayerKind.Local) { _myHandle = playerHandle; }
                builder.AddPlayer(new PlayerType<SteamNetworkingIdentity> { Kind = playerKind, Address = address }, playerHandle);
            }
            _session = builder.StartP2PSession<GameState>(client);
            _initialized = true;

            if (_myHandle.Id == -1)
            {
                throw new InvalidOperationException("No local players in multiplayer runner");
            }
        }

        public override void Stop() { OnDisable(); }

        public override void Poll(float deltaTime)
        {
            if (!_initialized) { return; }

            _session.PollRemoteClients();

            foreach (RollbackEvent<GameInput, SteamNetworkingIdentity> ev in _session.DrainEvents())
            {
                Debug.Log($"[Game] Received {ev.Kind} event");
                switch (ev.Kind)
                {
                    case RollbackEventKind.WaitRecommendation:
                        var waitRec = ev.GetWaitRecommendation();
                        _waitRemaining = waitRec.SkipFrames;
                        break;
                }
            }

            // accumulate time and update frame
            float fpsDelta = 1.0f / GameManager.TPS;
            if (_session.FramesAhead > 0)
            {
                fpsDelta *= 1.1f;
            }

            _time += deltaTime;
            while (_time > fpsDelta)
            {
                _time -= fpsDelta;
                GameLoop();
            }
        }

        void GameLoop()
        {
            if (_session.CurrentState != SessionState.Running) { return; }

            if (_waitRemaining > 0)
            {
                Debug.Log("[Game] Skipping frame due to wait recommendation");
                _waitRemaining--;
                return;
            }

            InputFlags f1Input = InputFlags.None;
            if (Input.GetKey(KeyCode.A))
                f1Input |= InputFlags.Left;
            if (Input.GetKey(KeyCode.D))
                f1Input |= InputFlags.Right;
            if (Input.GetKey(KeyCode.W))
                f1Input |= InputFlags.Up;


            _session.AddLocalInput(_myHandle, new GameInput(f1Input));
            try
            {
                List<RollbackRequest<GameState, GameInput>> requests = _session.AdvanceFrame();
                foreach (RollbackRequest<GameState, GameInput> request in requests)
                {
                    switch (request.Kind)
                    {
                        case RollbackRequestKind.SaveGameStateReq:
                            var saveReq = request.GetSaveGameStateReq();
                            saveReq.Cell.Save(saveReq.Frame, _curState, _curState.Checksum());
                            break;
                        case RollbackRequestKind.LoadGameStateReq:
                            var loadReq = request.GetLoadGameStateReq();
                            loadReq.Cell.Load(out _curState);
                            break;
                        case RollbackRequestKind.AdvanceFrameReq:
                            _curState.Advance(request.GetAdvanceFrameRequest().Inputs);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log($"[Game] Exception {e}");
            }

            _view.Render(_curState);
        }
    }
}