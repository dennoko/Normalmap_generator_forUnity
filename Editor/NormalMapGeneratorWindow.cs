using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using DennokoVersionChecker = Dennoko.NormalmapGenerator.DennokoVersionChecker;

namespace NormalmapGenerator
{
    public class NormalMapGeneratorWindow : EditorWindow
    {
        // ── Assets (UI/*.meta の GUID) ───────────────────────────────────────
        private const string UXML_GUID       = "9e4a7c2d1b8f43a6b5c9e0d3f7a2b184";
        private const string THEME_USS_GUID  = "7b3c1e9a4d2f4b8e9c6a1d5f3e8b2c47";
        private const string WINDOW_USS_GUID = "2f8d6a3b5c1e4f7a8b9d0c2e6f4a1b39";

        // ── State ────────────────────────────────────────────────────────────
        private Texture2D         _inputTexture;
        private NormalMapSettings _settings = new NormalMapSettings();
        private NormalMapProcessor _processor;
        private ComputeShader     _computeShader;

        // Preview pipeline
        private PipelineContext _previewCtx;
        private int   _previewW, _previewH;
        private float _previewScale = 1f;

        // Preview resolution cap. The working resolution is min(source, cap),
        // so a source at or below the cap is processed at its native size and
        // the preview is then bit-identical to the saved output.
        private int _previewCap = 2048;
        private static readonly int[]    CapValues  = { 1024, 2048, 4096 };
        private static readonly string[] CapLabels  = { "1024", "2048", "4096" };
        private const string PrefKeyCap = "NormalMapGenerator_PreviewCap";

        // Debounce. Cheap changes only re-run the tail of the pipeline, so they
        // can settle much faster than a change that invalidates the distance field.
        private const double LightDebounceSeconds = 0.10;
        private const double HeavyDebounceSeconds = 0.30;
        private bool   _previewDirty;
        private double _lastChangeTime;
        private double _debounce = HeavyDebounceSeconds;
        private bool   _autoUpdatePreview = true;

        // State the preview was built from (drives the light/heavy debounce split)
        private NormalMapSettings _appliedSnapshot;
        private Texture2D _appliedTexture;
        private int       _appliedCap = -1;

        // Preview view transform (shared by both panes)
        private float   _zoom = 1f;
        private Vector2 _viewCenter = new Vector2(0.5f, 0.5f);
        private const float MaxZoom = 32f;

        // Status
        public enum StatusType { Info, Success, Error, Warning }
        private IVisualElementScheduledItem _statusResetSchedule;

        // Version / update check
        // ⚠ フィールド初期化子で NormalmapGeneratorVersion.Current を呼ばないこと。
        //    ScriptableObject のコンストラクタから GUIDToAssetPath が呼ばれ UnityException になる。
        //    実際のローカル版は CreateGUI 後の LoadVersionResultFromSessionState() で解決する。
        private DennokoVersionChecker.Result _versionResult = new DennokoVersionChecker.Result
        {
            State = DennokoVersionChecker.State.Checking,
            LocalVersion = "0.0.0",
        };

        // Localization
        private Dictionary<string, string> _locDict = new Dictionary<string, string>();
        private int _langIndex = 1;
        private readonly string[] _languages = { "en", "ja" };
        private const string PrefKeyLang = "NormalMapGenerator_Lang";

        // ── UI references ────────────────────────────────────────────────────
        private Label  _previewTitle, _previewCapLabel, _previewInputCaption, _previewHint;
        private Label  _inputTitle, _settingsTitle, _outputTitle,
                       _computeWarning, _statusLabel;
        private Label  _inputPlaceholder, _outputPlaceholder, _versionLabel;
        private DropdownField _capDropdown;
        private Button _langEnButton, _langJaButton, _autoUpdateButton, _updateButton,
                       _bevelResetButton, _generateButton, _resetAllButton, _versionReloadButton;
        private ObjectField _inputField;
        private EnumField   _inputModeField, _normalTypeField, _profileField;
        private Slider      _thresholdSlider, _strengthSlider;
        private SliderInt   _bevelRadiusSlider;
        private Toggle      _invertMaskToggle, _ditherToggle, _bevelToggle, _overwriteToggle;
        private VisualElement _bevelContent, _inputCanvas, _outputCanvas;
        private IMGUIContainer _inputImgui, _outputImgui;

