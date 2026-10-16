using System;
using System.IO;
using System.Reflection;

// 用反射驱动 Xingbian.Setup.exe 的 internal 方法（Program.ExtractPayload / Program.RunPowerShell），
// 验证安装器的两条核心管道：载荷解压完整 + 进程调用/退出码/输出捕获正确。
public static class P
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool ok, string name, string detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("[ ok ] " + name + (detail == null ? "" : "  -- " + detail)); }
        else { _fail++; Console.WriteLine("[FAIL] " + name + (detail == null ? "" : "  -- " + detail)); }
    }

    public static int Main()
    {
        string exe = @"D:\Documents\deepseek-harness\default-workspace\xingbian-ime\src\Xingbian.Setup\bin\Release\Xingbian.Setup.exe";
        Assembly asm = Assembly.Load(File.ReadAllBytes(exe));
        Type prog = asm.GetType("Xingbian.Setup.Program");
        Type form = asm.GetType("Xingbian.Setup.InstallForm");
        Check(prog != null, "类型 Xingbian.Setup.Program 可加载");
        Check(form != null, "类型 Xingbian.Setup.InstallForm 可加载");

        // --- 1. ExtractPayload：真实解压 ---
        string temp = Path.Combine(Path.GetTempPath(), "setupprobe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        object ret = prog.GetMethod("ExtractPayload", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                           .Invoke(null, new object[] { temp });
        string payload = (string)ret;
        Check(payload == temp, "ExtractPayload 返回目录", payload);

        string[] expectFiles = { "Xingbian.Tsf.dll", "Xingbian.Core.dll", "xingbian.bin", "register.ps1", "unregister.ps1" };
        long[] expectMin = { 40000, 20000, 3000000, 20000, 10000 };
        for (int i = 0; i < expectFiles.Length; i++)
        {
            string path = Path.Combine(temp, expectFiles[i]);
            bool ok = File.Exists(path) && new FileInfo(path).Length >= expectMin[i];
            Check(ok, "载荷文件 " + expectFiles[i], ok ? new FileInfo(path).Length + " B" : "缺失或过小");
        }

        // 解出的 ps1 必须带 BOM（PS 5.1 无 BOM 会按 GBK 解码中文报假语法错）
        byte[] head = new byte[3];
        using (FileStream fs = File.OpenRead(Path.Combine(temp, "register.ps1"))) { fs.Read(head, 0, 3); }
        Check(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF, "register.ps1 BOM 完整", BitConverter.ToString(head));
        using (FileStream fs = File.OpenRead(Path.Combine(temp, "unregister.ps1"))) { fs.Read(head, 0, 3); }
        Check(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF, "unregister.ps1 BOM 完整", BitConverter.ToString(head));

        // --- 2. RunPowerShell：退出码 + 输出捕获 ---
        string stub = Path.Combine(temp, "stub.ps1");
        File.WriteAllText(stub, "param([string]$InstallDir)\r\nWrite-Output ('DIR=' + $InstallDir)\r\nWrite-Error 'boom'\r\nexit 7\r\n");
        string errTail = null;
        System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
        object[] invokeArgs = new object[] { stub, " -InstallDir \"D:\\My IME\"",
            new Action<string>(delegate(string s) { lines.Add(s); }), errTail };
        int rc = (int)prog.GetMethod("RunPowerShell", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                         .Invoke(null, invokeArgs);
        errTail = (string)invokeArgs[3];
        Check(rc == 7, "RunPowerShell 退出码透传", "rc=" + rc);
        Check(lines.Exists(delegate(string s) { return s == "DIR=D:\\My IME"; }), "stdout 回传且带空格参数完整", string.Join(" | ", lines.ToArray()));
        Check(errTail != null && errTail.Contains("boom"), "stderr 进 errTail", errTail.Trim());

        // --- 3. DescribeExitCode ---
        string d4 = (string)prog.GetMethod("DescribeExitCode", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                                .Invoke(null, new object[] { 4 });
        Check(d4 != null && d4.Contains("回读"), "DescribeExitCode(4)", d4);

        // 清理
        try { Directory.Delete(temp, true); } catch (Exception) { }

        Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
        return _fail == 0 ? 0 : 1;
    }
}
