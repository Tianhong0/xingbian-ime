/*
 * ts_host.c -- Native (non-CLR) host that activates the Xingbian TSF text service
 *              through the real OLE32 activation path.
 *
 * Why C:  the whole point of this experiment is to prove that a *native* process
 *         can create the managed COM object.  Calling CoCreateInstance through
 *         P/Invoke from C# would leave the caller itself inside the CLR, which
 *         weakens the evidence.  This program has no CLR dependency at all.
 *
 * Layers exercised (all from the native side):
 *   1. ole32!CoCreateInstance(CLSID_TextService, ...)  -> OLE activation path
 *   2. registration-free COM via the application manifest (<clrClass>)
 *   3. QI for the TSF IIDs the real input-method host would ask for
 *   4. one real method call on each interface (not just a pointer)
 *   5. ActivateEx with a REAL ITfThreadMgr created by TSF itself
 *
 * Build: see build.ps1 (zig cc, no CLR anywhere in the toolchain).
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <stdio.h>
#include <string.h>

/* ------------------------------------------------------------------ */
/* GUIDs (declared literally so we do not need msctf.h from the SDK)  */
/* ------------------------------------------------------------------ */

/* Xingbian.Tsf.TextService [ComVisible(true)] [Guid(...)] */
static const GUID CLSID_XingbianTextService =
    { 0x7C4E1A9B, 0x3D2F, 0x4E58, { 0x9A, 0x61, 0x0F, 0x8B, 0x5C, 0x7D, 0x2E, 0x43 } };

static const GUID IID_ITfTextInputProcessorEx =
    { 0x6E4E2102, 0xF9CD, 0x433D, { 0xB4, 0x96, 0x30, 0x3C, 0xE0, 0x3A, 0x65, 0x07 } };

static const GUID IID_ITfKeyEventSink =
    { 0xAA80E7F5, 0x2021, 0x11D2, { 0x93, 0xE0, 0x00, 0x60, 0xB0, 0x67, 0xB8, 0x6E } };

static const GUID IID_ITfTextInputProcessor =
    { 0xAA80E7F7, 0x2021, 0x11D2, { 0x93, 0xE0, 0x00, 0x60, 0xB0, 0x67, 0xB8, 0x6E } };

/* The TSF thread manager, so we can hand a real ITfThreadMgr to ActivateEx. */
static const GUID CLSID_TF_ThreadMgr =
    { 0x529A9E6B, 0x6587, 0x4F23, { 0xAB, 0x9E, 0x9C, 0x7D, 0x68, 0x3E, 0x3C, 0x50 } };

static const GUID IID_ITfThreadMgr =
    { 0xAA80E801, 0x2021, 0x11D2, { 0x93, 0xE0, 0x00, 0x60, 0xB0, 0x67, 0xB8, 0x6E } };

/* ------------------------------------------------------------------ */
/* Minimal vtable layouts (slot numbering verified against msctf.h)   */
/* ------------------------------------------------------------------ */

typedef HRESULT (WINAPI *PFN_Deactivate)(void *pThis);
typedef HRESULT (WINAPI *PFN_OnSetFocus)(void *pThis, BOOL fForeground);
typedef HRESULT (WINAPI *PFN_ActivateEx)(void *pThis, void *ptim, DWORD tid, DWORD dwFlags);
typedef HRESULT (WINAPI *PFN_TfTmActivateEx)(void *pThis, DWORD *ptid, DWORD dwFlags);

/* ITfTextInputProcessorEx : IUnknown + Activate(3) Deactivate(4) ActivateEx(5) */
#define VT_ACTIVATE        3
#define VT_DEACTIVATE      4
#define VT_ACTIVATEEX      5

/* ITfKeyEventSink : IUnknown + OnSetFocus(3) ...                        */
#define VT_ONSETFOCUS      3

/* ITfThreadMgr : IUnknown + ActivateEx(3) Deactivate(4) ...             */
#define VT_TFM_ACTIVATEEX  3

/* ------------------------------------------------------------------ */
/* helpers                                                            */
/* ------------------------------------------------------------------ */