        [MenuItem("dennokoworks/Normalmap Generator")]
        public static void ShowWindow()
        {
            var win = GetWindow<NormalMapGeneratorWindow>();
            win.titleContent = new GUIContent("Normalmap Generator");
            win.minSize = new Vector2(560, 700);
        }

        // ── Lifecycle ────────────────────────────────────────────────────────
        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            _langIndex = EditorPrefs.GetInt(PrefKeyLang, 1);
            if (_langIndex < 0 || _langIndex >= _languages.Length) _langIndex = 1;
            _previewCap = EditorPrefs.GetInt(PrefKeyCap, 2048);
            if (System.Array.IndexOf(CapValues, _previewCap) < 0) _previewCap = 2048;
            LoadLocalization(_languages[_langIndex]);
            LoadComputeShader();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            ReleasePreviewContext();
        }

        private void OnDestroy()
        {
            ReleasePreviewContext();
        }

        private void ReleasePreviewContext()
        {
            if (_previewCtx == null) return;
            _previewCtx.Dispose();
            _previewCtx = null;
        }

        // ── CreateGUI ────────────────────────────────────────────────────────
        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;

            // テーマ非依存のためのルートクラス。USS 変数の定義元でもある
            root.AddToClassList("dennoko-root");
            // USS ロード失敗時も背景が明るくならないよう Surface0 を C# 側でも保証
            root.style.backgroundColor = (Color)new Color32(0x12, 0x12, 0x12, 0xFF);
            root.style.flexGrow = 1;

            // 標準フォント (OS のメイリオ)。生成・保護・再適用は DennokoUIFont に集約
            DennokoUIFont.Apply(root);

            AddStyleSheet(root, THEME_USS_GUID);
            AddStyleSheet(root, WINDOW_USS_GUID);

            string uxmlPath = AssetDatabase.GUIDToAssetPath(UXML_GUID);
            var uxml = string.IsNullOrEmpty(uxmlPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (uxml == null)
            {
                root.Add(new Label($"UXML Asset が見つかりません。GUID を確認してください: {UXML_GUID}"));
                return;
            }
            uxml.CloneTree(root);

            InitializeUI(root);
        }

        private void AddStyleSheet(VisualElement root, string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var uss = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (uss != null) root.styleSheets.Add(uss);
            else Debug.LogWarning($"[{nameof(NormalMapGeneratorWindow)}] USS が見つかりません。GUID を確認してください: {guid}");
        }

