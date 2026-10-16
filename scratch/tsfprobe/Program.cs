// TsfProbe —— 问真实的 msctf.dll：它认不认我们注册的输入法？
//
// 这是「注册成功」与「TSF 真正采纳」之间的那道验证。注册表写对了不等于
// TSF 会把我们列进语言列表、也不等于它会去 CoCreateInstance 我们的 CLSID。
// 这里通过官方 COM 接口 ITfInputProcessorProfileMgr / ITfInputProcessorProfiles
// 直接向 msctf 提问。

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TsfProbe
{
    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITfInputProcessorProfileMgr
    {
        [PreserveSig] int ActivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags);
        [PreserveSig] int DeactivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags);
        [PreserveSig] int GetProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, IntPtr pProfile, uint dwFlags);
        [PreserveSig] int EnumProfiles(ushort langid, out IntPtr ppEnum);
        [PreserveSig] int ReleaseInputProcessor(ref Guid clsid, uint dwFlags);
        [PreserveSig] int RegisterProfile(ref Guid clsid, ushort langid, ref Guid guidProfile, IntPtr pchDesc, uint cchDesc, IntPtr pchIconFile, uint cchFile, uint uIconIndex, IntPtr hklsubstitute, uint dwPreferredLayout, uint dwFlags);
        [PreserveSig] int UnregisterProfile(ref Guid clsid, ushort langid, ref Guid guidProfile, uint dwFlags);
        [PreserveSig] int GetActiveProfile(ref Guid catid, IntPtr pProfile);
    }

    [ComImport, Guid("1f02b6c5-7842-4ee6-8a0b-9a24183a95ca"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITfInputProcessorProfiles
    {
        [PreserveSig] int Register(ref Guid rclsid);
        [PreserveSig] int Unregister(ref Guid rclsid);
        [PreserveSig] int AddLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile,
                                             [MarshalAs(UnmanagedType.LPWStr)] string pchDesc, uint cchDesc,
                                             [MarshalAs(UnmanagedType.LPWStr)] string pchIconFile, uint cchFile, uint uIconIndex);
        [PreserveSig] int RemoveLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
        [PreserveSig] int EnumInputProcessorInfo(out IntPtr ppEnum);
        [PreserveSig] int GetDefaultLanguageProfile(ushort langid, ref Guid catid, out Guid pclsid, out Guid pguidProfile);
        [PreserveSig] int SetDefaultLanguageProfile(ushort langid, ref Guid rclsid, ref Guid guidProfile);
        [PreserveSig] int ActivateLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
        [PreserveSig] int GetActiveLanguageProfile(ref Guid rclsid, out ushort plangid, out Guid pguidProfile);
        [PreserveSig] int GetLanguageProfileDescription(ref Guid rclsid, ushort langid, ref Guid guidProfile, out IntPtr pbstrDesc);
        [PreserveSig] int GetCurrentLanguage(out ushort plangid);
        [PreserveSig] int ChangeCurrentLanguage(ushort langid);
        [PreserveSig] int GetLanguageList(out IntPtr ppLangId, out uint pulCount);
        [PreserveSig] int EnumLanguageProfiles(ushort langid, out IntPtr ppEnum);
        [PreserveSig] int EnableLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, [MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int IsEnabledLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, [MarshalAs(UnmanagedType.Bool)] out bool pfEnable);
        [PreserveSig] int EnableLanguageProfileByDefault(ref Guid rclsid, ushort langid, ref Guid guidProfile, [MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int SubstituteKeyboardLayout(ref Guid rclsid, ushort langid, ref Guid guidProfile, IntPtr hkl);
    }

    [ComImport, Guid("33c53a50-f456-4884-b049-85fd643ecfed")]
    internal class InputProcessorProfilesClass { }

    [ComImport, Guid("a4b544a1-438d-4b41-9325-869523e2d6c7")]
    internal class CategoryMgrClass { }

    internal static class Program
    {
        private static readonly Guid Clsid = new Guid("7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43");
        private static readonly Guid ProfileGuid = new Guid("B3F2C8D1-6A47-4C09-8E52-1D7A9F0B4C65");
        private const ushort LangId = 0x0804;

        private static int _pass, _fail;

        [STAThread]
        private static int Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            Console.WriteLine("TsfProbe —— 直接问 msctf.dll 认不认「形变输入法」");
            Console.WriteLine("CLSID       = " + Clsid.ToString("B").ToUpperInvariant());
            Console.WriteLine("ProfileGUID = " + ProfileGuid.ToString("B").ToUpperInvariant());
            Console.WriteLine("LANGID      = 0x" + LangId.ToString("X4"));
            Console.WriteLine();

            ITfInputProcessorProfiles profiles = null;
            try
            {
                profiles = (ITfInputProcessorProfiles)new InputProcessorProfilesClass();
                Console.WriteLine("[ok] CoCreateInstance(CLSID_TF_InputProcessorProfiles) 成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 无法创建 ITfInputProcessorProfiles: " + ex.Message);
                return 1;
            }

            // --- 1. TSF 是否知道这个 CLSID 注册过输入处理器 ---
            Console.WriteLine("--- 1. ITfInputProcessorProfiles ---");
            Guid c = Clsid;
            int hr = profiles.Register(ref c);   // 重复注册是幂等的
            Console.WriteLine("  Register(clsid) hr=0x" + hr.ToString("X8") + "  (S_OK=已登记, 其他=有问题)");
            Check("TSF 接受该 CLSID 的 Register", hr == 0, "hr=0x" + hr.ToString("X8"));

            // --- 2. 语言档案是否存在 ---
            IntPtr pDesc = IntPtr.Zero;
            Guid pg = ProfileGuid;
            hr = profiles.GetLanguageProfileDescription(ref c, LangId, ref pg, out pDesc);
            string desc = null;
            if (hr == 0 && pDesc != IntPtr.Zero) { desc = Marshal.PtrToStringBSTR(pDesc); Marshal.FreeBSTR(pDesc); }
            Console.WriteLine("  GetLanguageProfileDescription hr=0x" + hr.ToString("X8") + " desc=\"" + desc + "\"");
            Check("TSF 能找到语言档案且描述为「形变输入法」", hr == 0 && desc == "形变输入法",
                  "hr=0x" + hr.ToString("X8") + " desc=" + (desc ?? "(null)"));

            // --- 3. 档案是否启用 ---
            bool enabled = false;
            hr = profiles.IsEnabledLanguageProfile(ref c, LangId, ref pg, out enabled);
            Console.WriteLine("  IsEnabledLanguageProfile hr=0x" + hr.ToString("X8") + " enabled=" + enabled);
            Check("TSF 认为该档案已启用", hr == 0 && enabled, "hr=0x" + hr.ToString("X8") + " enabled=" + enabled);

            // --- 4. 当前语言 / 语言列表 ---
            Console.WriteLine("--- 2. 当前输入语言 ---");
            ushort cur;
            hr = profiles.GetCurrentLanguage(out cur);
            Console.WriteLine("  GetCurrentLanguage hr=0x" + hr.ToString("X8") + " langid=0x" + cur.ToString("X4"));
            Check("能取到当前语言", hr == 0, "hr=0x" + hr.ToString("X8"));

            // --- 5. 活动档案 ---
            Console.WriteLine("--- 3. 活动档案 ---");
            Guid dummy = Guid.Empty;
            ushort activeLang;
            Guid activeProfile;
            hr = profiles.GetActiveLanguageProfile(ref c, out activeLang, out activeProfile);
            Console.WriteLine("  GetActiveLanguageProfile hr=0x" + hr.ToString("X8"));

            // --- 6. 最硬的证据：让 TSF 真的去创建我们的对象 ---
            Console.WriteLine("--- 4. 让 TSF 真的激活这个档案（真 CoCreateInstance）---");
            ITfInputProcessorProfileMgr mgr = null;
            try
            {
                mgr = (ITfInputProcessorProfileMgr)new InputProcessorProfilesClass();
                Console.WriteLine("  QI 到 ITfInputProcessorProfileMgr 成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  无法取 ITfInputProcessorProfileMgr: " + ex.Message);
            }

            if (mgr != null)
            {
                // TF_PROFILETYPE_INPUTPROCESSOR = 1
                const uint TF_PROFILETYPE_INPUTPROCESSOR = 1;
                int hrAct = mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, LangId, ref c, ref pg, IntPtr.Zero, 0);
                Console.WriteLine("  ActivateProfile hr=0x" + hrAct.ToString("X8"));
                // 期望：0 或 0x80070005(需要更早的时机) —— 不是 CLASSNOTREG(0x80040154)
                bool notClassNotReg = hrAct != unchecked((int)0x80040154);
                Check("ActivateProfile 没有报「类未注册 0x80040154」", notClassNotReg,
                      "hr=0x" + hrAct.ToString("X8"));
            }

            Console.WriteLine();
            Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("[ ok ] " + name + " -- " + detail); }
            else { _fail++; Console.WriteLine("[FAIL] " + name + " -- " + detail); }
        }
    }
}
