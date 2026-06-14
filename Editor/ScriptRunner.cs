using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DingConfig
{
    /// <summary>
    /// 在新终端窗口中执行脚本，跨平台 (Windows / macOS)
    /// </summary>
    public static class ScriptRunner
    {
        /// <summary>
        /// 在新终端窗口中执行指定脚本文件
        /// </summary>
        /// <returns>是否成功启动</returns>
        public static bool Run(string scriptPath, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                error = "脚本文件不存在: " + scriptPath;
                return false;
            }

            var dir = Path.GetDirectoryName(scriptPath);
            var ext = Path.GetExtension(scriptPath).ToLowerInvariant();

            try
            {
                var psi = BuildProcessInfo(scriptPath, dir, ext);
                Process.Start(psi);
                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static ProcessStartInfo BuildProcessInfo(string scriptPath, string dir, string ext)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return BuildWindows(scriptPath, dir, ext);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return BuildMacOS(scriptPath, dir, ext);

            // Linux fallback
            return BuildLinux(scriptPath, dir, ext);
        }

        #region Windows

        private static ProcessStartInfo BuildWindows(string scriptPath, string dir, string ext)
        {
            switch (ext)
            {
                case ".bat":
                case ".cmd":
                    return new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/k \"cd /d \"{dir}\" && \"{scriptPath}\"\"",
                        UseShellExecute = true,
                    };

                case ".ps1":
                    return new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoExit -Command \"Set-Location '{dir}'; & '{scriptPath}'\"",
                        UseShellExecute = true,
                    };

                default:
                    return new ProcessStartInfo
                    {
                        FileName = scriptPath,
                        WorkingDirectory = dir,
                        UseShellExecute = true,
                    };
            }
        }

        #endregion

        #region macOS

        private static ProcessStartInfo BuildMacOS(string scriptPath, string dir, string ext)
        {
            // macOS 通过 osascript 在 Terminal.app 中打开新窗口执行
            var appleScript =
                $"tell application \"Terminal\"\n" +
                $"  do script \"cd '{dir}' && '{scriptPath}'\"\n" +
                $"  activate\n" +
                $"end tell";

            return new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{appleScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
            };
        }

        #endregion

        #region Linux

        private static ProcessStartInfo BuildLinux(string scriptPath, string dir, string ext)
        {
            // 尝试常见的终端模拟器
            string[] terminals = { "gnome-terminal", "xterm", "konsole", "xfce4-terminal" };

            foreach (var term in terminals)
            {
                var fullPath = FindExecutable(term);
                if (fullPath != null)
                {
                    if (term == "gnome-terminal")
                    {
                        return new ProcessStartInfo
                        {
                            FileName = fullPath,
                            Arguments = $"-- bash -c \"cd '{dir}' && '{scriptPath}'; exec bash\"",
                            UseShellExecute = false,
                        };
                    }

                    return new ProcessStartInfo
                    {
                        FileName = fullPath,
                        Arguments = $"-e \"bash -c \\\"cd '{dir}' && '{scriptPath}'; exec bash\\\"\"",
                        UseShellExecute = false,
                    };
                }
            }

            // fallback: 直接在后台执行
            return new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"cd '{dir}' && '{scriptPath}'\"",
                UseShellExecute = false,
            };
        }

        private static string FindExecutable(string name)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                var path = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return p.ExitCode == 0 && !string.IsNullOrEmpty(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}