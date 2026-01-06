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
    public class ManualRunner : SingleplayerRunner
    {
        public override void Poll(float deltaTime)
        {
            if (!_initialized) { return; }
            GameLoop();
        }
    }
}