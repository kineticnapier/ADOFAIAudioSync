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
    /// Records manual-play landing errors, provisionally divides the traversed range into
    /// beat-based windows, merges adjacent windows with similar correction slopes, then
    /// previews SetSpeed updates/additions. Absolute early/late bias is ignored; only the
    /// change in error over time affects BPM.
    /// </summary>
    internal static class PlayErrorCorrectionRuntime
    {
        private enum CaptureState
        {
            Idle,
            Armed,
            Recording,
            Completed
        }

        private sealed class HitSample
        {
            public int Floor;
            public double Beat;
            public double ChartTime;
            public double SongTime;
            public double ErrorSeconds;
        }

        private sealed class WindowModel
        {
            public int StartFloor;
            public int EndFloor;
            public double StartBeat;
            public double EndBeat;
            public List<HitSample> Samples = new List<HitSample>();
            public int SampleCount;
            public double Factor;
            public double CorrectionPercent;
            public double DriftMs;
            public double SpanSeconds;
            public double RmsMs;
            public double SmoothedFactor;
            public bool Valid;
            public string Reason = string.Empty;
        }

        private sealed class SpeedSuggestion
        {
            public int Floor;
            public int EndFloor;
            public double StartBeat;
            public double EndBeat;
            public int WindowCount;
            public int SampleCount;
            public int ExistingEventCount;
            public bool WillCreateBoundary;
            public double CurrentBpm;
            public double TargetBpm;
            public double Factor;
            public double CorrectionPercent;
            public double DriftMs;
            public double SpanSeconds;
            public double RmsMs;
            public bool Valid;
            public string Reason = string.Empty;
        }

        private sealed class EventTarget
        {
            public double Bpm;
            public bool ForceAbsolute;
        }

        private sealed class BoundaryTarget
        {
            public int Floor;
            public double Bpm;
            public bool IsRestore;
        }

        private static readonly List<HitSample> samples = new List<HitSample>();
        private static readonly List<SpeedSuggestion> suggestions = new List<SpeedSuggestion>();
        private static CaptureState state;
        private static string status = "未記録";
        private static int lastSeqId = int.MinValue;
        private static int firstFloor = -1;
        private static int lastFloor = -1;
        private static int noAudioFrames;
        private static int skippedAutoHits;
        private static int skippedJumps;
        private static bool applied;
        private static int appliedUpdatedCount;
        private static int appliedAddedCount;
        private static int appliedRestoreCount;
        private static string levelIdentity = string.Empty;

        private static FieldInfo songField;
        private static FieldInfo song2Field;
        private static bool audioFieldsResolved;
        private static AudioSource lastAudioSource;

        internal static void Initialize()
        {
            Reset("次のプレイを記録できます");
        }

        internal static void Shutdown()
        {
            Reset("終了");
            lastAudioSource = null;
        }

        internal static void Update()
        {
            if (!Main.Enabled || Main.Settings == null || !Main.Settings.EnablePlayErrorCorrection)
                return;

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (control && shift && Input.GetKeyDown(KeyCode.E))
                ToggleCapture();

            if (state != CaptureState.Armed && state != CaptureState.Recording)
                return;

            AudioSource source = GetActiveAudioSource();
            bool playing = source != null && source.clip != null && source.isPlaying;
            if (playing)
            {
                noAudioFrames = 0;
                if (state == CaptureState.Armed)
                    BeginRecording();
                TrackCurrentLanding();
            }
            else if (state == CaptureState.Recording)
            {
                noAudioFrames++;
                if (noAudioFrames >= 8)
                    FinishRecording("再生終了を検出");
            }
        }

        internal static void NotifyPlaybackStopped()
        {
            if (state == CaptureState.Recording)
                FinishRecording("エディター再生を停止");
        }

        internal static void DrawPanel(scnEditor editor)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("プレイ誤差から8拍単位で速度変化を生成・補正");
            GUILayout.Label("状態: " + status);
            GUILayout.Label("記録範囲: " + (firstFloor >= 0 ? firstFloor + " ～ " + lastFloor : "--") +
                            " / 有効ヒット: " + samples.Count +
                            " / Auto除外: " + skippedAutoHits);

            GUILayout.BeginHorizontal();
            string buttonText = state == CaptureState.Recording
                ? "記録を終了して解析"
                : state == CaptureState.Armed
                    ? "記録待機を解除"
                    : "次のプレイを記録  Ctrl+Shift+E";
            if (GUILayout.Button(buttonText, GUILayout.Height(28f)))
                ToggleCapture();
            if (GUILayout.Button("記録を消去", GUILayout.Width(95f), GUILayout.Height(28f)))
                Reset("記録を消去しました");
            GUILayout.EndHorizontal();

            float windowBeats = Main.Settings == null ? 8f : Main.Settings.ErrorCorrectionWindowBeats;
            float mergeThreshold = Main.Settings == null ? 0.05f : Main.Settings.ErrorCorrectionMergeThresholdPercent;
            GUILayout.Label(windowBeats.ToString("0.##", CultureInfo.InvariantCulture) +
                            "拍ごとに仮分割し、必要BPM差が " +
                            mergeThreshold.ToString("0.###", CultureInfo.InvariantCulture) +
                            "%以内の隣接区間を自動結合します。");
            float applyStrength = Main.Settings == null ? 50f : Main.Settings.ErrorCorrectionApplyStrengthPercent;
            GUILayout.Label("一定の早押し・遅押しは無視し、誤差の傾きだけを使います。補正は1回あたり " +
                            applyStrength.ToString("0", CultureInfo.InvariantCulture) +
                            "%だけ反映し、各有効区間の末尾で元のBPMへ復元します。");
            GUILayout.Label("旧版で適用後に悪化した譜面は、先にCtrl+Zまたはバックアップから戻してから再計測してください。");

            if (state == CaptureState.Completed)
            {
                GUILayout.Space(4f);
                if (GUILayout.Button("現在の設定で再解析", GUILayout.Height(24f)))
                    BuildSuggestions(editor);
                DrawSuggestions();

                int validCount = suggestions.Count(x => x.Valid);
                string applyLabel = applied
                    ? "適用済み: 既存 " + appliedUpdatedCount + " 件更新 / 境界 " +
                      appliedAddedCount + " 件追加（復元 " + appliedRestoreCount + " 件）"
                    : "有効な " + validCount + " 区間を安全適用（区間末尾で元BPMへ復元）";
                GUI.enabled = editor != null && validCount > 0 && !applied;
                if (GUILayout.Button(applyLabel, GUILayout.Height(34f)))
                    ApplySuggestions(editor);
                GUI.enabled = true;
            }
            GUILayout.EndVertical();
        }

        private static void DrawSuggestions()
        {
            if (suggestions.Count == 0)
            {
                GUILayout.Label("補正候補がありません。");
                return;
            }

            int valid = suggestions.Count(x => x.Valid);
            GUILayout.Label("結合後区間: " + suggestions.Count + "件 / 適用可能: " + valid + "件");
            int shown = Math.Min(suggestions.Count, 30);
            for (int i = 0; i < shown; i++)
            {
                SpeedSuggestion s = suggestions[i];
                string line = "床 " + s.Floor + "～" + Math.Max(s.Floor, s.EndFloor - 1) +
                              " / " + s.WindowCount + "窓 / n=" + s.SampleCount;
                if (s.Valid)
                {
                    line += "  " + s.CurrentBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                            " → " + s.TargetBpm.ToString("0.######", CultureInfo.InvariantCulture) +
                            " BPM  (" + FormatSigned(s.CorrectionPercent, "0.####") + "%)" +
                            " / drift " + FormatSigned(s.DriftMs, "0.0") + "ms" +
                            " / RMS " + s.RmsMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms" +
                            " / 既存" + s.ExistingEventCount + "件" +
                            (s.WillCreateBoundary ? " +境界追加" : string.Empty);
                }
                else
                {
                    line += "  スキップ: " + s.Reason;
                }
                GUILayout.Label(line);
            }
            if (suggestions.Count > shown)
                GUILayout.Label("ほか " + (suggestions.Count - shown) + " 件");
            if (skippedJumps > 0)
                GUILayout.Label("大きな床ジャンプを " + skippedJumps + " 回除外しました。");
        }

        private static void ToggleCapture()
        {
            if (state == CaptureState.Recording)
            {
                FinishRecording("手動終了");
                return;
            }
            if (state == CaptureState.Armed)
            {
                Reset("記録待機を解除しました");
                return;
            }

            samples.Clear();
            suggestions.Clear();
            firstFloor = -1;
            lastFloor = -1;
            lastSeqId = int.MinValue;
            noAudioFrames = 0;
            skippedAutoHits = 0;
            skippedJumps = 0;
            applied = false;
            appliedUpdatedCount = 0;
            appliedAddedCount = 0;
            appliedRestoreCount = 0;
            levelIdentity = ResolveLevelIdentity();
            state = CaptureState.Armed;
            status = "次に始まるプレイを待機中";
            if (Main.Settings != null) Main.Settings.TimingWindowVisible = true;
        }

        private static void BeginRecording()
        {
            samples.Clear();
            suggestions.Clear();
            firstFloor = -1;
            lastFloor = -1;
            skippedAutoHits = 0;
            skippedJumps = 0;
            applied = false;
            appliedUpdatedCount = 0;
            appliedAddedCount = 0;
            appliedRestoreCount = 0;
            scnControllerSafe(out lastSeqId);
            state = CaptureState.Recording;
            status = "プレイ誤差を記録中";
        }

        private static void TrackCurrentLanding()
        {
            scrController controller = scrController.instance;
            scrConductor conductor = scrConductor.instance;
            scrLevelMaker maker = scrLevelMaker.instance;
            if (controller == null || conductor == null || maker == null || maker.listFloors == null)
                return;

            int current = controller.currentSeqID;
            if (current == lastSeqId) return;
            if (lastSeqId == int.MinValue)
            {
                lastSeqId = current;
                return;
            }

            int advance = current - lastSeqId;
            lastSeqId = current;
            if (advance <= 0 || advance > 8)
            {
                skippedJumps++;
                return;
            }
            if (current < 0 || current >= maker.listFloors.Count) return;

            scrFloor floor = maker.listFloors[current];
            if (floor == null || floor.isFake || floor.freeroam || floor.auto) return;

            string grade = floor.grade.ToString();
            if (grade.IndexOf("Auto", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                skippedAutoHits++;
                return;
            }
            if (grade.IndexOf("Miss", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            double frameDelta = conductor.deltaSongPos;
            if (frameDelta < 0d || frameDelta > 0.5d) frameDelta = 0d;
            double songTime = conductor.songposition_minusv - frameDelta * 0.5d;
            double chartTime = floor.entryTime;
            double beat = floor.entryBeat;
            if (!IsFinite(songTime) || !IsFinite(chartTime) || !IsFinite(beat)) return;

            HitSample sample = new HitSample
            {
                Floor = current,
                Beat = beat,
                ChartTime = chartTime,
                SongTime = songTime,
                ErrorSeconds = songTime - chartTime
            };
            samples.Add(sample);
            if (firstFloor < 0) firstFloor = current;
            lastFloor = current;
            status = "記録中: 床 " + firstFloor + "～" + lastFloor + " / " + samples.Count + "ヒット";
        }

        private static void FinishRecording(string reason)
        {
            if (state != CaptureState.Recording)
            {
                Reset(reason);
                return;
            }

            state = CaptureState.Completed;
            noAudioFrames = 0;
            BuildSuggestions(scnEditor.instance);
            int validCount = suggestions.Count(x => x.Valid);
            if (validCount > 0)
            {
                status = reason + "。" + samples.Count + "ヒット、" + validCount + "区間を補正可能";
            }
            else if (suggestions.Count > 0 && !string.IsNullOrEmpty(suggestions[0].Reason))
            {
                status = reason + "。適用候補なし: " + suggestions[0].Reason;
            }
            else
            {
                status = reason + "。" + samples.Count + "ヒット、適用候補なし";
            }
        }

        private static void BuildSuggestions(scnEditor editor)
        {
            suggestions.Clear();
            applied = false;
            appliedUpdatedCount = 0;
            appliedAddedCount = 0;
            appliedRestoreCount = 0;

            if (editor == null || editor.events == null || editor.floors == null ||
                firstFloor < 0 || lastFloor <= firstFloor || samples.Count < 3)
                return;
            if (!string.IsNullOrEmpty(levelIdentity) && levelIdentity != ResolveLevelIdentity())
            {
                status = "譜面が変更されたため解析できません";
                return;
            }

            int safetyEndFloor = Math.Min(editor.floors.Count - 1, lastFloor + 1);
            List<LevelEvent> angularSpeeds = editor.events.Where(x =>
                x.eventType == LevelEventType.SetSpeed &&
                x.floor >= firstFloor && x.floor <= safetyEndFloor &&
                !IsZeroAngle(x)).ToList();
            if (angularSpeeds.Count > 0)
            {
                suggestions.Add(new SpeedSuggestion
                {
                    Floor = firstFloor,
                    EndFloor = lastFloor + 1,
                    Reason = "angleOffset付きSetSpeedが" + angularSpeeds.Count + "件あるため自動適用を停止"
                });
                status = "angleOffset付きSetSpeedがあるため解析を停止しました";
                return;
            }

            int minSamples = Main.Settings == null ? 6 :
                Mathf.Clamp(Main.Settings.ErrorCorrectionMinSamples, 3, 100);
            double maxPercent = Main.Settings == null ? 2d :
                Math.Max(0.01d, Math.Min(20d, Main.Settings.ErrorCorrectionMaxPercent));
            double windowBeats = Main.Settings == null ? 8d :
                Math.Max(1d, Math.Min(64d, Main.Settings.ErrorCorrectionWindowBeats));
            double mergeThresholdPercent = Main.Settings == null ? 0.05d :
                Math.Max(0.001d, Math.Min(5d, Main.Settings.ErrorCorrectionMergeThresholdPercent));
            double maxRmsMs = Main.Settings == null ? 45d :
                Math.Max(5d, Math.Min(500d, Main.Settings.ErrorCorrectionMaxRmsMs));
            double minChangePercent = Main.Settings == null ? 0.01d :
                Math.Max(0d, Math.Min(5d, Main.Settings.ErrorCorrectionMinChangePercent));

            List<WindowModel> windows = BuildBeatWindows(editor, windowBeats, minSamples,
                maxPercent, maxRmsMs);
            SmoothWindowFactors(windows);
            MergeWindows(editor, windows, minSamples, maxPercent, maxRmsMs,
                mergeThresholdPercent, minChangePercent);
        }

        private static List<WindowModel> BuildBeatWindows(scnEditor editor, double windowBeats,
            int minSamples, double maxPercent, double maxRmsMs)
        {
            List<HitSample> ordered = samples
                .Where(x => x.Floor >= firstFloor && x.Floor <= lastFloor)
                .OrderBy(x => x.Beat)
                .ThenBy(x => x.ChartTime)
                .ToList();
            List<WindowModel> result = new List<WindowModel>();
            if (ordered.Count == 0) return result;

            double finalBeat = ordered[ordered.Count - 1].Beat;
            double cursorBeat = ordered[0].Beat;
            int guard = 0;
            while (cursorBeat <= finalBeat + 0.000001d && guard++ < 100000)
            {
                double endBeat = cursorBeat + windowBeats;
                List<HitSample> segment = SelectSamples(ordered, cursorBeat, endBeat,
                    endBeat >= finalBeat - 0.000001d);

                // A sparse chart may not provide six judged landings in exactly eight beats.
                // Extend by another provisional window until the regression has enough data.
                while (segment.Count < minSamples && endBeat < finalBeat - 0.000001d)
                {
                    endBeat += windowBeats;
                    segment = SelectSamples(ordered, cursorBeat, endBeat,
                        endBeat >= finalBeat - 0.000001d);
                }

                int startFloor = FindFloorAtOrAfterBeat(editor, cursorBeat, firstFloor, lastFloor + 1);
                int endFloor = FindFloorAtOrAfterBeat(editor, endBeat, firstFloor, lastFloor + 1);
                if (endFloor <= startFloor) endFloor = Math.Min(lastFloor + 1, startFloor + 1);

                WindowModel window = AnalyzeWindow(segment, startFloor, endFloor,
                    cursorBeat, endBeat, minSamples, maxPercent, maxRmsMs);
                result.Add(window);

                if (endBeat <= cursorBeat + 0.000001d) break;
                cursorBeat = endBeat;
            }
            return result;
        }

        private static List<HitSample> SelectSamples(List<HitSample> source, double startBeat,
            double endBeat, bool includeEnd)
        {
            return source.Where(x => x.Beat >= startBeat - 0.000001d &&
                (includeEnd ? x.Beat <= endBeat + 0.000001d : x.Beat < endBeat - 0.000001d))
                .OrderBy(x => x.ChartTime)
                .ToList();
        }

        private static WindowModel AnalyzeWindow(List<HitSample> segmentSamples, int startFloor,
            int endFloor, double startBeat, double endBeat, int minSamples,
            double maxPercent, double maxRmsMs)
        {
            WindowModel window = new WindowModel
            {
                StartFloor = startFloor,
                EndFloor = endFloor,
                StartBeat = startBeat,
                EndBeat = endBeat,
                Samples = segmentSamples ?? new List<HitSample>(),
                SampleCount = segmentSamples == null ? 0 : segmentSamples.Count
            };

            if (window.SampleCount < minSamples)
            {
                window.Reason = "サンプル不足（" + window.SampleCount + "/" + minSamples + "）";
                return window;
            }

            double intercept;
            double slope;
            double rms;
            int used;
            if (!RobustRegression(window.Samples, out intercept, out slope, out rms, out used))
            {
                window.Reason = "誤差回帰に失敗";
                return window;
            }
            window.SampleCount = used;
            window.SpanSeconds = window.Samples.Max(x => x.ChartTime) -
                                 window.Samples.Min(x => x.ChartTime);
            window.RmsMs = rms * 1000d;
            if (window.SpanSeconds < 0.5d)
            {
                window.Reason = "計測区間が短すぎます";
                return window;
            }
            if (window.RmsMs > maxRmsMs)
            {
                window.Reason = "RMS " + window.RmsMs.ToString("0.0", CultureInfo.InvariantCulture) +
                                "ms が上限 " + maxRmsMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms を超過";
                return window;
            }

            double denominator = 1d + slope;
            if (denominator <= 0.1d || denominator >= 10d)
            {
                window.Reason = "算出倍率が異常";
                return window;
            }

            window.Factor = 1d / denominator;
            window.SmoothedFactor = window.Factor;
            window.CorrectionPercent = (window.Factor - 1d) * 100d;
            window.DriftMs = slope * window.SpanSeconds * 1000d;
            if (Math.Abs(window.CorrectionPercent) > maxPercent)
            {
                window.Reason = "補正量が安全上限 ±" + maxPercent.ToString("0.###", CultureInfo.InvariantCulture) + "% を超過";
                return window;
            }

            window.Valid = true;
            return window;
        }

        private static void SmoothWindowFactors(List<WindowModel> windows)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (!windows[i].Valid) continue;
                List<double> neighborhood = new List<double>();
                for (int j = Math.Max(0, i - 1); j <= Math.Min(windows.Count - 1, i + 1); j++)
                    if (windows[j].Valid) neighborhood.Add(windows[j].Factor);
                windows[i].SmoothedFactor = neighborhood.Count == 0
                    ? windows[i].Factor
                    : Median(neighborhood);
            }
        }

        private static void MergeWindows(scnEditor editor, List<WindowModel> windows,
            int minSamples, double maxPercent, double maxRmsMs,
            double mergeThresholdPercent, double minChangePercent)
        {
            int i = 0;
            while (i < windows.Count)
            {
                WindowModel first = windows[i];
                if (!first.Valid)
                {
                    suggestions.Add(InvalidSuggestion(first));
                    i++;
                    continue;
                }

                int start = i;
                int end = i;
                List<HitSample> regionSamples = new List<HitSample>(first.Samples);
                WindowModel regionModel = AnalyzeWindow(regionSamples, first.StartFloor,
                    first.EndFloor, first.StartBeat, first.EndBeat,
                    minSamples, maxPercent, maxRmsMs);

                while (end + 1 < windows.Count && windows[end + 1].Valid)
                {
                    WindowModel next = windows[end + 1];
                    double reference = regionModel.Valid ? regionModel.Factor : windows[end].SmoothedFactor;
                    double diffPercent = Math.Abs(next.SmoothedFactor / reference - 1d) * 100d;
                    if (diffPercent > mergeThresholdPercent) break;

                    List<HitSample> combinedSamples = regionSamples
                        .Concat(next.Samples)
                        .GroupBy(x => x.Floor)
                        .Select(x => x.OrderBy(y => y.ChartTime).First())
                        .OrderBy(x => x.ChartTime)
                        .ToList();
                    WindowModel combined = AnalyzeWindow(combinedSamples,
                        windows[start].StartFloor, next.EndFloor,
                        windows[start].StartBeat, next.EndBeat,
                        minSamples, maxPercent, maxRmsMs);
                    if (!combined.Valid) break;

                    regionSamples = combinedSamples;
                    regionModel = combined;
                    end++;
                }

                SpeedSuggestion suggestion = ToSuggestion(editor, regionModel,
                    windows[start].StartFloor, windows[end].EndFloor,
                    windows[start].StartBeat, windows[end].EndBeat,
                    end - start + 1, minChangePercent);
                suggestions.Add(suggestion);
                i = end + 1;
            }
        }

        private static SpeedSuggestion InvalidSuggestion(WindowModel window)
        {
            return new SpeedSuggestion
            {
                Floor = window.StartFloor,
                EndFloor = window.EndFloor,
                StartBeat = window.StartBeat,
                EndBeat = window.EndBeat,
                WindowCount = 1,
                SampleCount = window.SampleCount,
                RmsMs = window.RmsMs,
                Reason = window.Reason
            };
        }

        private static SpeedSuggestion ToSuggestion(scnEditor editor, WindowModel model,
            int startFloor, int endFloor, double startBeat, double endBeat,
            int windowCount, double minChangePercent)
        {
            SpeedSuggestion suggestion = new SpeedSuggestion
            {
                Floor = startFloor,
                EndFloor = endFloor,
                StartBeat = startBeat,
                EndBeat = endBeat,
                WindowCount = windowCount,
                SampleCount = model.SampleCount,
                Factor = model.Factor,
                CorrectionPercent = model.CorrectionPercent,
                DriftMs = model.DriftMs,
                SpanSeconds = model.SpanSeconds,
                RmsMs = model.RmsMs,
                Reason = model.Reason
            };

            if (!model.Valid)
                return suggestion;

            double strength = Main.Settings == null ? 0.5d :
                Math.Max(0.05d, Math.Min(1d, Main.Settings.ErrorCorrectionApplyStrengthPercent / 100d));
            suggestion.Factor = 1d + (model.Factor - 1d) * strength;
            suggestion.CorrectionPercent = (suggestion.Factor - 1d) * 100d;

            if (Math.Abs(suggestion.CorrectionPercent) < minChangePercent)
            {
                suggestion.Reason = "今回の適用量 " +
                    Math.Abs(suggestion.CorrectionPercent).ToString("0.####", CultureInfo.InvariantCulture) +
                    "% が最小変化 " + minChangePercent.ToString("0.####", CultureInfo.InvariantCulture) + "% 未満";
                return suggestion;
            }
            if (startFloor < 0 || startFloor >= editor.floors.Count)
            {
                suggestion.Reason = "開始床が無効";
                return suggestion;
            }

            double currentBpm = EffectiveBpmAtFloor(editor, startFloor);
            double targetBpm = currentBpm * suggestion.Factor;
            if (!IsFinitePositive(currentBpm) || !IsFinitePositive(targetBpm))
            {
                suggestion.Reason = "BPMが無効";
                return suggestion;
            }

            suggestion.CurrentBpm = currentBpm;
            suggestion.TargetBpm = targetBpm;
            suggestion.ExistingEventCount = editor.events.Count(x =>
                x.eventType == LevelEventType.SetSpeed &&
                x.floor >= startFloor && x.floor < endFloor && IsZeroAngle(x));
            suggestion.WillCreateBoundary = !editor.events.Any(x =>
                x.eventType == LevelEventType.SetSpeed && x.floor == startFloor && IsZeroAngle(x));
            suggestion.Valid = true;
            return suggestion;
        }

        private static bool RobustRegression(List<HitSample> source, out double intercept,
            out double slope, out double rms, out int used)
        {
            intercept = 0d;
            slope = 0d;
            rms = 0d;
            used = 0;
            if (source == null || source.Count < 3) return false;

            List<HitSample> ordered = source.OrderBy(s => s.ChartTime).ToList();
            List<double> x = ordered.Select(s => s.ChartTime - ordered[0].ChartTime).ToList();
            List<double> y = ordered.Select(s => s.ErrorSeconds).ToList();
            if (!LinearRegression(x, y, out intercept, out slope)) return false;

            List<double> residuals = new List<double>();
            for (int i = 0; i < x.Count; i++)
                residuals.Add(y[i] - (intercept + slope * x[i]));
            double medianResidual = Median(residuals);
            double mad = Median(residuals.Select(r => Math.Abs(r - medianResidual)));
            double threshold = Math.Max(0.020d, mad * 1.4826d * 3d);

            List<double> filteredX = new List<double>();
            List<double> filteredY = new List<double>();
            for (int i = 0; i < x.Count; i++)
            {
                if (Math.Abs(residuals[i] - medianResidual) <= threshold)
                {
                    filteredX.Add(x[i]);
                    filteredY.Add(y[i]);
                }
            }
            if (filteredX.Count < 3) return false;
            if (!LinearRegression(filteredX, filteredY, out intercept, out slope)) return false;

            double sumSquares = 0d;
            for (int i = 0; i < filteredX.Count; i++)
            {
                double residual = filteredY[i] - (intercept + slope * filteredX[i]);
                sumSquares += residual * residual;
            }
            rms = Math.Sqrt(sumSquares / filteredX.Count);
            used = filteredX.Count;
            return true;
        }

        private static void ApplySuggestions(scnEditor editor)
        {
            if (editor == null || suggestions.Count == 0)
            {
                status = "適用可能な候補がありません";
                return;
            }
            if (!string.IsNullOrEmpty(levelIdentity) && levelIdentity != ResolveLevelIdentity())
            {
                status = "譜面が変更されています。再計測してください";
                return;
            }

            List<SpeedSuggestion> validRegions = suggestions
                .Where(x => x.Valid)
                .OrderBy(x => x.Floor)
                .ToList();
            if (validRegions.Count == 0)
            {
                status = "有効な候補がありません";
                return;
            }

            try
            {
                // Snapshot the original effective BPM at every floor before touching any event.
                // Every target and every restore boundary is derived from this immutable baseline.
                double[] originalEffectiveBpm = new double[editor.floors.Count];
                for (int floor = 0; floor < originalEffectiveBpm.Length; floor++)
                    originalEffectiveBpm[floor] = EffectiveBpmAtFloor(editor, floor);

                Dictionary<LevelEvent, EventTarget> existingTargets =
                    new Dictionary<LevelEvent, EventTarget>();
                List<BoundaryTarget> boundaryTargets = new List<BoundaryTarget>();
                HashSet<int> restoreFloors = new HashSet<int>();

                for (int i = 0; i < validRegions.Count; i++)
                {
                    SpeedSuggestion region = validRegions[i];
                    int startFloor = Mathf.Clamp(region.Floor, 0, editor.floors.Count - 1);
                    int endFloor = Mathf.Clamp(region.EndFloor, startFloor + 1, editor.floors.Count);

                    // Only the last zero-angle SetSpeed on each floor determines that floor's final
                    // effective BPM. Rewriting every chained event caused duplicate multiplication.
                    List<IGrouping<int, LevelEvent>> eventFloors = editor.events.Where(x =>
                            x.eventType == LevelEventType.SetSpeed &&
                            x.floor >= startFloor && x.floor < endFloor &&
                            IsZeroAngle(x))
                        .GroupBy(x => x.floor)
                        .ToList();

                    for (int g = 0; g < eventFloors.Count; g++)
                    {
                        int floor = eventFloors[g].Key;
                        LevelEvent lastEvent = eventFloors[g]
                            .OrderBy(x => editor.events.IndexOf(x))
                            .Last();
                        double targetBpm = originalEffectiveBpm[floor] * region.Factor;
                        if (IsFinitePositive(targetBpm))
                        {
                            existingTargets[lastEvent] = new EventTarget
                            {
                                Bpm = targetBpm,
                                ForceAbsolute = false
                            };
                        }
                    }

                    bool hasStartBoundary = editor.events.Any(x =>
                        x.eventType == LevelEventType.SetSpeed &&
                        x.floor == startFloor && IsZeroAngle(x));
                    if (!hasStartBoundary)
                    {
                        double targetStartBpm = originalEffectiveBpm[startFloor] * region.Factor;
                        if (IsFinitePositive(targetStartBpm))
                        {
                            boundaryTargets.Add(new BoundaryTarget
                            {
                                Floor = startFloor,
                                Bpm = targetStartBpm,
                                IsRestore = false
                            });
                        }
                    }

                    // A SetSpeed persists until the next speed event. The old implementation did not
                    // restore the original BPM at the end of a valid region, so its correction leaked
                    // into invalid gaps and the rest of the chart. Adjacent valid regions do not need
                    // a restore because the next region writes its own boundary at the same floor.
                    bool nextRegionContinues = i + 1 < validRegions.Count &&
                                               validRegions[i + 1].Floor == endFloor;
                    if (!nextRegionContinues && endFloor < editor.floors.Count)
                    {
                        double restoreBpm = originalEffectiveBpm[endFloor];
                        List<LevelEvent> endEvents = editor.events.Where(x =>
                                x.eventType == LevelEventType.SetSpeed &&
                                x.floor == endFloor && IsZeroAngle(x))
                            .OrderBy(x => editor.events.IndexOf(x))
                            .ToList();

                        if (endEvents.Count > 0)
                        {
                            existingTargets[endEvents[endEvents.Count - 1]] = new EventTarget
                            {
                                Bpm = restoreBpm,
                                ForceAbsolute = false
                            };
                        }
                        else if (IsFinitePositive(restoreBpm))
                        {
                            boundaryTargets.Add(new BoundaryTarget
                            {
                                Floor = endFloor,
                                Bpm = restoreBpm,
                                IsRestore = true
                            });
                        }
                        restoreFloors.Add(endFloor);
                    }
                }

                // Deduplicate added boundaries. A restore takes precedence over a normal boundary only
                // when both somehow resolve to the same floor; contiguous regions normally avoid this.
                boundaryTargets = boundaryTargets
                    .GroupBy(x => x.Floor)
                    .Select(group => group.OrderByDescending(x => x.IsRestore).First())
                    .OrderBy(x => x.Floor)
                    .ToList();

                if (existingTargets.Count == 0 && boundaryTargets.Count == 0)
                {
                    status = "変更対象がありません";
                    return;
                }

                int validationWarnings = 0;

                using (new SaveStateScope(editor, false, true, false))
                {
                    Dictionary<LevelEvent, EventTarget> allTargets =
                        new Dictionary<LevelEvent, EventTarget>(existingTargets);
                    HashSet<LevelEvent> addedEvents = new HashSet<LevelEvent>();

                    for (int i = 0; i < boundaryTargets.Count; i++)
                    {
                        BoundaryTarget boundary = boundaryTargets[i];
                        LevelEvent speed = new LevelEvent(boundary.Floor, LevelEventType.SetSpeed);
                        speed["speedType"] = SpeedType.Bpm;
                        speed["angleOffset"] = 0f;
                        editor.events.Add(speed);
                        addedEvents.Add(speed);
                        allTargets[speed] = new EventTarget
                        {
                            Bpm = boundary.Bpm,
                            ForceAbsolute = true
                        };
                    }

                    List<LevelEvent> allSpeeds = editor.events
                        .Where(x => x.eventType == LevelEventType.SetSpeed)
                        .OrderBy(x => x.floor)
                        .ThenBy(GetAngleOffset)
                        .ThenBy(x => editor.events.IndexOf(x))
                        .ToList();

                    double effectiveBpm = editor.levelData.bpm;
                    for (int i = 0; i < allSpeeds.Count; i++)
                    {
                        LevelEvent speed = allSpeeds[i];
                        EventTarget target;
                        if (allTargets.TryGetValue(speed, out target))
                        {
                            SetTargetBpm(speed, target.Bpm, effectiveBpm,
                                target.ForceAbsolute || addedEvents.Contains(speed));
                            effectiveBpm = target.Bpm;
                        }
                        else
                        {
                            SpeedType type = ReadSpeedType(speed);
                            if (type == SpeedType.Bpm)
                                effectiveBpm = ReadDouble(speed, "beatsPerMinute", effectiveBpm);
                            else
                                effectiveBpm *= ReadDouble(speed, "bpmMultiplier", 1d);
                        }
                    }

                    editor.ApplyEventsToFloors();

                    // Verify the post-application timeline, not just the written event values.
                    // A mismatch here indicates ordering or chained-multiplier semantics changed.
                    for (int i = 0; i < validRegions.Count; i++)
                    {
                        SpeedSuggestion region = validRegions[i];
                        int floor = Mathf.Clamp(region.Floor, 0, editor.floors.Count - 1);
                        double expected = originalEffectiveBpm[floor] * region.Factor;
                        double actual = EffectiveBpmAtFloor(editor, floor);
                        if (!ApproximatelyPercent(actual, expected, 0.05d))
                            validationWarnings++;
                    }
                    foreach (int floor in restoreFloors)
                    {
                        if (floor < 0 || floor >= editor.floors.Count) continue;
                        double actual = EffectiveBpmAtFloor(editor, floor);
                        if (!ApproximatelyPercent(actual, originalEffectiveBpm[floor], 0.05d))
                            validationWarnings++;
                    }

                    editor.RemakePath(true, true);
                }

                applied = true;
                appliedUpdatedCount = existingTargets.Count;
                appliedAddedCount = boundaryTargets.Count;
                appliedRestoreCount = restoreFloors.Count;
                status = validationWarnings == 0
                    ? "安全適用完了: 既存" + appliedUpdatedCount + "件更新 / 境界" +
                      appliedAddedCount + "件追加 / 復元" + appliedRestoreCount + "件"
                    : "適用後検証で" + validationWarnings +
                      "件の不一致を検出しました。Ctrl+Zで戻してください";

                if (validationWarnings > 0 && Main.Logger != null)
                    Main.Logger.Warning("Play-error correction post-apply validation found " +
                                        validationWarnings + " mismatch(es). Undo is recommended.");
            }
            catch (Exception ex)
            {
                status = "一括補正に失敗: " + ex.Message + "。Ctrl+Zで戻してください";
                if (Main.Logger != null) Main.Logger.Error(ex.ToString());
            }
        }

        private static bool ApproximatelyPercent(double actual, double expected, double tolerancePercent)
        {
            if (!IsFinitePositive(actual) || !IsFinitePositive(expected)) return false;
            return Math.Abs(actual / expected - 1d) * 100d <= tolerancePercent;
        }

        private static void SetTargetBpm(LevelEvent speed, double targetBpm,
            double previousEffectiveBpm, bool forceAbsolute)
        {
            float bpm = (float)Math.Round(targetBpm, 6);
            SpeedType type = forceAbsolute ? SpeedType.Bpm : ReadSpeedType(speed);
            speed["speedType"] = type;
            speed["beatsPerMinute"] = bpm;
            speed["bpmMultiplier"] = previousEffectiveBpm > 0.000001d
                ? (float)Math.Round(targetBpm / previousEffectiveBpm, 9)
                : 1f;
            speed["angleOffset"] = 0f;
        }

        private static double EffectiveBpmAtFloor(scnEditor editor, int floor)
        {
            if (editor == null || editor.levelData == null || editor.floors == null ||
                floor < 0 || floor >= editor.floors.Count)
                return 0d;
            return editor.levelData.bpm * editor.floors[floor].speed;
        }

        private static int FindFloorAtOrAfterBeat(scnEditor editor, double beat,
            int minFloor, int maxFloorExclusive)
        {
            if (editor == null || editor.floors == null || editor.floors.Count == 0)
                return minFloor;
            int start = Mathf.Clamp(minFloor, 0, editor.floors.Count - 1);
            int end = Mathf.Clamp(maxFloorExclusive, start + 1, editor.floors.Count);
            for (int i = start; i < end; i++)
            {
                scrFloor floor = editor.floors[i];
                if (floor != null && IsFinite(floor.entryBeat) && floor.entryBeat >= beat - 0.000001d)
                    return i;
            }
            return end;
        }

        private static void Reset(string message)
        {
            samples.Clear();
            suggestions.Clear();
            state = CaptureState.Idle;
            status = message;
            lastSeqId = int.MinValue;
            firstFloor = -1;
            lastFloor = -1;
            noAudioFrames = 0;
            skippedAutoHits = 0;
            skippedJumps = 0;
            applied = false;
            appliedUpdatedCount = 0;
            appliedAddedCount = 0;
            appliedRestoreCount = 0;
            levelIdentity = string.Empty;
        }

        private static string ResolveLevelIdentity()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.levelData == null || editor.floors == null)
                return string.Empty;
            return editor.GetInstanceID() + "|" + editor.floors.Count + "|" +
                   editor.levelData.bpm.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void scnControllerSafe(out int seqId)
        {
            scrController controller = scrController.instance;
            seqId = controller == null ? int.MinValue : controller.currentSeqID;
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
            return IsFinite(slope) && IsFinite(intercept);
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] sorted = values.OrderBy(x => x).ToArray();
            if (sorted.Length == 0) return 0d;
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) * 0.5d
                : sorted[middle];
        }

        private static bool IsZeroAngle(LevelEvent speed)
        {
            return Math.Abs(GetAngleOffset(speed)) < 0.0001d;
        }

        private static double GetAngleOffset(LevelEvent speed)
        {
            return ReadDouble(speed, "angleOffset", 0d);
        }

        private static SpeedType ReadSpeedType(LevelEvent speed)
        {
            try
            {
                object value;
                if (speed.data.TryGetValue("speedType", out value))
                {
                    if (value is SpeedType) return (SpeedType)value;
                    SpeedType parsed;
                    if (Enum.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        true, out parsed))
                        return parsed;
                }
            }
            catch { }
            return SpeedType.Bpm;
        }

        private static double ReadDouble(LevelEvent levelEvent, string key, double fallback)
        {
            try
            {
                object value;
                if (levelEvent.data.TryGetValue(key, out value))
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch { }
            return fallback;
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0.000001d && IsFinite(value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string FormatSigned(double value, string format)
        {
            return (value >= 0d ? "+" : string.Empty) +
                   value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static void EnsureAudioFieldsResolved()
        {
            if (audioFieldsResolved) return;
            audioFieldsResolved = true;
            songField = AccessTools.Field(typeof(scrConductor), "song");
            song2Field = AccessTools.Field(typeof(scrConductor), "song2");
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
    }
}
