// LeadE2E —— 用「假的 TSF 上下文」驱动真实的 Xingbian.Tsf.TextService。
//
// 为什么需要这个（与 scratch\e2e 互补）：
//   scratch\e2e 用真实 msctf.dll + 真实 ITextStoreACP，验证的是「TSF 会不会正常调用我们」；
//   本工程用手写的假上下文，验证的是「我们的状态机与提交逻辑是否正确」——
//   尤其是 TrySetTextAtSelection 里手工按 x64 布局读 TF_SELECTION 的那段代码，
//   在真实 TSF 里通常走不到（主路径 InsertTextAtSelection 先成功了），
//   所以那条后备路径平时根本没被执行过。这里用「让主路径故意失败」的方式把它逼出来。
//
// 不修改任何 src\ 文件，只读引用真实编译产物。

using System;
using System.Runtime.InteropServices;
using System.Text;
using Xingbian.Tsf;
using Xingbian.Tsf.Interop;

namespace LeadE2E
{
    /// <summary>假的 ITfRange：只记录 SetText 写了什么。</summary>
    internal sealed class FakeRange : ITfRange
    {
        public string Written;
        public int SetTextCount;

        public int GetText(uint ec, uint dwFlags, IntPtr pchText, uint cchMax, out uint pcch)
        { pcch = 0; return HResult.S_OK; }

        public int SetText(uint ec, uint dwFlags, string pchText, int cch)
        {
            Written = pchText;
            SetTextCount++;
            return HResult.S_OK;
        }

