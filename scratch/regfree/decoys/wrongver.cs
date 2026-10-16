// Decoy assembly for the "Assembly registry value carries the wrong Version="
// scenario.  The TYPE name is correct; only the assembly version differs, so we
// learn whether fusion treats the Version= segment of a non-strong-named
// assembly identity as binding or advisory.
using System;
using System.Runtime.InteropServices;

[assembly: System.Reflection.AssemblyVersion("9.9.9.9")]

namespace Xingbian.Tsf
{
    [ComVisible(true)]
    [Guid("7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class TextService
    {
        public int Deactivate() { return 0; }
    }
}
