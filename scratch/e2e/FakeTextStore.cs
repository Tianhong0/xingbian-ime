// 假文本存储 —— 这是整个 harness 的核心。
//
// 目的：在**不注册 TIP** 的前提下，给 TSF 一个能吃的 ITextStoreACP，
//       从而让 msctf.dll 愿意为我们的 ITfContext 开编辑会话，
//       把 TextService 的提交路径真正跑起来。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Xingbian.Tsf.Interop;

namespace Xingbian.E2E
{
    /// <summary>
    /// RequestLock 的实现模型。这个枚举本身就是本次实验的自变量。
    /// </summary>
    public enum LockModel
    {
        /// <summary>
        /// 模型 A「同步授权」：RequestLock 内部立刻回调 sink.OnLockGranted(dwLockFlags)，
        /// 由 msctf 去调 ITfEditSession::DoEditSession，然后把 *phrSession 置 S_OK。
        /// </summary>
        SyncGrant = 0,

        /// <summary>
        /// 模型 B「延迟授权」：RequestLock 只记录锁状态，*phrSession = TS_S_ASYNC(0x40300) 后直接返回，
        /// 由宿主（本 harness）在合适时机自行回调 sink.OnLockGranted。
        /// </summary>
        DeferredGrant = 1
    }

    /// <summary>
    /// 一个最小可用但真实的 ITextStoreACP 实现：文本存在内存里，所有写入同时落日志。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class FakeTextStore : ITextStoreACP
    {
        // ---- 文本与选区状态 ----
        private readonly StringBuilder _text = new StringBuilder(256);
        private int _selPoint;

        // ---- 锁状态 ----
        private int _lockDepth;
        private uint _lastLockFlags;

        /// <summary>TSF 通过 AdviseSink 交进来的 sink；模型 A 里我们主动回调它。</summary>
        private ITextStoreACPSink _sink;

        /// <summary>模型 B 用：延迟授权时把待授权的锁标志存下来，由宿主稍后回调。</summary>
        private bool _deferredLockPending;
        private uint _deferredLockFlags;

        private IntPtr _hwnd;
        private uint _viewCookie = 1;

        /// <summary>模型 A 成功调用 OnLockGranted 的次数（用于报告两种模型的差异）。</summary>
        public int OnLockGrantedCalls;

        /// <summary>RequestLock 被调用的次数。</summary>
        public int RequestLockCalls;

        /// <summary>InsertTextAtSelection 被调用的次数。</summary>
        public int InsertTextAtSelectionCalls;

        public List<string> Events = new List<string>();

        public LockModel Model = LockModel.SyncGrant;

        /// <summary>最近一次 RequestLock 收到的锁标志（报告要对比两种模型，必须能看到它）。</summary>
        public uint LastLockFlags { get { return _lastLockFlags; } }

        /// <summary>当前嵌套锁深度。</summary>
        public int LockDepth { get { return _lockDepth; } }

        /// <summary>由 harness 注入：延迟模型下，宿主自己调用 OnLockGranted 时用。</summary>
        public ITextStoreACPSink Sink { get { return _sink; } }

        public IntPtr Hwnd
        {
            get { return _hwnd; }
            set { _hwnd = value; }
        }

        // =================================================================================
        // 断言用访问器
        // =================================================================================

        public string Text { get { return _text.ToString(); } }

        public int TextLength { get { return _text.Length; } }

        public void ResetDocument()
        {
            _text.Length = 0;
            _selPoint = 0;
        }

        private void Log(string s)
        {
            Events.Add(s);
        }

        // =================================================================================
        // 槽位 0 / 1 : AdviseSink / UnadviseSink
        // =================================================================================

        public int AdviseSink(ref Guid riid, IntPtr punk, uint dwMask)
        {
            Log("AdviseSink iid=" + riid.ToString("D") + " mask=0x" + dwMask.ToString("X"));
            try
            {
                object o = Marshal.GetObjectForIUnknown(punk);
                _sink = o as ITextStoreACPSink;
                Log("AdviseSink -> sink=" + (_sink != null ? "OK" : "NULL(as ITextStoreACPSink 失败)"));
                return _sink != null ? 0 : unchecked((int)0x80004002); // E_NOINTERFACE
            }
            catch (Exception ex)
            {
                Log("AdviseSink EX " + ex.GetType().Name + ": " + ex.Message);
                return unchecked((int)0x80004005);
            }
        }

        public int UnadviseSink(IntPtr punk)
        {
            Log("UnadviseSink");
            _sink = null;
            return 0;
        }

        // =================================================================================
        // 槽位 2 : RequestLock  ★★★ 本次实验的核心自变量 ★★★
        // =================================================================================

        public int RequestLock(uint dwLockFlags, out int phrSession)
        {
            RequestLockCalls++;
            _lockDepth++;
            _lastLockFlags = dwLockFlags;
            Log("RequestLock(flags=0x" + dwLockFlags.ToString("X") + ") depth=" + _lockDepth + " model=" + Model);

            if (Model == LockModel.SyncGrant)
            {
                // ---- 模型 A：立刻把锁交给 TSF，由 TSF 去调 DoEditSession ----
                phrSession = 0; // S_OK
                if (_sink != null)
                {
                    OnLockGrantedCalls++;
                    Log("RequestLock -> 同步回调 sink.OnLockGranted(0x" + dwLockFlags.ToString("X") + ") ...");
                    try
                    {
                        int hr = _sink.OnLockGranted(dwLockFlags);
                        Log("RequestLock -> OnLockGranted 返回 hr=0x" + hr.ToString("X8"));
                    }
                    catch (Exception ex)
                    {
                        Log("RequestLock -> OnLockGranted 抛异常 " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                else
                {
                    Log("RequestLock -> 没有 sink（TSF 尚未 AdviseSink），无法授权");
                }
                if ((dwLockFlags & Ts.TS_LF_SYNC) != 0)
                {
                    _lockDepth--; // 同步锁：授权期间持锁，返回前释放
                }
                return 0;
            }

            // ---- 模型 B：延迟授权 ----
            _deferredLockPending = true;
            _deferredLockFlags = dwLockFlags;
            phrSession = Ts.TS_S_ASYNC; // 0x00040300
            Log("RequestLock -> 返回 TS_S_ASYNC(0x" + Ts.TS_S_ASYNC.ToString("X8") + ")，等宿主自己回调 OnLockGranted");
            return 0;
        }

        /// <summary>模型 B 专用：宿主在 RequestEditSession 返回之后自己把锁授出去。</summary>
        public bool RunDeferredLock()
        {
            if (!_deferredLockPending)
            {
                return false;
            }
            _deferredLockPending = false;
            if (_sink == null)
            {
                Log("RunDeferredLock -> 没有 sink");
                return false;
            }
            OnLockGrantedCalls++;
            Log("RunDeferredLock -> 宿主手动回调 OnLockGranted(0x" + _deferredLockFlags.ToString("X") + ") ...");
            try
            {
                int hr = _sink.OnLockGranted(_deferredLockFlags);
                Log("RunDeferredLock -> OnLockGranted 返回 hr=0x" + hr.ToString("X8"));
            }
            catch (Exception ex)
            {
                Log("RunDeferredLock -> OnLockGranted 抛异常 " + ex.GetType().Name + ": " + ex.Message);
            }
            _lockDepth--;
            return true;
        }

        public bool HasDeferredLock { get { return _deferredLockPending; } }

        // =================================================================================
        // 槽位 3 : GetStatus —— 绝不能报 readonly，否则 RequestEditSession 直接 TS_E_READONLY
        // =================================================================================

        public int GetStatus(ref TS_STATUS pdcs)
        {
            pdcs.dwDynamicFlags = 0;
            pdcs.dwStaticFlags = 0;
            return 0;
        }

        // =================================================================================
        // 槽位 4 : QueryInsert
        // =================================================================================

        public int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd)
        {
            pacpResultStart = acpTestStart;
            pacpResultEnd = acpTestEnd;
            return 0;
        }

        // =================================================================================
        // 槽位 5 : GetSelection
        // =================================================================================

        public int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched)
        {
            pcFetched = 0;
            if (pSelection == IntPtr.Zero || ulCount == 0)
            {
                return Ts.TS_E_NOSELECTION;
            }

            var sel = new TS_SELECTION_ACP();
            sel.acpStart = _selPoint;
            sel.acpEnd = _selPoint;
            sel.style.ase = Ts.TS_AE_END;
            sel.style.fInterimChar = 0;

            Marshal.StructureToPtr(sel, pSelection, false);
            pcFetched = 1;
            Log("GetSelection -> [" + sel.acpStart + "," + sel.acpEnd + ") ase=TS_AE_END");
            return 0;
        }

        // =================================================================================
        // 槽位 6 : SetSelection
        // =================================================================================

        public int SetSelection(uint ulCount, IntPtr pSelection)
        {
            if (ulCount == 0 || pSelection == IntPtr.Zero)
            {
                return 0;
            }
            var sel = (TS_SELECTION_ACP)Marshal.PtrToStructure(pSelection, typeof(TS_SELECTION_ACP));
            _selPoint = Clamp(sel.acpEnd);
            Log("SetSelection -> caret=" + _selPoint);
            return 0;
        }

        // =================================================================================
        // 槽位 7 : GetText
        // =================================================================================

        public int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq, out uint pcchPlainRet,
                           IntPtr prgRunInfo, uint cRunInfoReq, out uint pcRunInfoRet, out int pacpNext)
        {
            pcchPlainRet = 0;
            pcRunInfoRet = 0;
            pacpNext = _text.Length;

            int start = Clamp(acpStart);
            int end = (acpEnd == -1) ? _text.Length : Clamp(acpEnd);
            if (end < start)
            {
                return Ts.TS_E_INVALIDPOS;
            }

            int avail = end - start;
            pcchPlainRet = 0;
            pcRunInfoRet = 0;
            pacpNext = end;

            if (pchPlain != IntPtr.Zero && cchPlainReq > 0)
            {
                int n = Math.Min(avail, (int)cchPlainReq);
                if (n > 0)
                {
                    char[] buf = new char[n];
                    _text.CopyTo(start, buf, 0, n);
                    Marshal.Copy(buf, 0, pchPlain, n);
                }
                pcchPlainRet = (uint)n;
            }

            if (prgRunInfo != IntPtr.Zero && cRunInfoReq > 0)
            {
                var ri = new TS_RUNINFO();
                int n = Math.Min(avail, (int)cchPlainReq);
                ri.uCount = (uint)Math.Max(n, 0);
                ri.type = Ts.TS_RT_PLAIN;
                Marshal.StructureToPtr(ri, prgRunInfo, false);
                pcRunInfoRet = 1;
            }

            return 0;
        }

        // =================================================================================
        // 槽位 8 : SetText
        // =================================================================================

        public int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, ref TS_TEXTCHANGE pChange)
        {
            int start = Clamp(acpStart);
            int end = Clamp(acpEnd);
            if (end < start)
            {
                return Ts.TS_E_INVALIDPOS;
            }

            string ins = (pchText == IntPtr.Zero || cch == 0) ? string.Empty : Marshal.PtrToStringUni(pchText, (int)cch);
            int oldLen = _text.Length;

            if (end > start)
            {
                _text.Remove(start, end - start);
            }
            if (ins.Length > 0)
            {
                _text.Insert(start, ins);
            }
            _selPoint = start + ins.Length;

            pChange.acpStart = start;
            pChange.acpOldEnd = end;
            pChange.acpNewEnd = start + ins.Length;

            Log("SetText[DOC-WRITE] start=" + start + " oldEnd=" + end + " ins=\"" + Esc(ins) + "\" (len " + oldLen + " -> " + _text.Length + ")");
            return 0;
        }

