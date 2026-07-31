using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Reuses fully downloaded OGG AudioClips by canonical file path. Entries are
    /// invalidated when the source file changes and evicted by least-recent use.
    /// </summary>
    internal static class OggAudioCacheRuntime
    {
        private sealed class CacheEntry
        {
            internal string Path;
            internal long FileLength;
            internal long LastWriteTicks;
            internal AudioClip Clip;
            internal long EstimatedBytes;
            internal long LastUse;
        }

        private static readonly Dictionary<string, CacheEntry> Entries =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<AudioClip> RetiredClips = new List<AudioClip>();

        private static string activeExternalLoadPath;
        private static string status = "未初期化";
        private static string lastLookupResult = "-";
        private static string currentUsageState = "-";
        private static AudioClip lastManagedClip;
        private static bool lastManagedClipWasCacheHit;
        private static int activeExternalLoads;
        private static long estimatedBytes;
        private static long useSerial;
        private static int hits;
        private static int misses;
        private static int stores;
        private static int evictions;
        private static int streamOverrideCount;
        private static int nextCleanupFrame;
        private static bool initialized;
        private static bool streamOverridePatchInstalled;

        internal static string Status { get { return status; } }
        internal static string LastLookupResult { get { return lastLookupResult; } }
        internal static string CurrentUsageState
        {
            get
            {
                RefreshCurrentUsageState();
                return currentUsageState;
            }
        }
        internal static int EntryCount { get { return Entries.Count; } }
        internal static double EstimatedMegabytes
        {
            get { return estimatedBytes / (1024d * 1024d); }
        }
        internal static int HitCount { get { return hits; } }
        internal static int MissCount { get { return misses; } }
        internal static int StoreCount { get { return stores; } }
        internal static int EvictionCount { get { return evictions; } }
        internal static int StreamOverrideCount { get { return streamOverrideCount; } }
        internal static bool StreamOverridePatchInstalled
        {
            get { return streamOverridePatchInstalled; }
        }

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            activeExternalLoadPath = null;
            status = "待機中";
            lastLookupResult = IsEnabled() ? "-" : "OFF";
            currentUsageState = IsEnabled() ? "-" : "OFF";
            lastManagedClip = null;
            lastManagedClipWasCacheHit = false;
            activeExternalLoads = 0;
            nextCleanupFrame = Time.frameCount + 120;
        }

        internal static IEnumerator WrapExternalLoad(
            AudioManager manager,
            string path,
            IEnumerator original)
        {
            if (original == null || !ShouldCachePath(path))
            {
                return original;
            }
            return RunCachedExternalLoad(manager, path, original);
        }

        internal static bool OverrideStreamAudio(bool requested)
        {
            if (!requested || !ShouldCachePath(activeExternalLoadPath))
            {
                return requested;
            }

            streamOverrideCount++;
            status = "OGGをメモリ展開中";
            return false;
        }

        internal static void SetStreamOverridePatchInstalled(bool installed)
        {
            streamOverridePatchInstalled = installed;
            if (!installed)
            {
                status = "OGG非ストリーミング化patch未適用";
                if (Main.Logger != null)
                {
                    Main.Logger.Warning(
                        "The OGG streamAudio assignment was not found; cache hits can still be reused, " +
                        "but newly loaded OGG clips may remain streaming.");
                }
            }
        }

        internal static void Update()
        {
            if (!initialized) return;

            if (!IsEnabled())
            {
                if (Entries.Count > 0)
                {
                    Clear("設定OFF");
                }
                currentUsageState = "OFF";
                return;
            }

            RefreshCurrentUsageState();
            if (Time.frameCount < nextCleanupFrame) return;
            nextCleanupFrame = Time.frameCount + 120;
            TrimToConfiguredBudget(null);
            DestroyRetiredClipsWhenSafe();
        }

        internal static void TrimToConfiguredBudget()
        {
            TrimToConfiguredBudget(null);
            DestroyRetiredClipsWhenSafe();
        }

        internal static void Clear(string reason)
        {
            foreach (CacheEntry entry in Entries.Values)
            {
                RetireClip(entry.Clip);
            }
            Entries.Clear();
            estimatedBytes = 0L;
            DestroyRetiredClipsWhenSafe();
            status = (reason ?? "手動") + " / cache 0件";
            lastLookupResult =
                string.Equals(reason, "設定OFF", StringComparison.Ordinal)
                    ? "OFF"
                    : "-";
            currentUsageState =
                string.Equals(reason, "設定OFF", StringComparison.Ordinal)
                    ? "OFF"
                    : "-";
            lastManagedClip = null;
            lastManagedClipWasCacheHit = false;
        }

        internal static void NotifyLifecycleStop()
        {
            DestroyRetiredClipsWhenSafe();
        }

        internal static void Shutdown()
        {
            activeExternalLoadPath = null;
            activeExternalLoads = 0;
            Clear("mod unload");
            initialized = false;
            status = "終了";
        }

        private static IEnumerator RunCachedExternalLoad(
            AudioManager manager,
            string path,
            IEnumerator original)
        {
            string canonicalPath = NormalizePath(path);
            string conductorName = GetConductorName(path);
            bool cacheHit = false;
            activeExternalLoads++;
            currentUsageState = "LOAD";

            IDisposable disposable = original as IDisposable;
            try
            {
                CacheEntry cached;
                if (TryGetValidEntry(canonicalPath, out cached) &&
                    InjectCachedClip(manager, conductorName, cached.Clip))
                {
                    cacheHit = true;
                    hits++;
                    lastLookupResult = "HIT";
                    lastManagedClip = cached.Clip;
                    lastManagedClipWasCacheHit = true;
                    cached.LastUse = NextUseSerial();
                    TryLoadAudioData(cached.Clip);
                    status = "cache hit: " + GetDisplayName(path);
                }
                else
                {
                    misses++;
                    lastLookupResult = "MISS";
                    status = "cache miss: " + GetDisplayName(path);
                    // Do not let AudioManager's filename-only dictionary return a stale
                    // streaming clip or a same-named file from another level.
                    if (manager != null && manager.audioLib != null &&
                        !string.IsNullOrEmpty(conductorName))
                    {
                        manager.audioLib.Remove(conductorName);
                    }
                }

                while (true)
                {
                    bool moved;
                    activeExternalLoadPath = path;
                    try
                    {
                        moved = original.MoveNext();
                    }
                    finally
                    {
                        activeExternalLoadPath = null;
                    }

                    if (!cacheHit)
                    {
                        TryStoreLoadedClip(manager, conductorName, canonicalPath);
                    }
                    if (!moved)
                    {
                        break;
                    }
                    yield return original.Current;
                }

                if (!cacheHit)
                {
                    TryStoreLoadedClip(manager, conductorName, canonicalPath);
                }
            }
            finally
            {
                activeExternalLoadPath = null;
                activeExternalLoads = Math.Max(0, activeExternalLoads - 1);
                RefreshCurrentUsageState();
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }

        private static bool TryGetValidEntry(string canonicalPath, out CacheEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(canonicalPath)) return false;

            CacheEntry candidate;
            if (!Entries.TryGetValue(canonicalPath, out candidate))
            {
                return false;
            }

            long fileLength;
            long lastWriteTicks;
            bool fingerprintAvailable = TryGetFingerprint(
                canonicalPath,
                out fileLength,
                out lastWriteTicks);
            if (candidate.Clip == null || !fingerprintAvailable ||
                candidate.FileLength != fileLength ||
                candidate.LastWriteTicks != lastWriteTicks)
            {
                RemoveEntry(candidate, false);
                status = "変更済みOGGを再読込";
                return false;
            }

            entry = candidate;
            return true;
        }

        private static void TryStoreLoadedClip(
            AudioManager manager,
            string conductorName,
            string canonicalPath)
        {
            if (!IsEnabled() || manager == null || manager.audioLib == null ||
                string.IsNullOrEmpty(conductorName) || string.IsNullOrEmpty(canonicalPath))
            {
                return;
            }

            AudioClip loadedClip;
            if (!manager.audioLib.TryGetValue(conductorName, out loadedClip) ||
                loadedClip == null)
            {
                return;
            }

            long fileLength;
            long lastWriteTicks;
            if (!TryGetFingerprint(canonicalPath, out fileLength, out lastWriteTicks))
            {
                status = "OGGの更新時刻を取得できません";
                return;
            }

            CacheEntry existing;
            if (Entries.TryGetValue(canonicalPath, out existing))
            {
                if (ReferenceEquals(existing.Clip, loadedClip) &&
                    existing.FileLength == fileLength &&
                    existing.LastWriteTicks == lastWriteTicks)
                {
                    existing.LastUse = NextUseSerial();
                    return;
                }
                RemoveEntry(existing, false);
            }

            TryLoadAudioData(loadedClip);
            CacheEntry entry = new CacheEntry
            {
                Path = canonicalPath,
                FileLength = fileLength,
                LastWriteTicks = lastWriteTicks,
                Clip = loadedClip,
                EstimatedBytes = EstimateDecodedBytes(loadedClip),
                LastUse = NextUseSerial()
            };
            Entries[canonicalPath] = entry;
            estimatedBytes += entry.EstimatedBytes;
            stores++;
            lastManagedClip = loadedClip;
            lastManagedClipWasCacheHit = false;
            status = "cache登録: " + GetDisplayName(canonicalPath);
            TrimToConfiguredBudget(canonicalPath);
        }

        private static bool InjectCachedClip(
            AudioManager manager,
            string conductorName,
            AudioClip audioClip)
        {
            if (manager == null || manager.audioLib == null ||
                string.IsNullOrEmpty(conductorName) || audioClip == null)
            {
                return false;
            }

            manager.audioLib[conductorName] = audioClip;
            return true;
        }

        private static void TrimToConfiguredBudget(string protectedPath)
        {
            long maximumBytes = GetMaximumBytes();
            while (estimatedBytes > maximumBytes && Entries.Count > 1)
            {
                CacheEntry oldest = null;
                foreach (CacheEntry candidate in Entries.Values)
                {
                    if (!string.IsNullOrEmpty(protectedPath) &&
                        string.Equals(candidate.Path, protectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (oldest == null || candidate.LastUse < oldest.LastUse)
                    {
                        oldest = candidate;
                    }
                }

                if (oldest == null) break;
                RemoveEntry(oldest, true);
            }

            if (estimatedBytes > maximumBytes && Entries.Count == 1)
            {
                status += "（単体で上限超過）";
            }
        }

        private static void RemoveEntry(CacheEntry entry, bool eviction)
        {
            if (entry == null) return;
            CacheEntry current;
            if (!Entries.TryGetValue(entry.Path, out current) ||
                !ReferenceEquals(current, entry))
            {
                return;
            }

            Entries.Remove(entry.Path);
            estimatedBytes = Math.Max(0L, estimatedBytes - entry.EstimatedBytes);
            if (eviction) evictions++;
            if (ReferenceEquals(lastManagedClip, entry.Clip))
            {
                lastManagedClip = null;
                lastManagedClipWasCacheHit = false;
            }
            RetireClip(entry.Clip);
        }

        private static void RefreshCurrentUsageState()
        {
            if (!IsEnabled())
            {
                currentUsageState = "OFF";
                return;
            }
            if (activeExternalLoads > 0)
            {
                currentUsageState = "LOAD";
                return;
            }

            AudioClip activeClip = GetActiveSongClip();
            if (activeClip != null)
            {
                if (!ContainsClip(activeClip))
                {
                    currentUsageState = "-";
                    return;
                }

                currentUsageState =
                    ReferenceEquals(activeClip, lastManagedClip) &&
                    lastManagedClipWasCacheHit
                        ? "HIT"
                        : "RAM";
                return;
            }

            if (lastManagedClip != null && ContainsClip(lastManagedClip))
            {
                currentUsageState = lastManagedClipWasCacheHit ? "HIT" : "RAM";
                return;
            }

            currentUsageState = "-";
        }

        private static AudioClip GetActiveSongClip()
        {
            try
            {
                scrConductor activeConductor = scrConductor.instance;
                if (activeConductor == null) return null;
                if (activeConductor.song != null &&
                    activeConductor.song.clip != null)
                {
                    return activeConductor.song.clip;
                }
                if (activeConductor.song2 != null)
                {
                    return activeConductor.song2.clip;
                }
            }
            catch
            {
                // A level transition can replace the conductor between reads.
            }
            return null;
        }

        private static bool ContainsClip(AudioClip audioClip)
        {
            if (audioClip == null) return false;
            foreach (CacheEntry entry in Entries.Values)
            {
                if (ReferenceEquals(entry.Clip, audioClip))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RetireClip(AudioClip audioClip)
        {
            if (audioClip == null || RetiredClips.Contains(audioClip)) return;
            RetiredClips.Add(audioClip);
        }

        private static void DestroyRetiredClipsWhenSafe()
        {
            for (int i = RetiredClips.Count - 1; i >= 0; i--)
            {
                AudioClip audioClip = RetiredClips[i];
                if (audioClip == null)
                {
                    RetiredClips.RemoveAt(i);
                    continue;
                }
                if (IsClipInUse(audioClip)) continue;

                try
                {
                    UnityEngine.Object.Destroy(audioClip);
                    RetiredClips.RemoveAt(i);
                }
                catch
                {
                    // Retry after the active AudioSource/dictionary has changed.
                }
            }
        }

        private static bool IsClipInUse(AudioClip audioClip)
        {
            try
            {
                foreach (CacheEntry cached in Entries.Values)
                {
                    if (ReferenceEquals(cached.Clip, audioClip))
                    {
                        return true;
                    }
                }

                scrConductor activeConductor = scrConductor.instance;
                if (activeConductor != null)
                {
                    if (activeConductor.song != null &&
                        ReferenceEquals(activeConductor.song.clip, audioClip))
                    {
                        return true;
                    }
                    if (activeConductor.song2 != null &&
                        ReferenceEquals(activeConductor.song2.clip, audioClip))
                    {
                        return true;
                    }
                }

                AudioManager manager = AudioManager.Instance;
                if (manager != null && manager.audioLib != null)
                {
                    foreach (AudioClip registered in manager.audioLib.Values)
                    {
                        if (ReferenceEquals(registered, audioClip))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private static void TryLoadAudioData(AudioClip audioClip)
        {
            try
            {
                if (audioClip != null) audioClip.LoadAudioData();
            }
            catch
            {
                // DownloadHandlerAudioClip may already have completed all loading.
            }
        }

        private static long EstimateDecodedBytes(AudioClip audioClip)
        {
            if (audioClip == null) return 0L;
            try
            {
                return Math.Max(0L, (long)audioClip.samples) *
                       Math.Max(1, audioClip.channels) * sizeof(float);
            }
            catch
            {
                return 0L;
            }
        }

        private static long GetMaximumBytes()
        {
            int megabytes = Main.Settings == null ? 512 : Main.Settings.OggCacheMaxMegabytes;
            megabytes = Math.Max(64, Math.Min(4096, megabytes));
            return (long)megabytes * 1024L * 1024L;
        }

        private static bool IsEnabled()
        {
            return initialized && Main.Enabled && Main.Settings != null &&
                   Main.Settings.EnableOggMemoryCache;
        }

        private static bool ShouldCachePath(string path)
        {
            if (!IsEnabled() || string.IsNullOrEmpty(path)) return false;
            try
            {
                return string.Equals(
                    Path.GetExtension(path),
                    ".ogg",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static string GetConductorName(string path)
        {
            try
            {
                return Path.GetFileName(path) + "*external";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetDisplayName(string path)
        {
            try
            {
                string fileName = Path.GetFileName(path);
                return string.IsNullOrEmpty(fileName) ? "(unknown.ogg)" : fileName;
            }
            catch
            {
                return "(unknown.ogg)";
            }
        }

        private static bool TryGetFingerprint(
            string canonicalPath,
            out long fileLength,
            out long lastWriteTicks)
        {
            fileLength = 0L;
            lastWriteTicks = 0L;
            try
            {
                FileInfo info = new FileInfo(canonicalPath);
                if (!info.Exists) return false;
                info.Refresh();
                fileLength = info.Length;
                lastWriteTicks = info.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long NextUseSerial()
        {
            useSerial++;
            if (useSerial <= 0L) useSerial = 1L;
            return useSerial;
        }
    }
}
