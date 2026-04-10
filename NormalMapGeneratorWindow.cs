using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace NormalmapGenerator
{
    public class NormalMapGeneratorWindow : EditorWindow
    {
        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------
        private Texture2D       _inputTexture;
        private NormalMapSettings _settings = new NormalMapSettings();
        private NormalMapProcessor _processor;
        private ComputeShader   _computeShader;

        // Preview
        private RenderTexture   _previewNormalRT;
        private bool            _previewDirty;
        private double          _lastChangeTime;
        private const double    DebounceSeconds = 0.6;
        private float           _lastWindowWidth;
        private const int       MaxPreviewSize = 4096;

        // Scroll
        private Vector2 _scroll;

        // Localization
        private Dictionary<string, string> _locDict = new Dictionary<string, string>();
        private int _langIndex = 1;
        private readonly string[] _languages = { "en", "ja" };

        // ----------------------------------------------------------------
        // Menu Item
        // ----------------------------------------------------------------
        [MenuItem("dennokoworks/Normalmap Generator")]
        public static void ShowWindow()
        {
            var win = GetWindow<NormalMapGeneratorWindow>("Normalmap Generator");
            win.minSize = new Vector2(560, 700);
        }

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------
        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            _langIndex = EditorPrefs.GetInt("NormalMapGenerator_Lang", 1);
            if (_langIndex < 0 || _langIndex >= _languages.Length) _langIndex = 1;
            LoadLocalization(_languages[_langIndex]);
            LoadComputeShader();
        }

        private void LoadLocalization(string lang)
        {
            _locDict.Clear();
            string path = $"Assets/Editor/Normalmap_generator/Localization/{lang}.json";
            TextAsset ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (ta != null)
            {
                var data = JsonUtility.FromJson<LocData>(ta.text);
                if (data != null && data.entries != null)
                {
                    foreach (var pair in data.entries)
                    {
                        if (!string.IsNullOrEmpty(pair.key))
                            _locDict[pair.key] = pair.value;
                    }
                }
            }
        }

        private string L(string key)
        {
            if (_locDict.TryGetValue(key, out string val)) return val;
            return key;
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

        // ----------------------------------------------------------------
        // Load ComputeShader from project
        // ----------------------------------------------------------------
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

        // ----------------------------------------------------------------
        // Debounce update
        // ----------------------------------------------------------------
        private void OnEditorUpdate()
        {
            if (_previewDirty && EditorApplication.timeSinceStartup - _lastChangeTime > DebounceSeconds)
            {
                _previewDirty = false;
                UpdatePreview();
                Repaint();
            }
        }

        // ----------------------------------------------------------------
        // GUI
        // ----------------------------------------------------------------
        private void OnGUI()
        {
            float currentWidth = position.width;
            if (Mathf.Abs(currentWidth - _lastWindowWidth) > 1f)
            {
                _lastWindowWidth = currentWidth;
                _previewDirty    = true;
                _lastChangeTime  = EditorApplication.timeSinceStartup;
            }

            EditorGUI.BeginChangeCheck();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawHeader();
            EditorGUILayout.Space(4);
            DrawInputSection();
            EditorGUILayout.Space(8);
            DrawSettingsSection();
            EditorGUILayout.Space(8);
            DrawOutputSection();
            EditorGUILayout.Space(8);
            DrawGenerateButton();
            EditorGUILayout.Space(12);
            DrawPreviewSection();

            EditorGUILayout.EndScrollView();

            if (EditorGUI.EndChangeCheck())
            {
                _previewDirty    = true;
                _lastChangeTime  = EditorApplication.timeSinceStartup;
            }
        }

        // ----------------------------------------------------------------
        // UI Sections
        // ----------------------------------------------------------------
        private void DrawHeader()
        {
            Rect rect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            // Background color normal map style (blue-purple)
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 1.0f));

            GUIStyle title = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 20,
                alignment = TextAnchor.MiddleCenter,
                normal  = { textColor = Color.white },
                hover   = { textColor = Color.white },
                active  = { textColor = Color.white },
                focused = { textColor = Color.white }
            };

            GUIStyle titleOutline = new GUIStyle(title)
            {
                normal  = { textColor = Color.black },
                hover   = { textColor = Color.black },
                active  = { textColor = Color.black },
                focused = { textColor = Color.black }
            };

            string titleText = "Normalmap Generator";
            
            // Draw text borders
            for(int x = -1; x <= 1; x++)
            {
                for(int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0) continue;
                    Rect offsetRect = rect;
                    offsetRect.x += x;
                    offsetRect.y += y;
                    GUI.Label(offsetRect, titleText, titleOutline);
                }
            }
            // Draw Main Text
            GUI.Label(rect, titleText, title);

            // Language Selector
            Rect langRect = new Rect(rect.xMax - 60, rect.y + 10, 50, 20);
            EditorGUI.BeginChangeCheck();
            _langIndex = EditorGUI.Popup(langRect, _langIndex, new string[] { "EN", "JA" });
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt("NormalMapGenerator_Lang", _langIndex);
                LoadLocalization(_languages[_langIndex]);
            }
        }

        private void DrawInputSection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(L("InputHeader"), EditorStyles.boldLabel);
            EditorGUILayout.Space(2);
            using (new EditorGUI.IndentLevelScope())
            {
                var prev = _inputTexture;
                _inputTexture = (Texture2D)EditorGUILayout.ObjectField(
                    L("MaskTexture"), _inputTexture, typeof(Texture2D), false);

                if (_inputTexture != prev)
                {
                    _previewDirty   = true;
                    _lastChangeTime = EditorApplication.timeSinceStartup;
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(L("SettingsHeader"), EditorStyles.boldLabel);
            EditorGUILayout.Space(2);
            using (new EditorGUI.IndentLevelScope())
            {
                _settings.InputMode   = (InputMode)EditorGUILayout.EnumPopup(L("InputMode"), _settings.InputMode);

                EditorGUILayout.Space(2);

                _settings.Threshold   = EditorGUILayout.Slider(L("Threshold"), _settings.Threshold, 0f, 1f);
                _settings.InvertMask  = EditorGUILayout.Toggle(L("InvertMask"), _settings.InvertMask);

                EditorGUILayout.Space(4);

                _settings.DisableBevel = EditorGUILayout.Toggle(L("DisableBevel"), _settings.DisableBevel);

                using (new EditorGUI.DisabledGroupScope(_settings.DisableBevel))
                {
                    _settings.BevelRadius = EditorGUILayout.IntSlider(L("BevelRadius"), _settings.BevelRadius, 1, 100);
                    _settings.ProfileType = (ProfileType)EditorGUILayout.EnumPopup(L("Profile"), _settings.ProfileType);
                }

                EditorGUILayout.Space(4);

                _settings.Strength      = EditorGUILayout.IntSlider(L("Strength"), _settings.Strength, 1, 50);
                _settings.NormalMapType = (NormalMapType)EditorGUILayout.EnumPopup(L("NormalType"), _settings.NormalMapType);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(L("OutputHeader"), EditorStyles.boldLabel);
            EditorGUILayout.Space(2);
            using (new EditorGUI.IndentLevelScope())
            {
                _settings.OverwriteExisting = EditorGUILayout.Toggle(L("OverwriteIfSameName"), _settings.OverwriteExisting);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawGenerateButton()
        {
            bool canGenerate = _inputTexture != null && _processor != null;

            using (new EditorGUI.DisabledGroupScope(!canGenerate))
            {
                if (_computeShader == null)
                {
                    EditorGUILayout.HelpBox(
                        L("ComputeShaderNotFound"),
                        MessageType.Error);
                }

                GUIStyle btn = new GUIStyle(GUI.skin.button) { 
                    fontSize = 14,
                    fontStyle = FontStyle.Bold
                };
                GUI.backgroundColor = new Color(0.6f, 0.8f, 1.0f);
                if (GUILayout.Button(L("GenerateBtn"), btn, GUILayout.Height(40)))
                {
                    GenerateNormalMap();
                }
                GUI.backgroundColor = Color.white;
            }
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(L("PreviewHeader"), EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            float availableWidth = EditorGUIUtility.currentViewWidth - 48f;
            float cellWidth  = (availableWidth - 16f) * 0.5f;
            float cellHeight = cellWidth;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                DrawTexturePreview("Input", _inputTexture, cellWidth, cellHeight);
                GUILayout.Space(8);
                DrawTexturePreview("Normal Map", _previewNormalRT, cellWidth, cellHeight);
                GUILayout.FlexibleSpace();
            }

            if (_inputTexture == null)
            {
                EditorGUILayout.HelpBox(L("AssignInputTex"), MessageType.Info);
            }
            else if (_processor == null)
            {
                EditorGUILayout.HelpBox(L("ComputeShaderNotLoaded"), MessageType.Warning);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawTexturePreview(string label, Texture tex, float w, float h)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(w)))
            {
                EditorGUILayout.LabelField(label, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(w));
                Rect rect = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
                if (tex != null)
                {
                    EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
                    GUI.Label(rect, "—", new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                        { alignment = TextAnchor.MiddleCenter });
                }
            }
        }

        // ----------------------------------------------------------------
        // Preview update
        //   Resize to PreviewSize while keeping aspect ratio, then process
        //   with BevelRadius scaled to match the downsampled resolution.
        // ----------------------------------------------------------------
        private void UpdatePreview()
        {
            if (_inputTexture == null || _processor == null) return;

            ReleasePreviewRT();

            int srcW = _inputTexture.width;
            int srcH = _inputTexture.height;

            // Use half window width as the target resolution for the preview, capped at 4k.
            int targetRes = Mathf.Clamp(Mathf.RoundToInt(position.width * 0.5f), 256, MaxPreviewSize);

            // Compute preview dimensions that preserve aspect ratio
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

            // Scale BevelRadius proportionally so the preview matches the actual output.
            // Use max(W,H) as the reference dimension (same axis JFA uses for step count).
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

        // Returns a copy of settings adjusted for the preview resolution.
        //
        // BevelRadius is pixel-based, so it scales linearly with resolution.
        // Strength must be scaled by the same factor: the Sobel gradient of the
        // bevel slope is proportional to (1 / BevelRadius), so halving the radius
        // doubles the gradient. Multiplying Strength by scale keeps the effective
        // deflection identical to the full-resolution output.
        //   sx_full    = Strength              / BevelRadius
        //   sx_preview = (Strength * scale) / (BevelRadius * scale)  ← same
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

        // ----------------------------------------------------------------
        // Generate and save
        // ----------------------------------------------------------------
        private void GenerateNormalMap()
        {
            if (_inputTexture == null || _processor == null) return;

            try
            {
                EditorUtility.DisplayProgressBar(L("WindowTitle"), L("GenerateProcessing"), 0.0f);
                _processor.ProcessAndSave(_inputTexture, _settings);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NormalMapGenerator] Error: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // Update preview with the latest settings
            UpdatePreview();
            Repaint();
        }



        // ----------------------------------------------------------------
        // Cleanup
        // ----------------------------------------------------------------
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