        // ── Binding ──────────────────────────────────────────────────────────
        private void InitializeUI(VisualElement root)
        {
            _statusLabel          = root.Q<Label>("status-label");
            _previewTitle         = root.Q<Label>("preview-title");
            _previewCapLabel      = root.Q<Label>("preview-cap-label");
            _previewInputCaption  = root.Q<Label>("preview-input-caption");
            _previewHint          = root.Q<Label>("preview-hint");
            _inputTitle           = root.Q<Label>("input-title");
            _settingsTitle        = root.Q<Label>("settings-title");
            _outputTitle          = root.Q<Label>("output-title");
            _computeWarning       = root.Q<Label>("compute-warning");
            _inputPlaceholder     = root.Q<Label>("preview-input-placeholder");
            _outputPlaceholder    = root.Q<Label>("preview-output-placeholder");
            _versionLabel         = root.Q<Label>("version-label");

            _capDropdown         = root.Q<DropdownField>("preview-cap-dropdown");
            _versionReloadButton = root.Q<Button>("version-reload-button");
            _langEnButton      = root.Q<Button>("lang-en");
            _langJaButton      = root.Q<Button>("lang-ja");
            _autoUpdateButton  = root.Q<Button>("auto-update-button");
            _updateButton      = root.Q<Button>("update-button");
            _bevelResetButton  = root.Q<Button>("bevel-reset");
            _generateButton    = root.Q<Button>("generate-button");
            _resetAllButton    = root.Q<Button>("reset-all-button");

            _inputField        = root.Q<ObjectField>("input-texture-field");
            _inputModeField    = root.Q<EnumField>("input-mode-field");
            _normalTypeField   = root.Q<EnumField>("normal-type-field");
            _profileField      = root.Q<EnumField>("profile-field");
            _thresholdSlider   = root.Q<Slider>("threshold-slider");
            _strengthSlider    = root.Q<Slider>("strength-slider");
            _bevelRadiusSlider = root.Q<SliderInt>("bevel-radius-slider");
            _invertMaskToggle  = root.Q<Toggle>("invert-mask-toggle");
            _ditherToggle      = root.Q<Toggle>("dither-toggle");
            _bevelToggle       = root.Q<Toggle>("bevel-toggle");
            _overwriteToggle   = root.Q<Toggle>("overwrite-toggle");

            _bevelContent  = root.Q<VisualElement>("bevel-content");
            _inputCanvas   = root.Q<VisualElement>("preview-input-canvas");
            _outputCanvas  = root.Q<VisualElement>("preview-output-canvas");
            _inputImgui    = root.Q<IMGUIContainer>("preview-input-imgui");
            _outputImgui   = root.Q<IMGUIContainer>("preview-output-imgui");

            // EnumField は UXML では型を持たないため、ここで初期化する
            _inputModeField.Init(_settings.InputMode);
            _normalTypeField.Init(_settings.NormalMapType);
            _profileField.Init(_settings.ProfileType);

            _capDropdown.choices = new List<string>(CapLabels);

            // ── プレビュー ──
            _inputImgui.onGUIHandler  = () => DrawPreviewTexture(_inputImgui, _inputTexture);
            _outputImgui.onGUIHandler = () => DrawPreviewTexture(_outputImgui, _previewCtx?.Result);
            BindSquareAspect(_inputCanvas);
            BindSquareAspect(_outputCanvas);
            BindPreviewNavigation(_inputCanvas);
            BindPreviewNavigation(_outputCanvas);

            _capDropdown.RegisterValueChangedCallback(_ =>
            {
                int i = Mathf.Clamp(_capDropdown.index, 0, CapValues.Length - 1);
                _previewCap = CapValues[i];
                EditorPrefs.SetInt(PrefKeyCap, _previewCap);
                MarkPreviewDirty();
            });

            _autoUpdateButton.clicked += () =>
            {
                _autoUpdatePreview = !_autoUpdatePreview;
                RefreshAutoUpdateButton();
                if (_autoUpdatePreview) MarkPreviewDirty();
            };

            _updateButton.clicked += () =>
            {
                // Explicit refresh also re-reads the source, so a re-imported
                // texture is picked up even though its instance ID is unchanged.
                _previewCtx?.Invalidate();
                _previewDirty = false;
                UpdatePreview();
            };

            // ── 言語 ──
            _langEnButton.clicked += () => SetLanguage(0);
            _langJaButton.clicked += () => SetLanguage(1);

            // ── バージョン ──
            _versionReloadButton.clicked += () =>
            {
                NormalmapGeneratorVersion.ForceRecheck();
                LoadVersionResultFromSessionState();   // 即座に「確認中...」表示へ
            };

            // ── 入力 ──
            _inputField.objectType = typeof(Texture2D);
            _inputField.RegisterValueChangedCallback(evt =>
            {
                _inputTexture = evt.newValue as Texture2D;
                RefreshActionState();
                MarkPreviewDirty();
                RefreshPreviewCanvases();
            });

            // ── 設定 ──
            _inputModeField.RegisterValueChangedCallback(evt =>
            {
                _settings.InputMode = (InputMode)evt.newValue;
                MarkPreviewDirty();
            });
            _thresholdSlider.RegisterValueChangedCallback(evt =>
            {
                _settings.Threshold = evt.newValue;
                MarkPreviewDirty();
            });
            _invertMaskToggle.RegisterValueChangedCallback(evt =>
            {
                _settings.InvertMask = evt.newValue;
                MarkPreviewDirty();
            });
            _strengthSlider.RegisterValueChangedCallback(evt =>
            {
                _settings.Strength = evt.newValue;
                MarkPreviewDirty();
            });
            _normalTypeField.RegisterValueChangedCallback(evt =>
            {
                _settings.NormalMapType = (NormalMapType)evt.newValue;
                MarkPreviewDirty();
            });
            _ditherToggle.RegisterValueChangedCallback(evt =>
            {
                _settings.Dither = evt.newValue;
                MarkPreviewDirty();
            });

            // ── ベベル (トグル付きセクション) ──
            _bevelToggle.RegisterValueChangedCallback(evt =>
            {
                _settings.DisableBevel = !evt.newValue;
                _bevelContent.SetEnabled(evt.newValue);
                MarkPreviewDirty();
            });
            _bevelContent.SetEnabled(!_settings.DisableBevel);

            _bevelRadiusSlider.RegisterValueChangedCallback(evt =>
            {
                _settings.BevelRadius = evt.newValue;
                MarkPreviewDirty();
            });
            _profileField.RegisterValueChangedCallback(evt =>
            {
                _settings.ProfileType = (ProfileType)evt.newValue;
                MarkPreviewDirty();
            });
            _bevelResetButton.clicked += () =>
            {
                var def = new NormalMapSettings();
                _settings.BevelRadius = def.BevelRadius;
                _settings.ProfileType = def.ProfileType;
                _bevelRadiusSlider.value = Mathf.RoundToInt(_settings.BevelRadius);
                _profileField.value      = _settings.ProfileType;
            };

            // ── 出力 ──
            _overwriteToggle.RegisterValueChangedCallback(evt =>
                _settings.OverwriteExisting = evt.newValue);
            _generateButton.clicked += GenerateNormalMap;
            _resetAllButton.clicked += () =>
            {
                if (EditorUtility.DisplayDialog(
                    L("ResetConfirmTitle"), L("ResetConfirmMsg"), L("Yes"), L("No")))
                    ResetAll();
            };

            SyncUIFromSettings();
            ApplyLocalization();
            RefreshLanguageButtons();
            RefreshAutoUpdateButton();
            RefreshActionState();
            RefreshPreviewCanvases();
            SetStatus("Ready", StatusType.Info);
            MarkPreviewDirty();
            StartVersionCheck();
        }

