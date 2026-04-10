using System.IO;
using UnityEditor;
using UnityEngine;

namespace NormalmapGenerator
{
    // ================================================================
    // Enums
    // ================================================================
    public enum InputMode    { Threshold, GrayWeight }
    public enum ProfileType  { Linear, Logarithmic, Exponential }
    public enum NormalMapType { DirectX, OpenGL }

    // ================================================================
    // Settings container
    // ================================================================
    [System.Serializable]
    public class NormalMapSettings
    {
        public InputMode    InputMode       = InputMode.Threshold;
        public float        Threshold       = 0.5f;
        public int          BevelRadius     = 15;
        public int          Strength        = 1;
        public ProfileType  ProfileType     = ProfileType.Linear;
        public NormalMapType NormalMapType  = NormalMapType.DirectX;
        public bool         InvertMask      = false;
        public bool         DisableBevel    = false;
        public bool         OverwriteExisting = true;
    }

    // ================================================================
    // Processing logic
    // ================================================================
    public class NormalMapProcessor
    {
        private readonly ComputeShader _cs;

        private readonly int _kBinarize;
        private readonly int _kJFAInit;
        private readonly int _kJFAStep;
        private readonly int _kJFADistance;
        private readonly int _kApplyProfile;
        private readonly int _kCompositeHeightMap;
        private readonly int _kGenerateNormalMap;

        public NormalMapProcessor(ComputeShader cs)
        {
            _cs = cs;
            _kBinarize          = cs.FindKernel("Binarize");
            _kJFAInit           = cs.FindKernel("JFA_Init");
            _kJFAStep           = cs.FindKernel("JFA_Step");
            _kJFADistance       = cs.FindKernel("JFA_Distance");
            _kApplyProfile      = cs.FindKernel("ApplyProfile");
            _kCompositeHeightMap = cs.FindKernel("CompositeHeightMap");
            _kGenerateNormalMap  = cs.FindKernel("GenerateNormalMap");
        }

