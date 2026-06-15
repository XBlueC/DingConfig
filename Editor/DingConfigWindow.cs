using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DingConfig
{
    public class DingConfigWindow : EditorWindow
    {
        // ===== 登录 =====
        private bool _isLoggedIn;
        private bool _isLoggingIn;
        private string _userName = "";
        private Task _loginTask;
        private Task<bool> _checkAuthTask;

        // ===== dws CLI 检测 =====
        private string _dwsVersion;           // null = 未检测完, "" = 未安装
        private Task<string> _dwsVersionTask;

        // ===== 空间配置 =====
        private string _spaceUrl = "";
        private string _localDir = "";

        // ===== 映射 =====
        private List<FileMapping> _mappings = new List<FileMapping>();
        private bool _isScanning;
        private Task _scanTask;

        // ===== 拉取 =====
        private bool _isPulling;
        private Task _pullTask;

        // ===== 导出脚本 =====
        private string _exportScriptPath = "";

        // ===== UI =====
        private Vector2 _mappingScrollPos;
        private int _selectedTab;
        private readonly string[] _tabNames = { "空间配置", "文件映射" };
        private string _statusMessage = "";
        private bool _needsAssetRefresh;
        private string _searchText = "";

        // ===== 样式 =====
        private Color _cHeaderBg, _cBorder;
        private Color _cText, _cTextDim, _cTextBright;
        private Color _cRowEven, _cRowOdd, _cDotGreen, _cDotGray;

        // 状态颜色（缓存，避免每行重复 Hex() 解析）
        private Color _cChanged, _cUpToDate, _cLocalOnly, _cOnlineOnly;

        private GUIStyle _sHeader, _sCell, _sCellBold, _sBadge, _sStatus;
        private GUIStyle _sTitle, _sName, _sLink, _sLinkHover, _sSwitch;

        // DrawCircle 纹理缓存，避免每帧 new Texture2D
        private readonly Dictionary<Color, Texture2D> _circleTexCache = new Dictionary<Color, Texture2D>();

        private void EnsureStyles()
        {
            if (_sCellBold != null && _sCellBold.normal != null) return;

            // ---- 配色 ----
            _cHeaderBg = Hex("#2D2D30");
            _cBorder = Hex("#3C3C3C");
            _cText = Hex("#CCCCCC");
            _cTextDim = Hex("#808080");
            _cTextBright = Hex("#E0E0E0");
            _cRowEven = Hex("#262626");
            _cRowOdd = Hex("#2B2B2D");
            _cDotGreen = Hex("#27AE60");
            _cDotGray = Hex("#555555");
            _cChanged = Hex("#E67E22");
            _cUpToDate = Hex("#2ECC71");
            _cLocalOnly = Hex("#888888");
            _cOnlineOnly = Hex("#5B9BF5");

            // ---- 文本样式 ----
            _sTitle = new GUIStyle(EditorStyles.label);
            _sTitle.fontSize = 13;
            _sTitle.fontStyle = FontStyle.Bold;
            _sTitle.normal.textColor = _cTextBright;

            _sName = new GUIStyle(EditorStyles.label);
            _sName.fontSize = 12;
            _sName.normal.textColor = _cText;

            _sLink = new GUIStyle(EditorStyles.label);
            _sLink.fontSize = 11;
            _sLink.normal.textColor = _cTextDim;
            _sLink.hover.textColor = _cTextBright;

            _sStatus = new GUIStyle(EditorStyles.label);
            _sStatus.fontSize = 11;
            _sStatus.normal.textColor = _cTextDim;

            _sHeader = new GUIStyle(EditorStyles.label);
            _sHeader.fontSize = 11;
            _sHeader.fontStyle = FontStyle.Bold;
            _sHeader.normal.textColor = _cTextDim;
            _sHeader.padding = new RectOffset(8, 4, 4, 4);

            _sCell = new GUIStyle(EditorStyles.label);
            _sCell.fontSize = 12;
            _sCell.normal.textColor = _cText;
            _sCell.padding = new RectOffset(8, 4, 4, 4);

            _sSwitch = new GUIStyle(EditorStyles.label);
            _sSwitch.fontSize = 12;
            _sSwitch.normal.textColor = _cTextDim;

            _sCellBold = new GUIStyle(_sCell);
            _sCellBold.fontStyle = FontStyle.Bold;

            _sBadge = new GUIStyle(EditorStyles.label);
            _sBadge.fontSize = 11;
            _sBadge.fontStyle = FontStyle.Bold;
            _sBadge.alignment = TextAnchor.MiddleCenter;
            _sBadge.normal.textColor = Color.white;

            _sLinkHover = new GUIStyle(_sLink);
            _sLinkHover.normal.textColor = _cTextBright;
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        private void DrawCircle(Rect rect, Color color)
        {
            if (!_circleTexCache.TryGetValue(color, out var tex))
            {
                var r = rect.width * 0.5f;
                var size = Mathf.CeilToInt(rect.width);
                tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
                for (int px = 0; px < size; px++)
                for (int py = 0; py < size; py++)
                {
                    var dx = px - r + 0.5f;
                    var dy = py - r + 0.5f;
                    tex.SetPixel(px, py, dx * dx + dy * dy <= r * r ? color : Color.clear);
                }

                tex.Apply();
                tex.hideFlags = HideFlags.DontSave;
                _circleTexCache[color] = tex;
            }

            GUI.DrawTexture(rect, tex);
        }


        [MenuItem("Tools/配表工具")]
        public static void ShowWindow()
        {
            var w = GetWindow<DingConfigWindow>("配表工具");
            w.minSize = new Vector2(600, 500);
            w.Show();
        }

        #region 生命周期

        private void OnEnable()
        {
            _spaceUrl = EditorPrefs.GetString("DingConfig.SpaceUrl", "");
            _localDir = EditorPrefs.GetString("DingConfig.LocalDir", "");
            _exportScriptPath = EditorPrefs.GetString("DingConfig.ExportScript", "");

            _dwsVersionTask = DwsCli.GetVersionAsync();
            EditorApplication.update += OnDwsVersionUpdate;

            _checkAuthTask = DwsCli.IsLoggedInAsync();
            EditorApplication.update += OnAuthCheckUpdate;
        }

        private void OnFocus()
        {
            if (_isScanning && (_scanTask == null || _scanTask.IsCompleted))
            {
                _isScanning = false;
                _scanTask = null;
                EditorApplication.update -= OnScanUpdate;
            }

            if (_isPulling && (_pullTask == null || _pullTask.IsCompleted))
            {
                _isPulling = false;
                _pullTask = null;
                EditorApplication.update -= OnPullUpdate;
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnDwsVersionUpdate;
            EditorApplication.update -= OnAuthCheckUpdate;
            EditorApplication.update -= OnLoginUpdate;
            EditorApplication.update -= OnScanUpdate;
            EditorApplication.update -= OnPullUpdate;
        }

        private void OnDwsVersionUpdate()
        {
            if (_dwsVersionTask == null || !_dwsVersionTask.IsCompleted) return;
            EditorApplication.update -= OnDwsVersionUpdate;
            _dwsVersion = _dwsVersionTask.IsFaulted ? "" : (_dwsVersionTask.Result ?? "");
            _dwsVersionTask = null;
            Repaint();
        }

        private void RedetectDws()
        {
            DwsCli.ResetPathCache();
            _dwsVersion = null;
            _dwsVersionTask = DwsCli.GetVersionAsync();
            EditorApplication.update += OnDwsVersionUpdate;
        }

        private void OnAuthCheckUpdate()
        {
            if (_checkAuthTask == null || !_checkAuthTask.IsCompleted) return;
            EditorApplication.update -= OnAuthCheckUpdate;
            _isLoggedIn = !_checkAuthTask.IsFaulted && _checkAuthTask.Result;
            if (_isLoggedIn)
            {
                _statusMessage = "已登录";
                LoadUserName();
                if (!string.IsNullOrEmpty(_spaceUrl))
                {
                    _selectedTab = 1;
                    StartScan();
                }
            }
            else
            {
                _statusMessage = "未登录，请先登录钉钉";
            }

            _checkAuthTask = null;
            Repaint();
        }

        private async void LoadUserName()
        {
            try
            {
                var resp = await DwsCli.GetSelfAsync();
                var user = resp.Result?.FirstOrDefault();
                _userName = user?.OrgUserName ?? "";
                Log($"用户名: {_userName}");
                Repaint();
            }
            catch (Exception ex)
            {
                Log($"获取用户名失败: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            EditorPrefs.SetString("DingConfig.SpaceUrl", _spaceUrl);
            EditorPrefs.SetString("DingConfig.LocalDir", _localDir);
            EditorPrefs.SetString("DingConfig.ExportScript", _exportScriptPath);
        }

        #endregion

        #region GUI

        private void OnGUI()
        {
            EnsureStyles();
            DrawHeader();
            DrawTabBar();
            GUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();
            if (_selectedTab == 0) DrawSettingsTab();
            else DrawMappingTab();
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();
        }

        // ===== 顶部标题栏 =====
        private void DrawHeader()
        {
            const float pad = 8f;
            var fullRect = EditorGUILayout.GetControlRect(false, 36);
            // 背景与内容区域对齐（左右各缩进 pad）
            var headerRect = new Rect(fullRect.x + pad, fullRect.y, fullRect.width - pad * 2, fullRect.height);
            EditorGUI.DrawRect(headerRect, _cHeaderBg);
            EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.yMax - 1, headerRect.width, 1), _cBorder);

            GUI.Label(new Rect(headerRect.x + 12, headerRect.y + 8, 200, 20), "配表工具", _sTitle);

            if (_isLoggedIn)
            {
                var centerY = headerRect.y + 14;
                var nameContent = new GUIContent(_userName);
                var nameWidth = _sName.CalcSize(nameContent).x;
                nameWidth = Mathf.Min(nameWidth, 120f);
                var linkContent = new GUIContent(_isLoggingIn ? "登录中..." : "切换");
                var linkWidth = _sSwitch.CalcSize(linkContent).x + 8f;
                var rightBlockW = 8f + nameWidth + 8f + linkWidth + 4f;
                var dotX = headerRect.xMax - rightBlockW;
                DrawCircle(new Rect(dotX, centerY, 8, 8), _cDotGreen);
                GUI.Label(new Rect(dotX + 12, headerRect.y + 8, nameWidth, 20), _userName, _sName);
                var linkRect = new Rect(dotX + 12 + nameWidth + 8, headerRect.y + 8, linkWidth, 20);
                if (_isLoggingIn)
                {
                    GUI.Label(linkRect, "登录中...", _sLink);
                }
                else
                {
                    DrawHoverLink(linkRect, "切换", _sSwitch);
                    if (GUI.Button(linkRect, "", GUIStyle.none)) StartLogin();
                }
            }
            else
            {
                var dotX = headerRect.xMax - 100;
                var centerY = headerRect.y + 14;
                DrawCircle(new Rect(dotX, centerY, 8, 8), _cDotGray);
                GUI.Label(new Rect(dotX + 12, headerRect.y + 8, 40, 20), "未登录", _sLink);
                var loginRect = new Rect(headerRect.xMax - 50, headerRect.y + 8, 44, 20);
                if (_isLoggingIn)
                {
                    GUI.Label(loginRect, "登录中...", _sLink);
                }
                else
                {
                    DrawHoverLink(loginRect, "登录");
                    if (GUI.Button(loginRect, "", GUIStyle.none)) StartLogin();
                }
            }
        }

        /// <summary>绘制带 hover 高亮背景的链接按钮</summary>
        private void DrawHoverLink(Rect rect, string text, GUIStyle baseStyle = null)
        {
            var sBase = baseStyle ?? _sLink;
            var hovering = rect.Contains(Event.current.mousePosition);
            if (hovering)
            {
                EditorGUI.DrawRect(rect, new Color(1f, 1f, 1f, 0.08f));
                var sHover = new GUIStyle(sBase);
                sHover.normal.textColor = _cTextBright;
                GUI.Label(rect, text, sHover);
                Repaint();
            }
            else
            {
                GUI.Label(rect, text, sBase);
            }
        }

        // ===== Tab 栏 =====
        private void DrawTabBar()
        {
            GUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames, GUILayout.MaxWidth(200));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ===== 空间配置 Tab =====
        private void DrawSettingsTab()
        {
            GUILayout.Space(8);

            // ===== dws CLI 状态 =====
            if (_dwsVersion == null)
            {
                EditorGUILayout.HelpBox("正在检测 dws CLI ...", MessageType.Info);
            }
            else if (string.IsNullOrEmpty(_dwsVersion))
            {
                EditorGUILayout.HelpBox(
                    "未检测到 dws CLI，请先安装钉钉命令行工具。",
                    MessageType.Warning);
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("重新检测", GUILayout.Width(80)))
                {
                    RedetectDws();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox($"dws CLI 已安装  ({_dwsVersion})", MessageType.None);
            }

            // 文档链接
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var linkRect = GUILayoutUtility.GetRect(new GUIContent("查看 dws 文档"), _sLink);
            DrawHoverLink(linkRect, "查看 dws 文档");
            if (GUI.Button(linkRect, "", GUIStyle.none))
                Application.OpenURL("https://open.dingtalk.com/document/development/dingtalk-cli-performing-tasks-within");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8);

            _spaceUrl = EditorGUILayout.TextField("知识库/文件夹 URL", _spaceUrl);
            if (string.IsNullOrEmpty(_spaceUrl))
                EditorGUILayout.HelpBox(
                    "粘贴钉钉知识库 URL 或文件夹 URL\n" +
                    "例: https://alidocs.dingtalk.com/i/spaces/xxx/overview\n" +
                    "例: https://alidocs.dingtalk.com/i/nodes/xxx",
                    MessageType.Info);

            GUILayout.Space(8);

            EditorGUILayout.BeginHorizontal();
            _localDir = EditorGUILayout.TextField("本地 Datas 目录", _localDir);
            if (GUILayout.Button("浏览", GUILayout.Width(52)))
            {
                var path = EditorUtility.OpenFolderPanel("选择 Datas 目录", _localDir, "");
                if (!string.IsNullOrEmpty(path)) _localDir = path;
            }

            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8);

            EditorGUILayout.BeginHorizontal();
            _exportScriptPath = EditorGUILayout.TextField("导出脚本路径", _exportScriptPath);
            if (GUILayout.Button("浏览", GUILayout.Width(52)))
            {
                var path = EditorUtility.OpenFilePanel("选择导出脚本", _exportScriptPath, "bat,sh,cmd,ps1,zsh");
                if (!string.IsNullOrEmpty(path)) _exportScriptPath = path;
            }

            EditorGUILayout.EndHorizontal();
        }

        // ===== 文件映射 Tab =====
        private void DrawMappingTab()
        {
            var busy = _isScanning || _isPulling;
            var hasChanges = _mappings.Any(m =>
                m.Status == MappingStatus.Changed || m.Status == MappingStatus.OnlineOnly);

            GUILayout.Space(4);

            // ===== 工具栏（按钮行） =====
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !busy && _isLoggedIn && !string.IsNullOrEmpty(_spaceUrl);
            if (GUILayout.Button(_isScanning ? "扫描中..." : "刷新映射", GUILayout.Width(80)))
            {
                SaveSettings();
                StartScan();
            }

            GUI.enabled = true;

            GUILayout.Space(6);

            GUI.enabled = !busy && _isLoggedIn && hasChanges;
            if (GUILayout.Button(_isPulling ? "拉取中..." : "拉取变更", GUILayout.Width(80)))
            {
                SaveSettings();
                StartPull();
            }

            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            // 导出按钮
            GUI.enabled = !string.IsNullOrEmpty(_exportScriptPath) && File.Exists(_exportScriptPath);
            if (GUILayout.Button("导出", GUILayout.Width(80)))
            {
                SaveSettings();
                if (!ScriptRunner.Run(_exportScriptPath, out var error))
                {
                    _statusMessage = "启动失败: " + error;
                    Debug.LogError("[配表工具] " + _statusMessage);
                }
            }

            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // 错误提示
            if (!string.IsNullOrEmpty(_statusMessage) &&
                (_statusMessage.Contains("错误") || _statusMessage.Contains("失败")))
            {
                GUILayout.Space(4);
                EditorGUILayout.HelpBox(_statusMessage, MessageType.Error);
            }

            // ===== 搜索框 =====
            if (_mappings.Count > 0)
            {
                _searchText = EditorGUILayout.TextField(_searchText, EditorStyles.toolbarSearchField);
            }

            // ===== 映射列表 =====
            GUILayout.Space(4);
            _mappingScrollPos = EditorGUILayout.BeginScrollView(_mappingScrollPos);

            if (_mappings.Count > 0)
            {
                DrawMappingTable();
            }
            else if (!_isScanning)
            {
                GUILayout.Space(30);
                EditorGUILayout.HelpBox("点击「刷新映射」扫描在线文档和本地文件", MessageType.None);
            }

            EditorGUILayout.EndScrollView();

            // 底部统计信息（居中固定）
            if (_mappings.Count > 0)
            {
                int changed = 0, newOnl = 0, upToDate = 0;
                foreach (var m in _mappings)
                {
                    switch (m.Status)
                    {
                        case MappingStatus.Changed: changed++; break;
                        case MappingStatus.OnlineOnly: newOnl++; break;
                        case MappingStatus.UpToDate: upToDate++; break;
                    }
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    $"共 {_mappings.Count} 个  |  变更 {changed}  新增 {newOnl}  最新 {upToDate}",
                    _sStatus);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
        }

        private void DrawMappingTable()
        {
            // 表头
            var headerRect = EditorGUILayout.GetControlRect(false, 28);
            EditorGUI.DrawRect(headerRect, _cHeaderBg);

            var x = headerRect.x;
            var colW1 = headerRect.width * 0.28f;
            var colW2 = headerRect.width * 0.24f;
            var colW3 = headerRect.width * 0.24f;
            var colW4 = headerRect.width * 0.10f;
            var colW5 = headerRect.width - colW1 - colW2 - colW3 - colW4;

            GUI.Label(new Rect(x, headerRect.y, colW1, 28), "在线文档", _sHeader);
            x += colW1;
            GUI.Label(new Rect(x, headerRect.y, colW2, 28), "在线修改", _sHeader);
            x += colW2;
            GUI.Label(new Rect(x, headerRect.y, colW3, 28), "本地文件", _sHeader);
            x += colW3;
            GUI.Label(new Rect(x, headerRect.y, colW4, 28), "状态", _sHeader);
            x += colW4;
            GUI.Label(new Rect(x, headerRect.y, colW5, 28), "操作", _sHeader);

            // 底部分隔线
            EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.yMax, headerRect.width, 1), _cBorder);

            // 过滤
            var filtered = string.IsNullOrEmpty(_searchText)
                ? _mappings
                : _mappings.Where(m =>
                        (m.OnlineName ?? "").IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (m.LocalName ?? "").IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            // 数据行
            for (int i = 0; i < filtered.Count; i++)
            {
                var m = filtered[i];
                var rowRect = EditorGUILayout.GetControlRect(false, 30);
                var bgColor = i % 2 == 0 ? _cRowEven : _cRowOdd;
                EditorGUI.DrawRect(rowRect, bgColor);

                var rx = rowRect.x;
                GUI.Label(new Rect(rx, rowRect.y, colW1, 30), m.OnlineName ?? "—", _sCell);
                rx += colW1;
                GUI.Label(new Rect(rx, rowRect.y, colW2, 30), PullState.FormatTime(m.OnlineModifiedTime), _sCell);
                rx += colW2;
                GUI.Label(new Rect(rx, rowRect.y, colW3, 30), m.LocalName ?? "—", _sCell);
                rx += colW3;

                // 状态文字（彩色粗体，左对齐与其他列一致）
                var (statusText, statusColor) = m.Status switch
                {
                    MappingStatus.Changed => ("有变更", _cChanged),
                    MappingStatus.UpToDate => ("最新", _cUpToDate),
                    MappingStatus.LocalOnly => ("仅本地", _cLocalOnly),
                    MappingStatus.OnlineOnly => ("新增", _cOnlineOnly),
                    _ => ("—", _cLocalOnly)
                };

                _sCellBold.normal.textColor = statusColor;
                GUI.Label(new Rect(rx, rowRect.y, colW4, 30), statusText, _sCellBold);
                rx += colW4;

                // 打开云文档按钮（紧凑小按钮，左对齐）
                if (!string.IsNullOrEmpty(m.OnlineNodeId))
                {
                    var btnW = 40f;
                    var btnRect = new Rect(rx + 4, rowRect.y + 7, btnW, 16);
                    if (GUI.Button(btnRect, "打开", EditorStyles.miniButton))
                    {
                        Application.OpenURL($"https://alidocs.dingtalk.com/i/nodes/{m.OnlineNodeId}");
                    }
                }
            }

            // 底部线
            var bottomSep = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(bottomSep, _cBorder);
        }

        #endregion

        #region 登录

        private void StartLogin()
        {
            _isLoggingIn = true;
            _loginTask = DwsCli.LoginAsync(Log);
            EditorApplication.update += OnLoginUpdate;
        }

        private void OnLoginUpdate()
        {
            if (_loginTask == null || !_loginTask.IsCompleted) return;
            EditorApplication.update -= OnLoginUpdate;
            if (_loginTask.IsFaulted)
            {
                var ex = _loginTask.Exception?.InnerException ?? _loginTask.Exception;
                _statusMessage = "登录失败: " + ex?.Message;
            }
            else
            {
                _isLoggedIn = true;
                _statusMessage = "登录成功!";
                LoadUserName();
            }

            _loginTask = null;
            _isLoggingIn = false;
            Repaint();
        }

        #endregion

        #region 扫描映射 + 变更检测

        private void StartScan()
        {
            _isScanning = true;
            _scanTask = DoScanAsync();
            EditorApplication.update += OnScanUpdate;
        }

        private void OnScanUpdate()
        {
            if (_scanTask == null || !_scanTask.IsCompleted)
            {
                Repaint();
                return;
            }

            EditorApplication.update -= OnScanUpdate;
            if (_scanTask.IsFaulted)
            {
                var ex = _scanTask.Exception?.InnerException ?? _scanTask.Exception;
                _statusMessage = "扫描失败: " + ex?.Message;
                Log("[错误] " + ex?.Message);
            }

            _scanTask = null;
            _isScanning = false;
            Repaint();
        }

        private async Task DoScanAsync()
        {
            _statusMessage = "正在扫描...";
            Repaint();

            try
            {
                var (spaceType, spaceId) = DwsCli.ParseSpaceUrl(_spaceUrl);

                Log("获取在线文档列表...");
                var onlineDocs = await DwsCli.ListDocsAsync(spaceType, spaceId, Log);
                Log($"在线文档: {onlineDocs.Count} 个");

                var syncState = PullState.Load(_localDir);

                var localFiles = ScanLocalFiles();
                var localByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in localFiles)
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(f);
                    localByName[nameNoExt] = f;
                }

                _mappings.Clear();
                var matchedLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var doc in onlineDocs)
                {
                    if (doc.IsFolder) continue;
                    var docNameNoExt = Path.GetFileNameWithoutExtension(doc.Name);

                    var mapping = new FileMapping
                    {
                        OnlineNodeId = doc.NodeId,
                        OnlineName = doc.Name,
                        OnlineExt = doc.Extension,
                        OnlineModifiedTime = doc.EffectiveModifiedTime,
                    };

                    if (localByName.TryGetValue(docNameNoExt, out var localPath))
                    {
                        mapping.LocalPath = localPath;
                        mapping.LocalName = Path.GetFileName(localPath);
                        matchedLocal.Add(docNameNoExt);

                        if (PullState.HasChanged(syncState, doc.NodeId, doc.EffectiveModifiedTime))
                            mapping.Status = MappingStatus.Changed;
                        else
                            mapping.Status = MappingStatus.UpToDate;
                    }
                    else
                    {
                        mapping.Status = MappingStatus.OnlineOnly;
                    }

                    _mappings.Add(mapping);
                }

                foreach (var kvp in localByName)
                {
                    if (matchedLocal.Contains(kvp.Key)) continue;
                    _mappings.Add(new FileMapping
                    {
                        LocalPath = kvp.Value,
                        LocalName = Path.GetFileName(kvp.Value),
                        Status = MappingStatus.LocalOnly
                    });
                }

                int changed = 0, newOnl = 0, upToDate = 0;
                foreach (var m in _mappings)
                {
                    switch (m.Status)
                    {
                        case MappingStatus.Changed: changed++; break;
                        case MappingStatus.OnlineOnly: newOnl++; break;
                        case MappingStatus.UpToDate: upToDate++; break;
                    }
                }
                _statusMessage = $"扫描完成: 有变更 {changed} | 新增 {newOnl} | 最新 {upToDate}";
                Log(_statusMessage);
            }
            catch (Exception ex)
            {
                _statusMessage = "扫描失败: " + ex.Message;
                Log("[错误] " + ex.Message);
            }
        }

        private List<string> ScanLocalFiles()
        {
            if (!Directory.Exists(_localDir))
            {
                Log("[警告] 本地目录不存在: " + _localDir);
                return new List<string>();
            }

            return Directory.GetFiles(_localDir, "*.xlsx")
                .Where(f => !Path.GetFileName(f).StartsWith("~"))
                .OrderBy(f => f)
                .ToList();
        }

        #endregion

        #region 拉取变更

        private void StartPull()
        {
            _isPulling = true;
            _pullTask = DoPullAsync();
            EditorApplication.update += OnPullUpdate;
        }

        private void OnPullUpdate()
        {
            if (_pullTask == null || !_pullTask.IsCompleted)
            {
                Repaint();
                return;
            }

            EditorApplication.update -= OnPullUpdate;
            if (_pullTask.IsFaulted)
            {
                var ex = _pullTask.Exception?.InnerException ?? _pullTask.Exception;
                _statusMessage = "错误: " + ex?.Message;
                Log("[错误] " + ex);
            }

            _pullTask = null;
            _isPulling = false;
            if (_needsAssetRefresh)
            {
                _needsAssetRefresh = false;
                AssetDatabase.Refresh();
            }

            Repaint();
        }

        private async Task DoPullAsync()
        {
            _statusMessage = "正在拉取变更...";
            Repaint();

            try
            {
                if (!Directory.Exists(_localDir))
                    Directory.CreateDirectory(_localDir);

                var syncState = PullState.Load(_localDir);
                int downloaded = 0, skipped = 0, failed = 0;

                var toDownload = _mappings
                    .Where(m => !string.IsNullOrEmpty(m.OnlineNodeId)
                                && (m.Status == MappingStatus.Changed
                                    || m.Status == MappingStatus.OnlineOnly))
                    .ToList();
                skipped = _mappings.Count - toDownload.Count;

                if (toDownload.Count == 0)
                {
                    _statusMessage = "没有需要下载的文件";
                    Log(_statusMessage);
                    return;
                }

                Log($"需要下载: {toDownload.Count} 个文件");

                foreach (var m in toDownload)
                {
                    var fileName = !string.IsNullOrEmpty(m.OnlineName) ? m.OnlineName : m.OnlineNodeId;
                    if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                        fileName += ".xlsx";

                    var outputPath = Path.Combine(_localDir, fileName);
                    var sw = Stopwatch.StartNew();
                    Log($"[下载] {m.OnlineName} ({PullState.FormatTime(m.OnlineModifiedTime)})");

                    try
                    {
                        if ("axls".Equals(m.OnlineExt, StringComparison.OrdinalIgnoreCase))
                            await DwsCli.ExportSheetViaRangeReadAsync(m.OnlineNodeId, outputPath, Log);
                        else
                            await DwsCli.DownloadFileAsync(m.OnlineNodeId, outputPath, Log);

                        sw.Stop();
                        Log($"  -> {m.OnlineName} 成功 ({sw.Elapsed.TotalSeconds:F1}s)");
                        downloaded++;

                        PullState.UpdateFile(syncState, m.OnlineNodeId,
                            m.OnlineName, m.OnlineModifiedTime);
                        m.Status = MappingStatus.UpToDate;
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        Log($"  -> {m.OnlineName} 失败 ({sw.Elapsed.TotalSeconds:F1}s): {ex.Message}");
                        failed++;
                    }
                }

                syncState.lastFullSyncTicks = DateTime.UtcNow.Ticks;
                PullState.Save(_localDir, syncState);

                _needsAssetRefresh = true;

                _statusMessage = $"拉取完成: 下载 {downloaded}, 跳过 {skipped}, 失败 {failed}";
                Log(_statusMessage);
            }
            catch (Exception ex)
            {
                _statusMessage = "拉取失败: " + ex.Message;
                Log("[错误] " + ex.Message);
            }
        }

        #endregion

        #region 日志

        private const string LogPrefix = "[DingConfig] ";

        private void Log(string message)
        {
            foreach (var part in message.Split('\n'))
            {
                var trimmed = part.TrimEnd('\r');
                if (trimmed.Length == 0) continue;

                if (trimmed.Contains("[错误]") || trimmed.Contains("[警告]"))
                    Debug.LogWarning(LogPrefix + trimmed);
                else
                    Debug.Log(LogPrefix + trimmed);
            }
        }

        #endregion
    }

    #region 数据结构

    public enum MappingStatus
    {
        UpToDate,
        Changed,
        OnlineOnly,
        LocalOnly
    }

    public class FileMapping
    {
        public string LocalPath;
        public string LocalName;
        public string OnlineNodeId;
        public string OnlineName;
        public string OnlineExt;
        public string OnlineModifiedTime;
        public MappingStatus Status;
    }

    #endregion
}