using UnityEditor;
using UnityEngine;

namespace Utils.SoftFloat
{
    public static class SFloatFields
    {
        public static sfloat SFloatField(string label, sfloat value)
        {
            float f = (float)value;
            float next = EditorGUILayout.FloatField(label, f);
            return (sfloat)next;
        }

        public static SVector2 SVector2Field(string label, SVector2 value)
        {
            var v = new Vector2((float)value.x, (float)value.y);
            v = EditorGUILayout.Vector2Field(label, v);
            return new SVector2((sfloat)v.x, (sfloat)v.y);
        }

        public static SVector3 SVector3Field(string label, SVector3 value)
        {
            var v = new Vector3((float)value.x, (float)value.y, (float)value.z);
            v = EditorGUILayout.Vector3Field(label, v);
            return new SVector3((sfloat)v.x, (sfloat)v.y, (sfloat)v.z);
        }
    }
}
