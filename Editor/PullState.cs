using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace DingConfig
{
    /// <summary>
    /// 同步状态管理：记录每个在线文档上次同步时的修改时间，用于变更检测。
    /// 状态文件保存在本地 Datas 目录下 (.pullstate.json)，跟数据文件走。
    /// </summary>
    public static class PullState
    {
        private const string StateFileName = ".pullstate.json";

        [Serializable]
        public class FileRecord
        {
            public string fileName; // 在线文档名 (如 item_config)
            public string nodeId; // 在线文档 nodeId
            public string modifiedTime; // 上次同步时的在线修改时间 (ISO 8601)
            public long lastSyncTicks; // 上次同步的本地时间 (UTC ticks)
        }

        [Serializable]
        public class SyncStateData
        {
            public long lastFullSyncTicks;
            public List<FileRecord> files = new List<FileRecord>();
        }

        public static SyncStateData Load(string localDir)
        {
            var path = GetStateFilePath(localDir);
            if (!File.Exists(path))
                return new SyncStateData();
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SyncStateData>(json)
                       ?? new SyncStateData();
            }
            catch
            {
                return new SyncStateData();
            }
        }

        public static void Save(string localDir, SyncStateData state)
        {
            if (string.IsNullOrEmpty(localDir) || !Directory.Exists(localDir))
                return;
            var path = GetStateFilePath(localDir);
            var json = JsonConvert.SerializeObject(state, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        private static string GetStateFilePath(string localDir)
        {
            return Path.Combine(localDir, StateFileName);
        }

        /// <summary>
        /// 检查某个在线文档是否有变更（比较在线修改时间与上次记录的修改时间）
        /// </summary>
        public static bool HasChanged(SyncStateData state, string nodeId, string currentModifiedTime)
        {
            var record = state.files.Find(f => f.nodeId == nodeId);
            if (record == null) return true; // 从未同步过 = 视为变更
            return record.modifiedTime != currentModifiedTime;
        }

        /// <summary>
        /// 更新某个文件的同步记录
        /// </summary>
        public static void UpdateFile(SyncStateData state, string nodeId, string fileName, string modifiedTime)
        {
            var existing = state.files.Find(f => f.nodeId == nodeId);
            if (existing != null)
            {
                existing.modifiedTime = modifiedTime;
                existing.fileName = fileName;
                existing.lastSyncTicks = DateTime.UtcNow.Ticks;
            }
            else
            {
                state.files.Add(new FileRecord
                {
                    nodeId = nodeId,
                    fileName = fileName,
                    modifiedTime = modifiedTime,
                    lastSyncTicks = DateTime.UtcNow.Ticks
                });
            }
        }

        public static string FormatTime(string isoTime)
        {
            if (string.IsNullOrEmpty(isoTime)) return "—";

            // Unix 时间戳（毫秒或秒）
            if (long.TryParse(isoTime, out var ts))
            {
                // 毫秒级 (>1e12) 或秒级 (>1e9)
                var ms = ts > 1_000_000_000_000L ? ts : ts * 1000L;
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return epoch.AddMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            // ISO 8601 格式
            string[] formats =
            {
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                "yyyy-MM-ddTHH:mm:ss.ffffffZ",
                "yyyy-MM-ddTHH:mm:ss.fffffZ",
                "yyyy-MM-ddTHH:mm:ss.ffffZ",
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                "yyyy-MM-ddTHH:mm:ss.ffZ",
                "yyyy-MM-ddTHH:mm:ss.fZ",
                "yyyy-MM-ddTHH:mm:ssZ",
                "yyyy-MM-ddTHH:mm:ss.fffffffzzz",
                "yyyy-MM-ddTHH:mm:sszzz",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-dd HH:mm:ss",
            };

            if (DateTime.TryParseExact(isoTime, formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc))
            {
                return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            // 回退: 通用解析
            if (DateTime.TryParse(isoTime, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            {
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            // 实在解析不了，截取可读部分
            var t = isoTime.Replace("T", " ");
            if (t.Length > 19) t = t.Substring(0, 19);
            return t;
        }

        public static string FormatTicks(long ticks)
        {
            if (ticks == 0) return "从未";
            var dt = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}