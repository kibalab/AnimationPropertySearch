#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace K13A.AnimationEditor.PropertySearch
{
    public class SearchWindow : EditorWindow
    {
        private SearchDataService _dataService;
        private SearchPresenter _presenter;
        private Vector2 _scroll;

        [MenuItem("K13A/Animation Property Search")]
        private static void Open()
        {
            GetWindow<SearchWindow>("Animation Search");
        }

        private void OnEnable()
        {
            _dataService ??= new SearchDataService();
            _presenter ??= new SearchPresenter(_dataService);
            _dataService.Initialize();
        }

        private void OnGUI()
        {
            if (_dataService == null || _presenter == null)
            {
                OnEnable();
            }

            _presenter.Draw(this, ref _scroll);
        }
    }
}
#endif