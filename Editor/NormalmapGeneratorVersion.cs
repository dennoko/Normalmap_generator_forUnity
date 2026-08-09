using UnityEditor;
using UnityEngine;
using System;
using System.IO;
using System.Runtime.CompilerServices;
// 名前空間 NormalmapGenerator から Dennoko.NormalmapGenerator を参照すると紛らわしいのでエイリアスを張る
using DennokoVersionChecker = Dennoko.NormalmapGenerator.DennokoVersionChecker;

namespace NormalmapGenerator
{
    // インポート時（コンパイル完了時）や起動時に自動的にアップデートチェックを実行する
    [InitializeOnLoad]
    internal static class NormalmapGeneratorVersion
    {
        // version.json の GUID (アセット移動に対応するため GUID 経由でパス解決する)
        private const string VersionJsonGuid = "e8fc01cbe32233747bbbf5421aa27554";
        // version.json をどうしても読めなかった場合の最終フォールバック（通常は使われない）
        private const string FallbackVersion = "0.0.0";
        private static string _currentCache = null;

        internal static string Current
        {
            get
            {
                // 失敗（null）はキャッシュしない。インポート直後で version.json が
                // まだ読めなかった場合でも、次回アクセス時に再試行できるようにする。
                if (string.IsNullOrEmpty(_currentCache))
                {
                    _currentCache = LoadLocalVersion();
                }
                return string.IsNullOrEmpty(_currentCache) ? FallbackVersion : _currentCache;
            }
        }

        // チェック先（設定されているリモートリポジトリに合わせる）
        internal const string RepoOwner       = "dennoko";
        internal const string RepoName        = "Normalmap_generator_forUnity";
        internal const string RepoBranch      = "main";
        internal const string VersionFilePath = "version.json";

        // セッションキー。State（比較結果）は保存しない — ローカル版が後から正しく解決され得るため、
        // 表示のたびに「保存した最新版 vs 現在のローカル版」で更新有無を再計算する。
        // ここでは取得が成功したか（Error だったか）だけ保存する。
        internal const string VerCheckDoneKey    = "NormalmapGenerator_VerCheck_Done";
        internal const string VerCheckErrorKey   = "NormalmapGenerator_VerCheck_Error";
        internal const string VerCheckLatestKey  = "NormalmapGenerator_VerCheck_Latest";
        internal const string VerCheckUrlKey     = "NormalmapGenerator_VerCheck_Url";
        internal const string VerCheckMessageKey = "NormalmapGenerator_VerCheck_Message";

        static NormalmapGeneratorVersion()
        {
            // 静的コンストラクタはドメインリロード中に走り、この時点では version.json が
            // AssetDatabase 未登録のことがある。delayCall で 1 tick 遅らせてから開始する。
            EditorApplication.delayCall += StartCheckBackgroundTask;
        }

        // 同一ドメイン内での二重リクエスト防止（ドメインリロードで false に戻る）
        private static bool _checking;

        internal static void StartCheckBackgroundTask()
        {
            // 成功済みなら再取得しない。だがエラー時は「インポート直後の一時的な失敗
            // （パッケージ取り込み時のドメインリロードでリクエストが中断される等）」を想定し、
            // 次のトリガー（ウィンドウを開く / ドメインリロード）で再試行する。
            bool done  = SessionState.GetBool(VerCheckDoneKey, false);
            bool error = SessionState.GetBool(VerCheckErrorKey, false);
            if (done && !error) return;
            if (_checking) return;
            _checking = true;

            DennokoVersionChecker.CheckAsync(
                RepoOwner, RepoName, RepoBranch, VersionFilePath, Current, OnVersionChecked);
        }

        /// <summary>手動での再取得。前回結果（成功/失敗・ローカル版キャッシュ）を破棄して再チェックする。</summary>
        internal static void ForceRecheck()
        {
            if (_checking) return; // 進行中なら何もしない
            _currentCache = null;  // ローカル版も読み直す（version.json を直したケースに対応）
            SessionState.SetBool(VerCheckDoneKey, false);
            SessionState.SetBool(VerCheckErrorKey, false);
            StartCheckBackgroundTask();
        }

        private static void OnVersionChecked(DennokoVersionChecker.Result result)
        {
            _checking = false;
            SessionState.SetBool(VerCheckDoneKey, true);
            SessionState.SetBool(VerCheckErrorKey, result.State == DennokoVersionChecker.State.Error);
            SessionState.SetString(VerCheckLatestKey, result.LatestVersion ?? string.Empty);
            SessionState.SetString(VerCheckUrlKey, result.Url ?? string.Empty);
            SessionState.SetString(VerCheckMessageKey, result.Message ?? string.Empty);

            // すでにエディタウィンドウが開かれている場合は表示を更新する
            var windows = Resources.FindObjectsOfTypeAll<NormalMapGeneratorWindow>();
            if (windows != null && windows.Length > 0)
            {
                foreach (var w in windows)
                {
                    if (w != null)
                    {
                        w.LoadVersionResultFromSessionState();
                    }
                }
            }
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
        }

        /// <summary>ローカルの version.json を読む。読めなければ null（呼び出し側で
        /// フォールバックし、次回アクセス時に再試行する）。</summary>
        private static string LoadLocalVersion()
        {
            // 1) GUID 経由（アセット移動に追従。ただし AssetDatabase 準備前は空を返し得る）
            var v = TryReadVersion(AssetDatabase.GUIDToAssetPath(VersionJsonGuid));
            if (v != null) return v;

            // 2) スクリプト位置からの相対探索（AssetDatabase 未準備でも解決できる保険）
            return TryReadVersion(ResolveVersionJsonByScriptPath());
        }

        private static string TryReadVersion(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var info = JsonUtility.FromJson<VersionInfo>(File.ReadAllText(path));
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NormalmapGeneratorVersion] Failed to read version.json ({path}): {e.Message}");
            }
            return null;
        }

        /// <summary>
        /// このスクリプトの位置を起点に上位フォルダを辿って version.json を探す。
        /// [CallerFilePath] はコンパイル時パスなので、他プロジェクトへインポートして
        /// 再コンパイルされれば、そのプロジェクト内の正しいパスに解決される
        /// （AssetDatabase のインポート完了状況に依存しない）。
        /// </summary>
        private static string ResolveVersionJsonByScriptPath([CallerFilePath] string scriptPath = null)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            var dir = Path.GetDirectoryName(scriptPath);
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "version.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