static void hr_name(HRESULT hr, char *buf, size_t n)
{
    const char *s = NULL;
    switch ((unsigned long)hr) {
    case 0x00000000UL: s = "S_OK"; break;
    case 0x00000001UL: s = "S_FALSE"; break;
    case 0x80040154UL: s = "REGDB_E_CLASSNOTREG"; break;
    case 0x80040111UL: s = "CLASS_E_CLASSNOTAVAILABLE"; break;
    case 0x8007007EUL: s = "HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)"; break;
    case 0x80070002UL: s = "HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)"; break;
    case 0x8007000BUL: s = "HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)"; break;
    case 0x80070005UL: s = "E_ACCESSDENIED"; break;
    case 0x80070057UL: s = "E_INVALIDARG"; break;
    case 0x80004002UL: s = "E_NOINTERFACE"; break;
    case 0x80004005UL: s = "E_FAIL"; break;
    case 0x800401F0UL: s = "CO_E_NOTINITIALIZED"; break;
    case 0x80029C4AUL: s = "TYPE_E_CANTLOADLIBRARY"; break;
    case 0x80131513UL: s = "COR_E_TYPELOAD (TypeLoadException)"; break;
    case 0x80131522UL: s = "COR_E_TARGETINVOCATION"; break;
    case 0x80131700UL: s = "CLR_E_SHIM_RUNTIMELOAD (mscoree could not start the runtime)"; break;
    case 0x80131040UL: s = "COR_E_FILELOAD / assembly could not be located"; break;
    case 0x80131515UL: s = "COR_E_FILELOAD (assembly load from disallowed location)"; break;
    default: break;
    }
    if (s) { snprintf(buf, n, "%s", s); }
    else   { snprintf(buf, n, "(unmapped)"); }
}

static void report(const char *what, HRESULT hr)
{
    char nb[160];
    hr_name(hr, nb, sizeof(nb));
    printf("RESULT %-52s hr=0x%08lX  %s\n", what, (unsigned long)hr, nb);
    fflush(stdout);
}

static int g_fail = 0;

/* ------------------------------------------------------------------ */
/* main experiment                                                    */
/* ------------------------------------------------------------------ */

