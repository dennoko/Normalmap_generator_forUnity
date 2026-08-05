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

        // Change detection
        private NormalMapSettings _pendingSnapshot;   // last state seen by OnGUI
        private NormalMapSettings _appliedSnapshot;   // state the preview was built from
        private Texture2D _pendingTexture, _appliedTexture;
        private int       _pendingCap = -1, _appliedCap = -1;

        // Preview view transform (shared by both panes)
        private float   _zoom = 1f;
        private Vector2 _viewCenter = new Vector2(0.5f, 0.5f);
        private const float MaxZoom = 32f;

        // Scroll
        private Vector2 _scroll;

        // Status
        public enum StatusType { Info, Success, Error, Warning }
        private string     _statusMessage   = "Ready";
        private StatusType _statusType      = StatusType.Info;
        private double     _statusResetTime = -1.0;

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
            if (_previewDirty && EditorApplication.timeSinceStartup - _lastChangeTime > _debounce)
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
            NormalmapTheme.Initialize();

            // ウィンドウ全面に Surface0 を塗る
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), NormalmapTheme.Surface0);

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

            // The preview resolution no longer depends on the window size, so a
            // resize costs nothing and must not schedule a rebuild.
            DetectChanges();
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

            GUILayout.Label(L("PreviewRes"), NormalmapTheme.CaptionStyle);
            EditorGUI.BeginChangeCheck();
            int capIndex = System.Array.IndexOf(CapValues, _previewCap);
            capIndex = EditorGUILayout.Popup(capIndex < 0 ? 1 : capIndex, CapLabels, GUILayout.Width(60));
            if (EditorGUI.EndChangeCheck())
            {
                _previewCap = CapValues[capIndex];
                EditorPrefs.SetInt(PrefKeyCap, _previewCap);
            }

            GUILayout.Space(4);
            _autoUpdatePreview = GUILayout.Toggle(_autoUpdatePreview,
                L("AutoUpdate"), EditorStyles.miniButton);
            GUILayout.Space(4);
            if (GUILayout.Button(L("Update"), EditorStyles.toolbarButton))
            {
                // Explicit refresh also re-reads the source, so a re-imported
                // texture is picked up even though its instance ID is unchanged.
                _previewCtx?.Invalidate();
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
            DrawTexturePreview("Normal Map", _previewCtx?.Result, cellWidth, cellHeight);
            GUILayout.Space(4);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (_inputTexture == null)
                DrawHintLabel(L("AssignInputTex"), NormalmapTheme.TextTertiary);
            else if (_processor == null)
                DrawHintLabel(L("ComputeShaderNotLoaded"), NormalmapTheme.SemanticWarning);
            else
                DrawHintLabel(BuildPreviewInfo(), NormalmapTheme.TextTertiary);

            GUILayout.Space(4);
            GUILayout.EndVertical();
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

        private void DrawTexturePreview(string label, Texture tex, float w, float h)
        {
            GUILayout.BeginVertical(GUILayout.Width(w));
            GUILayout.Label(label, NormalmapTheme.CaptionStyle, GUILayout.Width(w));
            Rect rect = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));

            if (tex != null)
            {
                EditorGUI.DrawRect(rect, NormalmapTheme.Surface0);
                Rect fit = FitRect(rect, (float)tex.width / Mathf.Max(1, tex.height));
                GUI.DrawTextureWithTexCoords(fit, tex, ComputeUvRect(), true);
                HandlePreviewInput(rect);
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

        private void HandlePreviewInput(Rect rect)
        {
            Event e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.ScrollWheel)
            {
                float before = _zoom;
                _zoom = Mathf.Clamp(_zoom * Mathf.Exp(-e.delta.y * 0.1f), 1f, MaxZoom);
                if (!Mathf.Approximately(before, _zoom))
                {
                    e.Use();
                    Repaint();
                }
            }
            else if (e.type == EventType.MouseDown && e.clickCount == 2)
            {
                _zoom = 1f;
                _viewCenter = new Vector2(0.5f, 0.5f);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && (e.button == 0 || e.button == 2) && _zoom > 1f)
            {
                // GUI y grows downward, UV y grows upward.
                _viewCenter.x -= e.delta.x / rect.width  / _zoom;
                _viewCenter.y += e.delta.y / rect.height / _zoom;
                e.Use();
                Repaint();
            }
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
                _inputTexture = (Texture2D)EditorGUILayout.ObjectField(
                    L("MaskTexture"), _inputTexture, typeof(Texture2D), false);
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
                _settings.Strength = EditorGUILayout.Slider(
                    L("Strength"), _settings.Strength, 0.1f, 50f);
                _settings.NormalMapType = (NormalMapType)EditorGUILayout.EnumPopup(
                    L("NormalType"), _settings.NormalMapType);
                _settings.Dither = EditorGUILayout.Toggle(
                    L("Dither"), _settings.Dither);
            });
        }

        private void DrawBevelSection()
        {
            bool bevelEnabled = !_settings.DisableBevel;
            DrawToggleSection(L("BevelHeader"), ref bevelEnabled, () =>
            {
                _settings.BevelRadius = EditorGUILayout.IntSlider(
                    L("BevelRadius"), Mathf.RoundToInt(_settings.BevelRadius), 1, 100);
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
            _zoom = 1f;
            _viewCenter = new Vector2(0.5f, 0.5f);
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

        // ── Change detection ─────────────────────────────────────────────────

        /// <summary>
        /// Schedules a preview rebuild when the UI state actually differs from
        /// the last state we saw. Comparing against a snapshot (rather than
        /// EditorGUI.BeginChangeCheck) keeps the debounce timer from being reset
        /// on every repaint while a change is still pending.
        /// </summary>
        private void DetectChanges()
        {
            bool changed = _pendingSnapshot == null
                        || _pendingTexture != _inputTexture
                        || _pendingCap != _previewCap
                        || !SettingsEqual(_pendingSnapshot, _settings);
            if (!changed) return;

            _pendingSnapshot = _settings.Clone();
            _pendingTexture  = _inputTexture;
            _pendingCap      = _previewCap;

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

        private static bool SettingsEqual(NormalMapSettings a, NormalMapSettings b)
        {
            return a.InputMode     == b.InputMode
                && a.Threshold     == b.Threshold
                && a.BevelRadius   == b.BevelRadius
                && a.Strength      == b.Strength
                && a.ProfileType   == b.ProfileType
                && a.NormalMapType == b.NormalMapType
                && a.InvertMask    == b.InvertMask
                && a.DisableBevel  == b.DisableBevel
                && a.Dither        == b.Dither;
        }

        // ── Preview update ───────────────────────────────────────────────────
        private void UpdatePreview()
        {
            if (_inputTexture == null || _processor == null) return;

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
