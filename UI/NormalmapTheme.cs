using UnityEngine;
using UnityEditor;

namespace NormalmapGenerator
{
    /// <summary>
    /// dennoko.dev カラースキーマに基づくテーマ定義。
    /// OnGUI の先頭で Initialize() を呼び出すことでスタイルを遅延初期化する。
    /// dennokoworks_color_schema の色値・デザイン仕様を Unity IMGUI 向けに実装したもの。
    /// </summary>
    internal static class NormalmapTheme
    {
        // ─── Colors ──────────────────────────────────────────────────────────

        // surface (Neutral Layer)
        public static readonly Color Surface0 = Hex(0x121212); // app background
        public static readonly Color Surface1 = Hex(0x1e1e1e); // cards, inputs
        public static readonly Color Surface2 = Hex(0x2c2c2c); // hover, toolbar

        // outline
        public static readonly Color Outline = Hex(0x3a3a3a);

        // typography
        public static readonly Color TextPrimary   = Hex(0xffffff);
        public static readonly Color TextSecondary = Hex(0xcccccc);
        public static readonly Color TextTertiary  = Hex(0xaaaaaa);
        public static readonly Color TextDisabled  = Hex(0x555555);

        // semantic
        public static readonly Color SemanticError   = Hex(0x9b1b30);
        public static readonly Color SemanticWarning = Hex(0xffb74d);
        public static readonly Color SemanticSuccess = Hex(0x4caf50);
        public static readonly Color SemanticInfo    = Hex(0x64b5f6);

        // interaction
        public static readonly Color HoverOverlay = new Color(1f, 1f, 1f, 0.05f);

        // ─── Cached Textures ─────────────────────────────────────────────────

        private static Texture2D _texSurface0;
        private static Texture2D _texSurface1;
        private static Texture2D _texSurface2;
        private static Texture2D _texCard;        // Surface1 fill + Outline border (3x3)
        private static Texture2D _texAccentCard;  // Surface2 fill + Outline border (3x3)

        // ─── Styles ──────────────────────────────────────────────────────────

        private static bool _initialized;

        // Layout / Container
        public static GUIStyle CardStyle      { get; private set; }
        public static GUIStyle CardOuterStyle { get; private set; }
        public static GUIStyle ToolbarStyle   { get; private set; }

        // Typography
        public static GUIStyle TitleStyle            { get; private set; }
        public static GUIStyle SectionHeaderStyle    { get; private set; }
        public static GUIStyle ToggleSectionOnStyle  { get; private set; }
        public static GUIStyle ToggleSectionOffStyle { get; private set; }
        public static GUIStyle SecondaryTextStyle    { get; private set; }
        public static GUIStyle CaptionStyle          { get; private set; }

        // Buttons
        public static GUIStyle ActionButtonStyle    { get; private set; }
        public static GUIStyle SecondaryButtonStyle { get; private set; }
        public static GUIStyle MiniButtonStyle      { get; private set; }

        // Status bar
        public static GUIStyle StatusInfoStyle    { get; private set; }
        public static GUIStyle StatusSuccessStyle { get; private set; }
        public static GUIStyle StatusErrorStyle   { get; private set; }
        public static GUIStyle StatusWarningStyle { get; private set; }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>OnGUI の先頭で呼び出す。初回のみスタイルを構築する。</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            EnsureTextures();
            BuildStyles();
        }

        private static void EnsureTextures()
        {
            if (!_texSurface0)   _texSurface0   = MakeTex(Surface0);
            if (!_texSurface1)   _texSurface1   = MakeTex(Surface1);
            if (!_texSurface2)   _texSurface2   = MakeTex(Surface2);
            if (!_texCard)       _texCard       = MakeBorderedTex(Surface1, Outline);
            if (!_texAccentCard) _texAccentCard = MakeBorderedTex(Surface2, Outline);
        }

        private static void BuildStyles()
        {
            // ── Container ────────────────────────────────────────────────────

            CardStyle = new GUIStyle();
            CardStyle.normal.background = _texCard;
            CardStyle.border  = new RectOffset(1, 1, 1, 1);
            CardStyle.padding = new RectOffset(10, 10, 8, 8);
            CardStyle.margin  = new RectOffset(4, 4, 6, 6);

            CardOuterStyle = new GUIStyle();
            CardOuterStyle.normal.background = _texCard;
            CardOuterStyle.border  = new RectOffset(1, 1, 1, 1);
            CardOuterStyle.padding = new RectOffset(0, 0, 0, 0);
            CardOuterStyle.margin  = new RectOffset(4, 4, 6, 6);

            ToolbarStyle = new GUIStyle();
            ToolbarStyle.normal.background = _texSurface2;
            ToolbarStyle.padding = new RectOffset(6, 6, 4, 4);
            ToolbarStyle.margin  = new RectOffset(0, 0, 0, 0);

            // ── Typography ───────────────────────────────────────────────────

            TitleStyle = new GUIStyle(EditorStyles.boldLabel);
            TitleStyle.fontSize = 14;
            TitleStyle.normal.textColor = TextPrimary;

            SectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel);
            SectionHeaderStyle.fontSize = 10;
            SectionHeaderStyle.normal.textColor = TextTertiary;
            SectionHeaderStyle.margin = new RectOffset(0, 0, 0, 2);

