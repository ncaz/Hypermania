using System;
using System.Collections.Generic;
using UnityEngine;

namespace Editors.MoveBuilder
{
    public enum HitboxKind
    {
        Hurtbox,
        Hitbox
    }

    [Serializable]
    public struct BoxProps
    {
        public HitboxKind Kind;

        public int Damage;
        public int HitstunTicks;
        public int BlockstunTicks;
        public Vector2 Knockback;
    }

    [Serializable]
    public struct BoxData
    {
        public string Name;

        // character relative space
        public Vector2 CenterLocal;
        public Vector2 SizeLocal;
        public BoxProps Props;
    }

    [Serializable]
    public class FrameData
    {
        public List<BoxData> Boxes = new List<BoxData>();
    }

    [CreateAssetMenu(menuName = "Hypermania/Move Builder Data")]
    public class MoveBuilderData : ScriptableObject
    {
        public AnimationClip Clip;

        public int TotalTicks;

        public List<FrameData> Frames = new List<FrameData>();

        public void EnsureSize(int totalTicks)
        {
            TotalTicks = Mathf.Max(1, totalTicks);
            while (Frames.Count < TotalTicks) Frames.Add(new FrameData());
            while (Frames.Count > TotalTicks) Frames.RemoveAt(Frames.Count - 1);
        }

        public FrameData GetFrame(int tick)
        {
            tick = Mathf.Clamp(tick, 0, TotalTicks - 1);
            return Frames[tick];
        }

        // Simple JSON export for runtime
        public string ToJson(bool pretty = true)
        {
            return JsonUtility.ToJson(new Wrapper { Data = this }, pretty);
        }

        [Serializable]
        private class Wrapper { public MoveBuilderData Data; }
    }
}