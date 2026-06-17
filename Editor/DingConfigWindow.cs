using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
        private string _dwsVersion; // null = 未检测完, "" = 未安装
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

        private bool _needsAssetRefresh;
        private string _searchText = "";

        // ===== Toast 气泡 =====
        private struct ToastItem
        {
            public string Text;
            public float BornTime;
            public bool IsError;
        }

        private readonly List<ToastItem> _toasts = new List<ToastItem>();
        private const float ToastDuration = 2.5f;
        private const float ToastFadeTime = 0.5f;
        private GUIStyle _sToast;

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

        // Toast 圆角纹理缓存
        private Texture2D _toastRoundTex;

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
                LoadUserName();
                if (!string.IsNullOrEmpty(_spaceUrl))
                {
                    _selectedTab = 1;
                    StartScan();
                }
            }
            else
            {
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
            GUILayout.Space(6);
            EditorGUILayout.BeginVertical();
            if (_selectedTab == 0) DrawSettingsTab();
            else DrawMappingTab();
            EditorGUILayout.EndVertical();
            GUILayout.Space(6);
            EditorGUILayout.EndHorizontal();

            DrawToasts();
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

            // ===== 卡片1: dws CLI 状态 =====
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                // 标题行：标题 + 文档链接
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("🔧 dws CLI 状态", _sTitle);
                GUILayout.FlexibleSpace();
                var docLinkRect = GUILayoutUtility.GetRect(new GUIContent("查看文档 →"), _sLink);
                DrawHoverLink(docLinkRect, "查看文档 →");
                if (GUI.Button(docLinkRect, "", GUIStyle.none))
                    Application.OpenURL("https://open.dingtalk.com/document/development/dingtalk-cli-performing-tasks-within");
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4);

                if (_dwsVersion == null)
                {
                    EditorGUILayout.HelpBox("正在检测 dws CLI ...", MessageType.Info);
                }
                else if (string.IsNullOrEmpty(_dwsVersion))
                {
                    EditorGUILayout.HelpBox(
                        "未检测到 dws CLI，请先安装钉钉命令行工具。",
                        MessageType.Warning);
                    GUILayout.Space(4);
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
                    EditorGUILayout.LabelField($"✓ 已安装  {_dwsVersion}", _sName);
                }
            }
            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            // ===== 卡片2: 远程空间配置 =====
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("📚 远程空间配置", _sTitle);
                GUILayout.Space(4);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("知识库 URL", GUILayout.Width(80));
                _spaceUrl = EditorGUILayout.TextField(_spaceUrl);
                EditorGUILayout.EndHorizontal();

                if (string.IsNullOrEmpty(_spaceUrl))
                {
                    GUILayout.Space(2);
                    EditorGUILayout.HelpBox(
                        "粘贴钉钉知识库 URL 或文件夹 URL\n" +
                        "例: https://alidocs.dingtalk.com/i/spaces/xxx/overview\n" +
                        "例: https://alidocs.dingtalk.com/i/nodes/xxx",
                        MessageType.Info);
                }
                else
                {
                    GUILayout.Space(2);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(84);
                    var wikiLinkRect = GUILayoutUtility.GetRect(new GUIContent("打开钉钉知识库页面 →"), _sLink);
                    DrawHoverLink(wikiLinkRect, "打开钉钉知识库页面 →");
                    if (GUI.Button(wikiLinkRect, "", GUIStyle.none))
                        Application.OpenURL("https://alidocs.dingtalk.com/i/desktop/spaces");
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            // ===== 卡片3: 本地配置 =====
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("💾 本地配置", _sTitle);
                GUILayout.Space(4);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Datas 目录", GUILayout.Width(80));
                _localDir = EditorGUILayout.TextField(_localDir);
                if (GUILayout.Button("浏览", GUILayout.Width(52)))
                {
                    var path = EditorUtility.OpenFolderPanel("选择 Datas 目录", _localDir, "");
                    if (!string.IsNullOrEmpty(path)) _localDir = path;
                }
                EditorGUILayout.EndHorizontal();

                GUILayout.Space(4);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("导出脚本", GUILayout.Width(80));
                _exportScriptPath = EditorGUILayout.TextField(_exportScriptPath);
                if (GUILayout.Button("浏览", GUILayout.Width(52)))
                {
                    var path = EditorUtility.OpenFilePanel("选择导出脚本", _exportScriptPath, "bat,sh,cmd,ps1,zsh");
                    if (!string.IsNullOrEmpty(path)) _exportScriptPath = path;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        // ===== 文件映射 Tab =====
        private void DrawMappingTab()
        {
            var busy = _isScanning || _isPulling || _isSingleOperating;
            var hasChanges = _mappings.Any(m =>
                m.Status == MappingStatus.Changed || m.Status == MappingStatus.OnlineOnly);

            GUILayout.Space(4);

            // ===== 工具栏（按钮行） =====
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !busy && _isLoggedIn && !string.IsNullOrEmpty(_spaceUrl);
            if (GUILayout.Button(_isScanning ? "扫描中..." : "刷新映射", GUILayout.Width(70)))
            {
                SaveSettings();
                StartScan();
            }

            GUI.enabled = true;

            GUILayout.Space(6);

            GUI.enabled = !busy && _isLoggedIn && hasChanges;
            if (GUILayout.Button(_isPulling ? "拉取中..." : "拉取变更", GUILayout.Width(70)))
            {
                SaveSettings();
                StartPull();
            }

            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            // 打开目录按钮
            GUI.enabled = !string.IsNullOrEmpty(_localDir) && Directory.Exists(_localDir);
            if (GUILayout.Button("本地目录", GUILayout.Width(64)))
                System.Diagnostics.Process.Start(_localDir);
            GUI.enabled = true;

            GUILayout.Space(6);

            GUI.enabled = !string.IsNullOrEmpty(_spaceUrl);
            if (GUILayout.Button("远程目录", GUILayout.Width(64)))
                Application.OpenURL(_spaceUrl);
            GUI.enabled = true;

            GUILayout.Space(6);

            // 导出按钮
            GUI.enabled = !string.IsNullOrEmpty(_exportScriptPath) && File.Exists(_exportScriptPath);
            if (GUILayout.Button("导出", GUILayout.Width(64)))
            {
                SaveSettings();
                if (!ScriptRunner.Run(_exportScriptPath, out var error))
                {
                    Debug.LogError("[DingConfig] 启动失败: " + error);
                }
            }

            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();


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
            var colW1 = headerRect.width * 0.22f; // 在线文档
            var colW2 = headerRect.width * 0.18f; // 在线修改
            var colW3 = headerRect.width * 0.22f; // 本地文件（与在线文档同宽）
            var colW4 = headerRect.width * 0.13f; // 状态（加宽）
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

                // 操作按钮组
                {
                    var bx = rx + 2f;
                    var btnH = 16f;
                    var btnY = rowRect.y + 7;
                    var hasRemote = !string.IsNullOrEmpty(m.OnlineNodeId);
                    var hasLocal = !string.IsNullOrEmpty(m.LocalPath);

                    // 打开文件：优先远程，其次本地
                    GUI.enabled = hasRemote || hasLocal;
                    if (GUI.Button(new Rect(bx, btnY, 42, btnH), "打开", EditorStyles.miniButton))
                    {
                        if (hasRemote)
                            Application.OpenURL("https://alidocs.dingtalk.com/i/nodes/" + m.OnlineNodeId);
                        else if (hasLocal)
                            System.Diagnostics.Process.Start(m.LocalPath);
                    }

                    GUI.enabled = true;
                    bx += 45;

                    // 拉取（仅当有变更或新增时可用）
                    GUI.enabled = !(_isScanning || _isPulling || _isSingleOperating) && (m.Status == MappingStatus.Changed || m.Status == MappingStatus.OnlineOnly);
                    if (GUI.Button(new Rect(bx, btnY, 42, btnH), "拉取", EditorStyles.miniButton))
                        StartSinglePull(m);
                    GUI.enabled = true;
                    bx += 45;

                    // 上传（仅当仅本地时可用）
                    GUI.enabled = !(_isScanning || _isPulling || _isSingleOperating) && m.Status == MappingStatus.LocalOnly;
                    if (GUI.Button(new Rect(bx, btnY, 42, btnH), "上传", EditorStyles.miniButton))
                        StartSingleUpload(m);
                    GUI.enabled = true;
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
                Log("[错误] 登录失败: " + ex?.Message);
            }
            else
            {
                _isLoggedIn = true;
                Log("登录成功!");
                ShowToast("登录成功!");
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
                Log("[错误] 扫描失败: " + ex?.Message);
                Log("[错误] " + ex?.Message);
            }

            _scanTask = null;
            _isScanning = false;
            Repaint();
        }

        private async Task DoScanAsync()
        {
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

                ShowToast($"扫描完成: 变更{changed} 新增{newOnl} 最新{upToDate}");
            }
            catch (Exception ex)
            {
                Log("[错误] 扫描失败: " + ex.Message);
                Log("[错误] " + ex.Message);
                ShowToast("扫描失败: " + ex.Message, true);
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
                Log("[错误] " + ex?.Message);
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
                    return;
                }

                Log($"需要下载: {toDownload.Count} 个文件");

                // 并发下载，最多同时3个任务
                const int maxConcurrency = 3;
                var semaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = new List<Task>();

                foreach (var m in toDownload)
                {
                    var mapping = m; // Capture for closure
                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var success = await DownloadAndUpdateMappingAsync(
                                mapping, _localDir, syncState, Log);

                            if (success)
                                Interlocked.Increment(ref downloaded);
                            else
                                Interlocked.Increment(ref failed);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                syncState.lastFullSyncTicks = DateTime.UtcNow.Ticks;
                PullState.Save(_localDir, syncState);

                _needsAssetRefresh = true;

                ShowToast($"拉取完成: 下载{downloaded} 跳过{skipped} 失败{failed}", failed > 0);
            }
            catch (Exception ex)
            {
                Log("[错误] 拉取失败: " + ex.Message);
                Log("[错误] " + ex.Message);
                ShowToast("拉取失败: " + ex.Message, true);
            }
        }

        #endregion

        #region 单表操作

        private bool _isSingleOperating;
        private Task _singleOpTask;

        private void OnSingleOpUpdate()
        {
            if (_singleOpTask == null || !_singleOpTask.IsCompleted)
            {
                Repaint();
                return;
            }

            EditorApplication.update -= OnSingleOpUpdate;
            if (_singleOpTask.IsFaulted)
            {
                var ex = _singleOpTask.Exception?.InnerException ?? _singleOpTask.Exception;
                Log("[错误] " + ex?.Message);
                Log("[错误] " + ex);
            }

            _singleOpTask = null;
            _isSingleOperating = false;
            if (_needsAssetRefresh)
            {
                _needsAssetRefresh = false;
                AssetDatabase.Refresh();
            }

            Repaint();
        }

        private void StartSinglePull(FileMapping m)
        {
            _isSingleOperating = true;
            _singleOpTask = DoSinglePullAsync(m);
            EditorApplication.update += OnSingleOpUpdate;
        }

        /// <summary>
        /// 下载单个文件并更新映射状态（供单表和批量拉取复用）
        /// </summary>
        private async Task<bool> DownloadAndUpdateMappingAsync(
            FileMapping mapping, string localDir, PullState.SyncStateData syncState,
            Action<string> log)
        {
            var fileName = !string.IsNullOrEmpty(mapping.OnlineName) ? mapping.OnlineName : mapping.OnlineNodeId;
            if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                fileName += ".xlsx";

            var outputPath = Path.Combine(localDir, fileName);

            // Check file lock
            if (File.Exists(outputPath))
            {
                try
                {
                    using (var test = File.Open(outputPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                    }
                }
                catch (IOException)
                {
                    log?.Invoke($"[跳过] {mapping.OnlineName}: 文件被占用，请关闭 Excel");
                    return false;
                }
            }

            var sw = Stopwatch.StartNew();
            log?.Invoke($"[下载] {mapping.OnlineName} ({PullState.FormatTime(mapping.OnlineModifiedTime)})");

            try
            {
                if ("axls".Equals(mapping.OnlineExt, StringComparison.OrdinalIgnoreCase))
                    await DwsCli.ExportSheetAsync(mapping.OnlineNodeId, outputPath, log);
                else
                    await DwsCli.DownloadFileAsync(mapping.OnlineNodeId, outputPath, log);

                sw.Stop();
                log?.Invoke($"  -> {mapping.OnlineName} 成功 ({sw.Elapsed.TotalSeconds:F1}s)");

                // Update sync state
                if (syncState != null)
                {
                    lock (syncState)
                    {
                        PullState.UpdateFile(syncState, mapping.OnlineNodeId,
                            mapping.OnlineName, mapping.OnlineModifiedTime);
                    }
                }

                // Update mapping
                mapping.LocalPath = outputPath;
                mapping.LocalName = fileName;
                mapping.Status = MappingStatus.UpToDate;

                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                log?.Invoke($"  -> {mapping.OnlineName} 失败 ({sw.Elapsed.TotalSeconds:F1}s): {ex.Message}");
                return false;
            }
        }

        private async Task DoSinglePullAsync(FileMapping m)
        {
            Repaint();

            try
            {
                if (!Directory.Exists(_localDir))
                    Directory.CreateDirectory(_localDir);

                var syncState = PullState.Load(_localDir);
                var success = await DownloadAndUpdateMappingAsync(m, _localDir, syncState, Log);

                if (success)
                {
                    PullState.Save(_localDir, syncState);
                    _needsAssetRefresh = true;
                    ShowToast($"拉取成功: {m.OnlineName}");
                }
                else
                {
                    ShowToast("拉取失败", true);
                }
            }
            catch (Exception ex)
            {
                var summary = ex.Message.Split('\n')[0].TrimEnd('\r');
                Log("[错误] 拉取失败: " + summary);
                ShowToast($"拉取失败: {summary}", true);
            }
        }

        private void StartSingleUpload(FileMapping m)
        {
            _isSingleOperating = true;
            _singleOpTask = DoSingleUploadAsync(m);
            EditorApplication.update += OnSingleOpUpdate;
        }

        private async Task DoSingleUploadAsync(FileMapping m)
        {
            Repaint();

            try
            {
                if (IsFileLocked(m.LocalPath))
                {
                    ShowToast("文件被占用，请关闭 Excel 后重试", true);
                    Log("[错误] 文件被占用: " + m.LocalPath);
                    return;
                }

                var sw = Stopwatch.StartNew();
                var isNewCreate = string.IsNullOrEmpty(m.OnlineNodeId);
                Log($"[单表上传] {m.LocalName} -> {(isNewCreate ? "(新建)" : m.OnlineName)}");

                // 解析目标空间
                if (string.IsNullOrEmpty(_spaceUrl))
                    throw new Exception("未配置目标空间，请先在设置页填写知识库/文件夹 URL");

                var parsed = DwsCli.ParseSpaceUrl(_spaceUrl);

                // 直接使用 dws doc upload --convert 一步完成上传+转换
                var nodeId = await DwsCli.DocUploadAsync(
                    m.LocalPath, parsed.type, parsed.id, Log);

                // 回写 nodeId
                m.OnlineNodeId = nodeId;
                m.OnlineName = m.LocalName;
                Log($"  -> 远程节点: {nodeId}");

                // 获取远程文档最新修改时间，更新同步状态
                try
                {
                    var docInfo = await DwsCli.GetDocInfoAsync(nodeId, Log);
                    m.OnlineModifiedTime = docInfo.EffectiveModifiedTime;
                }
                catch (Exception infoEx)
                {
                    Log($"  -> 警告: 获取远程修改时间失败 ({infoEx.Message.Split('\n')[0].TrimEnd('\r')})，使用当前时间");
                    m.OnlineModifiedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                }

                // 更新 PullState 同步记录
                var syncState = PullState.Load(_localDir);
                PullState.UpdateFile(syncState, nodeId, m.OnlineName, m.OnlineModifiedTime);
                PullState.Save(_localDir, syncState);

                m.Status = MappingStatus.UpToDate;

                sw.Stop();
                Log($"  -> 成功 ({sw.Elapsed.TotalSeconds:F1}s)");

                ShowToast($"上传成功: {m.LocalName}");
            }
            catch (Exception ex)
            {
                var summary = ex.Message.Split('\n')[0].TrimEnd('\r');
                Log("[错误] 上传失败: " + summary);
                ShowToast($"上传失败: {summary}", true);
            }
        }

        #endregion

        #region Toast 气泡

        /// <summary>检测文件是否被其他进程锁定写入（Excel等会阻止写入但允许读取）</summary>
        private static bool IsFileLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                    return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private void ShowToast(string text, bool isError = false)
        {
            _toasts.Add(new ToastItem { Text = text, BornTime = Time.realtimeSinceStartup, IsError = isError });
            if (_toasts.Count > 5) _toasts.RemoveAt(0);
            Repaint();
        }

        /// <summary>生成完整胶囊纹理（两端半圆+中间纯色），配合 GUIStyle.border 做 9-slice</summary>
        private GUIStyle _sToastBg;

        private void EnsureToastBgStyle()
        {
            if (_sToastBg != null) return;

            // 纹理尺寸: 宽 = radius*2 + 2(中间至少2px), 高 = radius*2
            // radius = 15 → tex 32×30, border left/right = 15
            const int texW = 32, texH = 30;
            float r = texH * 0.5f; // 15

            var tex = new Texture2D(texW, texH, TextureFormat.ARGB32, false);
            var pixels = new Color[texW * texH];
            for (int py = 0; py < texH; py++)
            for (int px = 0; px < texW; px++)
            {
                float dx = 0f, dy = 0f;
                if (px < r) dx = r - px;
                else if (px >= texW - r) dx = px - (texW - r - 1);
                if (py < r) dy = r - py;
                else if (py >= texH - r) dy = py - (texH - r - 1);

                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(r - dist + 0.5f);
                pixels[py * texW + px] = new Color(1f, 1f, 1f, a);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.hideFlags = HideFlags.DontSave;

            _sToastBg = new GUIStyle();
            _sToastBg.normal.background = tex;
            // border: left=15 right=15 top=14 bottom=14 → 只拉伸中间区域
            _sToastBg.border = new RectOffset(15, 15, 14, 14);
            _sToastBg.padding = new RectOffset(20, 20, 0, 0);
            _sToastBg.alignment = TextAnchor.MiddleCenter;
            _sToastBg.fontSize = 12;
            _sToastBg.fontStyle = FontStyle.Normal;
            _sToastBg.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
        }

        private void DrawToasts()
        {
            if (_toasts.Count == 0) return;

            // 清理过期（所有事件类型都需要执行）
            var now = Time.realtimeSinceStartup;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                if (now - _toasts[i].BornTime > ToastDuration + ToastFadeTime)
                    _toasts.RemoveAt(i);
            }

            if (_toasts.Count == 0) return;

            // 仅在 Repaint 事件中绘制，避免 Style.Draw 异常
            if (Event.current.type != EventType.Repaint)
            {
                Repaint();
                return;
            }

            EnsureToastBgStyle();

            const float toastH = 30f;
            const float gap = 8f;
            const float bottomPad = 40f;
            var baseY = position.height - bottomPad;

            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                var age = now - t.BornTime;
                float alpha = 1f;
                if (age > ToastDuration)
                    alpha = 1f - Mathf.Clamp01((age - ToastDuration) / ToastFadeTime);
                if (alpha <= 0f) continue;

                var content = new GUIContent(t.Text);
                var w = _sToastBg.CalcSize(content).x;
                w = Mathf.Max(w, toastH * 2.5f);
                var x = (position.width - w) * 0.5f;
                var y = baseY - toastH;

                var rect = new Rect(x, y, w, toastH);

                // 背景：用 GUI.color tint 白色纹理做半透明底色
                var bgColor = t.IsError
                    ? new Color(0.6f, 0.25f, 0.25f, 0.95f * alpha)
                    : new Color(0.25f, 0.25f, 0.28f, 0.95f * alpha);

                var prevColor = GUI.color;
                GUI.color = bgColor;
                _sToastBg.Draw(rect, false, false, false, false);
                GUI.color = prevColor; // 立即恢复，不影响文字

                // 文字：手动垂直居中（GUI.Label MiddleCenter 在 Editor 不可靠）
                if (_sToast == null)
                {
                    _sToast = new GUIStyle(EditorStyles.label);
                    _sToast.fontSize = 12;
                    _sToast.fontStyle = FontStyle.Normal;
                    _sToast.alignment = TextAnchor.UpperLeft;
                    _sToast.padding = new RectOffset(0, 0, 0, 0);
                    _sToast.margin = new RectOffset(0, 0, 0, 0);
                }

                var tcPrev = _sToast.normal.textColor;
                _sToast.normal.textColor = t.IsError
                    ? new Color(1f, 0.8f, 0.8f, alpha)
                    : new Color(0.95f, 0.95f, 0.95f, alpha);
                var textSize = _sToast.CalcSize(content);
                var textY = rect.y + (rect.height - textSize.y) * 0.5f;
                var textRect = new Rect(rect.x, textY, rect.width, textSize.y);
                _sToast.alignment = TextAnchor.UpperCenter;
                GUI.Label(textRect, t.Text, _sToast);
                _sToast.normal.textColor = tcPrev;

                baseY -= toastH + gap;
            }

            Repaint();
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