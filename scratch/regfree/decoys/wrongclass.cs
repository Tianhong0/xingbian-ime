// Decoy assembly for the "Class registry value points at a type that does not
// exist" scenario.  Identity is deliberately IDENTICAL to the real
// Xingbian.Tsf (0.1.0.0, neutral, PublicKeyToken=null) so that assembly
// resolution succeeds and the only thing wrong is the TYPE NAME -- exactly what
// a typo in InprocServer32\Class produces in the field.
using System;
using System.Runtime.InteropServices;

namespace Xingbian.Tsf
{
    [ComVisible(true)]
    [Guid("7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class WrongClass
    {
        public int Deactivate() { return 0; }
    }
}