            ToggleSectionOnStyle = new GUIStyle(EditorStyles.boldLabel);
            ToggleSectionOnStyle.fontSize = 10;
            ToggleSectionOnStyle.normal.textColor = TextPrimary;
            ToggleSectionOnStyle.margin = new RectOffset(0, 0, 0, 2);

            ToggleSectionOffStyle = new GUIStyle(EditorStyles.boldLabel);
            ToggleSectionOffStyle.fontSize = 10;
            ToggleSectionOffStyle.normal.textColor = TextTertiary;
            ToggleSectionOffStyle.margin = new RectOffset(0, 0, 0, 2);

            SecondaryTextStyle = new GUIStyle(EditorStyles.label);
            SecondaryTextStyle.normal.textColor = TextSecondary;
            SecondaryTextStyle.wordWrap = true;

            CaptionStyle = new GUIStyle(EditorStyles.miniLabel);
            CaptionStyle.normal.textColor = TextTertiary;

            // ── Buttons ──────────────────────────────────────────────────────

            ActionButtonStyle = new GUIStyle(GUI.skin.button);
            ActionButtonStyle.normal.background  = _texAccentCard;
            ActionButtonStyle.normal.textColor   = TextPrimary;
            ActionButtonStyle.hover.background   = MakeTex(Color.Lerp(Surface2, Color.white, 0.07f));
            ActionButtonStyle.hover.textColor    = TextPrimary;
            ActionButtonStyle.active.background  = MakeTex(Color.Lerp(Surface2, Color.white, 0.15f));
            ActionButtonStyle.active.textColor   = TextPrimary;
            ActionButtonStyle.border     = new RectOffset(1, 1, 1, 1);
            ActionButtonStyle.fontSize   = 13;
            ActionButtonStyle.fontStyle  = FontStyle.Bold;
            ActionButtonStyle.fixedHeight = 34;
            ActionButtonStyle.alignment  = TextAnchor.MiddleCenter;

            SecondaryButtonStyle = new GUIStyle(GUI.skin.button);
            SecondaryButtonStyle.normal.background = MakeBorderedTex(Surface1, Outline);
            SecondaryButtonStyle.normal.textColor  = TextSecondary;
            SecondaryButtonStyle.hover.background  = _texAccentCard;
            SecondaryButtonStyle.hover.textColor   = TextPrimary;
            SecondaryButtonStyle.active.background = MakeTex(Color.Lerp(Surface1, Color.white, 0.10f));
            SecondaryButtonStyle.active.textColor  = TextPrimary;
            SecondaryButtonStyle.border     = new RectOffset(1, 1, 1, 1);
            SecondaryButtonStyle.fontSize   = 11;
            SecondaryButtonStyle.fixedHeight = 26;
            SecondaryButtonStyle.alignment  = TextAnchor.MiddleCenter;

            MiniButtonStyle = new GUIStyle(EditorStyles.miniButton);
            MiniButtonStyle.normal.textColor = TextTertiary;
            MiniButtonStyle.hover.textColor  = TextSecondary;

            // ── Status Bar ───────────────────────────────────────────────────

            var statusBase = new GUIStyle(EditorStyles.helpBox);
            statusBase.border  = new RectOffset(1, 1, 1, 1);
            statusBase.padding = new RectOffset(8, 8, 5, 5);
            statusBase.margin  = new RectOffset(4, 4, 2, 2);
            statusBase.fontSize = 11;

            StatusInfoStyle = new GUIStyle(statusBase);
            StatusInfoStyle.normal.background = _texSurface1;
            StatusInfoStyle.normal.textColor  = TextSecondary;

            StatusSuccessStyle = new GUIStyle(statusBase);
            StatusSuccessStyle.normal.background = MakeTex(Color.Lerp(Surface1, SemanticSuccess, 0.3f));
            StatusSuccessStyle.normal.textColor  = SemanticSuccess;

            StatusErrorStyle = new GUIStyle(statusBase);
            StatusErrorStyle.normal.background = MakeTex(Color.Lerp(Surface1, SemanticError, 0.5f));
            StatusErrorStyle.normal.textColor  = new Color(1f, 0.65f, 0.65f);

            StatusWarningStyle = new GUIStyle(statusBase);
            StatusWarningStyle.normal.background = MakeTex(Color.Lerp(Surface1, SemanticWarning, 0.25f));
            StatusWarningStyle.normal.textColor  = SemanticWarning;
        }

        // ─── Texture Utilities ───────────────────────────────────────────────

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static Texture2D MakeBorderedTex(Color fillColor, Color borderColor)
        {
            const int size = 3;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y,
                        (x == 0 || x == size - 1 || y == 0 || y == size - 1)
                            ? borderColor
                            : fillColor);
            tex.Apply();
            tex.filterMode = FilterMode.Point;
            tex.hideFlags  = HideFlags.HideAndDontSave;
            return tex;
        }

        private static Color Hex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >>  8) & 0xFF) / 255f,
            ( rgb        & 0xFF) / 255f);
    }
}