        /// <summary>設定値を UI コントロールへ反映する（リセット時・初期化時）。</summary>
        private void SyncUIFromSettings()
        {
            _inputField.SetValueWithoutNotify(_inputTexture);
            _capDropdown.index       = Mathf.Max(0, System.Array.IndexOf(CapValues, _previewCap));
            _inputModeField.value    = _settings.InputMode;
            _thresholdSlider.value   = _settings.Threshold;
            _invertMaskToggle.value  = _settings.InvertMask;
            _strengthSlider.value    = _settings.Strength;
            _normalTypeField.value   = _settings.NormalMapType;
            _ditherToggle.value      = _settings.Dither;
            _bevelToggle.value       = !_settings.DisableBevel;
            _bevelRadiusSlider.value = Mathf.RoundToInt(_settings.BevelRadius);
            _profileField.value      = _settings.ProfileType;
            _overwriteToggle.value   = _settings.OverwriteExisting;
            _bevelContent.SetEnabled(!_settings.DisableBevel);
        }

        // ── Localization ─────────────────────────────────────────────────────
        private void LoadLocalization(string lang)
        {
            _locDict.Clear();
            string scriptDir = GetScriptDirectory();
            string path = $"{scriptDir}/Localization/{lang}.json";
            TextAsset ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (ta != null)
            {
                var data = JsonUtility.FromJson<LocData>(ta.text);
                if (data?.entries != null)
                    foreach (var pair in data.entries)
                        if (!string.IsNullOrEmpty(pair.key))
                            _locDict[pair.key] = pair.value;
            }
        }

        private string GetScriptDirectory()
        {
            MonoScript ms = MonoScript.FromScriptableObject(this);
            if (ms != null)
            {
                string scriptPath = AssetDatabase.GetAssetPath(ms);
                if (!string.IsNullOrEmpty(scriptPath))
                {
                    return System.IO.Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
                }
            }
            return "Assets/dennokoworks/Normalmap_generator/Editor";
        }

        private string L(string key) =>
            _locDict.TryGetValue(key, out string val) ? val : key;

        private void SetLanguage(int index)
        {
            if (index < 0 || index >= _languages.Length || index == _langIndex) return;
            _langIndex = index;
            EditorPrefs.SetInt(PrefKeyLang, _langIndex);
            LoadLocalization(_languages[_langIndex]);
            ApplyLocalization();
            RefreshLanguageButtons();
        }

