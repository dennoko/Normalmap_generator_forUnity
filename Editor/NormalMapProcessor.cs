using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace NormalmapGenerator
{
    // ================================================================
    // Enums
    // ================================================================
    public enum InputMode    { Threshold, GrayWeight }
    // Smoothstep is appended last so existing serialized values keep meaning.
    public enum ProfileType  { Linear, Logarithmic, Exponential, Smoothstep }
    public enum NormalMapType { DirectX, OpenGL }
    public enum SaveResult   { Saved, Skipped, Failed }

    // ================================================================
    // Settings container
    //
    // BevelRadius / Strength are float: when the preview runs at a reduced
    // resolution both get multiplied by the resolution ratio, and rounding
    // that to an int used to make the preview diverge badly from the output
    // (a radius of 3 at 0.14x scale rounded to 1, and a strength of 1 could
    // not scale below 1 at all).
    // ================================================================
    [System.Serializable]
    public class NormalMapSettings
    {
        public InputMode    InputMode       = InputMode.Threshold;
        public float        Threshold       = 0.5f;
        public float        BevelRadius     = 15f;
        public float        Strength        = 1f;
        public ProfileType  ProfileType     = ProfileType.Linear;
        public NormalMapType NormalMapType  = NormalMapType.DirectX;
        public bool         InvertMask      = false;
        public bool         DisableBevel    = false;
        public bool         OverwriteExisting = true;
        public bool         Dither          = true;

        public NormalMapSettings Clone() => (NormalMapSettings)MemberwiseClone();
    }

    // ================================================================
    // Pipeline stages, ordered. A change to a parameter invalidates its
    // stage and everything after it; earlier stages are reused as-is.
    // ================================================================
    internal enum Stage
    {
        Binarize  = 0,
        Jfa       = 1,
        Profile   = 2,
        Composite = 3,
        Normal    = 4,
        None      = 5,
    }

    // ================================================================
    // PipelineContext
    //   Owns one set of working RenderTextures plus the signature of the
    //   last run, so repeated runs at the same resolution neither reallocate
    //   nor redo work that no parameter change affects.
    // ================================================================
    public sealed class PipelineContext : System.IDisposable
    {
        public int Width  { get; private set; }
        public int Height { get; private set; }

        internal RenderTexture Binary, Gray, SeedA, SeedB, Distance, BlurTmp, Intensity, HeightMap, Normal;

        /// <summary>Normal map produced by the last run. Owned by this context.</summary>
        public RenderTexture Result => Normal;

        private readonly bool _wantMips;

        // --- cache bookkeeping ---
        internal bool  Valid;
        internal int   SigInputId;
        internal int   SigSrcW, SigSrcH;
        internal bool  SigInputIsLinear;
        internal float SigThreshold;
        internal bool  SigInvert;
        internal bool  JfaValid;
        internal int   JfaRangeCached;
        internal float SigBevelRadius;
        internal ProfileType SigProfile;
        internal bool  SigDisableBevel;
        internal InputMode SigMode;
        internal float SigStrength;
        internal NormalMapType SigNormalType;
        internal bool  SigDither;

        /// <param name="generateMips">
        /// Enable mip generation on the result. Required whenever the result is
        /// drawn smaller than its native size, which at 2048 is essentially
        /// always: point-sampling a 2048 texture into a 250px rect aliases badly.
        /// </param>
        public PipelineContext(bool generateMips = false)
        {
            _wantMips = generateMips;
        }

        internal void EnsureSize(int w, int h)
        {
            if (Width == w && Height == h && Binary != null) return;

            ReleaseTextures();
            Width  = w;
            Height = h;

            Binary    = Create(w, h, RenderTextureFormat.RFloat,  "Binary");
            Gray      = Create(w, h, RenderTextureFormat.RFloat,  "Gray");
            SeedA     = Create(w, h, RenderTextureFormat.RGFloat, "SeedA");
            SeedB     = Create(w, h, RenderTextureFormat.RGFloat, "SeedB");
            Distance  = Create(w, h, RenderTextureFormat.RFloat,  "Distance");
            BlurTmp   = Create(w, h, RenderTextureFormat.RFloat,  "BlurTmp");
            Intensity = Create(w, h, RenderTextureFormat.RFloat,  "Intensity");
            HeightMap = Create(w, h, RenderTextureFormat.RFloat,  "Height");
            Normal    = Create(w, h, RenderTextureFormat.ARGB32,  "Normal", sRGB: true, mips: _wantMips);

            Invalidate();
        }

        /// <summary>Force a full recomputation on the next run.</summary>
        public void Invalidate()
        {
            Valid    = false;
            JfaValid = false;
            JfaRangeCached = 0;
        }

        private static RenderTexture Create(int w, int h, RenderTextureFormat fmt, string label,
                                            bool sRGB = false, bool mips = false)
        {
            var desc = new RenderTextureDescriptor(w, h, fmt, 0)
            {
                enableRandomWrite = true,
                sRGB              = sRGB,
                useMipMap         = mips,
                autoGenerateMips  = false,
                msaaSamples       = 1,
            };

            var rt = new RenderTexture(desc)
            {
                // Point everywhere except the displayed result, which needs
                // proper minification filtering.
                filterMode = mips ? FilterMode.Bilinear : FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
                name       = "NormalMapGen_" + label,
            };
            rt.Create();
            return rt;
        }

        private void ReleaseTextures()
        {
            // Release() only frees the GPU-side surface; the native object stays
            // alive until DestroyImmediate. The previous implementation called
            // only Release(), leaking a RenderTexture per preview update.
            Destroy(ref Binary);
            Destroy(ref Gray);
            Destroy(ref SeedA);
            Destroy(ref SeedB);
            Destroy(ref Distance);
            Destroy(ref BlurTmp);
            Destroy(ref Intensity);
            Destroy(ref HeightMap);
            Destroy(ref Normal);
            Width = Height = 0;
        }

        private static void Destroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.DestroyImmediate(rt);
            rt = null;
        }

        public void Dispose()
        {
            ReleaseTextures();
            Valid = false;
        }
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
        private readonly int _kBlurH;
        private readonly int _kBlurV;
        private readonly int _kCompositeHeightMap;
        private readonly int _kGenerateNormalMap;

        public NormalMapProcessor(ComputeShader cs)
        {
            _cs = cs;
            _kBinarize           = cs.FindKernel("Binarize");
            _kJFAInit            = cs.FindKernel("JFA_Init");
            _kJFAStep            = cs.FindKernel("JFA_Step");
            _kJFADistance        = cs.FindKernel("JFA_Distance");
            _kBlurH              = cs.FindKernel("BlurProfileH");
            _kBlurV              = cs.FindKernel("BlurProfileV");
            _kCompositeHeightMap = cs.FindKernel("CompositeHeightMap");
            _kGenerateNormalMap  = cs.FindKernel("GenerateNormalMap");
        }

        // ----------------------------------------------------------------
        // Run
        //   Executes the pipeline into ctx at the given working resolution,
        //   reusing whatever the context already holds.
        //
        //   inputTex  : source texture, used at its native resolution.
        //               Binarize box-downsamples it to workW/workH.
        //   settings  : parameters already scaled to the working resolution.
        //   Returns ctx.Result (owned by ctx, do not release).
        // ----------------------------------------------------------------
        public RenderTexture Run(PipelineContext ctx, Texture inputTex, NormalMapSettings s,
                                 int workW, int workH)
        {
            ctx.EnsureSize(workW, workH);

            int srcW = inputTex.width;
            int srcH = inputTex.height;

            // An sRGB-flagged texture is decoded to linear by the sampler in a
            // Linear project; the shader re-encodes so that Threshold and the
            // GrayWeight ramp operate on the values the user actually sees.
            bool inputIsLinear = QualitySettings.activeColorSpace == ColorSpace.Linear
                                 && GraphicsFormatUtility.IsSRGBFormat(inputTex.graphicsFormat);

            float bevelRadius = Mathf.Max(0.25f, s.BevelRadius);

            // The JFA only has to propagate as far as the bevel reaches. Halving
            // from NextPowerOfTwo(radius) covers 2*range-1 >= radius pixels, so a
            // radius of 15 at 2048px costs 5 passes instead of 11.
            int jfaRange = Mathf.NextPowerOfTwo(Mathf.Max(1, Mathf.CeilToInt(bevelRadius)));
            jfaRange = Mathf.Clamp(jfaRange, 1, Mathf.Max(1, Mathf.NextPowerOfTwo(Mathf.Max(workW, workH)) / 2));

            // Smoothing scales with the bevel so it stays proportionally the same
            // at any working resolution (the old fixed 3x3 kernel did not).
            float blurSigma = Mathf.Clamp(bevelRadius * 0.05f, 0.6f, 4f);
            int   blurRadius = Mathf.Clamp(Mathf.CeilToInt(blurSigma * 3f), 1, 12);

            Stage dirty = DetermineDirty(ctx, inputTex.GetInstanceID(), srcW, srcH, inputIsLinear,
                                         s, bevelRadius, jfaRange);

            if (dirty == Stage.None)
                return ctx.Result;

            int gx = Mathf.CeilToInt(workW / 8f);
            int gy = Mathf.CeilToInt(workH / 8f);

            // -- Shared scalar parameters --
            _cs.SetInt  ("_Width",         workW);
            _cs.SetInt  ("_Height",        workH);
            _cs.SetInt  ("_SrcWidth",      srcW);
            _cs.SetInt  ("_SrcHeight",     srcH);
            _cs.SetInt  ("_InputIsLinear", inputIsLinear ? 1 : 0);
            _cs.SetFloat("_Threshold",     s.Threshold);
            _cs.SetFloat("_BevelRadius",   bevelRadius);
            _cs.SetFloat("_Strength",      s.Strength);
            _cs.SetInt  ("_ProfileType",   (int)s.ProfileType);
            _cs.SetInt  ("_NormalMapType", (int)s.NormalMapType);
            _cs.SetInt  ("_InputMode",     (int)s.InputMode);
            _cs.SetInt  ("_InvertMask",    s.InvertMask   ? 1 : 0);
            _cs.SetInt  ("_DisableBevel",  s.DisableBevel ? 1 : 0);
            _cs.SetFloat("_BlurSigma",     blurSigma);
            _cs.SetInt  ("_BlurRadius",    blurRadius);
            _cs.SetInt  ("_DitherEnabled", s.Dither ? 1 : 0);

            // ---- Stage 0: Binarize (+ box downsample) ----
            if (dirty <= Stage.Binarize)
            {
                _cs.SetTexture(_kBinarize, "_InputTex",  inputTex);
                _cs.SetTexture(_kBinarize, "_BinaryOut", ctx.Binary);
                _cs.SetTexture(_kBinarize, "_GrayOut",   ctx.Gray);
                _cs.Dispatch  (_kBinarize, gx, gy, 1);

                // The distance field was derived from the previous mask. With the
                // bevel disabled the JFA block below is skipped, so drop the flag
                // here or re-enabling the bevel would resurrect a stale field.
                ctx.JfaValid = false;
            }

            if (!s.DisableBevel)
            {
                // ---- Stage 1: JFA -> raw distance field ----
                if (dirty <= Stage.Jfa)
                {
                    _cs.SetTexture(_kJFAInit, "_BinaryIn",   ctx.Binary);
                    _cs.SetTexture(_kJFAInit, "_GrayIn",     ctx.Gray);
                    _cs.SetTexture(_kJFAInit, "_SeedBufOut", ctx.SeedA);
                    _cs.Dispatch  (_kJFAInit, gx, gy, 1);

                    bool ping = false;   // false -> read A, write B
                    for (int k = jfaRange; k >= 1; k /= 2)
                        DispatchJfaStep(ctx, k, ref ping, gx, gy);

                    // JFA+1: standard jump flooding mislabels a small fraction of
                    // pixels; one extra unit-step pass fixes nearly all of them
                    // for the cost of a single additional dispatch.
                    DispatchJfaStep(ctx, 1, ref ping, gx, gy);

                    RenderTexture finalSeed = ping ? ctx.SeedB : ctx.SeedA;

                    _cs.SetTexture(_kJFADistance, "_SeedBufIn",   finalSeed);
                    _cs.SetTexture(_kJFADistance, "_BinaryIn",    ctx.Binary);
                    _cs.SetTexture(_kJFADistance, "_DistanceOut", ctx.Distance);
                    _cs.Dispatch  (_kJFADistance, gx, gy, 1);

                    ctx.JfaValid       = true;
                    ctx.JfaRangeCached = jfaRange;
                }

                // ---- Stage 2: separable blur + profile curve ----
                if (dirty <= Stage.Profile)
                {
                    _cs.SetTexture(_kBlurH, "_DistanceIn", ctx.Distance);
                    _cs.SetTexture(_kBlurH, "_BlurTmpOut", ctx.BlurTmp);
                    _cs.Dispatch  (_kBlurH, gx, gy, 1);

                    _cs.SetTexture(_kBlurV, "_BlurTmpIn",   ctx.BlurTmp);
                    _cs.SetTexture(_kBlurV, "_IntensityOut", ctx.Intensity);
                    _cs.Dispatch  (_kBlurV, gx, gy, 1);
                }
            }

            // ---- Stage 3: composite height map ----
            if (dirty <= Stage.Composite)
            {
                _cs.SetTexture(_kCompositeHeightMap, "_BinaryIn",     ctx.Binary);
                _cs.SetTexture(_kCompositeHeightMap, "_GrayIn",       ctx.Gray);
                _cs.SetTexture(_kCompositeHeightMap, "_IntensityIn",  ctx.Intensity);
                _cs.SetTexture(_kCompositeHeightMap, "_HeightMapOut", ctx.HeightMap);
                _cs.Dispatch  (_kCompositeHeightMap, gx, gy, 1);
            }

            // ---- Stage 4: Sobel -> normal map ----
            if (dirty <= Stage.Normal)
            {
                _cs.SetTexture(_kGenerateNormalMap, "_HeightMapIn",  ctx.HeightMap);
                _cs.SetTexture(_kGenerateNormalMap, "_NormalMapOut", ctx.Normal);
                _cs.Dispatch  (_kGenerateNormalMap, gx, gy, 1);

                if (ctx.Normal.useMipMap)
                    ctx.Normal.GenerateMips();
            }

            StoreSignature(ctx, inputTex.GetInstanceID(), srcW, srcH, inputIsLinear, s, bevelRadius);
            return ctx.Result;
        }

        private void DispatchJfaStep(PipelineContext ctx, int k, ref bool ping, int gx, int gy)
        {
            RenderTexture src = ping ? ctx.SeedB : ctx.SeedA;
            RenderTexture dst = ping ? ctx.SeedA : ctx.SeedB;

            _cs.SetInt    ("_JFAStep", k);
            _cs.SetTexture(_kJFAStep, "_SeedBufIn",  src);
            _cs.SetTexture(_kJFAStep, "_SeedBufOut", dst);
            _cs.Dispatch  (_kJFAStep, gx, gy, 1);

            ping = !ping;
        }

        // ----------------------------------------------------------------
        // Cache invalidation
        //   Returns the earliest stage that has to re-run.
        // ----------------------------------------------------------------
        private static Stage DetermineDirty(PipelineContext c, int inputId, int srcW, int srcH,
                                            bool inputIsLinear, NormalMapSettings s,
                                            float bevelRadius, int jfaRange)
        {
            if (!c.Valid) return Stage.Binarize;

            if (c.SigInputId != inputId || c.SigSrcW != srcW || c.SigSrcH != srcH
                || c.SigInputIsLinear != inputIsLinear
                || c.SigThreshold != s.Threshold
                || c.SigInvert != s.InvertMask)
                return Stage.Binarize;

            if (!s.DisableBevel)
            {
                // A cached field computed for a larger range stays valid for a
                // smaller radius, so shrinking the bevel costs nothing.
                if (!c.JfaValid || jfaRange > c.JfaRangeCached)
                    return Stage.Jfa;

                if (c.SigBevelRadius != bevelRadius || c.SigProfile != s.ProfileType)
                    return Stage.Profile;
            }

            if (c.SigDisableBevel != s.DisableBevel || c.SigMode != s.InputMode)
                return Stage.Composite;

            if (c.SigStrength != s.Strength || c.SigNormalType != s.NormalMapType
                || c.SigDither != s.Dither)
                return Stage.Normal;

            return Stage.None;
        }

        private static void StoreSignature(PipelineContext c, int inputId, int srcW, int srcH,
                                           bool inputIsLinear, NormalMapSettings s, float bevelRadius)
        {
            c.SigInputId       = inputId;
            c.SigSrcW          = srcW;
            c.SigSrcH          = srcH;
            c.SigInputIsLinear = inputIsLinear;
            c.SigThreshold     = s.Threshold;
            c.SigInvert        = s.InvertMask;
            c.SigBevelRadius   = bevelRadius;
            c.SigProfile       = s.ProfileType;
            c.SigDisableBevel  = s.DisableBevel;
            c.SigMode          = s.InputMode;
            c.SigStrength      = s.Strength;
            c.SigNormalType    = s.NormalMapType;
            c.SigDither        = s.Dither;
            c.Valid            = true;
        }

        // ----------------------------------------------------------------
        // ProcessAndSave
        //   Runs the same pipeline at the source's native resolution, saves
        //   the result, refreshes AssetDatabase.
        // ----------------------------------------------------------------
        public SaveResult ProcessAndSave(Texture2D inputTex, NormalMapSettings s)
        {
            string assetPath = AssetDatabase.GetAssetPath(inputTex);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError("[NormalMapGenerator] Input texture is not an asset.");
                return SaveResult.Failed;
            }

            string dir  = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(assetPath);
            string outputDir = dir + "/output";

            EnsureDirectory(outputDir);

            string baseName = name + "_normal";
            string outputAssetPath = outputDir + "/" + baseName + ".png";

            if (!s.OverwriteExisting && File.Exists(ToPhysicalPath(outputAssetPath)))
            {
                int index = 1;
                while (File.Exists(ToPhysicalPath($"{outputDir}/{baseName} {index}.png")))
                {
                    index++;
                }
                outputAssetPath = $"{outputDir}/{baseName} {index}.png";
            }

            try
            {
                using (var ctx = new PipelineContext())
                {
                    RenderTexture rt = Run(ctx, inputTex, s, inputTex.width, inputTex.height);
                    SaveColorRT(rt, ToPhysicalPath(outputAssetPath));
                }

                AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate);

                TextureImporter importer = AssetImporter.GetAtPath(outputAssetPath) as TextureImporter;
                if (importer != null)
                {
                    bool needsReimport = false;
                    if (importer.textureType != TextureImporterType.NormalMap)
                    {
                        importer.textureType = TextureImporterType.NormalMap;
                        needsReimport = true;
                    }
                    if (importer.sRGBTexture)
                    {
                        importer.sRGBTexture = false;
                        needsReimport = true;
                    }
                    if (needsReimport)
                    {
                        importer.SaveAndReimport();
                    }
                }

                AssetDatabase.Refresh();
                Debug.Log($"[NormalMapGenerator] Saved: {outputAssetPath}");
                return SaveResult.Saved;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NormalMapGenerator] Save failed: {ex.Message}\n{ex.StackTrace}");
                return SaveResult.Failed;
            }
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static void SaveColorRT(RenderTexture rt, string physicalPath)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            // The RT is sRGB-flagged and the compute shader writes raw bytes
            // through the UAV, so this must stay a plain byte copy: matching
            // sRGB flags on both sides keeps ReadPixels from converting.
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
