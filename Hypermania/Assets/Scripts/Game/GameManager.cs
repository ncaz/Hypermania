using System;
using System.Collections;
using System.Collections.Generic;
using Game.View;
using Netcode.P2P;
using Netcode.Rollback;
using Steamworks;
using UnityEngine;

namespace Game
{
    public class GameManager : MonoBehaviour
    {
        public GameRunner Runner;
        private SteamMatchmakingClient _matchmakingClient;
        private P2PClient _p2pClient;
        private List<(PlayerHandle handle, PlayerKind playerKind, SteamNetworkingIdentity netId)> _players;

        public const int TPS = 64;

        void OnEnable()
        {
            _matchmakingClient = new SteamMatchmakingClient();
            _matchmakingClient.OnStartWithPlayers += OnStartWithPlayers;

            _p2pClient = null;
            _players = new List<(PlayerHandle handle, PlayerKind playerKind, SteamNetworkingIdentity netId)>();

            if (Runner == null) { Debug.LogError($"{nameof(GameManager)}: {Runner} reference is not assigned.", this); }
        }

        void OnDisable()
        {
            _matchmakingClient = null;
            _p2pClient = null;
            _players = null;
        }

        #region Controls

        public void CreateLobby() => StartCoroutine(CreateLobbyRoutine());
        IEnumerator CreateLobbyRoutine()
        {
            var task = _matchmakingClient.Create();
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }
        }

        public void JoinLobby(CSteamID lobbyId) => StartCoroutine(JoinLobbyRoutine(lobbyId));
        IEnumerator JoinLobbyRoutine(CSteamID lobbyId)
        {
            var task = _matchmakingClient.Join(lobbyId);
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }
        }

        public void LeaveLobby() => StartCoroutine(LeaveLobbyRoutine());
        IEnumerator LeaveLobbyRoutine()
        {
            var task = _matchmakingClient.Leave();
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }
        }

        public void StartGame() => StartCoroutine(StartGameRoutine());
        IEnumerator StartGameRoutine()
        {
            var task = _matchmakingClient.StartGame();
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }
        }

        public void StartLocalGame()
        {
            if (_matchmakingClient.CurrentLobby.IsValid())
            {
                throw new InvalidOperationException("cannot start local game while in valid lobby, leave the lobby first!");
            }
            _players.Clear();
            _players.Add((new PlayerHandle(0), PlayerKind.Local, default));
            OnAllPeersConnected();
        }

        public void StopGame()
        {
            Runner.Stop();
        }

        #endregion

        void OnStartWithPlayers(List<CSteamID> players)
        {
            // start connecting to all peers
            List<SteamNetworkingIdentity> peerAddr = new List<SteamNetworkingIdentity>();
            foreach (CSteamID id in players)
            {
                bool isLocal = id == SteamUser.GetSteamID();
                SteamNetworkingIdentity netId = new SteamNetworkingIdentity();
                netId.SetSteamID(id);
                if (!isLocal) { peerAddr.Add(netId); }
            }

            _p2pClient = new P2PClient(peerAddr);
            _p2pClient.OnAllPeersConnected += OnAllPeersConnected;
            _p2pClient.OnPeerDisconnected += OnPeerDisconnected;

            _players.Clear();
            for (int i = 0; i < players.Count; i++)
            {
                bool isLocal = players[i] == SteamUser.GetSteamID();
                SteamNetworkingIdentity netId = new SteamNetworkingIdentity();
                netId.SetSteamID(players[i]);
                _players.Add((new PlayerHandle(i), isLocal ? PlayerKind.Local : PlayerKind.Remote, netId));
            }

            _p2pClient.ConnectToPeers();
        }

        void OnAllPeersConnected()
        {
            if (_players == null)
            {
                throw new InvalidOperationException("players should be initialized if peers are connected");
            }
            Runner.Init(_players, _p2pClient);
        }

        void OnPeerDisconnected(SteamNetworkingIdentity id)
        {
            Runner.Stop();
        }

        void Update()
        {
            Runner.Poll(Time.deltaTime);
        }
    }
}