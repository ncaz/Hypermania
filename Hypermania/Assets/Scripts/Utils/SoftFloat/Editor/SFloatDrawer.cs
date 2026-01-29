using UnityEditor;
using UnityEngine;

namespace Utils.SoftFloat
{
    [CustomPropertyDrawer(typeof(sfloat))]
    public sealed class SFloatDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // sfloat is a struct with a private [SerializeField] uint rawValue;
            var rawProp = property.FindPropertyRelative("rawValue");
            if (rawProp == null || rawProp.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.LabelField(position, label.text, "sfloat: missing uint rawValue");
                return;
            }

            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.BeginChangeCheck();

            // Read raw -> sfloat -> float
            uint raw = unchecked((uint)rawProp.intValue);
            float current = (float)sfloat.FromRaw(raw);

            float next = EditorGUI.FloatField(position, label, current);

            if (EditorGUI.EndChangeCheck())
            {
                // Write float -> sfloat -> raw
                sfloat sf = (sfloat)next;
                rawProp.intValue = unchecked((int)sf.RawValue);
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight;
    }
}
