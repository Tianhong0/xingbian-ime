using System;
using System.Collections.Generic;
using System.IO;

namespace Xingbian.Cli
{
    /// <summary>
    /// 词典路径解析（仅 CLI 使用，不改变 Xingbian.Core 的公开契约）：
    ///   1. 命令行 --dict
    ///   2. 环境变量 XINGBIAN_DICT
    ///   3. 从 exe 目录逐级向上寻找 assets\build\xingbian.bin（真实数据，开发期方便）
    ///   4. 从 exe 目录逐级向上寻找 assets\build\_synth.bin（合成数据，快速回归）
    ///   5. 注册表 HKCU\Software\XingbianIME\DictPath  ← 以下三条与
    ///   6. exe 同目录\dict\xingbian.bin                 XingbianEngine.CreateDefault()
    ///   7. exe 同目录\xingbian.bin                      的查找顺序保持一致
    ///
    /// 第 5~7 条是「已部署」形态：把 exe / DLL 和 dict\ 目录拷到任意位置都能直接跑，
    /// 不依赖源码树里的 assets\ 目录。这里只读注册表，不写。
    /// </summary>
    internal static class DictResolver
    {
        public const string RealDictRelativePath = @"assets\build\xingbian.bin";
        public const string SyntheticDictRelativePath = @"assets\build\_synth.bin";

        /// <summary>与 XingbianEngine.CreateDefault() 保持一致。</summary>
        public const string RegistrySubKey = @"Software\XingbianIME";
        public const string RegistryValueName = "DictPath";
        public const string DeployedDictFileName = "xingbian.bin";
        public const string DeployedDictSubDirectory = "dict";

        public static string Resolve(string explicitPath, out string source)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                source = "--dict";
                return Path.GetFullPath(explicitPath);
            }

            string fromEnvironment = Environment.GetEnvironmentVariable("XINGBIAN_DICT");
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                source = "环境变量 XINGBIAN_DICT";
                return Path.GetFullPath(fromEnvironment);
            }

            string root = FindProjectRoot();
            if (root != null)
            {
                string real = Path.Combine(root, RealDictRelativePath);
                if (File.Exists(real))
                {
                    source = "自动发现 " + RealDictRelativePath;
                    return real;
                }

                string synthetic = Path.Combine(root, SyntheticDictRelativePath);
                if (File.Exists(synthetic))
                {
                    source = "自动发现 " + SyntheticDictRelativePath + "（合成数据，真实词典尚未生成）";
                    return synthetic;
                }
            }

            // 已部署形态（无源码树 assets\ 目录）：注册表 → dict\ 子目录 → 同目录。
            string fromRegistry = ReadRegistryPath();
            if (!string.IsNullOrEmpty(fromRegistry) && File.Exists(fromRegistry))
            {
                source = "注册表 HKCU\\" + RegistrySubKey + "\\" + RegistryValueName;
                return Path.GetFullPath(fromRegistry);
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                string inSubDirectory = Path.Combine(baseDirectory, DeployedDictSubDirectory, DeployedDictFileName);
                if (File.Exists(inSubDirectory))
                {
                    source = "自动发现 " + DeployedDictSubDirectory + "\\" + DeployedDictFileName + "（exe 同目录）";
                    return inSubDirectory;
                }

                string inBaseDirectory = Path.Combine(baseDirectory, DeployedDictFileName);
                if (File.Exists(inBaseDirectory))
                {
                    source = "自动发现 " + DeployedDictFileName + "（exe 同目录）";
                    return inBaseDirectory;
                }
            }

            source = null;
            return null;
        }

        /// <summary>只读注册表；非 Windows / 无权限 / 无该项时返回 null。</summary>
        private static string ReadRegistryPath()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistrySubKey, false))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    object value = key.GetValue(RegistryValueName);
                    return value == null ? null : value.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>从 exe 目录向上找含有 assets 目录的项目根。</summary>
        public static string FindProjectRoot()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(directory); i++)
            {
                if (Directory.Exists(Path.Combine(directory, "assets")))
                {
                    return directory;
                }

                DirectoryInfo parent = Directory.GetParent(directory);
                if (parent == null)
                {
                    break;
                }

                directory = parent.FullName;
            }

            return null;
        }

        public static string DescribeSearchOrder()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "  1. --dict <path>",
                "  2. 环境变量 XINGBIAN_DICT",
                "  3. <项目根>\\" + RealDictRelativePath,
                "  4. <项目根>\\" + SyntheticDictRelativePath,
                "  5. 注册表 HKCU\\" + RegistrySubKey + "\\" + RegistryValueName,
                "  6. <exe 目录>\\" + DeployedDictSubDirectory + "\\" + DeployedDictFileName,
                "  7. <exe 目录>\\" + DeployedDictFileName
            });
        }

        public static IList<string> MissingDictHint()
        {
            return new List<string>
            {
                "没有找到可用的词典文件。查找顺序：",
                DescribeSearchOrder()
            };
        }
    }
}
