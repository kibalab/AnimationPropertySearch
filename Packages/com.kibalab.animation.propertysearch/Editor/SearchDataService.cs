#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace K13A.AnimationEditor.PropertySearch
{
    sealed class SearchDataService
    {
        private string _search = string.Empty;

        private static readonly HashSet<string> FavoriteKeys = new();
        private readonly List<SearchEntry> _allEntries = new();
        private readonly List<SearchEntry> _filteredEntries = new();
        private readonly List<SearchEntry> _favoriteEntries = new();
        private readonly List<SearchEntry> _displayEntries = new();
        private readonly Dictionary<string, SearchEntry> _entryByKey = new();

        private EditorWindow _animationWindow;
        private AnimationClip _lastClip;
        private GameObject _lastRoot;

        private int _maxDepth = 1;
        private readonly HashSet<string> _selectedEntryKeys = new();
        private string _selectionAnchorKey;
        private int _favoriteDisplayCount;

        private static readonly BindingFlags InstFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type _animationWindowType;
        private static Type _animationWindowStateType;
        private static Type _animationWindowUtilityType;

        private static PropertyInfo _animationWindowStateProp;
        private static MethodInfo _animationWindowGetAllMethod;
        private static PropertyInfo _stateActiveClipProp;
        private static PropertyInfo _stateActiveRootGameObjectProp;
        private static MethodInfo _stateSelectByIdMethod;
        private static MethodInfo _stateForceRefreshMethod;
        private static MethodInfo _utilityGetPropertyNodeIdMethod;
        private static PropertyInfo _stateHierarchyStateProp;
        private static FieldInfo _stateHierarchyStateField;
        private static FieldInfo _hierarchyScrollPositionField;
        private static PropertyInfo _hierarchyScrollPositionProp;
        private static PropertyInfo _hierarchySelectedIdsProp;
        private static FieldInfo _hierarchySelectedIdsField;
        private static PropertyInfo _hierarchyLastClickedIdProp;
        private static FieldInfo _hierarchyLastClickedIdField;

        public string Search
        {
            get => _search;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_search, next, StringComparison.Ordinal))
                    return;

                _search = next;
                FilterEntries();
                RebuildDisplayEntries();
            }
        }

        public IReadOnlyList<SearchEntry> FilteredEntries => _filteredEntries;
        public IReadOnlyList<SearchEntry> FavoriteEntries => _favoriteEntries;
        public IReadOnlyList<SearchEntry> DisplayEntries => _displayEntries;
        public int FavoriteDisplayCount => _favoriteDisplayCount;
        public int MaxDepth => _maxDepth;
        public int TotalCount => _allEntries.Count;
        public int VisibleCount => _displayEntries.Count;
        public int SelectedCount => _selectedEntryKeys.Count;

        public void Initialize()
        {
            InitializeReflection();
            RebuildEntries();
            FilterEntries();
            RebuildFavorites();
            RebuildDisplayEntries();
        }

        public bool EnsureAnimationWindowAvailable()
        {
            return EnsureAnimationWindow();
        }

        public void RefreshIfNeeded()
        {
            if (!EnsureAnimationWindow())
                return;

            if (!RebuildEntries()) return;

            FilterEntries();
            RebuildFavorites();
            RebuildDisplayEntries();
        }

        public static bool IsFavorite(SearchEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.EntryKey))
                return false;

            return FavoriteKeys.Contains(entry.EntryKey);
        }

        public bool IsSelected(SearchEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.EntryKey))
                return false;

            return _selectedEntryKeys.Contains(entry.EntryKey);
        }

        public void ToggleFavorite(SearchEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.EntryKey))
                return;

            if (!FavoriteKeys.Add(entry.EntryKey))
                FavoriteKeys.Remove(entry.EntryKey);

            RebuildFavorites();
            RebuildDisplayEntries();
        }

        public static Texture GetTypeIcon(Type type)
        {
            if (type == null)
                return EditorGUIUtility.FindTexture("DefaultAsset Icon");

            GUIContent content = EditorGUIUtility.ObjectContent(null, type);
            if (content != null && content.image != null)
                return content.image;

            return EditorGUIUtility.FindTexture("DefaultAsset Icon");
        }

        public void SelectEntry(SearchEntry entry, bool shift, bool toggle, EditorWindow owner)
        {
            if (entry == null || string.IsNullOrEmpty(entry.EntryKey))
                return;

            RebuildDisplayEntries();
            var clickedIndex = GetDisplayIndex(entry.EntryKey);
            if (clickedIndex < 0)
                return;

            if (shift)
                SelectRange(clickedIndex, toggle);
            else if (toggle)
                ToggleSingle(entry.EntryKey);
            else
                SelectOnly(entry.EntryKey);

            var primaryEntry = ResolvePrimarySelectionEntry(entry);
            if (primaryEntry != null && IsSelected(primaryEntry))
                SelectInInspector(primaryEntry);

            ApplySelectionToAnimationWindow(primaryEntry ?? entry, owner);
        }

        private void InitializeReflection()
        {
            if (_animationWindowType != null)
                return;

            var editorAssembly = typeof(EditorWindow).Assembly;

            _animationWindowType = editorAssembly.GetType("UnityEditor.AnimationWindow");
            _animationWindowStateType = editorAssembly.GetType("UnityEditorInternal.AnimationWindowState");
            _animationWindowUtilityType = editorAssembly.GetType("UnityEditorInternal.AnimationWindowUtility");

            if (_animationWindowType != null)
            {
                _animationWindowStateProp = _animationWindowType.GetProperty("state", InstFlags);
                _animationWindowGetAllMethod = _animationWindowType.GetMethod("GetAllAnimationWindows", StaticFlags);
            }

            if (_animationWindowStateType != null)
            {
                _stateActiveClipProp = _animationWindowStateType.GetProperty("activeAnimationClip", InstFlags);
                _stateActiveRootGameObjectProp = _animationWindowStateType.GetProperty("activeRootGameObject", InstFlags);
                _stateSelectByIdMethod = _animationWindowStateType.GetMethod("SelectHierarchyItem", InstFlags, null, new[] { typeof(int), typeof(bool), typeof(bool) }, null);
                _stateForceRefreshMethod = _animationWindowStateType.GetMethod("ForceRefresh", InstFlags);

                _stateHierarchyStateProp = _animationWindowStateType.GetProperty("hierarchyState", InstFlags);
                if (_stateHierarchyStateProp == null)
                {
                    _stateHierarchyStateField = _animationWindowStateType.GetField("hierarchyState", InstFlags);
                }
            }

            if (_animationWindowUtilityType != null)
            {
                _utilityGetPropertyNodeIdMethod = _animationWindowUtilityType.GetMethod(
                    "GetPropertyNodeID",
                    StaticFlags,
                    null,
                    new[] { typeof(int), typeof(string), typeof(Type), typeof(string) },
                    null);
            }
        }

        private bool EnsureAnimationWindow()
        {
            if (_animationWindowType == null || _animationWindowStateProp == null || _animationWindowGetAllMethod == null)
                return false;

            if (_animationWindow != null)
                return true;

            if (_animationWindowGetAllMethod.Invoke(null, null) is not IList windows || windows.Count == 0)
                return false;

            foreach (var windowObj in windows)
            {
                if (windowObj is not EditorWindow window)
                    continue;

                var state = _animationWindowStateProp.GetValue(window);
                if (state == null || !_animationWindowStateType.IsInstanceOfType(state))
                    continue;

                var clip = _stateActiveClipProp != null ? _stateActiveClipProp.GetValue(state) as AnimationClip : null;
                if (clip == null) continue;

                _animationWindow = window;
                return true;
            }

            var first = windows[0] as EditorWindow;
            if (first == null)
                return false;

            var firstState = _animationWindowStateProp.GetValue(first);
            if (firstState == null || !_animationWindowStateType.IsInstanceOfType(firstState))
                return false;

            _animationWindow = first;
            return true;
        }

        bool TryGetChannelGroup(string propertyName, out string groupName)
        {
            groupName = propertyName;
            if (string.IsNullOrEmpty(propertyName))
                return false;

            int dot = propertyName.LastIndexOf('.');
            if (dot <= 0 || dot >= propertyName.Length - 1)
                return false;

            string suffix = propertyName.Substring(dot + 1);
            if (suffix != "x" && suffix != "y" && suffix != "z" && suffix != "w" &&
                suffix != "r" && suffix != "g" && suffix != "b" && suffix != "a")
                return false;

            groupName = propertyName.Substring(0, dot);
            return true;
        }

        string MakeGroupDisplayName(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
                return groupName;

            string name = groupName;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1)
                name = name.Substring(dot + 1);

            if (name.StartsWith("m_"))
                name = name.Substring(2);
            if (name.StartsWith("_"))
                name = name.Substring(1);
            if (name.StartsWith("local", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("local".Length);

            return ObjectNames.NicifyVariableName(name);
        }

        string BuildPropertyKey(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return string.Empty;

            string key = propertyName;
            int dotIndex = propertyName.IndexOf('.');
            string group = dotIndex >= 0 ? propertyName.Substring(0, dotIndex) : propertyName;

            if (!string.IsNullOrEmpty(group))
            {
                key += "|" + group;

                string normalized = group;
                if (normalized.StartsWith("m_"))
                    normalized = normalized.Substring(2);
                if (normalized.StartsWith("local", StringComparison.Ordinal))
                    normalized = normalized.Substring("local".Length);

                if (!string.IsNullOrEmpty(normalized))
                    key += "|" + normalized;
            }

            return key;
        }

        bool RebuildEntries()
        {
            if (!EnsureAnimationWindow())
            {
                _allEntries.Clear();
                _filteredEntries.Clear();
                _favoriteEntries.Clear();
                _animationWindow = null;
                _lastClip = null;
                _lastRoot = null;
                _maxDepth = 1;
                _selectedEntryKeys.Clear();
                _selectionAnchorKey = null;
                _entryByKey.Clear();
                _displayEntries.Clear();
                _favoriteDisplayCount = 0;
                return false;
            }

            object state = _animationWindowStateProp.GetValue(_animationWindow);
            if (state == null)
            {
                _allEntries.Clear();
                _filteredEntries.Clear();
                _favoriteEntries.Clear();
                _maxDepth = 1;
                _selectedEntryKeys.Clear();
                _selectionAnchorKey = null;
                _entryByKey.Clear();
                _displayEntries.Clear();
                _favoriteDisplayCount = 0;
                return false;
            }

            AnimationClip clip = _stateActiveClipProp != null ? _stateActiveClipProp.GetValue(state) as AnimationClip : null;
            GameObject root = _stateActiveRootGameObjectProp != null ? _stateActiveRootGameObjectProp.GetValue(state) as GameObject : null;

            if (clip == _lastClip && root == _lastRoot && _allEntries.Count > 0)
                return false;

            _lastClip = clip;
            _lastRoot = root;

            _allEntries.Clear();
            _favoriteEntries.Clear();
            _maxDepth = 1;

            if (clip == null)
                return false;

            PropertyInfo allCurvesProp = _animationWindowStateType.GetProperty("allCurves", InstFlags);
            if (allCurvesProp == null)
                return false;

            IList allCurves = allCurvesProp.GetValue(state) as IList;
            if (allCurves == null)
                return false;

            var groupSeen = new HashSet<string>();

            foreach (var curve in allCurves)
            {
                if (curve == null)
                    continue;

                Type curveType = curve.GetType();
                PropertyInfo bindingProp = curveType.GetProperty("binding", InstFlags);
                if (bindingProp == null)
                    continue;

                object bindingObj = bindingProp.GetValue(curve);
                if (bindingObj == null)
                    continue;

                var binding = (EditorCurveBinding)bindingObj;
                if (binding.type == null)
                    continue;

                string path = binding.path ?? string.Empty;
                string objectName = GetObjectNameFromPath(root, path);
                string componentName = binding.type.Name;
                string propertyNameRaw = binding.propertyName ?? string.Empty;

                bool isChannel = TryGetChannelGroup(propertyNameRaw, out string groupName);
                string displayPropertyName;
                string keySource;
                string nodePropertyName;
                string entryKey;

                if (isChannel)
                {
                    string groupKey = path + "|" + binding.type.FullName + "|" + groupName;
                    if (!groupSeen.Add(groupKey))
                        continue;

                    displayPropertyName = MakeGroupDisplayName(groupName);
                    keySource = groupName;
                    nodePropertyName = groupName;
                    entryKey = groupKey;
                }
                else
                {
                    displayPropertyName = MakeGroupDisplayName(propertyNameRaw);
                    keySource = propertyNameRaw;
                    nodePropertyName = propertyNameRaw;
                    entryKey = path + "|" + binding.type.FullName + "|" + propertyNameRaw;
                }

                var entry = new SearchEntry
                {
                    Binding = binding,
                    Clip = clip,
                    ObjectName = objectName,
                    ComponentName = componentName,
                    PropertyName = displayPropertyName,
                    Path = path,
                    PropertyKey = BuildPropertyKey(keySource),
                    DisplayText = $"{objectName}  :  {componentName}.{displayPropertyName}",
                    NodePropertyName = nodePropertyName,
                    EntryKey = entryKey
                };

                int pathDepth = 0;
                if (!string.IsNullOrEmpty(path))
                {
                    pathDepth = 1 + path.Count(static key => key == '/');
                }

                entry.Depth = pathDepth + 2;
                _maxDepth = Mathf.Max(_maxDepth, entry.Depth);

                entry.HierarchyId = 0;
                if (_utilityGetPropertyNodeIdMethod != null)
                {
                    try
                    {
                        var idObj = _utilityGetPropertyNodeIdMethod.Invoke(
                            null,
                            new object[] { 0, binding.path, binding.type, entry.NodePropertyName });

                        if (idObj is int hierarchyId)
                        {
                            entry.HierarchyId = hierarchyId;
                        }
                    }
                    catch
                    {
                        entry.HierarchyId = 0;
                    }
                }

                _allEntries.Add(entry);
            }

            _maxDepth = Mathf.Max(_maxDepth, 1);
            RebuildEntryLookup();
            PruneSelectionKeys();
            return true;
        }

        private static string GetObjectNameFromPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return root != null ? root.name : "<root>";
            }

            var index = path.LastIndexOf('/');
            if (index >= 0 && index < path.Length - 1)
            {
                return path.Substring(index + 1);
            }

            return path;
        }

        private void FilterEntries()
        {
            _filteredEntries.Clear();
            if (_allEntries.Count == 0)
                return;

            var query = _search;
            if (string.IsNullOrEmpty(query))
            {
                _filteredEntries.AddRange(_allEntries);
                return;
            }

            query = query.Trim();
            if (query.Length > 2 && query[1] == ':')
            {
                var prefix = char.ToLowerInvariant(query[0]);
                var term = query.Substring(2);
                if (string.IsNullOrEmpty(term))
                    return;

                switch (prefix)
                {
                    case 't':
                    {
                        foreach (var entry in _allEntries)
                        {
                            if (ContainsCaseSensitive(entry.ComponentName, term))
                            {
                                _filteredEntries.Add(entry);
                            }
                        }

                        break;
                    }
                    case 'p':
                    {
                        foreach (var entry in _allEntries)
                        {
                            if (ContainsCaseSensitive(entry.PropertyName, term) ||
                                ContainsCaseSensitive(entry.PropertyKey, term))
                            {
                                _filteredEntries.Add(entry);
                            }
                        }

                        break;
                    }
                    default:
                    {
                        foreach (var entry in _allEntries)
                        {
                            if (ContainsCaseSensitive(entry.ObjectName, term) ||
                                ContainsCaseSensitive(entry.Path, term))
                            {
                                _filteredEntries.Add(entry);
                            }
                        }

                        break;
                    }
                }
            }
            else
            {
                foreach (var entry in _allEntries)
                {
                    if (ContainsCaseSensitive(entry.ObjectName, query) ||
                        ContainsCaseSensitive(entry.Path, query))
                    {
                        _filteredEntries.Add(entry);
                    }
                }
            }
        }

        private static bool ContainsCaseSensitive(string source, string term)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(term))
                return false;

            return source.IndexOf(term, StringComparison.Ordinal) >= 0;
        }

        private void RebuildFavorites()
        {
            _favoriteEntries.Clear();
            if (_allEntries.Count == 0 || FavoriteKeys.Count == 0)
                return;

            foreach (var entry in _allEntries)
            {
                if (IsFavorite(entry))
                    _favoriteEntries.Add(entry);
            }
        }

        private void RebuildEntryLookup()
        {
            _entryByKey.Clear();
            foreach (var entry in _allEntries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.EntryKey)) continue;

                _entryByKey.TryAdd(entry.EntryKey, entry);
            }
        }

        private void PruneSelectionKeys()
        {
            if (_selectedEntryKeys.Count > 0)
            {
                var toRemove = new List<string>();
                foreach (var key in _selectedEntryKeys)
                {
                    if (!_entryByKey.ContainsKey(key))
                    {
                        toRemove.Add(key);
                    }
                }

                foreach (var t in toRemove)
                {
                    _selectedEntryKeys.Remove(t);
                }
            }

            if (!string.IsNullOrEmpty(_selectionAnchorKey) && !_entryByKey.ContainsKey(_selectionAnchorKey))
            {
                _selectionAnchorKey = null;
            }
        }

        void RebuildDisplayEntries()
        {
            _displayEntries.Clear();
            _favoriteDisplayCount = 0;

            var seen = new HashSet<string>();
            foreach (var entry in _favoriteEntries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.EntryKey)) continue;

                if (seen.Add(entry.EntryKey))
                    _displayEntries.Add(entry);
            }

            _favoriteDisplayCount = _displayEntries.Count;

            foreach (var entry in _filteredEntries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.EntryKey)) continue;

                if (seen.Add(entry.EntryKey))
                {
                    _displayEntries.Add(entry);
                }
            }
        }

        private int GetDisplayIndex(string entryKey)
        {
            if (string.IsNullOrEmpty(entryKey))
                return -1;

            for (var i = 0; i < _displayEntries.Count; i++)
            {
                var entry = _displayEntries[i];
                if (entry != null && entry.EntryKey == entryKey)
                    return i;
            }

            return -1;
        }

        private void SelectOnly(string entryKey)
        {
            _selectedEntryKeys.Clear();
            _selectedEntryKeys.Add(entryKey);
            _selectionAnchorKey = entryKey;
        }

        private void ToggleSingle(string entryKey)
        {
            if (!_selectedEntryKeys.Add(entryKey))
            {
                _selectedEntryKeys.Remove(entryKey);
                if (_selectionAnchorKey == entryKey)
                {
                    _selectionAnchorKey = GetFirstSelectedKeyInDisplayOrder();
                }
            }
            else
            {
                _selectionAnchorKey = entryKey;
            }
        }

        private void SelectRange(int clickedIndex, bool additive)
        {
            if (clickedIndex < 0 || clickedIndex >= _displayEntries.Count)
                return;

            var anchorIndex = GetDisplayIndex(_selectionAnchorKey);
            if (anchorIndex < 0)
            {
                anchorIndex = clickedIndex;
            }

            if (!additive)
                _selectedEntryKeys.Clear();

            var min = Mathf.Min(anchorIndex, clickedIndex);
            var max = Mathf.Max(anchorIndex, clickedIndex);
            for (var i = min; i <= max; i++)
            {
                var entry = _displayEntries[i];
                if (entry != null && !string.IsNullOrEmpty(entry.EntryKey))
                    _selectedEntryKeys.Add(entry.EntryKey);
            }

            var clickedEntry = _displayEntries[clickedIndex];
            _selectionAnchorKey = clickedEntry != null ? clickedEntry.EntryKey : _selectionAnchorKey;
        }

        private string GetFirstSelectedKeyInDisplayOrder()
        {
            foreach (var entry in _displayEntries)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.EntryKey) && _selectedEntryKeys.Contains(entry.EntryKey))
                {
                    return entry.EntryKey;
                }
            }

            return null;
        }

        private SearchEntry ResolvePrimarySelectionEntry(SearchEntry clickedEntry)
        {
            if (clickedEntry != null &&
                !string.IsNullOrEmpty(clickedEntry.EntryKey) &&
                _selectedEntryKeys.Contains(clickedEntry.EntryKey))
            {
                return clickedEntry;
            }

            foreach (var entry in _displayEntries)
            {
                if (entry != null &&
                    !string.IsNullOrEmpty(entry.EntryKey) &&
                    _selectedEntryKeys.Contains(entry.EntryKey)
                   )
                {
                    return entry;
                }
            }

            return clickedEntry;
        }

        private static object GetHierarchyState(object state)
        {
            if (state == null)
                return null;

            if (_stateHierarchyStateProp != null)
                return _stateHierarchyStateProp.GetValue(state);

            return _stateHierarchyStateField != null ? _stateHierarchyStateField.GetValue(state) : null;
        }

        private static void SetHierarchyScroll(object hierarchyState, float ratio, int approxCount)
        {
            if (hierarchyState == null)
                return;

            var hierarchyType = hierarchyState.GetType();
            if (_hierarchyScrollPositionField == null && _hierarchyScrollPositionProp == null)
            {
                _hierarchyScrollPositionField = hierarchyType.GetField("scrollPos", InstFlags);
                if (_hierarchyScrollPositionField == null)
                {
                    _hierarchyScrollPositionProp = hierarchyType.GetProperty("scrollPos", InstFlags);
                }
            }

            if (_hierarchyScrollPositionField == null && _hierarchyScrollPositionProp == null)
                return;

            Vector2 scroll;
            if (_hierarchyScrollPositionField != null)
            {
                scroll = (Vector2)_hierarchyScrollPositionField.GetValue(hierarchyState);
            }
            else
            {
                scroll = (Vector2)_hierarchyScrollPositionProp.GetValue(hierarchyState);
            }

            var rowHeight = EditorGUIUtility.singleLineHeight;
            var totalHeight = Mathf.Max(rowHeight * Mathf.Max(1, approxCount), 1f);
            scroll.y = totalHeight * Mathf.Clamp01(ratio);

            if (_hierarchyScrollPositionField != null)
            {
                _hierarchyScrollPositionField.SetValue(hierarchyState, scroll);
                return;
            }

            _hierarchyScrollPositionProp.SetValue(hierarchyState, scroll);
        }

        private void SelectInInspector(SearchEntry entry)
        {
            if (entry == null || entry.Clip == null) return;
            if (!EnsureAnimationWindow() || _animationWindowStateProp == null || _stateActiveRootGameObjectProp == null) return;

            var state = _animationWindowStateProp.GetValue(_animationWindow);
            if (state == null) return;

            var root = _stateActiveRootGameObjectProp.GetValue(state) as GameObject;
            if (root == null) return;

            var target = AnimationUtility.GetAnimatedObject(root, entry.Binding);
            if (target == null) return;

            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private void ApplySelectionToAnimationWindow(SearchEntry primaryEntry, EditorWindow owner)
        {
            if (primaryEntry == null || !EnsureAnimationWindow())
                return;

            var state = _animationWindowStateProp.GetValue(_animationWindow);
            if (state == null || !_animationWindowStateType.IsInstanceOfType(state))
                return;

            var clip = _stateActiveClipProp != null ? _stateActiveClipProp.GetValue(state) as AnimationClip : null;
            if (clip != primaryEntry.Clip)
            {
                if (_stateActiveClipProp != null)
                    _stateActiveClipProp.SetValue(state, primaryEntry.Clip);
                _stateForceRefreshMethod?.Invoke(state, null);
            }

            var selectedIds = new List<int>();
            foreach (var entry in _displayEntries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.EntryKey)) continue;
                if (!_selectedEntryKeys.Contains(entry.EntryKey)) continue;

                var id = ResolveHierarchyId(entry);
                if (id != 0 && !selectedIds.Contains(id))
                {
                    selectedIds.Add(id);
                }
            }

            if (selectedIds.Count == 0)
            {
                TryForceHierarchySelection(state, selectedIds, 0);
                _animationWindow.Repaint();
                owner?.Repaint();
                return;
            }

            var primaryId = ResolveHierarchyId(primaryEntry);
            if (primaryId != 0 && !selectedIds.Contains(primaryId))
            {
                selectedIds.Add(primaryId);
            }

            if (primaryId == 0 && selectedIds.Count > 0)
            {
                primaryId = selectedIds[^1];
            }

            if (_stateSelectByIdMethod != null && selectedIds.Count > 0)
            {
                try
                {
                    var orderedIds = new List<int>(selectedIds);
                    if (primaryId != 0)
                    {
                        orderedIds.Remove(primaryId);
                        orderedIds.Add(primaryId);
                    }

                    var additive = false;
                    foreach (var id in orderedIds)
                    {
                        _stateSelectByIdMethod.Invoke(state, new object[] { id, additive, true });
                        additive = true;
                    }
                }
                catch
                {
                    // Fallback below still enforces selection list.
                }
            }

            TryForceHierarchySelection(state, selectedIds, primaryId);

            try
            {
                var index = _allEntries.IndexOf(primaryEntry);
                if (index >= 0 && _allEntries.Count > 0)
                {
                    var ratio = (float)index / Mathf.Max(1, _allEntries.Count - 1);
                    var hierarchyState = GetHierarchyState(state);
                    SetHierarchyScroll(hierarchyState, ratio, _allEntries.Count);
                }
            }
            catch
            {
                // Intentionally ignored. Selection still succeeds.
            }

            _animationWindow.Focus();
            _animationWindow.Repaint();
            owner?.Repaint();
        }

        private static int ResolveHierarchyId(SearchEntry entry)
        {
            if (entry == null) return 0;
            if (entry.HierarchyId != 0) return entry.HierarchyId;
            if (_utilityGetPropertyNodeIdMethod == null) return 0;

            try
            {
                var binding = entry.Binding;
                var nodePropertyName = string.IsNullOrEmpty(entry.NodePropertyName)
                    ? binding.propertyName
                    : entry.NodePropertyName;

                var idObj = _utilityGetPropertyNodeIdMethod.Invoke(
                    null,
                    new object[] { 0, binding.path, binding.type, nodePropertyName }
                );
                if (idObj is int hierarchyId)
                {
                    entry.HierarchyId = hierarchyId;
                    return hierarchyId;
                }
            }
            catch
            {
                // ignored
            }

            return 0;
        }

        private static void TryForceHierarchySelection(object state, IList<int> selectedIds, int lastClickedId)
        {
            if (state == null) return;

            var hierarchyState = GetHierarchyState(state);
            if (hierarchyState == null) return;

            var hierarchyType = hierarchyState.GetType();
            if (_hierarchySelectedIdsProp == null && _hierarchySelectedIdsField == null)
            {
                _hierarchySelectedIdsProp = hierarchyType.GetProperty("selectedIDs", InstFlags);
                _hierarchySelectedIdsField = hierarchyType.GetField("selectedIDs", InstFlags)
                                             ?? hierarchyType.GetField("m_SelectedIDs", InstFlags);
            }

            if (_hierarchyLastClickedIdProp == null && _hierarchyLastClickedIdField == null)
            {
                _hierarchyLastClickedIdProp = hierarchyType.GetProperty("lastClickedID", InstFlags);
                _hierarchyLastClickedIdField = hierarchyType.GetField("lastClickedID", InstFlags)
                                               ?? hierarchyType.GetField("m_LastClickedID", InstFlags);
            }

            try
            {
                IList ids = null;
                if (_hierarchySelectedIdsProp != null)
                {
                    ids = _hierarchySelectedIdsProp.GetValue(hierarchyState) as IList;
                }
                else if (_hierarchySelectedIdsField != null)
                {
                    ids = _hierarchySelectedIdsField.GetValue(hierarchyState) as IList;
                }

                if (ids != null)
                {
                    ids.Clear();
                    if (selectedIds != null)
                    {
                        foreach (var id in selectedIds) ids.Add(id);
                    }
                }
                else
                {
                    var fallback = selectedIds != null ? new List<int>(selectedIds) : new List<int>();
                    if (_hierarchySelectedIdsProp != null && _hierarchySelectedIdsProp.CanWrite)
                    {
                        _hierarchySelectedIdsProp.SetValue(hierarchyState, fallback);
                    }
                    else if (_hierarchySelectedIdsField != null)
                    {
                        _hierarchySelectedIdsField.SetValue(hierarchyState, fallback);
                    }
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                if (_hierarchyLastClickedIdProp != null && _hierarchyLastClickedIdProp.CanWrite)
                {
                    _hierarchyLastClickedIdProp.SetValue(hierarchyState, lastClickedId);
                }
                else if (_hierarchyLastClickedIdField != null)
                {
                    _hierarchyLastClickedIdField.SetValue(hierarchyState, lastClickedId);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}
#endif