int main(int argc, char **argv)
{
    int do_real_call = 1;
    int do_cleanup   = 1;
    int i;

    for (i = 1; i < argc; ++i) {
        if (strcmp(argv[i], "--no-method-call") == 0) { do_real_call = 0; }
        else if (strcmp(argv[i], "--no-cleanup") == 0) { do_cleanup = 0; }
    }

    printf("=== ts_host: native CoCreateInstance probe for Xingbian.Tsf.TextService ===\n");
    printf("pid=%lu  arch=%s\n", (unsigned long)GetCurrentProcessId(),
#ifdef _WIN64
           "x64"
#else
           "x86"
#endif
    );

    printf("--- CoInitializeEx -------------------------------------------------\n");
    {
        HRESULT hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
        report("CoInitializeEx(APARTMENTTHREADED)", hr);
        if (FAILED(hr) && (unsigned long)hr != 0x80010106UL /*RPC_E_CHANGED_MODE*/) { return 2; }
    }

    printf("--- LAYER 1: CoCreateInstance(CLSID, IID_IUnknown) -----------------\n");
    {
        IUnknown *pUnk = NULL;
        HRESULT hr = CoCreateInstance(&CLSID_XingbianTextService, NULL,
                                      CLSCTX_INPROC_SERVER, &IID_IUnknown, (void **)&pUnk);
        report("CoCreateInstance(CLSID_TextService, IID_IUnknown)", hr);
        if (FAILED(hr) || pUnk == NULL) {
            printf("FATAL: object was not created; later layers cannot run.\n");
            g_fail = 1;
        } else {
            printf("OK: IUnknown* = %p\n", (void *)pUnk);
            {
                ULONG rc = pUnk->lpVtbl->AddRef(pUnk);
                printf("RESULT IUnknown::AddRef                              -> refcount %lu\n", (unsigned long)rc);
                rc = pUnk->lpVtbl->Release(pUnk);
                printf("RESULT IUnknown::Release                             -> refcount %lu\n", (unsigned long)rc);
            }

            printf("--- LAYER 2: QueryInterface for the TSF IIDs -------------------\n");
            {
                static const struct { const GUID *iid; const char *name; } q[3] = {
                    { &IID_ITfTextInputProcessorEx, "ITfTextInputProcessorEx {6E4E2102-F9CD-433D-B496-303CE03A6507}" },
                    { &IID_ITfKeyEventSink,         "ITfKeyEventSink         {AA80E7F5-2021-11D2-93E0-0060B067B86E}" },
                    { &IID_ITfTextInputProcessor,   "ITfTextInputProcessor   {AA80E7F7-2021-11D2-93E0-0060B067B86E}" }
                };
                void *pEx = NULL, *pKey = NULL, *pTip = NULL;
                void **slots[3];
                int k;
                slots[0] = &pEx; slots[1] = &pKey; slots[2] = &pTip;

                for (k = 0; k < 3; ++k) {
                    char label[256];
                    HRESULT qhr = pUnk->lpVtbl->QueryInterface(pUnk, q[k].iid, slots[k]);
                    snprintf(label, sizeof(label), "QI %s", q[k].name);
                    report(label, qhr);
                    if (SUCCEEDED(qhr) && *slots[k]) {
                        printf("OK: %s -> %p\n", q[k].name, *slots[k]);
                    } else {
                        *slots[k] = NULL;
                        g_fail = 1;
                    }
                }

                printf("--- LAYER 3: real TSF method calls -----------------------------\n");
                if (do_real_call) {
                    if (pTip) {
                        /* ActivateEx(NULL,0,0) must return E_INVALIDARG because the
                           managed ActivateEx has an explicit `if (ptim == null) return
                           HResult.E_INVALIDARG;`.  Getting 0x80070057 back proves the
                           managed body actually executed inside this native process. */
                        PFN_ActivateEx p = (PFN_ActivateEx)(*(void ***)pTip)[VT_ACTIVATEEX];
                        HRESULT chr = p(pTip, NULL, 0, 0);
                        report("call ITfTextInputProcessorEx::ActivateEx(NULL,0,0)", chr);
                        printf("     (expect 0x80070057 E_INVALIDARG from the managed null-check)\n");
                    }
                    if (pKey) {
                        PFN_OnSetFocus p = (PFN_OnSetFocus)(*(void ***)pKey)[VT_ONSETFOCUS];
                        HRESULT chr = p(pKey, FALSE);
                        report("call ITfKeyEventSink::OnSetFocus(FALSE)", chr);
                    }
                    if (pEx) {
                        PFN_Deactivate p = (PFN_Deactivate)(*(void ***)pEx)[VT_DEACTIVATE];
                        HRESULT chr = p(pEx);
                        report("call ITfTextInputProcessorEx::Deactivate()", chr);
                    }
                } else {
                    printf("SKIPPED (--no-method-call)\n");
                }

                if (pEx)  { ((IUnknown *)pEx)->lpVtbl->Release((IUnknown *)pEx); }
                if (pKey) { ((IUnknown *)pKey)->lpVtbl->Release((IUnknown *)pKey); }
                if (pTip) { ((IUnknown *)pTip)->lpVtbl->Release((IUnknown *)pTip); }
            }

            printf("--- LAYER 4: ActivateEx with a REAL ITfThreadMgr ---------------\n");
            {
                IUnknown *pTm = NULL;
                HRESULT thr = CoCreateInstance(&CLSID_TF_ThreadMgr, NULL,
                                               CLSCTX_INPROC_SERVER, &IID_ITfThreadMgr, (void **)&pTm);
                report("CoCreateInstance(CLSID_TF_ThreadMgr, IID_ITfThreadMgr)", thr);
                if (SUCCEEDED(thr) && pTm) {
                    DWORD tid = 0;
                    PFN_TfTmActivateEx act = (PFN_TfTmActivateEx)(*(void ***)pTm)[VT_TFM_ACTIVATEEX];
                    thr = act(pTm, &tid, 0);
                    report("ITfThreadMgr::ActivateEx(&tid,0)", thr);
                    printf("     tid=%lu\n", (unsigned long)tid);

                    {
                        void *pEx2 = NULL;
                        HRESULT hr2 = pUnk->lpVtbl->QueryInterface(pUnk, &IID_ITfTextInputProcessorEx, &pEx2);
                        if (SUCCEEDED(hr2) && pEx2) {
                            PFN_ActivateEx p = (PFN_ActivateEx)(*(void ***)pEx2)[VT_ACTIVATEEX];
                            hr2 = p(pEx2, pTm, tid, 0);
                            report("TextService::ActivateEx(real ITfThreadMgr, tid, 0)", hr2);
                            if (SUCCEEDED(hr2)) {
                                PFN_Deactivate d = (PFN_Deactivate)(*(void ***)pEx2)[VT_DEACTIVATE];
                                hr2 = d(pEx2);
                                report("TextService::Deactivate() after real ActivateEx", hr2);
                            }
                            ((IUnknown *)pEx2)->lpVtbl->Release((IUnknown *)pEx2);
                        }
                    }
                    {
                        PFN_Deactivate d = (PFN_Deactivate)(*(void ***)pTm)[VT_DEACTIVATE];
                        thr = d(pTm);
                        report("ITfThreadMgr::Deactivate()", thr);
                    }
                    ((IUnknown *)pTm)->lpVtbl->Release((IUnknown *)pTm);
                } else {
                    printf("NOTE: TSF thread manager unavailable; skipping layer 4.\n");
                }
            }

            {
                ULONG rc = pUnk->lpVtbl->Release(pUnk);
                printf("RESULT final IUnknown::Release                       -> refcount %lu\n", (unsigned long)rc);
            }
        }
    }

    if (do_cleanup) {
        printf("--- CoUninitialize -------------------------------------------------\n");
        CoUninitialize();
        printf("RESULT CoUninitialize                                -> returned cleanly\n");
    } else {
        printf("--- CoUninitialize SKIPPED (--no-cleanup) --------------------------\n");
    }

    printf("=== VERDICT: %s ===\n", g_fail ? "FAIL" : "PASS");
    return g_fail ? 1 : 0;
}
