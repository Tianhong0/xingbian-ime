// ============================================================================
//  XingbianTsfIds.cs
//  —— 形变输入法的 GUID / 常量唯一权威源（single source of truth）
//
//  为什么单独一个文件：注册脚本（install\register.ps1）、卸载脚本、以及后续
//  可能出现的安装器都必须用**完全一致**的 GUID。散落在各处迟早会写歪一个
//  半字节，而 CLSID 写歪的表现是"输入法装不上或装上后 Win+空格 里出现但
//  一选就崩"——极难排查。所以这里集中定义，别处一律引用。
//
//  两个 GUID 都是标准 RFC 4122 version-4 格式（第三组首位为 4、第四组首位
//  在 8..b 之间），可以被任何 COM/注册表工具正常解析。
// ============================================================================

using System;

namespace Xingbian.Tsf
{
    /// <summary>
    /// 本 TIP 的全部对外标识常量。改这里就等于改产品身份，务必同步注册脚本。
    /// </summary>
    public static class XingbianTsfIds
    {
        /// <summary>
        /// 文本服务 COM 类的 CLSID（HKCR\CLSID\{这个}）。
        /// 注册表里 InprocServer32 指向 mscoree.dll（.NET Framework 的 COM 垫片），
        /// 由 Assembly / Class / RuntimeVersion 三个值告诉 CLR 该加载哪个类。
        /// </summary>
        public const string Clsid = "7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43";

        /// <summary>
        /// 语言配置（Language Profile）GUID。同一个 CLSID 可以有多个 profile
        /// （比如"简体形变"和"繁体形变"），本工程只注册一个。
        /// </summary>
        public const string ProfileGuid = "B3F2C8D1-6A47-4C09-8E52-1D7A9F0B4C65";

        /// <summary>LANGID：0x0804 = zh-CN（简体中文）。</summary>
        public const ushort LangId = 0x0804;

        /// <summary>语言配置在"文本服务和输入语言"对话框里显示的名字。</summary>
        public const string Description = "形变输入法";

        /// <summary>输入法 DLL 的文件名（不含目录）。</summary>
        public const string DllName = "Xingbian.Tsf.dll";

        /// <summary>本程序集运行的 CLR 版本（注册表 RuntimeVersion 值）。</summary>
        public const string RuntimeVersion = "v4.0.30319";

        /// <summary>COM 类的完整类型名（注册表 Class 值）。</summary>
        public const string ClassFullName = "Xingbian.Tsf.TextService";

        // ---- 便捷转换 ----------------------------------------------------------

        /// <summary>CLSID 的 Guid 形式。</summary>
        public static Guid ClsidGuid
        {
            get { return new Guid(Clsid); }
        }

        /// <summary>Profile GUID 的 Guid 形式。</summary>
        public static Guid ProfileGuidValue
        {
            get { return new Guid(ProfileGuid); }
        }

        /// <summary>
        /// 注册表里 LanguageProfile 子键名用的语言 ID 字符串，形如 "0x00000804"。
        /// </summary>
        public static string LangIdKeyName
        {
            get { return "0x" + LangId.ToString("X8"); }
        }
    }
}
