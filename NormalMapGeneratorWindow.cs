using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace NormalmapGenerator
{
    public class NormalMapGeneratorWindow : EditorWindow
    {
        // ── State ────────────────────────────────────────────────────────────
        private Texture2D         _inputTexture;
        private NormalMapSettings _settings = new NormalMapSettings();
        private NormalMapProcessor _processor;
        private ComputeShader     _computeShader;

        // Preview
        private RenderTexture _previewNormalRT;
        private bool          _previewDirty;
        private double        _lastChangeTime;
        private const double  DebounceSeconds = 0.6;
        private float         _lastWindowWidth;
        private const int     MaxPreviewSize = 4096;
        private bool          _autoUpdatePreview = true;

        // Scroll
        private Vector2 _scroll;

        // Status
        public enum StatusType { Info, Success, Error, Warning }
        private string     _statusMessage   = "Ready";
        private StatusType _statusType      = StatusType.Info;
        private double     _statusResetTime = -1.0;

        // Section toggles (bevel enable state is derived from _settings.DisableBevel)

        // Localization
        private Dictionary<string, string> _locDict = new Dictionary<string, string>();
        private int _langIndex = 1;
        private readonly string[] _languages = { "en", "ja" };

        [MenuItem("dennokoworks/Normalmap Generator")]
        public static void ShowWindow()
        {
            var win = GetWindow<NormalMapGeneratorWindow>("Normalmap Generator");
            win.minSize = new Vector2(560, 700);
        }

        // ── Lifecycle ────────────────────────────────────────────────────────
        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            _langIndex = EditorPrefs.GetInt("NormalMapGenerator_Lang", 1);
            if (_langIndex < 0 || _langIndex >= _languages.Length) _langIndex = 1;
            LoadLocalization(_languages[_langIndex]);
            LoadComputeShader();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            ReleasePreviewRT();
        }

        private void OnDestroy()
        {
            ReleasePreviewRT();
        }

        // ── Localization ─────────────────────────────────────────────────────
        private void LoadLocalization(string lang)
        {
            _locDict.Clear();
            string path = $"Assets/Editor/Normalmap_generator/Localization/{lang}.json";
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

        private string L(string key) =>
            _locDict.TryGetValue(key, out string val) ? val : key;

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

        // ── Debounce / Status Reset ───────────────────────────────────────────
        private void OnEditorUpdate()
        {
            if (_previewDirty && EditorApplication.timeSinceStartup - _lastChangeTime > DebounceSeconds)
            {
                _previewDirty = false;
                if (_autoUpdatePreview)
                {
                    UpdatePreview();
                    Repaint();
                }
            }

            if (_statusResetTime > 0 && EditorApplication.timeSinceStartup > _statusResetTime)
            {
                _statusMessage   = "Ready";
                _statusType      = StatusType.Info;
                _statusResetTime = -1.0;
                Repaint();
            }
        }

        // ── OnGUI ────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            float currentWidth = position.width;
            if (Mathf.Abs(currentWidth - _lastWindowWidth) > 1f)
            {
                _lastWindowWidth = currentWidth;
                _previewDirty    = true;
                _lastChangeTime  = EditorApplication.timeSinceStartup;
            }

            NormalmapTheme.Initialize();

            // ウィンドウ全面に Surface0 を塗る
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), NormalmapTheme.Surface0);

            EditorGUI.BeginChangeCheck();

            DrawHeader();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPreviewArea();
            DrawInputSection();
            DrawSettingsSection();
            DrawBevelSection();
            GUILayout.Space(4);
            EditorGUILayout.EndScrollView();

            DrawFooter();
            DrawStatusBar();

            if (EditorGUI.EndChangeCheck())
            {
                _previewDirty   = true;
                _lastChangeTime = EditorApplication.timeSinceStartup;
            }
        }

        // ── Header ───────────────────────────────────────────────────────────
        private void DrawHeader()
        {
            // ヘッダー領域の確保
            Rect headerRect = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
            
            // 背景描画（ノーマルマップらしい青紫）
            EditorGUI.DrawRect(headerRect, new Color(128/255f, 128/255f, 1f));

            string titleText = "Normalmap Generator";

            // タイトルスタイル（白文字）
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };

            // 縁取りスタイル（黒文字）
            GUIStyle outlineStyle = new GUIStyle(titleStyle)
            {
                normal = { textColor = Color.black }
            };

            // 縁取りの描画（8方向オフセット）
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0) continue;
                    Rect offRect = headerRect;
                    offRect.x += x;
                    offRect.y += y;
                    GUI.Label(offRect, titleText, outlineStyle);
                }
            }

            // メインタイトルの描画
            GUI.Label(headerRect, titleText, titleStyle);

            // 言語切り替えツールバー（背景の上に重ねる）
            Rect toolbarRect = new Rect(headerRect.xMax - 80, headerRect.y + 11, 72, 20);
            EditorGUI.BeginChangeCheck();
            _langIndex = GUI.Toolbar(toolbarRect, _langIndex, new[] { "EN", "JA" }, EditorStyles.miniButton);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt("NormalMapGenerator_Lang", _langIndex);
                LoadLocalization(_languages[_langIndex]);
            }

            // セパレータはタイトルのすぐ下に
            DrawSeparator();
        }

        // ── Preview ──────────────────────────────────────────────────────────
        private void DrawPreviewArea()
        {
            GUILayout.BeginVertical(NormalmapTheme.CardOuterStyle);

            // ツールバー行
            GUILayout.BeginHorizontal(NormalmapTheme.ToolbarStyle);
            GUILayout.Label(L("PreviewHeader"), NormalmapTheme.SectionHeaderStyle);
            GUILayout.FlexibleSpace();
            _autoUpdatePreview = GUILayout.Toggle(_autoUpdatePreview,
                L("AutoUpdate"), EditorStyles.miniButton);
            GUILayout.Space(4);
            if (GUILayout.Button(L("Update"), EditorStyles.toolbarButton))
            {
                UpdatePreview();
                Repaint();
            }
            GUILayout.Space(2);
            GUILayout.EndHorizontal();

            // プレビュー画像
            float availableWidth = EditorGUIUtility.currentViewWidth - 40f;
            float cellWidth  = (availableWidth - 16f) * 0.5f;
            float cellHeight = cellWidth;

            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            DrawTexturePreview(L("InputHeader"), _inputTexture, cellWidth, cellHeight);
            GUILayout.Space(8);
            DrawTexturePreview("Normal Map", _previewNormalRT, cellWidth, cellHeight);
            GUILayout.Space(4);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (_inputTexture == null)
                DrawHintLabel(L("AssignInputTex"), NormalmapTheme.TextTertiary);
            else if (_processor == null)
                DrawHintLabel(L("ComputeShaderNotLoaded"), NormalmapTheme.SemanticWarning);

            GUILayout.Space(4);
            GUILayout.EndVertical();
        }

        private void DrawTexturePreview(string label, Texture tex, float w, float h)
        {
            GUILayout.BeginVertical(GUILayout.Width(w));
            GUILayout.Label(label, NormalmapTheme.CaptionStyle, GUILayout.Width(w));
            Rect rect = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            if (tex != null)
            {
                EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(rect, NormalmapTheme.Surface0);
                var centered = new GUIStyle(NormalmapTheme.CaptionStyle)
                    { alignment = TextAnchor.MiddleCenter };
                GUI.Label(rect, "—", centered);
            }
            GUILayout.EndVertical();
        }

        private void DrawHintLabel(string text, Color color)
        {
            var style = new GUIStyle(NormalmapTheme.CaptionStyle)
                { normal = { textColor = color } };
            GUILayout.BeginHorizontal();
            GUILayout.Space(8);
            GUILayout.Label(text, style);
            GUILayout.EndHorizontal();
        }

        // ── Settings Sections ────────────────────────────────────────────────
        private void DrawInputSection()
        {
            DrawSection(L("InputHeader"), () =>
            {
                var prev = _inputTexture;
                _inputTexture = (Texture2D)EditorGUILayout.ObjectField(
                    L("MaskTexture"), _inputTexture, typeof(Texture2D), false);
                if (_inputTexture != prev)
                {
                    _previewDirty   = true;
                    _lastChangeTime = EditorApplication.timeSinceStartup;
                }
            });
        }

        private void DrawSettingsSection()
        {
            DrawSection(L("SettingsHeader"), () =>
            {
                _settings.InputMode = (InputMode)EditorGUILayout.EnumPopup(
                    L("InputMode"), _settings.InputMode);
                EditorGUILayout.Space(2);
                _settings.Threshold = EditorGUILayout.Slider(
                    L("Threshold"), _settings.Threshold, 0f, 1f);
                _settings.InvertMask = EditorGUILayout.Toggle(
                    L("InvertMask"), _settings.InvertMask);
                EditorGUILayout.Space(4);
                _settings.Strength = EditorGUILayout.IntSlider(
                    L("Strength"), _settings.Strength, 1, 50);
                _settings.NormalMapType = (NormalMapType)EditorGUILayout.EnumPopup(
                    L("NormalType"), _settings.NormalMapType);
            });
        }

        private void DrawBevelSection()
        {
            bool bevelEnabled = !_settings.DisableBevel;
            DrawToggleSection(L("BevelHeader"), ref bevelEnabled, () =>
            {
                _settings.BevelRadius = EditorGUILayout.IntSlider(
                    L("BevelRadius"), _settings.BevelRadius, 1, 100);
                _settings.ProfileType = (ProfileType)EditorGUILayout.EnumPopup(
                    L("Profile"), _settings.ProfileType);
            }, onReset: () =>
            {
                var def = new NormalMapSettings();
                _settings.BevelRadius = def.BevelRadius;
                _settings.ProfileType = def.ProfileType;
            });
            _settings.DisableBevel = !bevelEnabled;
        }

        // ── Footer ───────────────────────────────────────────────────────────
        private void DrawFooter()
        {
            GUILayout.BeginVertical(NormalmapTheme.CardStyle);

            // 出力設定行
            GUILayout.BeginHorizontal();
            GUILayout.Label(L("OutputHeader"), NormalmapTheme.SectionHeaderStyle);
            GUILayout.FlexibleSpace();
            _settings.OverwriteExisting = EditorGUILayout.ToggleLeft(
                L("OverwriteIfSameName"), _settings.OverwriteExisting,
                NormalmapTheme.SecondaryTextStyle, GUILayout.Width(220));
            GUILayout.EndHorizontal();

            DrawSeparator();

            if (_computeShader == null)
            {
                var warnStyle = new GUIStyle(NormalmapTheme.CaptionStyle)
                    { normal = { textColor = NormalmapTheme.SemanticWarning } };
                GUILayout.Label(L("ComputeShaderNotFound"), warnStyle);
                EditorGUILayout.Space(4);
            }

            bool canGenerate = _inputTexture != null && _processor != null;
            using (new EditorGUI.DisabledGroupScope(!canGenerate))
            {
                if (GUILayout.Button(L("GenerateBtn"), NormalmapTheme.ActionButtonStyle))
                    GenerateNormalMap();
            }

            EditorGUILayout.Space(4);

            if (GUILayout.Button(L("ResetAll"), NormalmapTheme.SecondaryButtonStyle))
            {
                if (EditorUtility.DisplayDialog(
                    L("ResetConfirmTitle"), L("ResetConfirmMsg"), L("Yes"), L("No")))
                    ResetAll();
            }

            GUILayout.EndVertical();
        }

        // ── Status bar ───────────────────────────────────────────────────────
        private void DrawStatusBar()
        {
            var style = _statusType switch
            {
                StatusType.Success => NormalmapTheme.StatusSuccessStyle,
                StatusType.Error   => NormalmapTheme.StatusErrorStyle,
                StatusType.Warning => NormalmapTheme.StatusWarningStyle,
                _                  => NormalmapTheme.StatusInfoStyle,
            };
            GUILayout.Box(_statusMessage, style, GUILayout.ExpandWidth(true));
        }

        // ── Section helpers ──────────────────────────────────────────────────

        /// <summary>常時表示の設定セクション。</summary>
        private void DrawSection(string title, System.Action content)
        {
            GUILayout.BeginVertical(NormalmapTheme.CardStyle);
            GUILayout.Label(title, NormalmapTheme.SectionHeaderStyle);
            DrawSeparator();
            content?.Invoke();
            GUILayout.EndVertical();
        }

        /// <summary>
        /// ON/OFF トグル付きセクション。
        /// OFF 時もコンテンツは表示されグレーアウトされる（設定値が保持されていることを示す）。
        /// </summary>
        private void DrawToggleSection(string title, ref bool toggle,
            System.Action content, System.Action onReset = null)
        {
            GUILayout.BeginVertical(NormalmapTheme.CardStyle);

            GUILayout.BeginHorizontal();
            var headerStyle = toggle
                ? NormalmapTheme.ToggleSectionOnStyle
                : NormalmapTheme.ToggleSectionOffStyle;

            EditorGUI.BeginChangeCheck();
            bool newToggle = EditorGUILayout.ToggleLeft(
                title, toggle, headerStyle, GUILayout.ExpandWidth(true));
            if (EditorGUI.EndChangeCheck())
            {
                toggle = newToggle;
                Repaint();
            }

            if (onReset != null &&
                GUILayout.Button("Reset", NormalmapTheme.MiniButtonStyle, GUILayout.Width(50)))
            {
                onReset.Invoke();
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();

            DrawSeparator();

            using (new EditorGUI.DisabledGroupScope(!toggle))
                content?.Invoke();

            GUILayout.EndVertical();
        }

        /// <summary>Outline 色の 1px 横区切り線。</summary>
        private void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, NormalmapTheme.Outline);
            EditorGUILayout.Space(4);
        }

        // ── Generate ─────────────────────────────────────────────────────────
        private void GenerateNormalMap()
        {
            if (_inputTexture == null || _processor == null) return;
            try
            {
                EditorUtility.DisplayProgressBar("Normalmap Generator", L("GenerateProcessing"), 0.0f);
                _processor.ProcessAndSave(_inputTexture, _settings);
                SetStatus(L("GenerateSuccess"), StatusType.Success);
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
            Repaint();
        }

        private void ResetAll()
        {
            _settings = new NormalMapSettings();
            SetStatus("Reset.", StatusType.Info);
        }

        private void SetStatus(string message, StatusType type, double autoResetSeconds = 3.0)
        {
            _statusMessage   = message;
            _statusType      = type;
            _statusResetTime = type == StatusType.Info
                ? -1.0
                : EditorApplication.timeSinceStartup + autoResetSeconds;
            Repaint();
        }

        // ── Preview update ───────────────────────────────────────────────────
        private void UpdatePreview()
        {
            if (_inputTexture == null || _processor == null) return;

            ReleasePreviewRT();

            int srcW = _inputTexture.width;
            int srcH = _inputTexture.height;
            int targetRes = Mathf.Clamp(
                Mathf.RoundToInt(position.width * 0.5f), 256, MaxPreviewSize);

            int prevW, prevH;
            if (srcW >= srcH)
            {
                prevW = targetRes;
                prevH = Mathf.Max(1, Mathf.RoundToInt(targetRes * (float)srcH / srcW));
            }
            else
            {
                prevH = targetRes;
                prevW = Mathf.Max(1, Mathf.RoundToInt(targetRes * (float)srcW / srcH));
            }

            var previewIn = RenderTexture.GetTemporary(prevW, prevH, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(_inputTexture, previewIn);

            float scale = (float)Mathf.Max(prevW, prevH) / Mathf.Max(srcW, srcH);
            var previewSettings = ScaleForPreview(_settings, scale);

            try
            {
                _previewNormalRT = _processor.Process(previewIn, previewSettings);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NormalMapGenerator] Preview error: {ex.Message}");
            }
            finally
            {
                RenderTexture.ReleaseTemporary(previewIn);
            }
        }

        private static NormalMapSettings ScaleForPreview(NormalMapSettings src, float scale)
        {
            return new NormalMapSettings
            {
                InputMode         = src.InputMode,
                Threshold         = src.Threshold,
                BevelRadius       = Mathf.Max(1, Mathf.RoundToInt(src.BevelRadius * scale)),
                Strength          = Mathf.Max(1, Mathf.RoundToInt(src.Strength * scale)),
                ProfileType       = src.ProfileType,
                NormalMapType     = src.NormalMapType,
                InvertMask        = src.InvertMask,
                DisableBevel      = src.DisableBevel,
                OverwriteExisting = src.OverwriteExisting,
            };
        }

        private void ReleasePreviewRT()
        {
            if (_previewNormalRT != null)
            {
                _previewNormalRT.Release();
                _previewNormalRT = null;
            }
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
