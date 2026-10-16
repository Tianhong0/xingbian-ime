using System;
using System.IO;
public static class P { public static void Main() {
  var bytes = File.ReadAllBytes(@"D:\Documents\deepseek-harness\default-workspace\xingbian-ime\src\Xingbian.Setup\bin\Release\Xingbian.Setup.exe");
  var asm = System.Reflection.Assembly.Load(bytes);
  foreach (var r in asm.GetManifestResourceNames()) Console.WriteLine(r);
} }
