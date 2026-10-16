using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace Xingbian.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // /uninstall [InstallDir] —— 静默卸载（也弹 UAC，因为 manifest 要求）
            if (args.Length >= 1 && string.Equals(args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                string dir = args.Length >= 2 ? args[1] : DefaultInstallDir();
                return Uninstaller.Run(dir);
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallForm());
            return 0;
        }

        internal static string DefaultInstallDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XingbianIME");
        }

        /// <summary>把嵌入的载荷写到临时目录，返回该目录；失败返回 null。</summary>
        internal static string ExtractPayload(string tempDir)
        {
            try
            {
                Directory.CreateDirectory(tempDir);
                WriteResource(tempDir, "Xingbian.Setup.Tsf.dll", "Xingbian.Tsf.dll");
                WriteResource(tempDir, "Xingbian.Setup.Core.dll", "Xingbian.Core.dll");
                WriteResource(tempDir, "Xingbian.Setup.Dict.bin", "xingbian.bin");
                WriteResource(tempDir, "Xingbian.Setup.register.ps1", "register.ps1");
                WriteResource(tempDir, "Xingbian.Setup.unregister.ps1", "unregister.ps1");
                return tempDir;
            }
            catch (Exception ex)
            {
                MessageBox.Show("解压安装载荷失败：\r\n" + ex.Message, "形变输入法安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        private static void WriteResource(string dir, string resourceName, string fileName)
        {
            Assembly asm = typeof(Program).Assembly;
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null)
                {
                    throw new IOException("嵌入资源缺失: " + resourceName);
                }
                string path = Path.Combine(dir, fileName);
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                    }
                }
            }
        }

        /// <summary>调用 powershell.exe 跑指定脚本，返回退出码。输出通过 Action&lt;string&gt; 回传。</summary>
        internal static int RunPowerShell(string scriptPath, string arguments, Action<string> onOutput, out string errorTail)
        {
            StringBuilder tail = new StringBuilder();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psi.FileName))
            {
                psi.FileName = "powershell.exe";
            }
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        if (onOutput != null) { onOutput(e.Data); }
                    }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        tail.AppendLine(e.Data);
                        if (onOutput != null) { onOutput("[stderr] " + e.Data); }
                    }
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                errorTail = tail.ToString();
                return p.ExitCode;
            }
        }

        internal static string DescribeExitCode(int code)
        {
            switch (code)
            {
                case 0: return "成功";
                case 2: return "权限不足（需要管理员）";
                case 3: return "源文件缺失";
                case 4: return "注册表写入/回读校验失败";
                default: return "退出码 " + code;
            }
        }
    }
}