        // ----------------------------------------------------------------
        // Process
        //   inputTex     : Texture (Texture2D or RenderTexture) to process
        //   settings     : processing parameters
        //   Returns a RenderTexture (ARGB32) with the normal map.
        //   Caller is responsible for releasing the returned RT.
        // ----------------------------------------------------------------
        public RenderTexture Process(Texture inputTex, NormalMapSettings s)
        {
            int w = inputTex.width;
            int h = inputTex.height;

            // Allocate working RenderTextures
            RenderTexture binaryRT    = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture grayRT      = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture seedBufA    = CreateRT(w, h, RenderTextureFormat.RGFloat);
            RenderTexture seedBufB    = CreateRT(w, h, RenderTextureFormat.RGFloat);
            RenderTexture distanceRT  = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture intensityRT = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture heightRT    = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture normalRT    = CreateRT(w, h, RenderTextureFormat.ARGB32);

            int gx = Mathf.CeilToInt(w / 8f);
            int gy = Mathf.CeilToInt(h / 8f);

            // -- Set shared scalar parameters --
            _cs.SetInt  ("_Width",      w);
            _cs.SetInt  ("_Height",     h);
            _cs.SetFloat("_Threshold",  s.Threshold);
            _cs.SetInt  ("_BevelRadius", s.BevelRadius);
            _cs.SetFloat("_Strength",   s.Strength);
            _cs.SetInt  ("_ProfileType",  (int)s.ProfileType);
            _cs.SetInt  ("_NormalMapType", (int)s.NormalMapType);
            _cs.SetInt  ("_InputMode",  (int)s.InputMode);
            _cs.SetInt  ("_InvertMask", s.InvertMask  ? 1 : 0);
            _cs.SetInt  ("_DisableBevel", s.DisableBevel ? 1 : 0);

            // ---- Step 1: Binarize ----
            _cs.SetTexture(_kBinarize, "_InputTex",  inputTex);
            _cs.SetTexture(_kBinarize, "_BinaryOut", binaryRT);
            _cs.SetTexture(_kBinarize, "_GrayOut",   grayRT);
            _cs.Dispatch (_kBinarize, gx, gy, 1);

            if (!s.DisableBevel)
            {
                // ---- Step 2a: JFA Init ----
                _cs.SetTexture(_kJFAInit, "_BinaryIn",   binaryRT);
                _cs.SetTexture(_kJFAInit, "_SeedBufOut", seedBufA);
                _cs.Dispatch (_kJFAInit, gx, gy, 1);

                // ---- Step 2b: JFA passes (ping-pong seedBufA / seedBufB) ----
                int maxDim   = Mathf.Max(w, h);
                int k        = Mathf.NextPowerOfTwo(maxDim) / 2;
                bool pingPong = false;  // false → read A, write B

                while (k >= 1)
                {
                    RenderTexture src = pingPong ? seedBufB : seedBufA;
                    RenderTexture dst = pingPong ? seedBufA : seedBufB;

                    _cs.SetInt    ("_JFAStep",    k);
                    _cs.SetTexture(_kJFAStep, "_SeedBufIn",  src);
                    _cs.SetTexture(_kJFAStep, "_SeedBufOut", dst);
                    _cs.Dispatch (_kJFAStep, gx, gy, 1);

                    pingPong = !pingPong;
                    k /= 2;
                }

                // dst of last pass: when pingPong is true the last dst was seedBufB, when false it was seedBufA
                RenderTexture finalSeedBuf = pingPong ? seedBufB : seedBufA;

                // ---- Step 2c: JFA Distance ----
                _cs.SetTexture(_kJFADistance, "_SeedBufIn",   finalSeedBuf);
                _cs.SetTexture(_kJFADistance, "_BinaryIn",    binaryRT);
                _cs.SetTexture(_kJFADistance, "_DistanceOut", distanceRT);
                _cs.Dispatch (_kJFADistance, gx, gy, 1);

                // ---- Step 3: Apply Profile + 3×3 Gaussian blur ----
                _cs.SetTexture(_kApplyProfile, "_DistanceIn",  distanceRT);
                _cs.SetTexture(_kApplyProfile, "_IntensityOut", intensityRT);
                _cs.Dispatch (_kApplyProfile, gx, gy, 1);
            }

            // ---- Step 4: Composite Height Map ----
            _cs.SetTexture(_kCompositeHeightMap, "_BinaryIn",    binaryRT);
            _cs.SetTexture(_kCompositeHeightMap, "_GrayIn",      grayRT);
            _cs.SetTexture(_kCompositeHeightMap, "_IntensityIn", intensityRT);
            _cs.SetTexture(_kCompositeHeightMap, "_HeightMapOut", heightRT);
            _cs.Dispatch (_kCompositeHeightMap, gx, gy, 1);

            // ---- Step 5: Generate Normal Map ----
            _cs.SetTexture(_kGenerateNormalMap, "_HeightMapIn",  heightRT);
            _cs.SetTexture(_kGenerateNormalMap, "_NormalMapOut", normalRT);
            _cs.Dispatch (_kGenerateNormalMap, gx, gy, 1);

            // Cleanup working textures (keep normalRT for caller)
            binaryRT.Release();
            grayRT.Release();
            seedBufA.Release();
            seedBufB.Release();
            distanceRT.Release();
            heightRT.Release();
            // intensityRT is kept for intermediates if needed; otherwise release
            intensityRT.Release();

            return normalRT;
        }

        // ----------------------------------------------------------------
        // ProcessAndSave
        //   Runs the full pipeline, saves outputs, refreshes AssetDatabase.
        // ----------------------------------------------------------------
        public void ProcessAndSave(Texture2D inputTex, NormalMapSettings s)
        {
            string assetPath = AssetDatabase.GetAssetPath(inputTex);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError("[NormalMapGenerator] Input texture is not an asset.");
                return;
            }

            string dir  = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(assetPath);

            string outputDir = dir + "/output";

            EnsureDirectory(outputDir);

            string outputAssetPath = outputDir + "/" + name + "_normal.png";

            if (!s.OverwriteExisting && File.Exists(ToPhysicalPath(outputAssetPath)))
            {
                Debug.Log($"[NormalMapGenerator] Skipped (already exists): {outputAssetPath}");
                return;
            }

            int w = inputTex.width;
            int h = inputTex.height;

            // Allocate working RenderTextures (need intermediates too)
            RenderTexture binaryRT    = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture grayRT      = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture seedBufA    = CreateRT(w, h, RenderTextureFormat.RGFloat);
            RenderTexture seedBufB    = CreateRT(w, h, RenderTextureFormat.RGFloat);
            RenderTexture distanceRT  = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture intensityRT = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture heightRT    = CreateRT(w, h, RenderTextureFormat.RFloat);
            RenderTexture normalRT    = CreateRT(w, h, RenderTextureFormat.ARGB32);

            int gx = Mathf.CeilToInt(w / 8f);
            int gy = Mathf.CeilToInt(h / 8f);

