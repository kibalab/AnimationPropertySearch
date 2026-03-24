#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace K13A.AnimationEditor.PropertySearch
{
    sealed class SearchEntry
    {
        public EditorCurveBinding Binding;
        public AnimationClip Clip;

        public string ObjectName;
        public string ComponentName;
        public string PropertyName;
        public string Path;
        public string PropertyKey;
        public string DisplayText;

        public string NodePropertyName;
        public string EntryKey;
        public int Depth;
        public int HierarchyId;
    }
}
#endif