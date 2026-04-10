using UnityEditor;
using UnityEngine;

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
        private const int       PreviewSize     = 512;
        private const int       PreviewDisplaySize = 256;

        // Scroll
        private Vector2 _scroll;

        // ----------------------------------------------------------------
        // Menu Item
        // ----------------------------------------------------------------
        [MenuItem("dennokoworks/Normal Map Generator")]
        public static void ShowWindow()
        {
            var win = GetWindow<NormalMapGeneratorWindow>("Normal Map Generator");
            win.minSize = new Vector2(560, 700);
        }

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------
        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
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
            GUIStyle title = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 16,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("Normal Map Generator", title, GUILayout.Height(28));
        }

        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                var prev = _inputTexture;
                _inputTexture = (Texture2D)EditorGUILayout.ObjectField(
                    "Mask Texture", _inputTexture, typeof(Texture2D), false);

                if (_inputTexture != prev)
                {
                    _previewDirty   = true;
                    _lastChangeTime = EditorApplication.timeSinceStartup;
                }
            }
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                _settings.InputMode   = (InputMode)EditorGUILayout.EnumPopup("Input Mode", _settings.InputMode);

                EditorGUILayout.Space(2);

                _settings.Threshold   = EditorGUILayout.Slider("Threshold", _settings.Threshold, 0f, 1f);
                _settings.InvertMask  = EditorGUILayout.Toggle("Invert Mask", _settings.InvertMask);

                EditorGUILayout.Space(4);

                _settings.DisableBevel = EditorGUILayout.Toggle("Disable Bevel", _settings.DisableBevel);

                using (new EditorGUI.DisabledGroupScope(_settings.DisableBevel))
                {
                    _settings.BevelRadius = EditorGUILayout.IntSlider("Bevel Radius (px)", _settings.BevelRadius, 1, 200);
                    _settings.ProfileType = (ProfileType)EditorGUILayout.EnumPopup("Profile", _settings.ProfileType);
                }

                EditorGUILayout.Space(4);

                _settings.Strength      = EditorGUILayout.Slider("Strength", _settings.Strength, 0.01f, 10f);
                _settings.NormalMapType = (NormalMapType)EditorGUILayout.EnumPopup("Normal Type", _settings.NormalMapType);
            }
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                _settings.OverwriteExisting  = EditorGUILayout.Toggle("Overwrite Existing", _settings.OverwriteExisting);
                _settings.SaveIntermediates  = EditorGUILayout.Toggle("Save Intermediates", _settings.SaveIntermediates);

                if (_settings.SaveIntermediates)
                {
                    EditorGUILayout.HelpBox(
                        "Intermediates: binary, bevel, height maps saved to processing/ subfolder.",
                        MessageType.Info);
                }
            }
        }

        private void DrawGenerateButton()
        {
            bool canGenerate = _inputTexture != null && _processor != null;

            using (new EditorGUI.DisabledGroupScope(!canGenerate))
            {
                if (_computeShader == null)
                {
                    EditorGUILayout.HelpBox(
                        "ComputeShader not found. Make sure NormalMapGenerator.compute is in the project.",
                        MessageType.Error);
                }

                GUIStyle btn = new GUIStyle(GUI.skin.button) { fontSize = 13 };
                if (GUILayout.Button("Generate Normal Map", btn, GUILayout.Height(36)))
                {
                    GenerateNormalMap();
                }
            }
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            float availableWidth = EditorGUIUtility.currentViewWidth - 24f;
            float cellWidth  = Mathf.Min(PreviewDisplaySize, (availableWidth - 16f) * 0.5f);
            float cellHeight = cellWidth;

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawTexturePreview("Input", _inputTexture, cellWidth, cellHeight);
                GUILayout.Space(8);
                DrawTexturePreview("Normal Map", _previewNormalRT, cellWidth, cellHeight);
            }

            if (_inputTexture == null)
            {
                EditorGUILayout.HelpBox("Assign an input texture to see the preview.", MessageType.Info);
            }
            else if (_processor == null)
            {
                EditorGUILayout.HelpBox("ComputeShader not loaded.", MessageType.Warning);
            }
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
        // Preview update (512×512 resize → Process)
        // ----------------------------------------------------------------
        private void UpdatePreview()
        {
            if (_inputTexture == null || _processor == null) return;

            ReleasePreviewRT();

            // Resize input to 512×512 via Blit (no isReadable requirement)
            var previewIn = RenderTexture.GetTemporary(PreviewSize, PreviewSize, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(_inputTexture, previewIn);

            try
            {
                _previewNormalRT = _processor.Process(previewIn, _settings);
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

        // ----------------------------------------------------------------
        // Generate and save
        // ----------------------------------------------------------------
        private void GenerateNormalMap()
        {
            if (_inputTexture == null || _processor == null) return;

            try
            {
                EditorUtility.DisplayProgressBar("Normal Map Generator", "Processing...", 0.0f);
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
}
