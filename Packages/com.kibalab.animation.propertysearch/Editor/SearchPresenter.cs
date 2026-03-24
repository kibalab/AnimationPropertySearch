#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace K13A.AnimationEditor.PropertySearch
{
    sealed class SearchPresenter
    {
        private readonly SearchDataService _service;

        private GUIStyle _starStyleEmpty;
        private GUIStyle _starStyleFilled;
        private GUIStyle _pathStyle;

        public SearchPresenter(SearchDataService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public void Draw(EditorWindow owner, ref Vector2 scroll)
        {
            if (!_service.EnsureAnimationWindowAvailable())
            {
                EditorGUILayout.HelpBox("Open an Animation window and try again.", MessageType.Info);
                return;
            }

            _service.RefreshIfNeeded();
            DrawSearchToolbar();

            var scrollOpened = false;
            try
            {
                scroll = EditorGUILayout.BeginScrollView(scroll);
                scrollOpened = true;

                var display = _service.DisplayEntries;
                if (display.Count == 0)
                {
                    GUILayout.Label("No search results.", EditorStyles.helpBox);
                }
                else
                {
                    var favoriteCount = _service.FavoriteDisplayCount;
                    for (var i = 0; i < display.Count; i++)
                    {
                        DrawRow(owner, display[i], i);

                        if (favoriteCount <= 0 || i + 1 != favoriteCount || favoriteCount >= display.Count) continue;

                        EditorGUILayout.Space(4f);
                        EditorGUILayout.LabelField(string.Empty, GUI.skin.horizontalSlider);
                        EditorGUILayout.Space(4f);
                    }
                }
            }
            finally
            {
                if (scrollOpened)
                    EditorGUILayout.EndScrollView();
            }

            DrawFooter();
        }

        private void DrawSearchToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var nextSearch = EditorGUILayout.TextField(_service.Search, EditorStyles.toolbarSearchField, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("x", EditorStyles.toolbarButton, GUILayout.Width(20f)))
            {
                nextSearch = string.Empty;
                GUI.FocusControl(null);
            }

            _service.Search = nextSearch;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Search Tip: t:ComponentType / p:PropertyName", EditorStyles.miniLabel);
            EditorGUILayout.Space(2f);
        }

        private void EnsureStarStyles()
        {
            if (_starStyleEmpty != null && _starStyleFilled != null)
                return;

            var baseLabel = EditorStyles.label;

            _starStyleEmpty = new GUIStyle(baseLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = baseLabel.fontSize + 1,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal =
                {
                    textColor = Color.white
                },
                hover =
                {
                    textColor = Color.white
                },
                active =
                {
                    textColor = Color.white
                }
            };

            _starStyleFilled = new GUIStyle(baseLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = baseLabel.fontSize + 1,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };

            var yellow = new Color(1f, 0.85f, 0.2f, 1f);
            _starStyleFilled.normal.textColor = yellow;
            _starStyleFilled.hover.textColor = yellow;
            _starStyleFilled.active.textColor = yellow;
        }

        private void DrawRow(EditorWindow owner, SearchEntry entry, int index)
        {
            var rowRect = GUILayoutUtility.GetRect(0f, EditorGUIUtility.singleLineHeight + 6f, GUILayout.ExpandWidth(true));
            DrawRowBackground(rowRect, index, _service.IsSelected(entry));

            const float starWidth = 20f;
            var starRect = new Rect(rowRect.xMax - starWidth, rowRect.y + 2f, starWidth, rowRect.height - 4f);
            var labelRect = new Rect(rowRect.x, rowRect.y, rowRect.width - starWidth, rowRect.height);

            DrawRowContent(labelRect, entry);

            EnsureStarStyles();
            var isFavorite = SearchDataService.IsFavorite(entry);
            var starText = isFavorite ? "★" : "☆";
            var starStyle = isFavorite ? _starStyleFilled : _starStyleEmpty;

            if (GUI.Button(starRect, starText, starStyle))
            {
                _service.ToggleFavorite(entry);
                owner?.Repaint();
            }

            if (
                Event.current.type != EventType.MouseDown
                || !labelRect.Contains(Event.current.mousePosition)
                || Event.current.button != 0
            ) return;

            var shift = Event.current.shift;
            var toggle = Event.current.control || Event.current.command;
            _service.SelectEntry(entry, shift, toggle, owner);
            Event.current.Use();
        }

        static void DrawRowBackground(Rect rowRect, int index, bool isSelected)
        {
            if (Event.current.type != EventType.Repaint) return;

            var background = (index % 2 == 0)
                ? new Color(0f, 0f, 0f, 0.10f)
                : new Color(0f, 0f, 0f, 0.04f);

            if (rowRect.Contains(Event.current.mousePosition))
            {
                background = new Color(0.24f, 0.48f, 0.90f, 0.20f);
            }

            if (isSelected)
            {
                background = new Color(0.24f, 0.48f, 0.90f, 0.35f);
            }

            EditorGUI.DrawRect(rowRect, background);
        }

        void DrawDepthLines(Rect rowRect, int depth)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            var maxDepth = _service.MaxDepth;
            if (depth <= 0 || maxDepth <= 0)
                return;

            var levels = Mathf.Min(depth, maxDepth);
            const float indentPerLevel = 14f;
            float denominator = Mathf.Max(1, maxDepth);

            for (var i = 0; i < levels; i++)
            {
                var baseX = rowRect.x + 6f + indentPerLevel * i;
                var x = baseX + indentPerLevel * 0.5f;

                var alpha = (maxDepth - i) / denominator;
                alpha *= 0.6f;

                var color = new Color(1f, 1f, 1f, alpha);
                var lineRect = new Rect(x, rowRect.y + 1f, 1f, rowRect.height - 2f);
                EditorGUI.DrawRect(lineRect, color);
            }
        }

        private void DrawRowContent(Rect rowRect, SearchEntry entry)
        {
            DrawDepthLines(rowRect, entry.Depth);

            const float indentPerLevel = 14f;
            var indent = indentPerLevel * Mathf.Max(0, entry.Depth);

            var iconRect = new Rect(rowRect.x + 6f + indent, rowRect.y + 6f, 12f, 12f);
            var textWidth = (rowRect.x + rowRect.width) - (iconRect.xMax + 4f);
            if (textWidth < 10f)
            {
                textWidth = 10f;
            }

            var textRect = new Rect(iconRect.xMax + 4f, rowRect.y + 2f, textWidth, rowRect.height - 4f);
            var icon = SearchDataService.GetTypeIcon(entry.Binding.type);
            if (icon != null)
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);

            var path = string.IsNullOrEmpty(entry.Path) ? "<root>" : entry.Path;
            EnsurePathStyle();

            const float padding = 6f;
            var pathContent = new GUIContent(path);
            var pathWidth = Mathf.Min(_pathStyle.CalcSize(pathContent).x, textRect.width * 0.55f);

            var mainRect = textRect;
            mainRect.height = EditorGUIUtility.singleLineHeight;
            mainRect.width = Mathf.Max(10f, textRect.width - pathWidth - padding);

            var pathRect = textRect;
            pathRect.height = EditorGUIUtility.singleLineHeight;
            pathRect.x = mainRect.xMax + padding;
            pathRect.width = Mathf.Max(10f, textRect.xMax - pathRect.x);

            EditorGUI.LabelField(mainRect, entry.DisplayText, EditorStyles.label);
            GUI.Label(pathRect, pathContent, _pathStyle);
        }

        private void EnsurePathStyle()
        {
            if (_pathStyle != null)
                return;

            var baseSize = EditorStyles.label.fontSize;
            if (baseSize <= 0)
            {
                baseSize = 12;
            }

            _pathStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = Mathf.Max(8, Mathf.RoundToInt(baseSize * 0.5f)),
                clipping = TextClipping.Clip
            };
        }

        private void DrawFooter()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                $"Total: {_service.TotalCount}    Visible: {_service.VisibleCount}    Max Depth: {_service.MaxDepth}    Selected: {_service.SelectedCount}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }
    }
}
#endif