using UnityEditor;
using UnityEngine;

namespace Utils.SoftFloat
{
    [CustomPropertyDrawer(typeof(SVector2))]
    public sealed class SVector2Drawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var xProp = property.FindPropertyRelative("x");
            var yProp = property.FindPropertyRelative("y");

            if (xProp == null || yProp == null)
            {
                EditorGUI.LabelField(position, label.text, "SVector2: missing fields");
                return;
            }

            var xRaw = xProp.FindPropertyRelative("rawValue");
            var yRaw = yProp.FindPropertyRelative("rawValue");

            if (
                xRaw == null
                || yRaw == null
                || xRaw.propertyType != SerializedPropertyType.Integer
                || yRaw.propertyType != SerializedPropertyType.Integer
            )
            {
                EditorGUI.LabelField(position, label.text, "SVector2: invalid sfloat layout");
                return;
            }

            EditorGUI.BeginProperty(position, label, property);
            position = EditorGUI.PrefixLabel(position, label);

            using (new EditorGUI.IndentLevelScope(-EditorGUI.indentLevel))
            {
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 14f;

                uint rx = unchecked((uint)xRaw.intValue);
                uint ry = unchecked((uint)yRaw.intValue);

                var current = new Vector2((float)sfloat.FromRaw(rx), (float)sfloat.FromRaw(ry));

                EditorGUI.BeginChangeCheck();
                var next = EditorGUI.Vector2Field(position, GUIContent.none, current);
                if (EditorGUI.EndChangeCheck())
                {
                    var sx = (sfloat)next.x;
                    var sy = (sfloat)next.y;

                    xRaw.intValue = unchecked((int)sx.RawValue);
                    yRaw.intValue = unchecked((int)sy.RawValue);
                }

                EditorGUIUtility.labelWidth = labelWidth;
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight;
    }
}