        // =================================================================================
        // 槽位 9-12 : GetFormattedText / GetEmbedded / QueryInsertEmbedded / InsertEmbedded
        // =================================================================================

        public int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject)
        {
            ppDataObject = IntPtr.Zero;
            return Ts.TS_E_NOSERVICE;
        }

        public int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppunk)
        {
            ppunk = IntPtr.Zero;
            return Ts.TS_E_NOOBJECT;
        }

        public int QueryInsertEmbedded(ref Guid pguidService, IntPtr pFormatEtc, out int pfInsertable)
        {
            pfInsertable = 0;
            return 0;
        }

        public int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, ref TS_TEXTCHANGE pChange)
        {
            return Ts.TS_E_FORMAT;
        }

        // =================================================================================
        // 槽位 13 : InsertTextAtSelection  ★★★ 见证奇迹的地方 ★★★
        // =================================================================================

        public int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
                                         out int pacpStart, out int pacpEnd, ref TS_TEXTCHANGE pChange)
        {
            InsertTextAtSelectionCalls++;
            string ins = (pchText == IntPtr.Zero || cch == 0) ? string.Empty : Marshal.PtrToStringUni(pchText, (int)cch);

            int at = _selPoint;
            _text.Insert(at, ins);
            _selPoint = at + ins.Length;

            pacpStart = at;
            pacpEnd = at + ins.Length;
            pChange.acpStart = at;
            pChange.acpOldEnd = at;
            pChange.acpNewEnd = pacpEnd;

            Log("InsertTextAtSelection[DOC-WRITE] dwFlags=0x" + dwFlags.ToString("X8") + " cch=" + cch +
                " text=\"" + Esc(ins) + "\" -> 文档现在=\"" + Esc(_text.ToString()) + "\"");
            return 0;
        }

        // =================================================================================
        // 槽位 14 : InsertEmbeddedAtSelection
        // =================================================================================

        public int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject, out int pacpStart, out int pacpEnd,
                                             ref TS_TEXTCHANGE pChange)
        {
            pacpStart = _selPoint;
            pacpEnd = _selPoint;
            return Ts.TS_E_FORMAT;
        }

        // =================================================================================
        // 槽位 15-19 : 属性相关 —— 全部最小实现
        // =================================================================================

        public int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs)
        {
            return 0;
        }

        public int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags)
        {
            return 0;
        }

        public int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags)
        {
            return 0;
        }

        public int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags,
                                          out int pacpNext, out int pfFound, out int plFoundOffset)
        {
            pacpNext = Clamp(acpStart);
            pfFound = 0;
            plFoundOffset = -1;
            return 0;
        }

        public int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched)
        {
            pcFetched = 0;
            return 0;
        }

        // =================================================================================
        // 槽位 20-25 : 布局 / 窗口
        // =================================================================================

        public int GetEndACP(out int pacp)
        {
            pacp = _text.Length;
            return 0;
        }

        public int GetActiveView(out uint pvcView)
        {
            pvcView = _viewCookie;
            return 0;
        }

        public int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp)
        {
            pacp = _selPoint;
            return Ts.TS_E_INVALIDPOINT;
        }

        public int GetTextExt(uint vcView, int acpStart, int acpEnd, out RECT prc, out int pfClipped)
        {
            prc.left = 0;
            prc.top = 0;
            prc.right = Math.Max(100, _text.Length * 16);
            prc.bottom = 20;
            pfClipped = 0;
            return 0;
        }

        public int GetScreenExt(uint vcView, out RECT prc)
        {
            prc.left = 0;
            prc.top = 0;
            prc.right = Math.Max(100, _text.Length * 16);
            prc.bottom = 20;
            return 0;
        }

        public int GetWnd(uint vcView, out IntPtr phwnd)
        {
            // ★ 必须返回真实窗口句柄，否则 TSF 可能拒绝若干操作
            phwnd = _hwnd;
            return 0;
        }

        // =================================================================================

        private int Clamp(int v)
        {
            if (v < 0) return 0;
            if (v > _text.Length) return _text.Length;
            return v;
        }

        internal static string Esc(string s)
        {
            if (s == null) return "<null>";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\x" + ((int)c).ToString("X2"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