        /// <summary>ローカライズ文字列を全コントロールへ流し込む。</summary>
        private void ApplyLocalization()
        {
            if (_statusLabel == null) return;

            _previewTitle.text        = L("PreviewHeader");
            _previewCapLabel.text     = L("PreviewRes");
            _autoUpdateButton.text    = L("AutoUpdate");
            _updateButton.text        = L("Update");
            _previewInputCaption.text = L("InputHeader");

            _inputTitle.text    = L("InputHeader");
            _inputField.label   = L("MaskTexture");

            _settingsTitle.text      = L("SettingsHeader");
            _inputModeField.label    = L("InputMode");
            _thresholdSlider.label   = L("Threshold");
            _invertMaskToggle.label  = L("InvertMask");
            _strengthSlider.label    = L("Strength");
            _normalTypeField.label   = L("NormalType");
            _ditherToggle.label      = L("Dither");

            _bevelToggle.text        = L("BevelHeader");
            _bevelRadiusSlider.label = L("BevelRadius");
            _profileField.label      = L("Profile");

            _outputTitle.text     = L("OutputHeader");
            _overwriteToggle.text = L("OverwriteIfSameName");
            _generateButton.text  = L("GenerateBtn");
            _resetAllButton.text  = L("ResetAll");
            _computeWarning.text  = L("ComputeShaderNotFound");

            _versionReloadButton.tooltip = L("VersionRecheck");
            ApplyVersionLabel();   // 接尾辞 (更新あり/確認中/取得失敗) も言語に追従させる

            UpdatePreviewHint();
        }

        private void RefreshLanguageButtons()
        {
            _langEnButton.EnableInClassList("dennoko-button-active", _langIndex == 0);
            _langJaButton.EnableInClassList("dennoko-button-active", _langIndex == 1);
        }

        private void RefreshAutoUpdateButton()
        {
            _autoUpdateButton.EnableInClassList("dennoko-button-active", _autoUpdatePreview);
        }

        private void RefreshActionState()
        {
            bool computeMissing = _computeShader == null;
            _computeWarning.EnableInClassList("nmg-hidden", !computeMissing);
            _generateButton.SetEnabled(_inputTexture != null && _processor != null);
        }

        // ── Version / update check ───────────────────────────────────────────

        private void StartVersionCheck()
        {
            LoadVersionResultFromSessionState();
            // 取得の要否は StartCheckBackgroundTask 内で判定する（成功済みなら何もしない／
            // 前回エラーなら再試行）。開き直すたびに一時的な失敗から自己回復できる。
            NormalmapGeneratorVersion.StartCheckBackgroundTask();
        }

        /// <summary>
        /// セッションに保存された取得結果からラベル表示を組み立てる。
        /// State（更新有無）はキャッシュせず、常に「現在のローカル版 vs 取得済みの最新版」で
        /// 再計算する。こうしないとローカル版が後から解決された場合に矛盾表示が残る。
        /// </summary>
        internal void LoadVersionResultFromSessionState()
        {
            string local  = NormalmapGeneratorVersion.Current;
            string latest = SessionState.GetString(NormalmapGeneratorVersion.VerCheckLatestKey, string.Empty);
            bool   done   = SessionState.GetBool(NormalmapGeneratorVersion.VerCheckDoneKey, false);
            bool   error  = SessionState.GetBool(NormalmapGeneratorVersion.VerCheckErrorKey, false);

            DennokoVersionChecker.State state;
            if (!done)
                state = DennokoVersionChecker.State.Checking;
            else if (error || string.IsNullOrEmpty(latest))
                state = DennokoVersionChecker.State.Error;
            else if (DennokoVersionChecker.IsUpdateAvailable(latest, local))
                state = DennokoVersionChecker.State.UpdateAvailable;
            else
                state = DennokoVersionChecker.State.UpToDate;

            _versionResult = new DennokoVersionChecker.Result
            {
                State         = state,
                LocalVersion  = local,
                LatestVersion = latest,
                Url           = SessionState.GetString(NormalmapGeneratorVersion.VerCheckUrlKey, string.Empty),
                Message       = SessionState.GetString(NormalmapGeneratorVersion.VerCheckMessageKey, string.Empty),
            };
            ApplyVersionLabel();
        }

