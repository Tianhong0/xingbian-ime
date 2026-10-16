using System;
using System.IO;
using System.Windows.Forms;

namespace Xingbian.Setup
{
    /// <summary>
    /// /uninstall 静默卸载路径（控制面板「程序和功能」调用）：
    /// 从自身解出 unregister.ps1，按注册时记录的 InstallDir 卸载，无 UI（除错误弹窗）。
    /// </summary>
    internal static class Uninstaller
    {
        internal static int Run(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir))
            {
                dir = Program.DefaultInstallDir();
            }
            string temp = Path.Combine(Path.GetTempPath(), "XingbianSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string payload = Program.ExtractPayload(temp);
            if (payload == null) { return 4; }
            try
            {
                string errTail;
                int rc = Program.RunPowerShell(Path.Combine(payload, "unregister.ps1"),
                    " -Quiet -RemoveFiles -InstallDir \"" + dir + "\"", null, out errTail);
                if (rc == 0)
                {
                    try
                    {
                        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\XingbianIME", false);
                    }
                    catch (Exception) { }
                    return 0;
                }
                MessageBox.Show("卸载失败（" + Program.DescribeExitCode(rc) + "）：\r\n" + errTail,
                    "形变输入法卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return rc;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (Exception) { }
            }
        }
    }
}
