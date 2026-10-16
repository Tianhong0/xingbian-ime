using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VerifyCom
{
    /// <summary>
    /// 独立校验「注册契约」与「真实程序集」是否严丝合缝。
    ///
    /// 为什么需要它：安装脚本写进注册表的值（CLSID / Assembly 全名 / Class / RuntimeVersion /
    /// CodeBase）如果与 DLL 里的实际内容差一个字符，CLR 就会在宿主进程里加载失败。那种失败的表象是
    /// 「输入法在 Win+空格 里能看到，一选就静默失败或宿主崩溃」，极难排查。本程序在沙箱内
    /// （只读、不写注册表）就能把这类不匹配全部抓出来。
    ///
    /// 退出码 0 = 全部通过；1 = 有失败项。
    /// </summary>
    internal static class Program
    {
        private static int s_failures;
        private static int s_checks;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("[FATAL] " + ex.GetType().FullName);
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
                Exception inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    Console.WriteLine("  --> 内层: " + inner.GetType().FullName + ": " + inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }
                if (ex is ReflectionTypeLoadException)
                {
                    Type[] types = ((ReflectionTypeLoadException)ex).Types;
                    Exception[] loaders = ((ReflectionTypeLoadException)ex).LoaderExceptions;
                    Console.WriteLine("  LoaderExceptions (" + (loaders == null ? 0 : loaders.Length) + "):");
                    if (loaders != null)
                    {
                        for (int i = 0; i < loaders.Length; i++)
                        {
                            if (loaders[i] != null)
                            {
                                Console.WriteLine("    [" + i + "] " + loaders[i].GetType().FullName + ": " + loaders[i].Message);
                            }
                        }
                    }
                    if (types != null)
                    {
                        for (int i = 0; i < types.Length; i++)
                        {
                            Console.WriteLine("    type[" + i + "] = " + (types[i] == null ? "(null)" : types[i].FullName));
                        }
                    }
                }
                Console.WriteLine("RESULT: FATAL");
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            string dllPath;
            if (args != null && args.Length > 0)
            {
                dllPath = Path.GetFullPath(args[0]);
            }
            else
            {
                dllPath = Path.GetFullPath(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        @"..\..\..\..\src\Xingbian.Tsf\bin\Release\Xingbian.Tsf.dll"));
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== 注册契约校验 ===");
            Console.WriteLine("目标程序集: " + dllPath);
            Console.WriteLine();

            if (!File.Exists(dllPath))
            {
                Console.WriteLine("[FAIL] 程序集不存在。先跑 dotnet build src\\Xingbian.Tsf\\Xingbian.Tsf.csproj -c Release");
                return 1;
            }

            // ---- 1. 程序集标识（InprocServer32 的 Assembly 值必须与它逐字一致） ----
            // 用字节加载而非 LoadFrom：本机 D: 盘会被 CLR 判定为「网络位置」而触发
            // loadFromRemoteSources 沙箱检查（FileLoadException 0x80131515），
            // 从字节加载可以完全绕开该检查，且不影响任何反射结论。
            Assembly asm = Assembly.Load(File.ReadAllBytes(dllPath));
            AssemblyName an = asm.GetName();

            Console.WriteLine("--- 1. 程序集标识 ---");
            Console.WriteLine("  SimpleName     : " + an.Name);
            Console.WriteLine("  Version        : " + an.Version);
            Console.WriteLine("  Culture        : " + (string.IsNullOrEmpty(an.CultureName) ? "(null)" : an.CultureName));
            Console.WriteLine("  PublicKeyToken : " + DescribeToken(an.GetPublicKeyToken()));
            Console.WriteLine("  FullName       : " + an.FullName);
            Console.WriteLine("  ImageRuntime   : " + asm.ImageRuntimeVersion);
            // 从字节加载时 Assembly.Location 为空（程序集没有文件位置），
            // 所以后面一律用 dllPath 而不是 asm.Location 来引用这个文件的路径。
            Console.WriteLine("  文件路径        : " + dllPath);
            Console.WriteLine("  文件大小        : " + new FileInfo(dllPath).Length + " B");
            Console.WriteLine();

            Check("程序集简单名为 Xingbian.Tsf", an.Name == "Xingbian.Tsf");
            Check("程序集版本为 0.1.0.0", an.Version != null && an.Version.ToString() == "0.1.0.0");
            Check("程序集无公钥（PublicKeyToken=null）", an.GetPublicKeyToken() == null || an.GetPublicKeyToken().Length == 0);
            Check("程序集为中性文化", string.IsNullOrEmpty(an.CultureName));
            // mscoree 的 Assembly 值里 RuntimeVersion 必须与映像的 CLR 版本匹配，否则加载失败。
            Check("ImageRuntimeVersion 为 v4.0.30319", asm.ImageRuntimeVersion == "v4.0.30319");

            // ---- 2. 依赖项（少拷一个 DLL 就加载失败） ----
            Console.WriteLine("--- 2. 引用的程序集 ---");
            AssemblyName[] refs = asm.GetReferencedAssemblies();
            List<string> refNames = new List<string>();
            for (int i = 0; i < refs.Length; i++)
            {
                refNames.Add(refs[i].Name);
                Console.WriteLine("  " + refs[i].FullName);
            }
            Console.WriteLine();

            bool hasCore = false;
            for (int i = 0; i < refNames.Count; i++)
            {
                if (refNames[i] == "Xingbian.Core") { hasCore = true; }
            }
            Check("引用了 Xingbian.Core（安装时必须同目录拷贝）", hasCore);

            bool leakedDrawing = false;
            for (int i = 0; i < refNames.Count; i++)
            {
                if (refNames[i] == "System.Drawing" || refNames[i] == "System.Windows.Forms" ||
                    refNames[i] == "PresentationCore" || refNames[i] == "WindowsBase")
                {
                    leakedDrawing = true;
                }
            }
            Check("未引用 WinForms/System.Drawing/WPF（候选窗是纯 Win32）", !leakedDrawing);

            // ---- 3. COM 可见性与 GUID ----
            Console.WriteLine("--- 3. COM 注册信息 ---");
            Type svcType = asm.GetType("Xingbian.Tsf.TextService", false);
            if (svcType == null)
            {
                Console.WriteLine("[FAIL] 找不到类型 Xingbian.Tsf.TextService —— 注册表的 Class 值会指向一个不存在的类，CLR 加载必失败。");
                s_failures++;
                Console.WriteLine();
                return Finish();
            }

            Check("类型 Xingbian.Tsf.TextService 存在", true);
            Check("类型是 public", svcType.IsPublic);
            Check("类型是 sealed", svcType.IsSealed);
            Check("类型不是抽象的（COM 可实例化）", !svcType.IsAbstract);

            ComVisibleAttribute cva = (ComVisibleAttribute)Attribute.GetCustomAttribute(svcType, typeof(ComVisibleAttribute));
            Check("带 [ComVisible(true)]", cva != null && cva.Value);

            GuidAttribute ga = (GuidAttribute)Attribute.GetCustomAttribute(svcType, typeof(GuidAttribute));
            Check("带 [Guid] 特性", ga != null);

            const string ExpectedClsid = "7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43";
            string actualClsid = ga == null ? "(无)" : ga.Value;
            Console.WriteLine("  类型 GUID      : " + actualClsid);
            Console.WriteLine("  期望 CLSID     : " + ExpectedClsid);
            Check("类型的 GUID 与安装脚本要写的 CLSID 一致", string.Equals(actualClsid, ExpectedClsid, StringComparison.OrdinalIgnoreCase));

            ClassInterfaceAttribute cia = (ClassInterfaceAttribute)Attribute.GetCustomAttribute(svcType, typeof(ClassInterfaceAttribute));
            Check("带 [ClassInterface(ClassInterfaceType.None)]",
                cia != null && cia.Value == ClassInterfaceType.None);

            // 供安装脚本照抄的全部注册表值，直接打印出来便于逐字比对。
            Console.WriteLine();
            Console.WriteLine("  → InprocServer32\\Assembly      = " + an.FullName);
            Console.WriteLine("  → InprocServer32\\Class         = Xingbian.Tsf.TextService");
            Console.WriteLine("  → InprocServer32\\RuntimeVersion= " + asm.ImageRuntimeVersion);
            Console.WriteLine("  → InprocServer32\\(默认)         = C:\\Windows\\System32\\mscoree.dll");
            Console.WriteLine();

            // ---- 4. 实现的 TSF 回调接口 ----
            Console.WriteLine("--- 4. 实现的 TSF 接口 ---");
            string[] requiredIfaces = new string[]
            {
                "Xingbian.Tsf.Interop.ITfTextInputProcessorEx",
                "Xingbian.Tsf.Interop.ITfTextInputProcessor",
                "Xingbian.Tsf.Interop.ITfKeyEventSink",
                "Xingbian.Tsf.Interop.ITfThreadMgrEventSink",
                "Xingbian.Tsf.Interop.ITfEditSession"
            };

            Type[] ifaces = svcType.GetInterfaces();
            List<string> ifaceNames = new List<string>();
            for (int i = 0; i < ifaces.Length; i++)
            {
                ifaceNames.Add(ifaces[i].FullName);
                Console.WriteLine("  " + ifaces[i].FullName + "   [" + ifaces[i].GUID.ToString("D") + "]");
            }
            Console.WriteLine();

            for (int i = 0; i < requiredIfaces.Length; i++)
            {
                Check("实现 " + requiredIfaces[i], ifaceNames.Contains(requiredIfaces[i]));
            }

            // ---- 5. CCW：原生侧能否按 IID QI 到这些回调接口 ----
            // 这是整个架构的地基：msctf.dll 拿到我们传进去的托管对象后，必须能 QI 到 ITfKeyEventSink 等 IID。
            Console.WriteLine("--- 5. CCW 接口暴露（原生侧可 QI） ---");
            object instance = null;
            try
            {
                instance = Activator.CreateInstance(svcType);
                Check("可无参实例化 TextService", instance != null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 实例化失败: " + ex.GetType().Name + ": " + ex.Message);
                s_failures++;
            }

            if (instance != null)
            {
                for (int i = 0; i < requiredIfaces.Length; i++)
                {
                    Type itf = asm.GetType(requiredIfaces[i], false);
                    if (itf == null)
                    {
                        Console.WriteLine("[FAIL] 接口类型缺失: " + requiredIfaces[i]);
                        s_failures++;
                        s_checks++;
                        continue;
                    }

                    // 逐个接口做真实的原生 QI，不是在托管侧做 is 判断。
                    try
                    {
                        IntPtr ccw = Marshal.GetComInterfaceForObject(instance, itf);
                        try
                        {
                            Guid iid = itf.GUID;
                            IntPtr got;
                            int hr = Marshal.QueryInterface(ccw, ref iid, out got);
                            if (hr == 0 && got != IntPtr.Zero)
                            {
                                Marshal.Release(got);
                                s_checks++;
                                Console.WriteLine("  [ ok ] QI " + itf.Name + " -> hr=0x00000000 iid=" + itf.GUID.ToString("D"));
                            }
                            else
                            {
                                s_checks++;
                                s_failures++;
                                Console.WriteLine("  [FAIL] QI " + itf.Name + " -> hr=0x" + hr.ToString("X8"));
                            }
                        }
                        finally
                        {
                            Marshal.Release(ccw);
                        }
                    }
                    catch (Exception ex)
                    {
                        s_checks++;
                        s_failures++;
                        Console.WriteLine("  [FAIL] " + itf.Name + " 取 CCW 抛异常: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            // ---- 6. mscoree.dll 与宿主侧前提 ----
            Console.WriteLine();
            Console.WriteLine("--- 6. 运行时前提 ---");
            string mscoree = Path.Combine(Environment.SystemDirectory, "mscoree.dll");
            Check("存在 " + mscoree, File.Exists(mscoree));
            string msctf = Path.Combine(Environment.SystemDirectory, "msctf.dll");
            Check("存在 " + msctf, File.Exists(msctf));
            string inputDll = Path.Combine(Environment.SystemDirectory, "input.dll");
            Check("存在 " + inputDll + "（InstallLayoutOrTip 用）", File.Exists(inputDll));

            // 核心 DLL 是否就在旁边（安装脚本要把它一起拷过去）
            string coreBeside = Path.Combine(Path.GetDirectoryName(dllPath), "Xingbian.Core.dll");
            Check("构建输出目录里 Xingbian.Core.dll 与主 DLL 同目录", File.Exists(coreBeside));

            Console.WriteLine();
            return Finish();
        }

        private static string DescribeToken(byte[] token)
        {
            if (token == null || token.Length == 0) { return "null"; }
            StringBuilder sb = new StringBuilder(token.Length * 2);
            for (int i = 0; i < token.Length; i++) { sb.Append(token[i].ToString("x2")); }
            return sb.ToString();
        }

        private static void Check(string what, bool ok)
        {
            s_checks++;
            if (ok)
            {
                Console.WriteLine("  [ ok ] " + what);
            }
            else
            {
                s_failures++;
                Console.WriteLine("  [FAIL] " + what);
            }
        }

        private static int Finish()
        {
            Console.WriteLine("=== " + (s_checks - s_failures) + "/" + s_checks + " 项通过 ===");
            if (s_failures == 0)
            {
                Console.WriteLine("RESULT: ALL PASS");
                return 0;
            }
            Console.WriteLine("RESULT: " + s_failures + " FAILED");
            return 1;
        }
    }
}