            _cs.SetInt  ("_Width",       w);
            _cs.SetInt  ("_Height",      h);
            _cs.SetFloat("_Threshold",   s.Threshold);
            _cs.SetInt  ("_BevelRadius", s.BevelRadius);
            _cs.SetFloat("_Strength",    s.Strength);
            _cs.SetInt  ("_ProfileType",   (int)s.ProfileType);
            _cs.SetInt  ("_NormalMapType", (int)s.NormalMapType);
            _cs.SetInt  ("_InputMode",   (int)s.InputMode);
            _cs.SetInt  ("_InvertMask",  s.InvertMask   ? 1 : 0);
            _cs.SetInt  ("_DisableBevel", s.DisableBevel ? 1 : 0);

            // Step 1
            _cs.SetTexture(_kBinarize, "_InputTex",  inputTex);
            _cs.SetTexture(_kBinarize, "_BinaryOut", binaryRT);
            _cs.SetTexture(_kBinarize, "_GrayOut",   grayRT);
            _cs.Dispatch (_kBinarize, gx, gy, 1);

            if (!s.DisableBevel)
            {
                _cs.SetTexture(_kJFAInit, "_BinaryIn",   binaryRT);
                _cs.SetTexture(_kJFAInit, "_SeedBufOut", seedBufA);
                _cs.Dispatch (_kJFAInit, gx, gy, 1);

                int maxDim  = Mathf.Max(w, h);
                int k       = Mathf.NextPowerOfTwo(maxDim) / 2;
                bool ping   = false;
                while (k >= 1)
                {
                    RenderTexture src = ping ? seedBufB : seedBufA;
                    RenderTexture dst = ping ? seedBufA : seedBufB;
                    _cs.SetInt    ("_JFAStep", k);
                    _cs.SetTexture(_kJFAStep, "_SeedBufIn",  src);
                    _cs.SetTexture(_kJFAStep, "_SeedBufOut", dst);
                    _cs.Dispatch (_kJFAStep, gx, gy, 1);
                    ping = !ping;
                    k /= 2;
                }
                RenderTexture finalSeed = ping ? seedBufB : seedBufA;

                _cs.SetTexture(_kJFADistance, "_SeedBufIn",   finalSeed);
                _cs.SetTexture(_kJFADistance, "_BinaryIn",    binaryRT);
                _cs.SetTexture(_kJFADistance, "_DistanceOut", distanceRT);
                _cs.Dispatch (_kJFADistance, gx, gy, 1);

                _cs.SetTexture(_kApplyProfile, "_DistanceIn",   distanceRT);
                _cs.SetTexture(_kApplyProfile, "_IntensityOut", intensityRT);
                _cs.Dispatch (_kApplyProfile, gx, gy, 1);
            }

            _cs.SetTexture(_kCompositeHeightMap, "_BinaryIn",    binaryRT);
            _cs.SetTexture(_kCompositeHeightMap, "_GrayIn",      grayRT);
            _cs.SetTexture(_kCompositeHeightMap, "_IntensityIn", intensityRT);
            _cs.SetTexture(_kCompositeHeightMap, "_HeightMapOut", heightRT);
            _cs.Dispatch (_kCompositeHeightMap, gx, gy, 1);

            _cs.SetTexture(_kGenerateNormalMap, "_HeightMapIn",  heightRT);
            _cs.SetTexture(_kGenerateNormalMap, "_NormalMapOut", normalRT);
            _cs.Dispatch (_kGenerateNormalMap, gx, gy, 1);

            // Save normal map
            SaveColorRT(normalRT, ToPhysicalPath(outputAssetPath));

            // Cleanup
            binaryRT.Release();
            grayRT.Release();
            seedBufA.Release();
            seedBufB.Release();
            distanceRT.Release();
            intensityRT.Release();
            heightRT.Release();
            normalRT.Release();

            AssetDatabase.Refresh();
            Debug.Log($"[NormalMapGenerator] Saved: {outputAssetPath}");
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static RenderTexture CreateRT(int w, int h, RenderTextureFormat fmt)
        {
            var rt = new RenderTexture(w, h, 0, fmt)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            rt.Create();
            return rt;
        }

        private static void SaveColorRT(RenderTexture rt, string physicalPath)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            byte[] bytes = tex.EncodeToPNG();
            File.WriteAllBytes(physicalPath, bytes);
            Object.DestroyImmediate(tex);
        }

        private static void EnsureDirectory(string assetPath)
        {
            string physical = ToPhysicalPath(assetPath);
            if (!Directory.Exists(physical))
                Directory.CreateDirectory(physical);
        }

        private static string ToPhysicalPath(string assetPath)
        {
            // assetPath example: "Assets/Foo/Bar/output"
            // Application.dataPath: "C:/Project/Assets"
            return Application.dataPath + "/" + assetPath.Substring("Assets/".Length);
        }
    }
}