        private void ApplyVersionLabel()
        {
            if (_versionLabel == null) return;

            var r = _versionResult;
            string baseText = "v" + r.LocalVersion;
            string text;
            bool update = false, error = false;
            switch (r.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                    text = baseText + "  " + string.Format(L("VersionUpdateAvailable"), r.LatestVersion);
                    update = true;
                    break;
                case DennokoVersionChecker.State.Error:
                    text = baseText + "  " + L("VersionCheckFailed");
                    error = true;
                    break;
                case DennokoVersionChecker.State.Checking:
                    text = baseText + "  " + L("VersionChecking");
                    break;
                default: // UpToDate
                    text = baseText;
                    break;
            }
            _versionLabel.text = text;
            _versionLabel.EnableInClassList("dennoko-version-label--update", update);
            _versionLabel.EnableInClassList("dennoko-version-label--error", error);
        }

        // ── ComputeShader ────────────────────────────────────────────────────
        private void LoadComputeShader()
        {
            string[] guids = AssetDatabase.FindAssets("NormalMapGenerator t:ComputeShader");
            if (guids.Length == 0)
            {
                Debug.LogWarning("[NormalMapGenerator] ComputeShader 'NormalMapGenerator.compute' not found.");
                return;
            }
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            _computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (_computeShader != null)
                _processor = new NormalMapProcessor(_computeShader);
        }

        // ── Debounce ─────────────────────────────────────────────────────────
        private void OnEditorUpdate()
        {
            if (!_previewDirty) return;
            if (EditorApplication.timeSinceStartup - _lastChangeTime <= _debounce) return;

            _previewDirty = false;
            if (_autoUpdatePreview) UpdatePreview();
        }

        /// <summary>
        /// プレビュー再生成を予約する。デバウンス時間は、距離場を作り直す必要が
        /// あるか（heavy）どうかで切り替える。
        /// </summary>
        private void MarkPreviewDirty()
        {
            _previewDirty   = true;
            _lastChangeTime = EditorApplication.timeSinceStartup;
            _debounce       = IsHeavyChange() ? HeavyDebounceSeconds : LightDebounceSeconds;
        }

        /// <summary>True when the pending change invalidates the distance field.</summary>
        private bool IsHeavyChange()
        {
            if (_appliedSnapshot == null) return true;
            if (_appliedTexture != _inputTexture) return true;
            if (_appliedCap != _previewCap) return true;
            if (_appliedSnapshot.Threshold  != _settings.Threshold)  return true;
            if (_appliedSnapshot.InvertMask != _settings.InvertMask) return true;
            // A larger radius needs a wider jump-flood range; a smaller one reuses it.
            if (_settings.BevelRadius > _appliedSnapshot.BevelRadius) return true;
            return false;
        }

        // ── Preview rendering ────────────────────────────────────────────────

        /// <summary>
        /// IMGUIContainer の中身。テクスチャの描画だけを行い、背景・枠線は USS が持つ。
        /// UI Toolkit にはテクスチャを UV 指定で描く手段がないためここだけ IMGUI を使う。
        /// </summary>
        private void DrawPreviewTexture(IMGUIContainer container, Texture tex)
        {
            if (tex == null || Event.current.type != EventType.Repaint) return;

            Rect local = container.contentRect;
            if (local.width < 1f || local.height < 1f) return;

            Rect area = new Rect(0f, 0f, local.width, local.height);
            Rect fit  = FitRect(area, (float)tex.width / Mathf.Max(1, tex.height));
            GUI.DrawTextureWithTexCoords(fit, tex, ComputeUvRect(), true);
        }

