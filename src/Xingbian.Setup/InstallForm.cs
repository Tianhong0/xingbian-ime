using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Xingbian.Setup
{
    internal sealed class InstallForm : Form
    {
        private readonly TextBox _dirBox;
        private readonly Button _browse;
        private readonly Button _btnInstall;
        private readonly Button _btnUninstall;
        private readonly TextBox _log;
        private readonly Label _status;

        public InstallForm()
        {
            Text = "形变输入法 安装程序";
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 400);

            Label l1 = new Label();
            l1.Text = "安装位置：";
            l1.Location = new Point(14, 16);
            l1.AutoSize = true;
            Controls.Add(l1);

            _dirBox = new TextBox();
            _dirBox.Text = Program.DefaultInstallDir();
            _dirBox.Location = new Point(90, 13);
            _dirBox.Width = 440;
            Controls.Add(_dirBox);

            _browse = new Button();
            _browse.Text = "浏览…";
            _browse.Location = new Point(536, 11);
            _browse.Click += new EventHandler(OnBrowse);
            Controls.Add(_browse);

            _btnInstall = new Button();
            _btnInstall.Text = "安装";
            _btnInstall.Location = new Point(90, 48);
            _btnInstall.Size = new Size(120, 32);
            _btnInstall.Click += new EventHandler(OnInstall);
            Controls.Add(_btnInstall);

            _btnUninstall = new Button();
            _btnUninstall.Text = "卸载";
            _btnUninstall.Location = new Point(226, 48);
            _btnUninstall.Size = new Size(120, 32);
            _btnUninstall.Click += new EventHandler(OnUninstall);
            Controls.Add(_btnUninstall);

            _status = new Label();
            _status.Text = "就绪。安装会注册到系统输入法框架（需要管理员权限，本程序已请求）。";
            _status.Location = new Point(14, 92);
            _status.AutoSize = false;
            _status.Size = new Size(592, 24);
            Controls.Add(_status);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Location = new Point(14, 120);
            _log.Size = new Size(592, 264);
            _log.Font = new Font("Consolas", 9F);
            Controls.Add(_log);
        }

        private void Log(string line)
        {
            if (_log.InvokeRequired)
            {
                _log.BeginInvoke((Action)delegate { _log.AppendText(line + Environment.NewLine); });
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择形变输入法的安装目录";
                dlg.ShowNewFolderButton = true;
                if (Directory.Exists(_dirBox.Text)) { dlg.SelectedPath = _dirBox.Text; }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _dirBox.Text = dlg.SelectedPath;
                }
            }
        }

        private void OnInstall(object sender, EventArgs e)
        {
            string dir = NormalizeDir(_dirBox.Text);
            if (dir == null) { return; }
            SetBusy(true);
            _status.Text = "正在安装…";
            Log("=== 安装开始，目标目录 " + dir + " ===");

            Action worker = delegate
            {
                string temp = Path.Combine(Path.GetTempPath(), "XingbianSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string payload = Program.ExtractPayload(temp);
                if (payload == null)
                {
                    Done(false, "解压载荷失败。");
                    return;
                }
                try
                {
                    // 复制文件到目标目录（注册脚本 -SkipCopy 不复制，安装器自己做，支持自定义位置）
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.Copy(Path.Combine(payload, "Xingbian.Tsf.dll"), Path.Combine(dir, "Xingbian.Tsf.dll"), true);
                        File.Copy(Path.Combine(payload, "Xingbian.Core.dll"), Path.Combine(dir, "Xingbian.Core.dll"), true);
                        Directory.CreateDirectory(Path.Combine(dir, "dict"));
                        File.Copy(Path.Combine(payload, "xingbian.bin"), Path.Combine(dir, "dict", "xingbian.bin"), true);
                        // 卸载器副本：控制面板卸载用
                        File.Copy(AssemblyPath(), Path.Combine(dir, "Xingbian.Setup.exe"), true);
                    }
                    catch (Exception ex)
                    {
                        Done(false, "复制文件失败：" + ex.Message);
                        return;
                    }

                    string args = " -SkipCopy -Quiet -InstallDir \"" + dir + "\"";
                    string errTail;
                    int rc = Program.RunPowerShell(Path.Combine(payload, "register.ps1"), args, Log, out errTail);
                    if (rc == 0)
                    {
                        WriteUninstallEntry(dir);
                        Done(true, "安装成功！请按以下步骤启用：\r\n  1. 注销并重新登录（或重启 ctfmon.exe）\r\n  2. Win + 空格 切换到「形变输入法」\r\n  3. 敲 nima 后按空格上屏 → 亻尔 女马");
                    }
                    else
                    {
                        Done(false, "注册失败（" + Program.DescribeExitCode(rc) + "）。详见日志" + (string.IsNullOrEmpty(errTail) ? "。" : "：\r\n" + errTail));
                    }
                }
                finally
                {
                    TryDeleteDir(temp);
                }
            };
            worker.BeginInvoke(delegate(IAsyncResult ar) { worker.EndInvoke(ar); }, null);
        }

        private void OnUninstall(object sender, EventArgs e)
        {
            string dir = NormalizeDir(_dirBox.Text);
            if (dir == null) { return; }
            if (MessageBox.Show("确定从系统中卸载形变输入法吗？\r\n（输入法将被从语言列表移除并删除注册信息；\r\n安装目录文件同时删除。）", "确认卸载",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            SetBusy(true);
            _status.Text = "正在卸载…";
            Log("=== 卸载开始，目标目录 " + dir + " ===");
            Action worker = delegate
            {
                string temp = Path.Combine(Path.GetTempPath(), "XingbianSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string payload = Program.ExtractPayload(temp);
                if (payload == null)
                {
                    Done(false, "解压载荷失败。");
                    return;
                }
                try
                {
                    string args = " -Quiet -RemoveFiles -InstallDir \"" + dir + "\"";
                    string errTail;
                    int rc = Program.RunPowerShell(Path.Combine(payload, "unregister.ps1"), args, Log, out errTail);
                    if (rc == 0)
                    {
                        RemoveUninstallEntry();
                        Done(true, "卸载完成。");
                    }
                    else
                    {
                        Done(false, "卸载脚本返回 " + Program.DescribeExitCode(rc) + (string.IsNullOrEmpty(errTail) ? "。" : "：\r\n" + errTail));
                    }
                }
                finally
                {
                    TryDeleteDir(temp);
                }
            };
            worker.BeginInvoke(delegate(IAsyncResult ar) { worker.EndInvoke(ar); }, null);
        }

        // ------------------------------------------------------------------

        /// <summary>把注册信息写进「控制面板-程序和功能」（卸载入口），并声明无自带卸载串。</summary>
        private static void WriteUninstallEntry(string dir)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\XingbianIME"))
                {
                    k.SetValue("DisplayName", "形变输入法 (Xingbian IME)");
                    k.SetValue("DisplayVersion", "0.1.0");
                    k.SetValue("Publisher", "XingbianIME");
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("DisplayIcon", Path.Combine(dir, "Xingbian.Setup.exe"));
                    k.SetValue("UninstallString", "\"" + Path.Combine(dir, "Xingbian.Setup.exe") + "\" /uninstall \"" + dir + "\"");
                    k.SetValue("QuietUninstallString", "\"" + Path.Combine(dir, "Xingbian.Setup.exe") + "\" /uninstall \"" + dir + "\"");
                    k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception)
            {
                // 卸载入口写失败不算安装失败 —— 用户仍可用本安装器的「卸载」按钮
            }
        }

        private static void RemoveUninstallEntry()
        {
            try
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\XingbianIME", false);
            }
            catch (Exception) { }
        }

        private static string AssemblyPath()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, true); } }
            catch (Exception) { }
        }

        private string NormalizeDir(string raw)
        {
            string dir = (raw ?? "").Trim().TrimEnd('\\');
            if (dir.Length == 0)
            {
                MessageBox.Show("请填写安装目录。", "形变输入法安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            if (!Path.IsPathRooted(dir) || dir.Length < 3)
            {
                MessageBox.Show("安装目录必须是本机绝对路径（如 C:\\Program Files\\XingbianIME 或 D:\\MyIME）。", "形变输入法安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return dir;
        }

        private void SetBusy(bool busy)
        {
            _btnInstall.Enabled = !busy;
            _btnUninstall.Enabled = !busy;
            _browse.Enabled = !busy;
            _dirBox.Enabled = !busy;
        }

        private void Done(bool ok, string message)
        {
            Log(ok ? "=== 完成 ===" : "=== 失败 ===");
            Log(message);
            Action ui = delegate
            {
                SetBusy(false);
                _status.Text = ok ? "完成。" : "失败：" + message;
                if (ok)
                {
                    MessageBox.Show(message, "形变输入法安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(message, "形变输入法安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            if (InvokeRequired) { BeginInvoke(ui); } else { ui(); }
        }
    }
}
