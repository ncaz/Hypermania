using System;
using System.Collections.Generic;
using Game.View;
using Netcode.P2P;
using Netcode.Rollback;
using Steamworks;
using UnityEngine;

namespace Game
{
    public abstract class GameRunner: MonoBehaviour
    {
        [SerializeField] protected GameView _view;
        public abstract void Init(List<(PlayerHandle playerHandle, PlayerKind playerKind, SteamNetworkingIdentity address)> players, P2PClient client);
        public abstract void Poll(float deltaTime);
        public abstract void Stop();
    }
}