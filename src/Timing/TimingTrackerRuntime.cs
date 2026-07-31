using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Timing
{
    /// <summary>
    /// Tap-based BPM and phase anchor editor.
    ///
    /// Ctrl+T selects the first beat of the new measured section. The first tap is
    /// interpreted as that exact floor beat. Regression over all taps estimates both:
    ///  - slope: source/chart BPM
    ///  - intercept: phase difference between the selected floor and the music beat
    ///
    /// Phase correction changes the immediately preceding zero-angle SetSpeed so the
    /// selected floor arrives at the measured phase. The selected floor then receives
    /// the measured BPM. No restore boundary is generated after the selected floor;
    /// the measured BPM intentionally continues into the following section.
    /// </summary>
    internal static class TimingTrackerRuntime
    {
        private const int WindowId = 731904;
        private const double MinimumTapSpacingSeconds = 0.060d;
        private const int MinimumTapCount = 4;
        private const int RecommendedTapCount = 8;

        // target chart BPM = source pulse BPM * factor.
        // factor=2 means the user tapped every two chart beats (half-time pulse).
        private static readonly double[] BeatFactors =
        {
            0.125d, 0.25d, 0.5d, 1d, 2d, 4d, 8d
        };

        private enum CaptureState
        {
            Idle,
            WaitingForFirstTap,
            Capturing,
            Completed
        }

        private sealed class TapAnalysis
        {
            public bool Valid;
            public string Error = string.Empty;
            public int TapCount;
            public int UsedTapCount;
            public int OutlierCount;
            public int MissedPulseCount;
            public double PulsePeriodSeconds;
            public double AudiblePulseBpm;
            public double Pitch;
            public double SourcePulseBpm;
            public double SourcePulseBpmStdError;
            public double CurrentEffectiveBpm;
            public int AutoFactorIndex;
            public double RmsJitterMs;
            public double MaxJitterMs;
            public double RegressionInterceptDsp;
            public double PhaseOffsetMs = double.NaN;
            public double PhaseStdErrorMs;
            public double FirstAudioTimeSeconds;
            public double ExpectedStartDsp = -1d;
            public string Confidence = "低";
        }

        private sealed class MeasurementTake
        {
            public int Number;
            public double TargetBpm;
            public double BpmUncertainty;
            public double PhaseMs;
            public double PhaseUncertaintyMs;
            public double Factor;
            public int TapCount;
            public int UsedTapCount;
            public int OutlierCount;
            public double RmsMs;
            public double MaxMs;
            public string Confidence = "低";
        }

        private sealed class AggregateMeasurement
        {
            public bool Valid;
            public string Error = string.Empty;
            public int TakeCount;
            public int TotalUsedTaps;
            public double TargetBpm;
            public double BpmSpread;
            public double PhaseMs;
            public double PhaseSpreadMs;
            public string Confidence = "低";
        }

        private sealed class ApplyPreview
        {
            public bool Valid;
            public string Error = string.Empty;
            public string Warning = string.Empty;
            public int TakeCount;
            public int AnchorFloor = -1;
            public double TargetBpm;
            public double PhaseMs;
            public double AnchorCurrentBpm;
            public double AnchorTargetBpm;
            public double AnchorSpanSeconds;
            public double AnchorCorrectionPercent;
            public bool ApplyPhase;
        }

        private static FieldInfo songField;
        private static FieldInfo song2Field;
        private static bool audioFieldsResolved;
        private static AudioSource lastAudioSource;

        private static readonly List<double> tapDspTimes = new List<double>();
        private static readonly List<MeasurementTake> savedTakes = new List<MeasurementTake>();
        private static Rect windowRect = new Rect(24f, 70f, 700f, 680f);
        private static Vector2 scrollPosition;
        private static CaptureState state;
        private static int startFloor = -1;
        private static string status = "エディター待機中";
        private static TapAnalysis analysis;
        private static int factorShift;
        private static double firstTapAudioTime;
        private static double expectedStartDsp = -1d;
        private static string currentLevelIdentity = string.Empty;
        private static bool waitingForTapKey;
        private static bool showExperimentalPlayCorrection;
        private static bool measurementApplied;

        internal static string Status { get { return status; } }
        internal static int PointCount { get { return tapDspTimes.Count; } }
        internal static int ArmedFloor { get { return startFloor; } }

        internal static void Initialize()
        {
            EnsureTapKeySetting();
            waitingForTapKey = false;
            showExperimentalPlayCorrection = false;
            ResetSession("開始床を選択してください", true, true);
        }

        internal static void Shutdown()
        {
            ResetSession("終了", true, true);
            waitingForTapKey = false;
            lastAudioSource = null;
        }

        internal static void CloseWindow()
        {
            if (Main.Settings != null) Main.Settings.TimingWindowVisible = false;
        }

        internal static void ReloadForCurrentLevel()
        {
            currentLevelIdentity = string.Empty;
            EnsureCurrentLevel();
            ResetSession("譜面を再読込しました。開始床を選択してください", true, true);
        }

        internal static void Update()
        {
            if (!Main.Enabled || Main.Settings == null || !Main.Settings.EnableTimingTracker)
                return;

            EnsureCurrentLevel();

            if (waitingForTapKey)
            {
                CaptureTapKeyBinding();
                return;
            }

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (control && Input.GetKeyDown(KeyCode.F6))
                Main.Settings.TimingWindowVisible = !Main.Settings.TimingWindowVisible;

            if (control && Input.GetKeyDown(KeyCode.T))
                ArmSelectedFloor();

            KeyCode tapKey = GetTapKey();
            if (!control && Input.GetKeyDown(tapKey))
                RecordTap();

            if (Input.GetKeyDown(KeyCode.Backspace) &&
                (state == CaptureState.Capturing || state == CaptureState.Completed))
                RemoveLastTap();

            if (control && Input.GetKeyDown(KeyCode.Return) && HasApplicableMeasurement())
                ApplyCurrentMeasurement();
            else if (Input.GetKeyDown(KeyCode.Return) && state == CaptureState.Capturing)
                FinishCapture();

            if (Input.GetKeyDown(KeyCode.Escape) && state != CaptureState.Idle)
                ResetSession("現在Takeを取り消しました", false, false);
        }

        internal static void DrawWindow()
        {
            if (!Main.Enabled || Main.Settings == null || !Main.Settings.EnableTimingTracker ||
                !Main.Settings.TimingWindowVisible)
                return;

            EnsureCurrentLevel();
            windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Screen.width - 120f));
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Screen.height - 60f));
            windowRect = GUI.Window(WindowId, windowRect, DrawWindowContents,
                "ADOFAI BPM + Phase Tap Anchor v0.9.19");
        }

        private static void DrawWindowContents(int id)
        {
            scnEditor editor = scnEditor.instance;
            GUILayout.BeginVertical();
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, false, true,
                GUILayout.Height(Mathf.Max(300f, windowRect.height - 58f)));

            GUILayout.Label("計測床: " + (startFloor >= 0 ? startFloor.ToString() : "未指定") +
                            " / 現在のタップ: " + tapDspTimes.Count +
                            " / 保存Take: " + savedTakes.Count);
            GUILayout.Label("状態: " + status);

            GUILayout.BeginHorizontal();
            GUILayout.Label("タップキー: " + GetTapKeyLabel(), GUILayout.Width(145f));
            if (GUILayout.Button(waitingForTapKey ? "次に押したキーを待機中..." : "タップキーを変更", GUILayout.Height(26f)))
                waitingForTapKey = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("選択床を新しい同期点にする  Ctrl+T", GUILayout.Height(30f)))
                ArmSelectedFloor();
            if (GUILayout.Button("計測とTakeを消去", GUILayout.Width(125f), GUILayout.Height(30f)))
                ResetSession("計測を消去しました", false, true);
            GUILayout.EndHorizontal();

            GUILayout.Space(5f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("1. 新しい区間の先頭床を選び、Ctrl+T");
            GUILayout.Label("2. その床より少し前から再生");
            GUILayout.Label("3. 選択床の音が来た瞬間を1回目として " + GetTapKeyLabel() + " を一定間隔で押す");
            GUILayout.Label("4. Enterで確定。BPMと選択床の位相を同時に解析");
            GUILayout.Label("5. 適用すると、直前の0° SetSpeed～選択床で位相を合わせ、選択床から計測BPMを継続");
            GUILayout.EndVertical();

            GUILayout.Space(6f);
            DrawCaptureStatus();
            GUILayout.Space(6f);
            DrawAnalysisAndApply(editor);

            GUILayout.Space(12f);
            if (GUILayout.Button((showExperimentalPlayCorrection ? "▼" : "▶") +
                                 " 実験的機能：プレイ誤差からSetSpeedを一括補正", GUILayout.Height(28f)))
                showExperimentalPlayCorrection = !showExperimentalPlayCorrection;
            if (showExperimentalPlayCorrection)
            {
                bool enabled = Main.Settings != null && Main.Settings.EnablePlayErrorCorrection;
                bool changed = GUILayout.Toggle(enabled, "プレイ誤差補正を有効にする（通常はOFF推奨）");
                if (Main.Settings != null) Main.Settings.EnablePlayErrorCorrection = changed;
                if (changed)
                    PlayErrorCorrectionRuntime.DrawPanel(editor);
                else
                    GUILayout.Label("無効です。上のチェックを付けた場合だけ記録・解析します。");
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label("ウィンドウ: Ctrl+F6 / タップ: " + GetTapKeyLabel() + " / 適用: Ctrl+Enter");
            if (GUILayout.Button("閉じる", GUILayout.Width(65f)))
                Main.Settings.TimingWindowVisible = false;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 25f));
        }

        private static void DrawCaptureStatus()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            switch (state)
            {
                case CaptureState.Idle:
                    GUILayout.Label("計測床を指定すると待機状態になります。");
                    break;
                case CaptureState.WaitingForFirstTap:
                    GUILayout.Label("待機中：少し前から再生し、計測床の音で最初の" + GetTapKeyLabel() + "を押してください。");
                    break;
                case CaptureState.Capturing:
                    GUILayout.Label("計測中：同じ脈を保ってください。Enterで確定、Backspaceで直前を削除。");
                    if (tapDspTimes.Count >= RecommendedTapCount)
                    {
                        TapAnalysis live = AnalyzeTaps(scnEditor.instance);
                        if (live.Valid)
                            GUILayout.Label("現在の信頼度: " + live.Confidence + " / RMS " +
                                            live.RmsJitterMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
                    }
                    break;
                case CaptureState.Completed:
                    GUILayout.Label("計測完了：現在Takeを保存して再計測するか、そのまま適用できます。");
                    break;
            }

            Rect meter = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            GUI.Box(meter, GUIContent.none);
            int shown = Math.Min(tapDspTimes.Count, 32);
            for (int i = 0; i < shown; i++)
            {
                float x = meter.x + 6f + (meter.width - 12f) * (shown <= 1 ? 0f : (float)i / (shown - 1));
                GUI.DrawTexture(new Rect(x - 1f, meter.y + 4f, 3f, meter.height - 8f), Texture2D.whiteTexture);
            }
            GUILayout.EndVertical();
        }

        private static void DrawAnalysisAndApply(scnEditor editor)
        {
            TapAnalysis current = null;
            MeasurementTake currentTake = null;
            if (tapDspTimes.Count >= MinimumTapCount)
            {
                current = AnalyzeTaps(editor);
                analysis = current;
                if (current.Valid)
                    currentTake = BuildCurrentTake(current);
            }

            if (tapDspTimes.Count < MinimumTapCount && savedTakes.Count == 0)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("あと " + Math.Max(0, MinimumTapCount - tapDspTimes.Count) +
                                " 回以上タップすると解析できます。推奨は8～16回です。");
                GUILayout.EndVertical();
                return;
            }

            if (current != null && !current.Valid)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("解析できません: " + current.Error);
                GUILayout.EndVertical();
            }
            else if (currentTake != null)
            {
                DrawCurrentTake(current, currentTake);
            }

            DrawSavedTakes();

            AggregateMeasurement aggregate = BuildAggregate(currentTake);
            if (!aggregate.Valid)
                return;

            GUILayout.Space(6f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("適用に使う統合結果");
            GUILayout.Label("Take: " + aggregate.TakeCount + " / 使用タップ合計: " + aggregate.TotalUsedTaps);
            GUILayout.Label("BPM: " + aggregate.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                            " / Take間ばらつき: ±" + aggregate.BpmSpread.ToString("0.######", CultureInfo.InvariantCulture));
            GUILayout.Label("位相: " + FormatSigned(aggregate.PhaseMs, "0.0") +
                            "ms / Take間ばらつき: ±" + aggregate.PhaseSpreadMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
            GUILayout.Label("統合信頼度: " + aggregate.Confidence);
            GUILayout.EndVertical();

            DrawApplyPreview(editor, aggregate);
        }

        private static void DrawCurrentTake(TapAnalysis current, MeasurementTake take)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("現在Take");
            GUILayout.Label("タップ: " + current.UsedTapCount + "/" + current.TapCount +
                            " 使用 / 外れ値除外: " + current.OutlierCount +
                            " / 押し忘れ推定: " + current.MissedPulseCount);
            GUILayout.Label("聞こえている脈: " + current.AudiblePulseBpm.ToString("0.###", CultureInfo.InvariantCulture) +
                            " BPM / pitch: " + current.Pitch.ToString("0.###", CultureInfo.InvariantCulture));
            GUILayout.Label("1タップ = " + FactorLabel(take.Factor) + "拍 と推定");
            GUILayout.Label("現在BPM: " + current.CurrentEffectiveBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                            " → 計測BPM: " + take.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                            " ±" + take.BpmUncertainty.ToString("0.######", CultureInfo.InvariantCulture));
            if (!double.IsNaN(take.PhaseMs))
            {
                GUILayout.Label("選択床の位相: " + FormatSigned(take.PhaseMs, "0.0") +
                                "ms（+ は譜面が曲より早く到着）");
            }
            else
            {
                GUILayout.Label("位相: 開始DSP基準を取得できなかったためBPMのみ使用可能");
            }
            GUILayout.Label("タップぶれ RMS: " + current.RmsJitterMs.ToString("0.0", CultureInfo.InvariantCulture) +
                            "ms / 最大: " + current.MaxJitterMs.ToString("0.0", CultureInfo.InvariantCulture) +
                            "ms / 信頼度: " + current.Confidence);
            if (current.FirstAudioTimeSeconds >= 0d)
                GUILayout.Label("最初のタップの音源位置: " + FormatTime(current.FirstAudioTimeSeconds));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("拍間隔 ÷2", GUILayout.Width(95f)))
                factorShift = Mathf.Max(factorShift - 1, -current.AutoFactorIndex);
            if (GUILayout.Button("自動判定へ戻す", GUILayout.Width(125f)))
                factorShift = 0;
            if (GUILayout.Button("拍間隔 ×2", GUILayout.Width(95f)))
                factorShift = Mathf.Min(factorShift + 1, BeatFactors.Length - 1 - current.AutoFactorIndex);
            GUILayout.EndHorizontal();

            if (current.Confidence == "低")
                GUILayout.Label("信頼度が低めです。Takeを追加して中央値を使うか、8～16回で取り直してください。");

            GUI.enabled = state == CaptureState.Completed && !measurementApplied;
            if (GUILayout.Button("このTakeを保存して同じ床をもう一度計測", GUILayout.Height(28f)))
                SaveCurrentTakeAndRearm();
            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        private static void DrawSavedTakes()
        {
            if (savedTakes.Count == 0) return;

            GUILayout.Space(6f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("保存Take一覧（中央値で統合）");
            if (GUILayout.Button("保存Takeを消去", GUILayout.Width(115f)))
            {
                savedTakes.Clear();
                measurementApplied = false;
                status = "保存Takeを消去しました";
            }
            GUILayout.EndHorizontal();

            for (int i = 0; i < savedTakes.Count; i++)
            {
                MeasurementTake take = savedTakes[i];
                GUILayout.Label("Take " + take.Number + ": " +
                                take.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) + " BPM / " +
                                FormatSigned(take.PhaseMs, "0.0") + "ms / RMS " +
                                take.RmsMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms / " + take.Confidence);
            }
            GUILayout.EndVertical();
        }

        private static void DrawApplyPreview(scnEditor editor, AggregateMeasurement aggregate)
        {
            GUILayout.Space(6f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("適用プレビュー");

            bool phaseEnabled = Main.Settings == null || Main.Settings.EnableTapPhaseCorrection;
            bool newPhaseEnabled = GUILayout.Toggle(phaseEnabled, "BPMと同時に選択床の位相も補正する");
            if (Main.Settings != null && newPhaseEnabled != Main.Settings.EnableTapPhaseCorrection)
                Main.Settings.EnableTapPhaseCorrection = newPhaseEnabled;

            ApplyPreview preview = BuildApplyPreview(editor, aggregate);
            if (!preview.Valid)
            {
                GUILayout.Label("適用不可: " + preview.Error);
                if (preview.Error.IndexOf("位相", StringComparison.OrdinalIgnoreCase) >= 0)
                    GUILayout.Label("上の位相補正をOFFにすると、計測床へのBPM適用だけは実行できます。");
                GUILayout.EndVertical();
                return;
            }

            if (preview.ApplyPhase)
            {
                GUILayout.Label("床 " + preview.AnchorFloor + "（直前の0° SetSpeed）: " +
                                preview.AnchorCurrentBpm.ToString("0.######", CultureInfo.InvariantCulture) + " → " +
                                preview.AnchorTargetBpm.ToString("0.######", CultureInfo.InvariantCulture) + " BPM");
                GUILayout.Label("アンカー～計測床: " + preview.AnchorSpanSeconds.ToString("0.###", CultureInfo.InvariantCulture) +
                                "秒 / 位相補正量: " + FormatSigned(preview.AnchorCorrectionPercent, "0.####") + "%");
            }
            else
            {
                GUILayout.Label("位相補正: なし" + (string.IsNullOrEmpty(preview.Warning) ? string.Empty : "（" + preview.Warning + "）"));
            }

            GUILayout.Label("床 " + startFloor + ": " + preview.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                            " BPMへ設定");
            GUILayout.Label("後端の復元イベントは追加しません。このBPMを後続区間へそのまま引き継ぎます。");
            if (!string.IsNullOrEmpty(preview.Warning))
                GUILayout.Label("注意: " + preview.Warning);

            string applyLabel = measurementApplied
                ? "適用済み（同じ結果の二重適用を防止中）"
                : preview.ApplyPhase
                    ? "BPM＋位相を1回のUndo単位で適用"
                    : "BPMを1回のUndo単位で適用";
            GUI.enabled = editor != null && !measurementApplied;
            if (GUILayout.Button(applyLabel, GUILayout.Height(36f)))
                ApplyMeasurement(editor, aggregate);
            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        private static void ArmSelectedFloor()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                status = "エディターが見つかりません";
                return;
            }

            int floor;
            try { floor = EditorSelectionCompat.ResolveSelectedFloor(editor, -1); }
            catch { floor = -1; }
            if (floor < 0)
            {
                status = "床を1個選択してください";
                return;
            }

            startFloor = floor;
            ResetSession("床 " + floor + " を新しい同期点に設定。少し前から再生してください", false, true);
            if (Main.Settings != null) Main.Settings.TimingWindowVisible = true;
        }

        private static void RecordTap()
        {
            if (state == CaptureState.Idle || startFloor < 0)
            {
                status = "先に計測床を指定してください";
                return;
            }

            AudioSource source = GetActiveAudioSource();
            if (source == null || source.clip == null || !source.isPlaying)
            {
                status = "再生中の音源が見つかりません";
                return;
            }

            double now = AudioSettings.dspTime;
            if (tapDspTimes.Count > 0 && now - tapDspTimes[tapDspTimes.Count - 1] < MinimumTapSpacingSeconds)
            {
                status = "近すぎる二重入力を無視しました";
                return;
            }

            if (state == CaptureState.WaitingForFirstTap || state == CaptureState.Completed)
            {
                tapDspTimes.Clear();
                factorShift = 0;
                analysis = null;
                measurementApplied = false;
                firstTapAudioTime = ReadAudioTime(source);
                expectedStartDsp = ResolveExpectedStartDsp();
                state = CaptureState.Capturing;
            }

            tapDspTimes.Add(now);
            analysis = null;
            status = "計測中: " + tapDspTimes.Count + "回（Enterで確定）";
        }

        private static double ResolveExpectedStartDsp()
        {
            try
            {
                scnEditor editor = scnEditor.instance;
                scrConductor conductor = scrConductor.instance;
                if (editor == null || conductor == null || editor.floors == null ||
                    startFloor < 0 || startFloor >= editor.floors.Count)
                    return -1d;
                return conductor.dspTimeSongPosZero + editor.floors[startFloor].entryTimePitchAdj;
            }
            catch
            {
                return -1d;
            }
        }

        private static void RemoveLastTap()
        {
            if (tapDspTimes.Count == 0) return;
            tapDspTimes.RemoveAt(tapDspTimes.Count - 1);
            analysis = null;
            factorShift = 0;
            measurementApplied = false;
            state = tapDspTimes.Count == 0 ? CaptureState.WaitingForFirstTap : CaptureState.Capturing;
            status = "直前のタップを削除しました。残り " + tapDspTimes.Count + "回";
        }

        private static void FinishCapture()
        {
            if (tapDspTimes.Count < MinimumTapCount)
            {
                status = "最低" + MinimumTapCount + "回タップしてください";
                return;
            }

            analysis = AnalyzeTaps(scnEditor.instance);
            if (!analysis.Valid)
            {
                status = "解析失敗: " + analysis.Error;
                return;
            }

            state = CaptureState.Completed;
            measurementApplied = false;
            status = "計測完了: " + analysis.UsedTapCount + "/" + analysis.TapCount + "タップを使用 / 信頼度 " + analysis.Confidence;
        }

        private static TapAnalysis AnalyzeTaps(scnEditor editor)
        {
            TapAnalysis result = new TapAnalysis();
            result.FirstAudioTimeSeconds = firstTapAudioTime;
            result.ExpectedStartDsp = expectedStartDsp;
            result.TapCount = tapDspTimes.Count;

            if (tapDspTimes.Count < MinimumTapCount)
            {
                result.Error = "タップ数が不足しています";
                return result;
            }

            List<double> intervals = new List<double>();
            for (int i = 1; i < tapDspTimes.Count; i++)
            {
                double interval = tapDspTimes[i] - tapDspTimes[i - 1];
                if (interval >= MinimumTapSpacingSeconds && interval <= 8d)
                    intervals.Add(interval);
            }
            if (intervals.Count < MinimumTapCount - 1)
            {
                result.Error = "有効なタップ間隔が不足しています";
                return result;
            }

            double seedPeriod = Median(intervals);
            if (seedPeriod <= 0d)
            {
                result.Error = "タップ間隔を計算できません";
                return result;
            }

            List<double> normalizedIntervals = new List<double>();
            for (int i = 0; i < intervals.Count; i++)
            {
                int steps = Mathf.Clamp((int)Math.Round(intervals[i] / seedPeriod), 1, 8);
                normalizedIntervals.Add(intervals[i] / steps);
            }
            double periodSeed = Median(normalizedIntervals);

            List<double> pulseIndices = new List<double> { 0d };
            double cumulative = 0d;
            int missed = 0;
            for (int i = 0; i < intervals.Count; i++)
            {
                int steps = Mathf.Clamp((int)Math.Round(intervals[i] / periodSeed), 1, 8);
                cumulative += steps;
                pulseIndices.Add(cumulative);
                missed += steps - 1;
            }

            double intercept;
            double period;
            if (!LinearRegression(pulseIndices, tapDspTimes, out intercept, out period) || period <= 0d)
            {
                result.Error = "回帰計算に失敗しました";
                return result;
            }

            List<double> firstResiduals = BuildResidualsMs(pulseIndices, tapDspTimes, intercept, period);
            double residualMedian = Median(firstResiduals);
            double mad = Median(firstResiduals.Select(x => Math.Abs(x - residualMedian)));
            double outlierThresholdMs = Math.Max(30d, 3.5d * 1.4826d * mad);

            List<double> usedX = new List<double>();
            List<double> usedY = new List<double>();
            for (int i = 0; i < tapDspTimes.Count; i++)
            {
                if (Math.Abs(firstResiduals[i] - residualMedian) <= outlierThresholdMs)
                {
                    usedX.Add(pulseIndices[i]);
                    usedY.Add(tapDspTimes[i]);
                }
            }

            if (usedX.Count >= MinimumTapCount && usedX.Count < tapDspTimes.Count)
            {
                if (!LinearRegression(usedX, usedY, out intercept, out period) || period <= 0d)
                {
                    result.Error = "外れ値除外後の回帰計算に失敗しました";
                    return result;
                }
            }
            else
            {
                usedX = new List<double>(pulseIndices);
                usedY = new List<double>(tapDspTimes);
            }

            List<double> residualsMs = BuildResidualsMs(usedX, usedY, intercept, period);
            double rms = Math.Sqrt(residualsMs.Sum(x => x * x) / residualsMs.Count);
            double max = residualsMs.Max(x => Math.Abs(x));
            double audiblePulseBpm = 60d / period;

            double slopeStdError;
            double interceptStdError;
            CalculateRegressionErrors(usedX, usedY, intercept, period, out slopeStdError, out interceptStdError);

            AudioSource source = GetActiveAudioSource();
            double pitch = source != null && source.pitch > 0.0001f ? source.pitch : 1d;
            double sourcePulseBpm = audiblePulseBpm / pitch;
            double sourcePulseBpmStdError = 60d * slopeStdError / (period * period) / pitch;

            double currentBpm = 0d;
            if (editor != null && editor.levelData != null && editor.floors != null &&
                startFloor >= 0 && startFloor < editor.floors.Count)
                currentBpm = editor.levelData.bpm * editor.floors[startFloor].speed;
            if (currentBpm <= 0.001d && editor != null && editor.levelData != null)
                currentBpm = editor.levelData.bpm;
            if (currentBpm <= 0.001d)
                currentBpm = sourcePulseBpm;

            int bestFactor = 0;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < BeatFactors.Length; i++)
            {
                double candidate = sourcePulseBpm * BeatFactors[i];
                if (candidate <= 0d) continue;
                double distance = Math.Abs(Math.Log(candidate / currentBpm));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFactor = i;
                }
            }

            result.Valid = true;
            result.UsedTapCount = usedY.Count;
            result.OutlierCount = tapDspTimes.Count - usedY.Count;
            result.MissedPulseCount = missed;
            result.PulsePeriodSeconds = period;
            result.AudiblePulseBpm = audiblePulseBpm;
            result.Pitch = pitch;
            result.SourcePulseBpm = sourcePulseBpm;
            result.SourcePulseBpmStdError = sourcePulseBpmStdError;
            result.CurrentEffectiveBpm = currentBpm;
            result.AutoFactorIndex = bestFactor;
            result.RmsJitterMs = rms;
            result.MaxJitterMs = max;
            result.RegressionInterceptDsp = intercept;
            result.PhaseStdErrorMs = interceptStdError * 1000d;
            if (expectedStartDsp >= 0d)
                result.PhaseOffsetMs = (intercept - expectedStartDsp) * 1000d;
            result.Confidence = DetermineConfidence(result.UsedTapCount, result.OutlierCount, rms, max);
            return result;
        }

        private static List<double> BuildResidualsMs(IList<double> x, IList<double> y,
            double intercept, double slope)
        {
            List<double> result = new List<double>(x.Count);
            for (int i = 0; i < x.Count; i++)
                result.Add((y[i] - (intercept + slope * x[i])) * 1000d);
            return result;
        }

        private static void CalculateRegressionErrors(IList<double> x, IList<double> y,
            double intercept, double slope, out double slopeStdError, out double interceptStdError)
        {
            slopeStdError = 0d;
            interceptStdError = 0d;
            if (x == null || y == null || x.Count != y.Count || x.Count < 3) return;

            double meanX = x.Average();
            double sxx = 0d;
            double sse = 0d;
            for (int i = 0; i < x.Count; i++)
            {
                double dx = x[i] - meanX;
                sxx += dx * dx;
                double residual = y[i] - (intercept + slope * x[i]);
                sse += residual * residual;
            }
            if (sxx <= 1e-12d) return;
            double variance = sse / Math.Max(1, x.Count - 2);
            slopeStdError = Math.Sqrt(Math.Max(0d, variance / sxx));
            interceptStdError = Math.Sqrt(Math.Max(0d, variance * (1d / x.Count + meanX * meanX / sxx)));
        }

        private static string DetermineConfidence(int usedTaps, int outliers, double rms, double max)
        {
            double outlierRatio = usedTaps + outliers <= 0 ? 1d : (double)outliers / (usedTaps + outliers);
            if (usedTaps >= 8 && rms <= 25d && max <= 60d && outlierRatio <= 0.25d)
                return "高";
            if (usedTaps >= 6 && rms <= 45d && max <= 100d && outlierRatio <= 0.40d)
                return "中";
            return "低";
        }

        private static bool LinearRegression(IList<double> x, IList<double> y,
            out double intercept, out double slope)
        {
            intercept = 0d;
            slope = 0d;
            if (x == null || y == null || x.Count != y.Count || x.Count < 2) return false;

            double meanX = x.Average();
            double meanY = y.Average();
            double numerator = 0d;
            double denominator = 0d;
            for (int i = 0; i < x.Count; i++)
            {
                double dx = x[i] - meanX;
                numerator += dx * (y[i] - meanY);
                denominator += dx * dx;
            }
            if (denominator <= 1e-12d) return false;
            slope = numerator / denominator;
            intercept = meanY - slope * meanX;
            return !double.IsNaN(slope) && !double.IsInfinity(slope);
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] sorted = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) * 0.5d
                : sorted[middle];
        }

        private static double RobustSpread(IEnumerable<double> values)
        {
            double[] valid = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToArray();
            if (valid.Length <= 1) return 0d;
            double median = Median(valid);
            return 1.4826d * Median(valid.Select(x => Math.Abs(x - median)));
        }

        private static MeasurementTake BuildCurrentTake(TapAnalysis current)
        {
            if (current == null || !current.Valid) return null;
            int factorIndex = Mathf.Clamp(current.AutoFactorIndex + factorShift, 0, BeatFactors.Length - 1);
            double factor = BeatFactors[factorIndex];
            return new MeasurementTake
            {
                Number = savedTakes.Count + 1,
                TargetBpm = current.SourcePulseBpm * factor,
                BpmUncertainty = current.SourcePulseBpmStdError * factor,
                PhaseMs = current.PhaseOffsetMs,
                PhaseUncertaintyMs = current.PhaseStdErrorMs,
                Factor = factor,
                TapCount = current.TapCount,
                UsedTapCount = current.UsedTapCount,
                OutlierCount = current.OutlierCount,
                RmsMs = current.RmsJitterMs,
                MaxMs = current.MaxJitterMs,
                Confidence = current.Confidence
            };
        }

        private static void SaveCurrentTakeAndRearm()
        {
            TapAnalysis current = AnalyzeTaps(scnEditor.instance);
            MeasurementTake take = BuildCurrentTake(current);
            if (take == null || take.TargetBpm <= 0.01d || take.TargetBpm >= 1000000d)
            {
                status = "現在Takeを保存できません";
                return;
            }

            take.Number = savedTakes.Count + 1;
            savedTakes.Add(take);
            tapDspTimes.Clear();
            analysis = null;
            factorShift = 0;
            firstTapAudioTime = -1d;
            expectedStartDsp = -1d;
            state = CaptureState.WaitingForFirstTap;
            measurementApplied = false;
            status = "Take " + take.Number + " を保存。同じ床をもう一度計測できます";
        }

        private static AggregateMeasurement BuildAggregate(MeasurementTake currentTake)
        {
            AggregateMeasurement result = new AggregateMeasurement();
            List<MeasurementTake> all = new List<MeasurementTake>(savedTakes);
            if (currentTake != null && state == CaptureState.Completed)
                all.Add(currentTake);
            if (all.Count == 0)
            {
                result.Error = "使用できるTakeがありません";
                return result;
            }

            double target = Median(all.Select(x => x.TargetBpm));
            double phase = Median(all.Select(x => x.PhaseMs));
            if (double.IsNaN(target) || target <= 0.01d || target >= 1000000d)
            {
                result.Error = "統合BPMが無効です";
                return result;
            }

            result.Valid = true;
            result.TakeCount = all.Count;
            result.TotalUsedTaps = all.Sum(x => x.UsedTapCount);
            result.TargetBpm = target;
            result.BpmSpread = all.Count == 1 ? all[0].BpmUncertainty : RobustSpread(all.Select(x => x.TargetBpm));
            result.PhaseMs = phase;
            result.PhaseSpreadMs = all.Count == 1 ? all[0].PhaseUncertaintyMs : RobustSpread(all.Select(x => x.PhaseMs));

            int high = all.Count(x => x.Confidence == "高");
            int low = all.Count(x => x.Confidence == "低");
            if (high >= Math.Max(1, (all.Count + 1) / 2) && result.PhaseSpreadMs <= 12d)
                result.Confidence = "高";
            else if (low < all.Count && result.PhaseSpreadMs <= 30d)
                result.Confidence = "中";
            else
                result.Confidence = "低";
            return result;
        }

        private static bool HasApplicableMeasurement()
        {
            if (startFloor < 0 || measurementApplied || state == CaptureState.Capturing) return false;
            if (savedTakes.Count > 0) return true;
            return state == CaptureState.Completed && tapDspTimes.Count >= MinimumTapCount;
        }

        private static void ApplyCurrentMeasurement()
        {
            scnEditor editor = scnEditor.instance;
            TapAnalysis current = state == CaptureState.Completed ? AnalyzeTaps(editor) : null;
            MeasurementTake currentTake = BuildCurrentTake(current);
            AggregateMeasurement aggregate = BuildAggregate(currentTake);
            if (!aggregate.Valid)
            {
                status = "適用できる計測結果がありません: " + aggregate.Error;
                return;
            }
            ApplyMeasurement(editor, aggregate);
        }

        private static ApplyPreview BuildApplyPreview(scnEditor editor, AggregateMeasurement aggregate)
        {
            ApplyPreview preview = new ApplyPreview();
            preview.TakeCount = aggregate == null ? 0 : aggregate.TakeCount;
            if (editor == null || editor.levelData == null || editor.floors == null ||
                startFloor < 0 || startFloor >= editor.floors.Count)
            {
                preview.Error = "計測床が無効です";
                return preview;
            }
            if (aggregate == null || !aggregate.Valid)
            {
                preview.Error = aggregate == null ? "統合結果がありません" : aggregate.Error;
                return preview;
            }
            if (HasNonZeroAngleSpeed(editor, startFloor, startFloor))
            {
                preview.Error = "計測床にangleOffset付きSetSpeedがあります。自動適用は安全のため停止しました";
                return preview;
            }

            preview.TargetBpm = aggregate.TargetBpm;
            preview.PhaseMs = aggregate.PhaseMs;
            bool phaseRequested = Main.Settings == null || Main.Settings.EnableTapPhaseCorrection;
            double absolutePhaseLimit = Main.Settings == null ? 150d : Main.Settings.TapPhaseMaxAbsoluteMs;
            if (phaseRequested && !double.IsNaN(aggregate.PhaseMs) && Math.Abs(aggregate.PhaseMs) > absolutePhaseLimit)
            {
                preview.Error = "位相差が絶対安全上限 ±" + absolutePhaseLimit.ToString("0", CultureInfo.InvariantCulture) +
                                "msを超えます。最初のタップが選択床の拍だったか確認してください";
                return preview;
            }
            float ignoreMs = Main.Settings == null ? 2f : Main.Settings.TapPhaseIgnoreMs;
            if (!phaseRequested || double.IsNaN(aggregate.PhaseMs) || Math.Abs(aggregate.PhaseMs) < ignoreMs)
            {
                preview.Valid = true;
                preview.ApplyPhase = false;
                if (!phaseRequested) preview.Warning = "位相補正がOFF";
                else if (double.IsNaN(aggregate.PhaseMs)) preview.Warning = "位相基準を取得できませんでした";
                else preview.Warning = "位相差が無視閾値未満";
                return preview;
            }

            int anchorFloor = FindPreviousZeroAngleSpeedFloor(editor, startFloor);
            if (anchorFloor < 0) anchorFloor = 0;
            if (anchorFloor >= startFloor)
            {
                preview.Valid = true;
                preview.ApplyPhase = false;
                preview.Warning = "計測床より前に位相調整区間がありません";
                return preview;
            }
            if (HasNonZeroAngleSpeed(editor, anchorFloor, startFloor - 1))
            {
                preview.Error = "位相アンカー～計測床にangleOffset付きSetSpeedがあります";
                return preview;
            }

            double span = editor.floors[startFloor].entryTimePitchAdj - editor.floors[anchorFloor].entryTimePitchAdj;
            double minimumSpan = Main.Settings == null ? 0.35d : Main.Settings.TapMinimumAnchorSpanSeconds;
            if (span < minimumSpan)
            {
                preview.Error = "位相調整区間が短すぎます（" + span.ToString("0.###", CultureInfo.InvariantCulture) + "秒）";
                return preview;
            }

            double desiredSpan = span + aggregate.PhaseMs / 1000d;
            if (desiredSpan <= 0.05d)
            {
                preview.Error = "位相補正後の区間長が不正です";
                return preview;
            }

            double currentAnchorBpm = editor.levelData.bpm * editor.floors[anchorFloor].speed;
            if (currentAnchorBpm <= 0.01d)
            {
                preview.Error = "アンカーBPMを取得できません";
                return preview;
            }
            double targetAnchorBpm = currentAnchorBpm * span / desiredSpan;
            double correctionPercent = (targetAnchorBpm / currentAnchorBpm - 1d) * 100d;
            double maxPercent = Main.Settings == null ? 3d : Main.Settings.TapPhaseMaxCorrectionPercent;
            if (Math.Abs(correctionPercent) > maxPercent)
            {
                preview.Error = "位相補正が安全上限 ±" + maxPercent.ToString("0.##", CultureInfo.InvariantCulture) +
                                "%を超えます（" + FormatSigned(correctionPercent, "0.####") + "%）";
                return preview;
            }

            preview.Valid = true;
            preview.ApplyPhase = true;
            preview.AnchorFloor = anchorFloor;
            preview.AnchorCurrentBpm = currentAnchorBpm;
            preview.AnchorTargetBpm = targetAnchorBpm;
            preview.AnchorSpanSeconds = span;
            preview.AnchorCorrectionPercent = correctionPercent;
            return preview;
        }

        private static void ApplyMeasurement(scnEditor editor, AggregateMeasurement aggregate)
        {
            ApplyPreview preview = BuildApplyPreview(editor, aggregate);
            if (!preview.Valid)
            {
                status = "適用できません: " + preview.Error;
                return;
            }

            try
            {
                using (new SaveStateScope(editor, false, true, false))
                {
                    if (preview.ApplyPhase)
                    {
                        LevelEvent anchorSpeed = FindLastZeroAngleSpeedEvent(editor, preview.AnchorFloor);
                        if (anchorSpeed == null)
                        {
                            anchorSpeed = new LevelEvent(preview.AnchorFloor, LevelEventType.SetSpeed);
                            editor.events.Add(anchorSpeed);
                        }
                        SetAbsoluteBpm(anchorSpeed, preview.AnchorTargetBpm);
                    }

                    LevelEvent startSpeed = FindLastZeroAngleSpeedEvent(editor, startFloor);
                    if (startSpeed == null)
                    {
                        startSpeed = new LevelEvent(startFloor, LevelEventType.SetSpeed);
                        editor.events.Add(startSpeed);
                    }
                    SetAbsoluteBpm(startSpeed, preview.TargetBpm);

                    editor.ApplyEventsToFloors();
                    editor.RemakePath(true, true);
                }

                measurementApplied = true;
                status = preview.ApplyPhase
                    ? "床 " + preview.AnchorFloor + " で位相を補正し、床 " + startFloor + " から " +
                      preview.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                      " BPMを継続適用しました（後端復元なし）"
                    : "床 " + startFloor + " から " +
                      preview.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                      " BPMを継続適用しました（後端復元なし）";
            }
            catch (Exception ex)
            {
                status = "BPM/位相適用失敗: " + ex.Message;
                if (Main.Logger != null) Main.Logger.Error(ex.ToString());
            }
        }

        private static void SetAbsoluteBpm(LevelEvent speed, double bpm)
        {
            float rounded = (float)Math.Round(bpm, 6);
            speed["speedType"] = SpeedType.Bpm;
            speed["beatsPerMinute"] = rounded;
            speed["bpmMultiplier"] = 1f;
            speed["angleOffset"] = 0f;
        }

        private static int FindPreviousZeroAngleSpeedFloor(scnEditor editor, int beforeFloor)
        {
            if (editor == null || editor.events == null) return -1;
            int best = -1;
            for (int i = 0; i < editor.events.Count; i++)
            {
                LevelEvent e = editor.events[i];
                if (e == null || !e.active || e.eventType != LevelEventType.SetSpeed || e.floor >= beforeFloor) continue;
                if (!IsZeroAngleSpeed(e)) continue;
                if (e.floor > best) best = e.floor;
            }
            return best;
        }

        private static LevelEvent FindLastZeroAngleSpeedEvent(scnEditor editor, int floor)
        {
            if (editor == null || editor.events == null) return null;
            for (int i = editor.events.Count - 1; i >= 0; i--)
            {
                LevelEvent e = editor.events[i];
                if (e != null && e.active && e.eventType == LevelEventType.SetSpeed && e.floor == floor && IsZeroAngleSpeed(e))
                    return e;
            }
            return null;
        }

        private static bool HasNonZeroAngleSpeed(scnEditor editor, int floorFrom, int floorTo)
        {
            if (editor == null || editor.events == null || floorTo < floorFrom) return false;
            for (int i = 0; i < editor.events.Count; i++)
            {
                LevelEvent e = editor.events[i];
                if (e == null || !e.active || e.eventType != LevelEventType.SetSpeed ||
                    e.floor < floorFrom || e.floor > floorTo)
                    continue;
                if (!IsZeroAngleSpeed(e)) return true;
            }
            return false;
        }

        private static bool IsZeroAngleSpeed(LevelEvent e)
        {
            if (e == null) return false;
            object value;
            if (!e.data.TryGetValue("angleOffset", out value)) return true;
            try
            {
                return Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture)) < 0.0001d;
            }
            catch
            {
                return true;
            }
        }

        private static void ResetSession(string message, bool clearFloor, bool clearTakes)
        {
            tapDspTimes.Clear();
            analysis = null;
            factorShift = 0;
            firstTapAudioTime = -1d;
            expectedStartDsp = -1d;
            measurementApplied = false;
            if (clearTakes) savedTakes.Clear();
            if (clearFloor) startFloor = -1;
            state = startFloor >= 0 ? CaptureState.WaitingForFirstTap : CaptureState.Idle;
            status = message;
        }

        private static void EnsureCurrentLevel()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.levelData == null || editor.floors == null) return;

            string identity = editor.floors.Count + "|" +
                              editor.levelData.bpm.ToString("R", CultureInfo.InvariantCulture) + "|" +
                              editor.GetInstanceID();
            if (string.IsNullOrEmpty(currentLevelIdentity))
            {
                currentLevelIdentity = identity;
                return;
            }
            if (identity == currentLevelIdentity) return;

            currentLevelIdentity = identity;
            ResetSession("譜面が変わりました。計測床を選び直してください", true, true);
        }

        private static void EnsureAudioFieldsResolved()
        {
            if (audioFieldsResolved) return;
            audioFieldsResolved = true;
            try
            {
                songField = AccessTools.Field(typeof(scrConductor), "song");
                song2Field = AccessTools.Field(typeof(scrConductor), "song2");
            }
            catch (Exception ex)
            {
                if (Main.Logger != null) Main.Logger.Warning("AudioSource field lookup failed: " + ex.Message);
            }
        }

        private static AudioSource GetActiveAudioSource()
        {
            EnsureAudioFieldsResolved();
            scrConductor conductor = scrConductor.instance;
            if (conductor == null) return null;

            AudioSource first = ReadAudioSource(songField, conductor);
            AudioSource second = ReadAudioSource(song2Field, conductor);

            if (lastAudioSource != null && lastAudioSource.clip != null && lastAudioSource.isPlaying)
                return lastAudioSource;

            bool firstPlaying = first != null && first.clip != null && first.isPlaying;
            bool secondPlaying = second != null && second.clip != null && second.isPlaying;
            if (firstPlaying && !secondPlaying) lastAudioSource = first;
            else if (secondPlaying && !firstPlaying) lastAudioSource = second;
            else if (firstPlaying && secondPlaying)
                lastAudioSource = first.timeSamples >= second.timeSamples ? first : second;
            else if (first != null && first.clip != null) lastAudioSource = first;
            else lastAudioSource = second;
            return lastAudioSource;
        }

        private static AudioSource ReadAudioSource(FieldInfo field, scrConductor conductor)
        {
            if (field == null || conductor == null) return null;
            try { return field.GetValue(conductor) as AudioSource; }
            catch { return null; }
        }

        private static double ReadAudioTime(AudioSource source)
        {
            if (source == null || source.clip == null || source.clip.frequency <= 0) return -1d;
            try { return (double)source.timeSamples / source.clip.frequency; }
            catch { return -1d; }
        }

        private static void EnsureTapKeySetting()
        {
            if (Main.Settings == null) return;
            KeyCode parsed;
            if (string.IsNullOrEmpty(Main.Settings.TapKeyName) ||
                !Enum.TryParse(Main.Settings.TapKeyName, true, out parsed) || parsed == KeyCode.None)
                Main.Settings.TapKeyName = KeyCode.F10.ToString();
        }

        private static KeyCode GetTapKey()
        {
            EnsureTapKeySetting();
            KeyCode parsed;
            if (Main.Settings != null &&
                Enum.TryParse(Main.Settings.TapKeyName, true, out parsed) && parsed != KeyCode.None)
                return parsed;
            return KeyCode.F10;
        }

        private static string GetTapKeyLabel()
        {
            return GetTapKey().ToString();
        }

        private static void CaptureTapKeyBinding()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                waitingForTapKey = false;
                status = "キー変更をキャンセルしました";
                return;
            }

            Array values = Enum.GetValues(typeof(KeyCode));
            for (int i = 0; i < values.Length; i++)
            {
                KeyCode code = (KeyCode)values.GetValue(i);
                if (!IsBindableKey(code) || !Input.GetKeyDown(code)) continue;
                Main.Settings.TapKeyName = code.ToString();
                waitingForTapKey = false;
                status = "タップキーを " + code + " に変更しました";
                Main.SaveSettingsNow();
                return;
            }
        }

        private static bool IsBindableKey(KeyCode code)
        {
            if (code == KeyCode.None || code == KeyCode.Escape ||
                code == KeyCode.Return || code == KeyCode.KeypadEnter ||
                code == KeyCode.Backspace ||
                code == KeyCode.LeftControl || code == KeyCode.RightControl ||
                code == KeyCode.LeftShift || code == KeyCode.RightShift ||
                code == KeyCode.LeftAlt || code == KeyCode.RightAlt)
                return false;
            string name = code.ToString();
            return !name.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase) &&
                   !name.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase);
        }

        private static string FactorLabel(double factor)
        {
            if (Math.Abs(factor - Math.Round(factor)) < 0.000001d)
                return Math.Round(factor).ToString(CultureInfo.InvariantCulture);
            if (Math.Abs(factor - 0.5d) < 0.000001d) return "1/2";
            if (Math.Abs(factor - 0.25d) < 0.000001d) return "1/4";
            if (Math.Abs(factor - 0.125d) < 0.000001d) return "1/8";
            return factor.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatSigned(double value, string format)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "--";
            return value.ToString("+" + format + ";-" + format + ";0", CultureInfo.InvariantCulture);
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0d) return "--";
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            if (span.TotalHours >= 1d)
                return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" +
                       span.Seconds.ToString("00") + "." + span.Milliseconds.ToString("000");
            return ((int)span.TotalMinutes).ToString("00") + ":" + span.Seconds.ToString("00") + "." +
                   span.Milliseconds.ToString("000");
        }
    }
}