        public int GetFormattedText(uint ec, out IntPtr ppDataObject) { ppDataObject = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetEmbedded(uint ec, ref Guid rguidService, ref Guid riid, out IntPtr ppunk) { ppunk = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int InsertEmbedded(uint ec, uint dwFlags, IntPtr pDataObject) { return HResult.E_NOTIMPL; }
        public int ShiftStart(uint ec, int cchReq, out int pcch, IntPtr pHalt) { pcch = 0; return HResult.S_OK; }
        public int ShiftEnd(uint ec, int cchReq, out int pcch, IntPtr pHalt) { pcch = 0; return HResult.S_OK; }
        public int ShiftStartToRange(uint ec, ITfRange pRange, int aPos) { return HResult.S_OK; }
        public int ShiftEndToRange(uint ec, ITfRange pRange, int aPos) { return HResult.S_OK; }
        public int ShiftStartRegion(uint ec, int dir, out bool pfNoRegion) { pfNoRegion = true; return HResult.S_OK; }
        public int ShiftEndRegion(uint ec, int dir, out bool pfNoRegion) { pfNoRegion = true; return HResult.S_OK; }
        public int IsEmpty(uint ec, out bool pfEmpty) { pfEmpty = true; return HResult.S_OK; }
        public int Collapse(uint ec, int aPos) { return HResult.S_OK; }
        public int IsEqualStart(uint ec, ITfRange pWith, int aPos, out bool pfEqual) { pfEqual = false; return HResult.S_OK; }
        public int IsEqualEnd(uint ec, ITfRange pWith, int aPos, out bool pfEqual) { pfEqual = false; return HResult.S_OK; }
        public int CompareStart(uint ec, ITfRange pWith, int aPos, out int plResult) { plResult = 0; return HResult.S_OK; }
        public int CompareEnd(uint ec, ITfRange pWith, int aPos, out int plResult) { plResult = 0; return HResult.S_OK; }
        public int AdjustForInsert(uint ec, uint cchInsert, out bool pfInsertOk) { pfInsertOk = true; return HResult.S_OK; }
        public int GetGravity(out int pgStart, out int pgEnd) { pgStart = 0; pgEnd = 0; return HResult.S_OK; }
        public int SetGravity(uint ec, int gStart, int gEnd) { return HResult.S_OK; }
        public int Clone(out ITfRange ppClone) { ppClone = null; return HResult.E_NOTIMPL; }
        public int GetContext(out ITfContext ppContext) { ppContext = null; return HResult.E_NOTIMPL; }
    }

    /// <summary>
    /// 假的 ITfContext。同时实现 ITfInsertAtSelection（TextService 会 `as` 到它）。
    /// 「文档」就是一个 StringBuilder。
    /// </summary>
    internal sealed class FakeContext : ITfContext, ITfInsertAtSelection
    {
        public readonly StringBuilder Document = new StringBuilder();
        public readonly FakeRange Range = new FakeRange();

        /// <summary>true = 主路径 InsertTextAtSelection 故意失败，逼 TextService 走 SetText 后备路径。</summary>
        public bool BreakPrimaryPath;

        public int EditSessionCount;

        // ---- ITfInsertAtSelection ----

        public int InsertTextAtSelection(uint ec, uint dwFlags, string pchText, int cch, out ITfRange ppRange)
        {
            ppRange = null;
            if (BreakPrimaryPath) { return HResult.E_NOTIMPL; }

            Document.Append(pchText);
            ppRange = Range;
            return HResult.S_OK;
        }

        public int InsertEmbeddedAtSelection(uint ec, uint dwFlags, IntPtr pDataObject, out ITfRange ppRange)
        { ppRange = null; return HResult.E_NOTIMPL; }

        // ---- ITfContext ----

        /// <summary>
        /// TextService 用 TF_ES_SYNC 调用，期望编辑会话在返回前就已经执行完。
        /// 这里就同步把 DoEditSession 调掉，语义与真实 TSF 的同步会话一致。
        /// </summary>
        public int RequestEditSession(uint tid, ITfEditSession pes, uint dwFlags, out int phrSession)
        {
            EditSessionCount++;
            phrSession = HResult.S_OK;
            if (pes == null) { return HResult.E_POINTER; }
            return pes.DoEditSession(0x1234);   // 任意非 0 cookie
        }

        public int InWriteSession(uint tid, out bool pfWriteSession) { pfWriteSession = true; return HResult.S_OK; }

        /// <summary>
        /// 按 x64 布局填 TF_SELECTION：偏移 0 是 ITfRange*，偏移 8 是 TF_SELECTIONSTYLE。
        /// 这是验证 TextService 里那段「16 字节手工布局」代码的关键。
        /// </summary>
        public int GetSelection(uint ec, uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched)
        {
            pcFetched = 0;
            if (pSelection == IntPtr.Zero) { return HResult.E_POINTER; }
            if (ulIndex > 0 || ulCount == 0) { return HResult.S_OK; }

            IntPtr pRange = Marshal.GetComInterfaceForObject(Range, typeof(ITfRange));
            Marshal.WriteIntPtr(pSelection, 0, pRange);           // ITfRange* range
            Marshal.WriteInt32(pSelection, 8, 0);                 // TF_SELECTIONSTYLE.ase = TF_AE_NONE
            Marshal.WriteInt32(pSelection, 12, 0);                // .fInterimChar = FALSE

            pcFetched = 1;
            return HResult.S_OK;
        }

        public int SetSelection(uint ec, uint ulCount, IntPtr pSelection) { return HResult.S_OK; }
        public int GetStart(uint ec, out ITfRange ppStart) { ppStart = Range; return HResult.S_OK; }
        public int GetEnd(uint ec, out ITfRange ppEnd) { ppEnd = Range; return HResult.S_OK; }
        public int GetActiveView(out ITfContextView ppView) { ppView = null; return HResult.E_NOTIMPL; }
        public int EnumViews(out IntPtr ppEnum) { ppEnum = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetStatus(IntPtr pdcs) { return HResult.S_OK; }
        public int GetProperty(ref Guid guidProp, out IntPtr ppProp) { ppProp = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetAppProperty(ref Guid guidProp, out IntPtr ppProp) { ppProp = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int TrackProperties(IntPtr prgProp, uint cProp, IntPtr prgAppProp, uint cAppProp, out IntPtr ppProperty)
        { ppProperty = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int EnumProperties(out IntPtr ppEnum) { ppEnum = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetDocumentMgr(out ITfDocumentMgr ppDm) { ppDm = null; return HResult.E_NOTIMPL; }
        public int CreateRangeBackup(uint ec, ITfRange pRange, out IntPtr ppBackup) { ppBackup = IntPtr.Zero; return HResult.E_NOTIMPL; }
    }

    /// <summary>假的 ITfThreadMgr，兼作 ITfSource 与 ITfKeystrokeMgr。</summary>
    internal sealed class FakeThreadMgr : ITfThreadMgr, ITfSource, ITfKeystrokeMgr
    {
        public bool AdvisedThreadMgrSink;
        public bool AdvisedKeySink;
        public uint LastAdviseTid;

        public int Activate(out uint ptid) { ptid = 1; return HResult.S_OK; }
        public int Deactivate() { return HResult.S_OK; }
        public int CreateDocumentMgr(out ITfDocumentMgr ppdim) { ppdim = null; return HResult.E_NOTIMPL; }
        public int EnumDocumentMgrs(out IntPtr ppEnum) { ppEnum = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetFocus(out ITfDocumentMgr ppdimFocus) { ppdimFocus = null; return HResult.E_NOTIMPL; }
        public int SetFocus(ITfDocumentMgr pdimFocus) { return HResult.S_OK; }
        public int AssociateFocus(IntPtr hwnd, ITfDocumentMgr pdimNew, out ITfDocumentMgr ppdimPrev)
        { ppdimPrev = null; return HResult.E_NOTIMPL; }
        public int IsThreadFocus(out bool pfThreadFocus) { pfThreadFocus = true; return HResult.S_OK; }
        public int GetFunctionProvider(ref Guid clsid, out IntPtr ppFuncProv) { ppFuncProv = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int EnumFunctionProviders(out IntPtr ppEnum) { ppEnum = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetGlobalCompartment(out ITfCompartmentMgr ppCompMgr) { ppCompMgr = null; return HResult.E_NOTIMPL; }

        // ITfSource
        public int AdviseSink(ref Guid riid, object punk, out uint pdwCookie)
        {
            pdwCookie = 1;
            if (riid == TsfIids.IID_ITfThreadMgrEventSink) { AdvisedThreadMgrSink = true; }
            return HResult.S_OK;
        }
        public int UnadviseSink(uint dwCookie) { AdvisedThreadMgrSink = false; return HResult.S_OK; }

        // ITfKeystrokeMgr
        public int AdviseKeyEventSink(uint tid, ITfKeyEventSink pSink, bool fForeground)
        { AdvisedKeySink = true; LastAdviseTid = tid; return HResult.S_OK; }
        public int UnadviseKeyEventSink(uint tid) { AdvisedKeySink = false; return HResult.S_OK; }
        public int GetForeground(out Guid pclsid) { pclsid = Guid.Empty; return HResult.S_OK; }
        public int TestKeyDown(IntPtr wParam, IntPtr lParam, out bool pfEaten) { pfEaten = false; return HResult.S_OK; }
        public int TestKeyUp(IntPtr wParam, IntPtr lParam, out bool pfEaten) { pfEaten = false; return HResult.S_OK; }
        public int KeyDown(IntPtr wParam, IntPtr lParam, out bool pfEaten) { pfEaten = false; return HResult.S_OK; }
        public int KeyUp(IntPtr wParam, IntPtr lParam, out bool pfEaten) { pfEaten = false; return HResult.S_OK; }
        public int GetPreservedKey(ITfContext pic, IntPtr pprekey, out Guid pguid) { pguid = Guid.Empty; return HResult.E_NOTIMPL; }
        public int IsPreservedKey(ref Guid rguid, IntPtr pprekey, out bool pfPreserved) { pfPreserved = false; return HResult.S_OK; }
        public int PreserveKey(uint tid, ref Guid rguid, IntPtr prekey, string pchDesc, uint cchDesc) { return HResult.E_NOTIMPL; }
        public int UnpreserveKey(ref Guid rguid, IntPtr prekey) { return HResult.E_NOTIMPL; }
        public int SetPreservedKeyDescription(ref Guid rguid, string pchDesc, uint cchDesc) { return HResult.E_NOTIMPL; }
        public int GetPreservedKeyDescription(ref Guid rguid, out string pbstrDesc) { pbstrDesc = null; return HResult.E_NOTIMPL; }
        public int SimulatePreservedKey(ITfContext pic, ref Guid rguid, out bool pfEaten) { pfEaten = false; return HResult.E_NOTIMPL; }
    }
}
