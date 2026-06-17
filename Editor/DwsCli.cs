using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DingConfig
{
    public enum SpaceType
    {
        Folder,
        Workspace
    }

    public class DocNodeInfo
    {
        [JsonProperty("nodeId")] public string NodeId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("extension")] public string Extension; // axls, xlsx, etc.
        [JsonProperty("nodeType")] public string NodeType;
        [JsonProperty("modifiedTime")] public string ModifiedTime; // ISO 8601 在线修改时间
        [JsonProperty("updateTime")] public string UpdateTime;
        [JsonProperty("lastEditTime")] public string LastEditTime;

        public bool IsFolder => string.Equals(NodeType, "folder", StringComparison.OrdinalIgnoreCase);
        public string EffectiveModifiedTime => ModifiedTime ?? UpdateTime ?? LastEditTime ?? "";
    }

    /// <summary>doc list 返回的包裹对象</summary>
    public class DocListResponse
    {
        [JsonProperty("nodes")] public List<DocNodeInfo> Nodes;
    }

    /// <summary>contact user get-self 返回的响应</summary>
    public class GetSelfResponse
    {
        [JsonProperty("result")] public List<UserInfo> Result;
        [JsonProperty("success")] public bool Success;
    }

    /// <summary>get-self result 中的单个用户条目</summary>
    public class UserInfo
    {
        [JsonProperty("isAdmin")] public bool IsAdmin;
        [JsonProperty("orgEmployeeModel")] public OrgEmployeeModel OrgEmployeeModel;
        public string OrgUserName => OrgEmployeeModel?.OrgUserName;
    }

    public class OrgEmployeeModel
    {
        [JsonProperty("orgUserName")] public string OrgUserName;
        [JsonProperty("orgName")] public string OrgName;
    }

    /// <summary>sheet range read 返回的表格数据</summary>
    public class SheetRangeData
    {
        [JsonProperty("displayValues")] public List<List<string>> DisplayValues;
    }

    public class DwsResult
    {
        public string Stdout;
        public string Stderr;
    }

    /// <summary>
    /// 封装 dws CLI 调用
    /// </summary>
    public static class DwsCli
    {
        private static string _resolvedDwsPath;

        /// <summary>
        /// 解析 dws 可执行文件的实际路径。
        /// Unity 进程的 PATH 不一定包含 npm global bin 或 ~/.local/bin，
        /// 因此需要主动搜索常见安装位置。
        /// </summary>
        private static string DwsPath
        {
            get
            {
                if (_resolvedDwsPath != null)
                    return _resolvedDwsPath;

                var ext = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows)
                    ? ".exe"
                    : "";
                var candidates = new[]
                {
                    "dws", // 依赖 PATH
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "dws" + ext),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "dws" + ext),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "dws.cmd"),
                };

                foreach (var c in candidates)
                {
                    // "dws" 不带路径 → 交给系统 PATH 解析
                    if (c == "dws")
                    {
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "dws" + ext,
                                Arguments = "--version",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                CreateNoWindow = true,
                            };
                            using var p = Process.Start(psi);
                            if (p != null)
                            {
                                p.WaitForExit(3000);
                                if (p.ExitCode == 0)
                                {
                                    _resolvedDwsPath = "dws" + ext;
                                    return _resolvedDwsPath;
                                }
                            }
                        }
                        catch
                        {
                            /* PATH 里找不到，继续候选 */
                        }
                    }
                    else if (File.Exists(c))
                    {
                        _resolvedDwsPath = c;
                        return _resolvedDwsPath;
                    }
                }

                // 全部失败，回退到裸命令名（让后续调用抛出明确异常）
                _resolvedDwsPath = "dws" + ext;
                return _resolvedDwsPath;
            }
        }

        /// <summary>重置路径缓存，允许重新搜索 dws 安装位置</summary>
        public static void ResetPathCache()
        {
            _resolvedDwsPath = null;
        }

        #region 基础执行

        /// <summary>
        /// 进程级超时（秒），防止 dws 进程卡死
        /// </summary>
        private const int ProcessTimeoutSeconds = 30;

        public static async Task<DwsResult> RunAsync(string args, Action<string> log = null)
        {
            log?.Invoke($"$ dws {args}");

            var psi = new ProcessStartInfo
            {
                FileName = DwsPath,
                Arguments = args + " --yes",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("无法启动 dws 进程，请确认已安装 dws CLI");

            // 并发读取 stdout 和 stderr，避免缓冲区满导致死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 进程级超时保护
            var timeoutTask = Task.Delay(ProcessTimeoutSeconds * 1000);
            var completed = await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), timeoutTask);

            if (completed == timeoutTask)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    /* 进程可能已退出 */
                }

                throw new Exception($"dws 进程超时 ({ProcessTimeoutSeconds}s)，已强制终止");
            }

            process.WaitForExit();

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            if (process.ExitCode != 0)
            {
                var error = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                throw new Exception($"dws 命令失败 (exit {process.ExitCode}):\n{error}");
            }

            return new DwsResult { Stdout = stdout, Stderr = stderr };
        }

        #endregion

        #region 安装检测

        /// <summary>检查 dws CLI 是否已安装，返回版本号或 null</summary>
        public static async Task<string> GetVersionAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = DwsPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var process = Process.Start(psi);
                if (process == null) return null;

                var output = await process.StandardOutput.ReadToEndAsync();
                process.WaitForExit();
                var version = output.Trim();
                return process.ExitCode == 0 && !string.IsNullOrEmpty(version)
                    ? version
                    : null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 认证

        public static async Task<bool> IsLoggedInAsync()
        {
            try
            {
                var result = await RunAsync("auth status -f json");
                return result.Stdout.Contains("valid") || result.Stdout.Contains("logged");
            }
            catch
            {
                return false;
            }
        }

        public static async Task LoginAsync(Action<string> log = null)
        {
            log?.Invoke("正在打开钉钉登录页面...");
            await RunAsync("auth login");
            log?.Invoke("登录完成");
        }

        public static async Task<GetSelfResponse> GetSelfAsync()
        {
            var result = await RunAsync("contact user get-self -f json");
            return JsonConvert.DeserializeObject<GetSelfResponse>(result.Stdout)
                   ?? new GetSelfResponse();
        }

        #endregion

        #region 空间 URL 解析

        /// <summary>
        /// 解析钉钉文档 URL，返回空间类型和 ID
        /// 支持:
        ///   https://alidocs.dingtalk.com/i/spaces/{id}/overview  → Workspace
        ///   https://alidocs.dingtalk.com/i/nodes/{id}             → Folder
        ///   纯 ID 字符串                                          → Folder (默认)
        /// </summary>
        public static (SpaceType type, string id) ParseSpaceUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("空间 URL 或 ID 不能为空");

            input = input.Trim();

            // 知识库 URL: /i/spaces/{id}/overview
            var spaceMatch = Regex.Match(input, @"/i/spaces/([^/?]+)");
            if (spaceMatch.Success)
                return (SpaceType.Workspace, spaceMatch.Groups[1].Value);

            // 文件夹 URL: /i/nodes/{id}
            var nodeMatch = Regex.Match(input, @"/i/nodes/([^/?]+)");
            if (nodeMatch.Success)
                return (SpaceType.Folder, nodeMatch.Groups[1].Value);

            // 纯 ID，默认当文件夹
            return (SpaceType.Folder, input);
        }

        #endregion

        #region 文档列表

        /// <summary>
        /// 列出文件夹或知识库内的文档
        /// </summary>
        public static async Task<List<DocNodeInfo>> ListDocsAsync(
            SpaceType spaceType, string spaceId, Action<string> log = null)
        {
            var flag = spaceType == SpaceType.Workspace ? "--workspace" : "--folder";
            var result = await RunAsync($"doc list {flag} \"{spaceId}\" --limit 50 -f json", log);
            var resp = JsonConvert.DeserializeObject<DocListResponse>(result.Stdout);
            return resp?.Nodes?.Where(n => !string.IsNullOrEmpty(n.NodeId)).ToList()
                   ?? new List<DocNodeInfo>();
        }

        #endregion

        #region 下载 / 导出

        /// <summary>
        /// 下载文件（适用于非 ALIDOC 类型，如原始 xlsx）
        /// </summary>
        public static async Task DownloadFileAsync(
            string nodeId, string outputPath, Action<string> log = null)
        {
            await RunAsync($"doc download --node \"{nodeId}\" --output \"{outputPath}\"", log);
        }

        /// <summary>
        /// 获取在线表格的所有工作表列表
        /// </summary>
        public static async Task<List<SheetInfo>> SheetListAsync(
            string nodeId, Action<string> log = null)
        {
            var result = await RunAsync($"sheet list --node \"{nodeId}\" -f json", log);
            try
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(result.Stdout);
                Newtonsoft.Json.Linq.JArray items;

                // Handle both direct array and object with items/sheets field
                if (token is Newtonsoft.Json.Linq.JArray arr)
                {
                    items = arr;
                }
                else if (token is Newtonsoft.Json.Linq.JObject obj)
                {
                    // Check for error first
                    if (obj["error"] != null)
                    {
                        var errMsg = obj["error"]?["message"]?.ToString() ?? result.Stdout;
                        throw new Exception("API错误: " + errMsg);
                    }

                    // Try common wrapper fields
                    items = obj["items"] as Newtonsoft.Json.Linq.JArray
                            ?? obj["sheets"] as Newtonsoft.Json.Linq.JArray
                            ?? obj["data"] as Newtonsoft.Json.Linq.JArray
                            ?? new Newtonsoft.Json.Linq.JArray();
                }
                else
                {
                    throw new Exception("未知的响应格式: " + result.Stdout);
                }

                var sheets = new List<SheetInfo>();
                foreach (var item in items)
                {
                    sheets.Add(new SheetInfo
                    {
                        Id = item["id"]?.ToString() ?? item["sheetId"]?.ToString() ?? "",
                        Name = item["name"]?.ToString() ?? item["title"]?.ToString() ?? ""
                    });
                }

                return sheets;
            }
            catch (Exception ex)
            {
                throw new Exception("解析工作表列表失败: " + ex.Message + "\n" + result.Stdout);
            }
        }

        /// <summary>
        /// 导出在线电子表格 (axls) 为 xlsx（通过 sheet export CLI）
        /// </summary>
        public static async Task ExportSheetAsync(
            string nodeId, string outputPath, Action<string> log = null)
        {
            await RunAsync(
                $"sheet export --node \"{nodeId}\" --output \"{outputPath}\" --timeout 300",
                log);
        }

        /// <summary>
        /// 获取文档元信息 (contentType / extension)
        /// </summary>
        public static async Task<DocNodeInfo> GetDocInfoAsync(
            string nodeId, Action<string> log = null)
        {
            var result = await RunAsync($"doc info --node \"{nodeId}\" -f json", log);
            try
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<DocNodeInfo>(result.Stdout);
            }
            catch (Exception ex)
            {
                throw new Exception("解析文档信息失败: " + ex.Message + "\n" + result.Stdout);
            }
        }

        /// <summary>
        /// 使用 dws doc upload --convert 直接上传 xlsx 文件并转换为钉钉在线表格。
        /// 返回新创建的 nodeId。
        /// </summary>
        public static async Task<string> DocUploadAsync(
            string localXlsxPath,
            SpaceType spaceType, string spaceId,
            Action<string> log = null)
        {
            var fileName = Path.GetFileName(localXlsxPath);
            log?.Invoke($"[upload] doc upload: {fileName}");

            string args;
            if (spaceType == SpaceType.Workspace)
                args = $"doc upload --file \"{localXlsxPath}\" --workspace \"{spaceId}\" --convert -f json";
            else
                args = $"doc upload --file \"{localXlsxPath}\" --folder \"{spaceId}\" --convert -f json";

            var result = await RunAsync(args, log);

            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(result.Stdout);
                var nodeId = json["nodeId"]?.ToString()
                             ?? json["id"]?.ToString()
                             ?? json["data"]?["nodeId"]?.ToString()
                             ?? json["data"]?["id"]?.ToString();
                if (string.IsNullOrEmpty(nodeId))
                    throw new Exception("上传成功但未返回 nodeId: " + result.Stdout);
                return nodeId;
            }
            catch (Exception ex) when (!(ex is Exception && ex.Message.StartsWith("上传成功")))
            {
                throw new Exception("解析上传结果失败: " + ex.Message + "\n" + result.Stdout);
            }
        }

        #endregion

        /// <summary>
        /// 工作表信息
        /// </summary>
        public class SheetInfo
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }
    }
}