        /// <summary>プレビュー領域を正方形に保つ（USS にアスペクト比指定がないため）。</summary>
        private static void BindSquareAspect(VisualElement canvas)
        {
            canvas.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float w = evt.newRect.width;
                if (w < 1f) return;

                // 解決後の高さ (max-height でクランプされうる) ではなく、
                // 自分が最後に設定した値と比べてループを防ぐ。
                float applied = canvas.style.height.keyword == StyleKeyword.Null
                    ? -1f
                    : canvas.style.height.value.value;
                if (Mathf.Abs(applied - w) < 0.5f) return;

                canvas.style.height = w;
            });
        }

        /// <summary>ホイールズーム / ドラッグパン / ダブルクリックリセット。</summary>
        private void BindPreviewNavigation(VisualElement canvas)
        {
            // TrickleDown で登録し、子の IMGUIContainer や親の ScrollView より先に処理する
            canvas.RegisterCallback<WheelEvent>(evt =>
            {
                float before = _zoom;
                float step   = Mathf.Clamp(evt.delta.y, -3f, 3f);
                _zoom = Mathf.Clamp(_zoom * Mathf.Exp(-step * 0.15f), 1f, MaxZoom);
                if (!Mathf.Approximately(before, _zoom)) RefreshPreviewCanvases();
                evt.StopPropagation();   // ScrollView 側のスクロールを抑止する
            }, TrickleDown.TrickleDown);

            canvas.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.clickCount >= 2)
                {
                    _zoom = 1f;
                    _viewCenter = new Vector2(0.5f, 0.5f);
                    RefreshPreviewCanvases();
                }
                else if (evt.button == 0 || evt.button == 2)
                {
                    canvas.CapturePointer(evt.pointerId);
                }
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);

            canvas.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!canvas.HasPointerCapture(evt.pointerId) || _zoom <= 1f) return;

                Rect r = canvas.contentRect;
                if (r.width < 1f || r.height < 1f) return;

                // 画面 y は下向き、UV y は上向き
                _viewCenter.x -= evt.deltaPosition.x / r.width  / _zoom;
                _viewCenter.y += evt.deltaPosition.y / r.height / _zoom;
                RefreshPreviewCanvases();
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);

            canvas.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (canvas.HasPointerCapture(evt.pointerId))
                    canvas.ReleasePointer(evt.pointerId);
            }, TrickleDown.TrickleDown);
        }

        /// <summary>2 つのプレビュー面とプレースホルダー・情報行を描き直す。</summary>
        private void RefreshPreviewCanvases()
        {
            if (_inputImgui == null) return;

            _inputImgui.MarkDirtyRepaint();
            _outputImgui.MarkDirtyRepaint();
            _inputPlaceholder.EnableInClassList("nmg-hidden", _inputTexture != null);
            _outputPlaceholder.EnableInClassList("nmg-hidden", _previewCtx?.Result != null);
            UpdatePreviewHint();
        }

        private void UpdatePreviewHint()
        {
            if (_previewHint == null) return;

            string text;
            bool warning = false;
            if (_inputTexture == null)      text = L("AssignInputTex");
            else if (_processor == null) { text = L("ComputeShaderNotLoaded"); warning = true; }
            else                            text = BuildPreviewInfo();

            _previewHint.text = text;
            _previewHint.EnableInClassList("dennoko-text-warning", warning);
        }

        private string BuildPreviewInfo()
        {
            if (_previewW == 0) return L("PreviewPending");

            string res = $"{_previewW}×{_previewH}";
            string src = $"{L("PreviewSource")} {_inputTexture.width}×{_inputTexture.height}";
            string mode = _previewScale >= 0.999f
                ? L("PreviewNative")
                : $"{Mathf.RoundToInt(_previewScale * 100f)}%";
            string zoom = _zoom > 1.001f ? $" / {L("PreviewZoom")} {_zoom:0.#}×" : "";
            return $"{res}  ({src}, {mode}){zoom}";
        }

        /// <summary>Largest rect inside <paramref name="outer"/> with the given w/h aspect.</summary>
        private static Rect FitRect(Rect outer, float aspect)
        {
            float outerAspect = outer.width / Mathf.Max(1f, outer.height);
            if (aspect > outerAspect)
            {
                float fh = outer.width / aspect;
                return new Rect(outer.x, outer.y + (outer.height - fh) * 0.5f, outer.width, fh);
            }
            float fw = outer.height * aspect;
            return new Rect(outer.x + (outer.width - fw) * 0.5f, outer.y, fw, outer.height);
        }

        /// <summary>UV window for the current zoom/pan, clamped to stay inside the texture.</summary>
        private Rect ComputeUvRect()
        {
            float size = 1f / _zoom;
            float half = size * 0.5f;
            float cx = Mathf.Clamp(_viewCenter.x, half, 1f - half);
            float cy = Mathf.Clamp(_viewCenter.y, half, 1f - half);
            _viewCenter = new Vector2(cx, cy);
            return new Rect(cx - half, cy - half, size, size);
        }

        // ── Status bar ───────────────────────────────────────────────────────

        /// <summary>ステータスを表示する。Info 以外は 3 秒後に Ready へ自動復帰。</summary>
        private void SetStatus(string message, StatusType type, long autoResetMs = 3000)
        {
            if (_statusLabel == null) return;

            _statusLabel.text = message;
            _statusLabel.EnableInClassList("dennoko-status--success", type == StatusType.Success);
            _statusLabel.EnableInClassList("dennoko-status--error",   type == StatusType.Error);
            _statusLabel.EnableInClassList("dennoko-status--warning", type == StatusType.Warning);

            _statusResetSchedule?.Pause();
            if (type != StatusType.Info)
            {
                _statusResetSchedule = _statusLabel.schedule
                    .Execute(() => SetStatus("Ready", StatusType.Info))
                    .StartingIn(autoResetMs);
            }
        }

        // ── Actions ──────────────────────────────────────────────────────────
        private void GenerateNormalMap()
        {
            if (_inputTexture == null || _processor == null) return;
            try
            {
                EditorUtility.DisplayProgressBar("Normalmap Generator", L("GenerateProcessing"), 0.0f);
                SaveResult result = _processor.ProcessAndSave(_inputTexture, _settings);
                if (result == SaveResult.Saved)
                {
                    SetStatus(L("GenerateSuccess"), StatusType.Success);
                }
                else if (result == SaveResult.Skipped)
                {
                    SetStatus(L("GenerateSkipped"), StatusType.Warning);
                }
                else
                {
                    SetStatus(L("GenerateFailed"), StatusType.Error);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NormalMapGenerator] Error: {ex.Message}\n{ex.StackTrace}");
                SetStatus($"Error: {ex.Message}", StatusType.Error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            UpdatePreview();
        }

        private void ResetAll()
        {
            _settings   = new NormalMapSettings();
            _zoom       = 1f;
            _viewCenter = new Vector2(0.5f, 0.5f);
            SyncUIFromSettings();
            RefreshPreviewCanvases();
            SetStatus("Reset.", StatusType.Info);
            MarkPreviewDirty();
        }

        // ── Preview update ───────────────────────────────────────────────────
        private void UpdatePreview()
        {
            if (_inputTexture == null || _processor == null)
            {
                RefreshPreviewCanvases();
                return;
            }

            _previewCtx ??= new PipelineContext(generateMips: true);

            int srcW = _inputTexture.width;
            int srcH = _inputTexture.height;
            int srcMax = Mathf.Max(srcW, srcH);

            // Source at or below the cap runs at native resolution, which makes
            // scale exactly 1 and the preview identical to the saved output.
            int   targetMax = Mathf.Min(srcMax, _previewCap);
            float scale     = (float)targetMax / Mathf.Max(1, srcMax);

            int prevW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int prevH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            try
            {
                _processor.Run(_previewCtx, _inputTexture,
                               ScaleForPreview(_settings, scale), prevW, prevH);

                _previewW     = prevW;
                _previewH     = prevH;
                _previewScale = scale;

                _appliedSnapshot = _settings.Clone();
                _appliedTexture  = _inputTexture;
                _appliedCap      = _previewCap;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NormalMapGenerator] Preview error: {ex.Message}\n{ex.StackTrace}");
                SetStatus($"Preview error: {ex.Message}", StatusType.Error);
            }

            RefreshPreviewCanvases();
        }

        /// <summary>
        /// Adapts pixel-denominated settings to a reduced working resolution.
        /// Both values stay float so a small radius or a strength of 1 keeps its
        /// proportion instead of collapsing to the integer minimum.
        /// At scale 1 this is the identity.
        /// </summary>
        private static NormalMapSettings ScaleForPreview(NormalMapSettings src, float scale)
        {
            if (scale >= 0.9999f) return src;

            var s = src.Clone();
            s.BevelRadius = src.BevelRadius * scale;
            s.Strength    = src.Strength * scale;
            return s;
        }
    }

    [System.Serializable]
    public class LocData
    {
        public List<LocPair> entries;
    }

    [System.Serializable]
    public class LocPair
    {
        public string key;
        public string value;
    }
}
