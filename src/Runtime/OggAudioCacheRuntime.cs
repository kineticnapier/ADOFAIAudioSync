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
        private const int OggPageHeaderBytes = 27;
        private const int OggTailProbeBytes = 128 * 1024;

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
        private static bool activeExternalLoadAllowsMemory;
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
            activeExternalLoadAllowsMemory = false;
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
            if (!requested || !activeExternalLoadAllowsMemory ||
                !ShouldCachePath(activeExternalLoadPath))
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
                RemoveManagerReferences(entry.Clip);
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
            activeExternalLoadAllowsMemory = false;
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
            bool cacheEligible = false;
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
                    // Do not let AudioManager's filename-only dictionary return a stale
                    // streaming clip or a same-named file from another level.
                    if (manager != null && manager.audioLib != null &&
                        !string.IsNullOrEmpty(conductorName))
                    {
                        manager.audioLib.Remove(conductorName);
                    }

                    long estimatedDecodedBytes;
                    cacheEligible = streamOverridePatchInstalled &&
                        TryPrepareMemoryLoad(canonicalPath, out estimatedDecodedBytes);
                    if (cacheEligible)
                    {
                        lastLookupResult = "MISS";
                        status = "cache miss: " + GetDisplayName(path) +
                                 " / preflight " +
                                 FormatMegabytes(estimatedDecodedBytes) + " MB";
                    }
                    else if (!streamOverridePatchInstalled)
                    {
                        lastLookupResult = "STREAM";
                        status = "非ストリーミング化patch未適用のため本体読込";
                    }
                }

                while (true)
                {
                    bool moved;
                    activeExternalLoadPath = path;
                    activeExternalLoadAllowsMemory = cacheEligible;
                    try
                    {
                        moved = original.MoveNext();
                    }
                    finally
                    {
                        activeExternalLoadPath = null;
                        activeExternalLoadAllowsMemory = false;
                    }

                    if (!cacheHit && cacheEligible)
                    {
                        TryStoreLoadedClip(manager, conductorName, canonicalPath);
                    }
                    if (!moved)
                    {
                        break;
                    }
                    yield return original.Current;
                }

                if (!cacheHit && cacheEligible)
                {
                    TryStoreLoadedClip(manager, conductorName, canonicalPath);
                }
            }
            finally
            {
                activeExternalLoadPath = null;
                activeExternalLoadAllowsMemory = false;
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
            bool protectedEntryWasTooLarge = false;
            while (estimatedBytes > maximumBytes && Entries.Count > 0)
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

                if (oldest == null && !string.IsNullOrEmpty(protectedPath))
                {
                    Entries.TryGetValue(protectedPath, out oldest);
                    protectedEntryWasTooLarge = oldest != null;
                }
                if (oldest == null) break;
                RemoveEntry(oldest, true);
            }

            if (protectedEntryWasTooLarge)
            {
                status = "OGG単体が上限を超えたためキャッシュ対象外";
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
            RemoveManagerReferences(entry.Clip);
            RetireClip(entry.Clip);
        }

        private static void RemoveManagerReferences(AudioClip audioClip)
        {
            if (audioClip == null) return;
            try
            {
                AudioManager manager = AudioManager.Instance;
                if (manager == null || manager.audioLib == null) return;

                List<string> keys = new List<string>();
                foreach (KeyValuePair<string, AudioClip> pair in manager.audioLib)
                {
                    if (ReferenceEquals(pair.Value, audioClip))
                        keys.Add(pair.Key);
                }
                for (int i = 0; i < keys.Count; i++)
                    manager.audioLib.Remove(keys[i]);
            }
            catch
            {
                // The dictionary can be replaced during a level transition. The retired
                // clip remains queued and will be checked again before destruction.
            }
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

        private static bool TryPrepareMemoryLoad(
            string canonicalPath,
            out long incomingBytes)
        {
            incomingBytes = 0L;
            if (!TryEstimateOggDecodedBytes(canonicalPath, out incomingBytes))
            {
                lastLookupResult = "STREAM";
                status = "OGG容量をデコード前に判定できないため本体読込: " +
                         GetDisplayName(canonicalPath);
                return false;
            }

            long maximumBytes = GetMaximumBytes();
            if (incomingBytes <= 0L || incomingBytes > maximumBytes)
            {
                lastLookupResult = "STREAM";
                status = "OGG推定PCM " + FormatMegabytes(incomingBytes) +
                         " MB が上限 " + FormatMegabytes(maximumBytes) +
                         " MB を超えるため本体読込";
                return false;
            }

            TrimToFitIncoming(incomingBytes, maximumBytes);
            if (estimatedBytes > maximumBytes - incomingBytes)
            {
                lastLookupResult = "STREAM";
                status = "OGGキャッシュ予算を確保できないため本体読込";
                return false;
            }
            return true;
        }

        private static void TrimToFitIncoming(long incomingBytes, long maximumBytes)
        {
            long remainingBudget = Math.Max(0L, maximumBytes - incomingBytes);
            while (estimatedBytes > remainingBudget && Entries.Count > 0)
            {
                CacheEntry oldest = null;
                foreach (CacheEntry candidate in Entries.Values)
                {
                    if (oldest == null || candidate.LastUse < oldest.LastUse)
                    {
                        oldest = candidate;
                    }
                }
                if (oldest == null) break;
                RemoveEntry(oldest, true);
            }
            DestroyRetiredClipsWhenSafe();
        }

        /// <summary>
        /// Reads only the Vorbis identification header and the final OGG page. This
        /// obtains the exact decoded sample count without creating an AudioClip.
        /// Unknown, chained, incomplete, or malformed files safely remain streaming.
        /// </summary>
        private static bool TryEstimateOggDecodedBytes(
            string canonicalPath,
            out long decodedBytes)
        {
            decodedBytes = 0L;
            if (string.IsNullOrEmpty(canonicalPath)) return false;

            try
            {
                using (FileStream stream = new FileStream(
                    canonicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    if (stream.Length < OggPageHeaderBytes + 30L) return false;

                    byte[] pageHeader = new byte[OggPageHeaderBytes];
                    if (!ReadExactly(stream, pageHeader, 0, pageHeader.Length) ||
                        !HasOggCapturePattern(pageHeader, 0) ||
                        pageHeader[4] != 0 ||
                        (pageHeader[5] & 0x02) == 0)
                    {
                        return false;
                    }

                    int segmentCount = pageHeader[26];
                    byte[] segmentTable = new byte[segmentCount];
                    if (!ReadExactly(stream, segmentTable, 0, segmentTable.Length))
                    {
                        return false;
                    }

                    int firstPacketBytes = 0;
                    bool firstPacketEnded = false;
                    for (int i = 0; i < segmentTable.Length; i++)
                    {
                        firstPacketBytes += segmentTable[i];
                        if (segmentTable[i] < 255)
                        {
                            firstPacketEnded = true;
                            break;
                        }
                    }
                    if (!firstPacketEnded || firstPacketBytes != 30) return false;

                    byte[] identification = new byte[30];
                    if (!ReadExactly(stream, identification, 0, identification.Length) ||
                        identification[0] != 1 ||
                        identification[1] != (byte)'v' ||
                        identification[2] != (byte)'o' ||
                        identification[3] != (byte)'r' ||
                        identification[4] != (byte)'b' ||
                        identification[5] != (byte)'i' ||
                        identification[6] != (byte)'s')
                    {
                        return false;
                    }

                    int channels = identification[11];
                    uint sampleRate = ReadUInt32LittleEndian(identification, 12);
                    uint streamSerial = ReadUInt32LittleEndian(pageHeader, 14);
                    if (channels <= 0 || sampleRate == 0U) return false;

                    int tailLength = (int)Math.Min(
                        (long)OggTailProbeBytes,
                        stream.Length);
                    byte[] tail = new byte[tailLength];
                    stream.Position = stream.Length - tailLength;
                    if (!ReadExactly(stream, tail, 0, tail.Length)) return false;

                    for (int i = tail.Length - OggPageHeaderBytes; i >= 0; i--)
                    {
                        if (!HasOggCapturePattern(tail, i) ||
                            tail[i + 4] != 0 ||
                            (tail[i + 5] & 0x04) == 0)
                        {
                            continue;
                        }

                        int tailSegmentCount = tail[i + 26];
                        int segmentTableEnd = i + OggPageHeaderBytes + tailSegmentCount;
                        if (segmentTableEnd > tail.Length) continue;

                        int bodyBytes = 0;
                        for (int segment = 0; segment < tailSegmentCount; segment++)
                        {
                            bodyBytes += tail[i + OggPageHeaderBytes + segment];
                        }
                        if (segmentTableEnd + bodyBytes > tail.Length) continue;

                        // The newest complete logical stream must be the Vorbis stream
                        // whose identification header was read above. If another serial
                        // ends later, the file is chained or multiplexed and summing only
                        // one granule position would underestimate the decoded allocation.
                        if (ReadUInt32LittleEndian(tail, i + 14) != streamSerial)
                        {
                            return false;
                        }

                        long finalGranule = ReadInt64LittleEndian(tail, i + 6);
                        if (finalGranule <= 0L) return false;
                        if (finalGranule > long.MaxValue / channels / sizeof(float))
                        {
                            return false;
                        }

                        decodedBytes = finalGranule * channels * sizeof(float);
                        return decodedBytes > 0L;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static bool ReadExactly(
            Stream stream,
            byte[] buffer,
            int offset,
            int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) return false;
                offset += read;
                count -= read;
            }
            return true;
        }

        private static bool HasOggCapturePattern(byte[] data, int offset)
        {
            return data != null && offset >= 0 && offset + 4 <= data.Length &&
                   data[offset] == (byte)'O' && data[offset + 1] == (byte)'g' &&
                   data[offset + 2] == (byte)'g' && data[offset + 3] == (byte)'S';
        }

        private static uint ReadUInt32LittleEndian(byte[] data, int offset)
        {
            return (uint)data[offset] |
                   ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) |
                   ((uint)data[offset + 3] << 24);
        }

        private static long ReadInt64LittleEndian(byte[] data, int offset)
        {
            ulong value = 0UL;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)data[offset + i] << (8 * i);
            }
            return unchecked((long)value);
        }

        private static string FormatMegabytes(long bytes)
        {
            return (Math.Max(0L, bytes) / (1024d * 1024d)).ToString("0.0");
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
