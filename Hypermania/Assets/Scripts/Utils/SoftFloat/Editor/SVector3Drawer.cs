using UnityEditor;
using UnityEngine;

namespace Utils.SoftFloat
{
    [CustomPropertyDrawer(typeof(SVector3))]
    public sealed class SVector3Drawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var xProp = property.FindPropertyRelative("x");
            var yProp = property.FindPropertyRelative("y");
            var zProp = property.FindPropertyRelative("z");

            if (xProp == null || yProp == null || zProp == null)
            {
                EditorGUI.LabelField(position, label.text, "SVector3: missing fields");
                return;
            }

            var xRaw = xProp.FindPropertyRelative("rawValue");
            var yRaw = yProp.FindPropertyRelative("rawValue");
            var zRaw = zProp.FindPropertyRelative("rawValue");

            if (
                xRaw == null
                || yRaw == null
                || zRaw == null
                || xRaw.propertyType != SerializedPropertyType.Integer
                || yRaw.propertyType != SerializedPropertyType.Integer
                || zRaw.propertyType != SerializedPropertyType.Integer
            )
            {
                EditorGUI.LabelField(position, label.text, "SVector3: invalid sfloat layout");
                return;
            }

            EditorGUI.BeginProperty(position, label, property);
            position = EditorGUI.PrefixLabel(position, label);

            using (new EditorGUI.IndentLevelScope(-EditorGUI.indentLevel))
            {
                uint rx = unchecked((uint)xRaw.intValue);
                uint ry = unchecked((uint)yRaw.intValue);
                uint rz = unchecked((uint)zRaw.intValue);

                var current = new Vector3(
                    (float)sfloat.FromRaw(rx),
                    (float)sfloat.FromRaw(ry),
                    (float)sfloat.FromRaw(rz)
                );

                EditorGUI.BeginChangeCheck();
                var next = EditorGUI.Vector3Field(position, GUIContent.none, current);
                if (EditorGUI.EndChangeCheck())
                {
                    var sx = (sfloat)next.x;
                    var sy = (sfloat)next.y;
                    var sz = (sfloat)next.z;

                    xRaw.intValue = unchecked((int)sx.RawValue);
                    yRaw.intValue = unchecked((int)sy.RawValue);
                    zRaw.intValue = unchecked((int)sz.RawValue);
                }
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight;
    }
}
