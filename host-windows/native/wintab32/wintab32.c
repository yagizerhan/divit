/* SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Divit contributors
 *
 * Divit wintab32.dll — a WinTab 1.4 provider (docs/wintab.md).
 *
 * Loaded into the drawing app's process (Krita, CSP, ...). Reads pen data from the Divit host's shared memory
 * (Local\Divit.WinTab.v1) and presents it to the app as WinTab packets.
 * When the host is not running it behaves like "tablet attached, pen out of range"; it never blocks the app.
 *
 * Threads: the app's threads (WT* calls) + one reader thread (started on the first WTOpen).
 * Shared state is protected by a single critical section.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "wintab.h"

/* ---------- Shared memory contract (must match the host's WinTabSink.cs) ---------- */
#define SHM_NAME        L"Local\\Divit.WinTab.v1"
#define SHM_MAGIC       0x54575644u
#define SHM_RING        1024
#define SHM_PKT         32
#define SHM_MAXCONS     16
#define OFF_SURFACE_W   36          /* u32, tablet surface width in micrometres (0 = unknown) */
#define OFF_SURFACE_H   40
#define OFF_GENERATION  44          /* u32, new random value every time the host opens the section */
#define OFF_WRITEINDEX  64
#define OFF_CONSUMERS   72
#define OFF_RING        256
#define SHM_SIZE        (OFF_RING + SHM_RING * SHM_PKT)

#define F_INRANGE 1
#define F_CONTACT 2
#define F_BARREL  4
#define F_ERASER  8

/* ---------- Virtual device ---------- */
#define TAB_MAX     32767       /* X/Y tablet units */
#define PRESS_MAX   4095        /* 4096 levels */
#define TAB_W_CM    26.6        /* default drawing surface: Tab S9 FE+ (approx.); the host reports the real size (offsets 36/40) */
#define TAB_H_CM    16.6
#define NCURSORS    3           /* 0 puck, 1 pen, 2 eraser — Qt/Krita derive the type from cursor % 3 */
#define CSR_PEN     1
#define CSR_ERASER  2
#define MAX_CTX     16
#define SUPPORTED_PKTDATA (PK_CONTEXT | PK_STATUS | PK_TIME | PK_CHANGED | PK_SERIAL_NUMBER | PK_CURSOR | PK_BUTTONS | \
                           PK_X | PK_Y | PK_Z | PK_NORMAL_PRESSURE | PK_TANGENT_PRESSURE | PK_ORIENTATION | PK_ROTATION)

/* Internal queued packet (converted to the app's requested layout by WTPacket*). */
typedef struct {
    UINT serial, status, cursor;
    DWORD time, buttons;
    WTPKT changed;
    LONG x, y, z;
    UINT npress, tpress;
    ORIENTATION ori;
} PKT;

typedef struct {
    BOOL used, enabled;
    HWND hwnd;
    LOGCONTEXTW lc;
    PKT *q; int qcap, qhead, qcount;
    UINT serial;
    /* previous values: for PK_CHANGED, relative buttons/coordinates and proximity messages */
    PKT last; BOOL haveLast;
    BOOL inProx;
    UINT curCursor;
} CTX;

static CRITICAL_SECTION g_cs;
static CTX g_ctx[MAX_CTX];
static HANDLE g_thread, g_stop;
static volatile LONG g_threadStarted;
static int g_slot = -1;
static volatile unsigned char *g_shm;
static HANDLE g_map, g_event;
static int g_sysOrgX, g_sysOrgY, g_sysExtX = 1920, g_sysExtY = 1080;   /* primary monitor if there is no host */
/* Test switches (environment variables): DIVIT_WINTAB_MAP = another shared memory name (so tests leave a running Divit alone),
 * DIVIT_WINTAB_ALWAYS=1 disables the foreground check (the test process is not in the foreground). */
static WCHAR g_mapName[128] = SHM_NAME;
static BOOL g_always;

static int Packed(WTPKT data);

/* ---------- Helpers ---------- */

/* Tablet surface size in cm as reported by the host (from the tablet's SURFACE_INFO), or the default. */
static double SurfaceCm(int off, double fallback)
{
    UINT um;
    if (!g_shm || *(volatile UINT *)g_shm != SHM_MAGIC) return fallback;
    um = *(volatile UINT *)(g_shm + off);
    return (um >= 20000u && um <= 2000000u) ? um / 10000.0 : fallback;
}

static void SysRect(void)
{
    if (g_shm && *(volatile UINT *)g_shm == SHM_MAGIC && *(volatile int *)(g_shm + 24) > 0) {
        g_sysOrgX = *(volatile int *)(g_shm + 16); g_sysOrgY = *(volatile int *)(g_shm + 20);
        g_sysExtX = *(volatile int *)(g_shm + 24); g_sysExtY = *(volatile int *)(g_shm + 28);
    } else {
        g_sysOrgX = 0; g_sysOrgY = 0;
        g_sysExtX = GetSystemMetrics(SM_CXSCREEN); g_sysExtY = GetSystemMetrics(SM_CYSCREEN);
    }
}

static void DefaultContext(LOGCONTEXTW *lc, BOOL system)
{
    ZeroMemory(lc, sizeof *lc);
    lstrcpynW(lc->lcName, system ? L"Divit system context" : L"Divit digitizing context", LCNAMELEN);
    lc->lcOptions = system ? CXO_SYSTEM : 0;
    lc->lcMsgBase = WT_DEFBASE;
    lc->lcPktRate = 360;
    lc->lcPktData = system ? (PK_X | PK_Y | PK_BUTTONS) : SUPPORTED_PKTDATA;
    lc->lcMoveMask = lc->lcPktData;
    lc->lcBtnDnMask = lc->lcBtnUpMask = 0xFFFFFFFF;
    lc->lcInExtX = TAB_MAX + 1; lc->lcInExtY = TAB_MAX + 1;
    SysRect();
    if (system) {
        lc->lcOutOrgX = g_sysOrgX; lc->lcOutOrgY = g_sysOrgY;
        lc->lcOutExtX = g_sysExtX; lc->lcOutExtY = -g_sysExtY;   /* screen coordinates: y down */
    } else {
        lc->lcOutExtX = TAB_MAX + 1; lc->lcOutExtY = TAB_MAX + 1;
    }
    lc->lcSensX = lc->lcSensY = lc->lcSensZ = 0x10000;
    lc->lcSysMode = FALSE;
    lc->lcSysOrgX = g_sysOrgX; lc->lcSysOrgY = g_sysOrgY;
    lc->lcSysExtX = g_sysExtX; lc->lcSysExtY = g_sysExtY;
    lc->lcSysSensX = lc->lcSysSensY = 0x10000;
}

static void ToW(const LOGCONTEXTA *a, LOGCONTEXTW *w)
{
    MultiByteToWideChar(CP_ACP, 0, a->lcName, -1, w->lcName, LCNAMELEN);
    w->lcName[LCNAMELEN - 1] = 0;
    memcpy((char *)w + sizeof w->lcName, (const char *)a + sizeof a->lcName, sizeof(LOGCONTEXTW) - sizeof w->lcName);
}

static void ToA(const LOGCONTEXTW *w, LOGCONTEXTA *a)
{
    WideCharToMultiByte(CP_ACP, 0, w->lcName, -1, a->lcName, LCNAMELEN, NULL, NULL);
    a->lcName[LCNAMELEN - 1] = 0;
    memcpy((char *)a + sizeof a->lcName, (const char *)w + sizeof w->lcName, sizeof(LOGCONTEXTA) - sizeof a->lcName);
}

static CTX *FromH(HCTX h)
{
    INT_PTR i = (INT_PTR)h - 1;
    if (i < 0 || i >= MAX_CTX || !g_ctx[i].used) return NULL;
    return &g_ctx[i];
}

static HCTX ToH(CTX *c) { return (HCTX)(INT_PTR)(c - g_ctx + 1); }

/* From tablet units to context output units (a negative extent flips the axis). */
static LONG Scale(LONG v, LONG inOrg, LONG inExt, LONG outOrg, LONG outExt)
{
    double t;
    if (inExt == 0) return outOrg;
    t = (double)(v - inOrg) / (double)inExt;
    if (inExt < 0) t = 1.0 - (double)(v - inOrg) / (double)(-inExt);
    if (t < 0) t = 0; else if (t > 1) t = 1;
    if (outExt >= 0) return outOrg + (LONG)(t * outExt + 0.5);
    return outOrg + (LONG)((1.0 - t) * (-outExt) + 0.5);
}

static void Post(CTX *c, UINT off, WPARAM w, LPARAM l)
{
    if ((c->lc.lcOptions & CXO_MESSAGES) && c->hwnd) PostMessageW(c->hwnd, c->lc.lcMsgBase + off, w, l);
}

/* ---------- Shared memory reader ---------- */

/* Take a free slot if we are not in the consumer table. The reader calls this on every wake: if the host crashes and
 * restarts, or wipes the table, our event is signaled again after at most one timeout (500 ms). (Registration used to
 * happen only on first attach; after a wipe packets arrived only on the 500 ms timeouts, in bursts.) */
static void Register(void)
{
    LONG me = (LONG)GetCurrentProcessId();
    int i;
    if (!g_shm) return;
    if (g_slot >= 0 && *(volatile LONG *)(g_shm + OFF_CONSUMERS + g_slot * 8) == me) return;
    g_slot = -1;
    for (i = 0; i < SHM_MAXCONS; i++)
        if (*(volatile LONG *)(g_shm + OFF_CONSUMERS + i * 8) == me) { g_slot = i; return; }
    for (i = 0; i < SHM_MAXCONS; i++)
        if (InterlockedCompareExchange((volatile LONG *)(g_shm + OFF_CONSUMERS + i * 8), me, 0) == 0) { g_slot = i; return; }
}

static BOOL Attach(void)
{
    WCHAR evName[160];
    if (g_shm && *(volatile UINT *)g_shm == SHM_MAGIC) { Register(); return TRUE; }
    {
        WCHAR v[8];
        if (!GetEnvironmentVariableW(L"DIVIT_WINTAB_MAP", g_mapName, 128) || !g_mapName[0]) lstrcpynW(g_mapName, SHM_NAME, 128);
        g_always = GetEnvironmentVariableW(L"DIVIT_WINTAB_ALWAYS", v, 8) && v[0] == L'1';
    }
    if (g_shm) { UnmapViewOfFile((LPCVOID)g_shm); g_shm = NULL; }
    if (g_map) { CloseHandle(g_map); g_map = NULL; }
    g_slot = -1;
    g_map = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, g_mapName);
    if (!g_map) return FALSE;
    g_shm = (volatile unsigned char *)MapViewOfFile(g_map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, SHM_SIZE);
    if (!g_shm || *(volatile UINT *)g_shm != SHM_MAGIC) return FALSE;

    if (!g_event) {
        swprintf(evName, 160, L"%s.%lu", g_mapName, GetCurrentProcessId());
        g_event = CreateEventW(NULL, FALSE, FALSE, evName);
    }
    Register();
    return TRUE;
}

static void Detach(void)
{
    if (g_shm && g_slot >= 0)
        InterlockedCompareExchange((volatile LONG *)(g_shm + OFF_CONSUMERS + g_slot * 8), 0, (LONG)GetCurrentProcessId());
    g_slot = -1;
}

/* Is this process in the foreground? Like real drivers we deliver packets only to the active app
 * (otherwise a background Krita would also draw while the user draws in another app). */
static BOOL ForegroundIsOurs(void)
{
    DWORD pid = 0;
    HWND fg;
    if (g_always) return TRUE;
    fg = GetForegroundWindow();
    if (!fg) return FALSE;
    GetWindowThreadProcessId(fg, &pid);
    return pid == GetCurrentProcessId();
}

static void Enqueue(CTX *c, const PKT *p)
{
    int idx;
    if (c->qcap <= 0) return;
    if (c->qcount == c->qcap) {          /* full: drop the oldest, set the error flag */
        c->qhead = (c->qhead + 1) % c->qcap; c->qcount--;
    }
    idx = (c->qhead + c->qcount) % c->qcap;
    c->q[idx] = *p;
    if (c->qcount + 1 == c->qcap) c->q[idx].status |= TPS_QUEUE_ERR;
    c->qcount++;
}

/* Turn one host sample into a WinTab packet for every open context. */
static void Deliver(const volatile unsigned char *src, BOOL foreground)
{
    UINT time = *(volatile UINT *)(src + 4);
    float nx = *(volatile float *)(src + 8), ny = *(volatile float *)(src + 12), pr = *(volatile float *)(src + 16);
    short tx = *(volatile short *)(src + 20), ty = *(volatile short *)(src + 22);
    UINT fl = *(volatile unsigned short *)(src + 24);
    BOOL inRange = (fl & F_INRANGE) != 0, contact = (fl & F_CONTACT) != 0;
    UINT cursor = (fl & F_ERASER) ? CSR_ERASER : CSR_PEN;
    LONG X = (LONG)(nx * TAB_MAX + 0.5f), Y = (LONG)((1.0f - ny) * TAB_MAX + 0.5f);   /* tablet space: y up */
    double tanX = tan(tx * 3.14159265358979 / 180.0), tanY = tan(ty * 3.14159265358979 / 180.0);
    double tilt = atan(sqrt(tanX * tanX + tanY * tanY)) * 180.0 / 3.14159265358979;   /* deviation from vertical */
    double az = atan2(tanX, -tanY) * 180.0 / 3.14159265358979;                          /* verified with an S Pen in Krita (WinTab), IN-10 */
    int i;
    if (az < 0) az += 360.0;

    for (i = 0; i < MAX_CTX; i++) {
        CTX *c = &g_ctx[i];
        PKT p;
        WTPKT changed = 0;
        BOOL newProx;
        if (!c->used || !c->enabled) continue;
        newProx = inRange && foreground;

        ZeroMemory(&p, sizeof p);
        p.time = time;
        p.cursor = cursor;
        p.status = (newProx ? 0 : TPS_PROXIMITY) | (cursor == CSR_ERASER ? TPS_INVERT : 0);
        p.buttons = (contact ? 1u : 0u) | ((fl & F_BARREL) ? 2u : 0u);
        p.x = Scale(X, c->lc.lcInOrgX, c->lc.lcInExtX, c->lc.lcOutOrgX, c->lc.lcOutExtX);
        p.y = Scale(Y, c->lc.lcInOrgY, c->lc.lcInExtY, c->lc.lcOutOrgY, c->lc.lcOutExtY);
        p.npress = contact ? (UINT)(pr * PRESS_MAX + 0.5f) : 0;
        p.ori.orAzimuth = (int)(az * 10.0 + 0.5) % 3600;
        p.ori.orAltitude = (int)((90.0 - tilt) * 10.0 + 0.5);
        if (cursor == CSR_ERASER) p.ori.orAltitude = -p.ori.orAltitude;   /* WinTab: inverted tip = negative altitude */

        /* Proximity: entering/leaving the context */
        if (newProx != c->inProx) {
            c->inProx = newProx;
            Post(c, 5, (WPARAM)ToH(c), MAKELPARAM(newProx ? 1 : 0, newProx ? 1 : 0));
        }
        if (!newProx && !(c->haveLast && !(c->last.status & TPS_PROXIMITY))) continue;   /* do not repeat packets while out of range */

        if (!c->haveLast || p.status != c->last.status) changed |= PK_STATUS;
        if (!c->haveLast || p.cursor != c->last.cursor) changed |= PK_CURSOR;
        if (!c->haveLast || p.buttons != c->last.buttons) changed |= PK_BUTTONS;
        if (!c->haveLast || p.x != c->last.x) changed |= PK_X;
        if (!c->haveLast || p.y != c->last.y) changed |= PK_Y;
        if (!c->haveLast || p.npress != c->last.npress) changed |= PK_NORMAL_PRESSURE;
        if (!c->haveLast || memcmp(&p.ori, &c->last.ori, sizeof p.ori)) changed |= PK_ORIENTATION;
        changed |= PK_TIME | PK_SERIAL_NUMBER;
        p.changed = changed;

        /* Move mask: produce a packet only if a field of interest changed (button events always). */
        if (!(changed & (c->lc.lcMoveMask | PK_BUTTONS | PK_STATUS | PK_CURSOR))) continue;

        if (cursor != c->curCursor) {
            c->curCursor = cursor;
            if (c->lc.lcOptions & CXO_CSRMESSAGES) Post(c, 7, c->serial + 1, (LPARAM)ToH(c));
        }

        p.serial = ++c->serial;
        Enqueue(c, &p);
        c->last = p; c->haveLast = TRUE;
        Post(c, 0, p.serial, (LPARAM)ToH(c));
    }
}

static DWORD WINAPI Reader(LPVOID unused)
{
    DWORD readIndex = 0;   /* unsigned: wraps at 2^32 together with the host's writeIndex */
    UINT gen = 0;
    BOOL attached = FALSE;
    (void)unused;
    for (;;) {
        HANDLE h[2];
        DWORD r;
        if (!attached) {
            EnterCriticalSection(&g_cs);
            attached = Attach();
            if (attached) {   /* skip old packets */
                gen = *(volatile UINT *)(g_shm + OFF_GENERATION);
                readIndex = *(volatile DWORD *)(g_shm + OFF_WRITEINDEX);
            }
            LeaveCriticalSection(&g_cs);
        }
        h[0] = g_stop; h[1] = g_event ? g_event : g_stop;
        r = WaitForMultipleObjects(2, h, FALSE, attached ? 500 : 1000);
        if (r == WAIT_OBJECT_0) break;
        if (!attached) continue;

        EnterCriticalSection(&g_cs);
        if (*(volatile UINT *)g_shm != SHM_MAGIC) {
            attached = FALSE;                         /* host closed: wait to reattach */
        } else {
            DWORD w;
            BOOL fg;
            Register();   /* re-register if our entry was wiped (host restarted) */
            w = *(volatile DWORD *)(g_shm + OFF_WRITEINDEX);
            MemoryBarrier();
            if (*(volatile UINT *)(g_shm + OFF_GENERATION) != gen) {   /* the host restarted: its index began at 0 */
                gen = *(volatile UINT *)(g_shm + OFF_GENERATION);
                readIndex = 0;
            }
            if (w - readIndex > SHM_RING) readIndex = w - SHM_RING;    /* fell too far behind (unsigned distance) */
            fg = ForegroundIsOurs();
            SysRect();
            while (readIndex != w) {
                Deliver(g_shm + OFF_RING + (readIndex & (SHM_RING - 1)) * SHM_PKT, fg);
                readIndex++;
            }
        }
        LeaveCriticalSection(&g_cs);
    }
    return 0;
}

static void StartReader(void)
{
    HMODULE self;
    if (InterlockedCompareExchange(&g_threadStarted, 1, 0) != 0) return;
    /* If the DLL were unloaded (FreeLibrary) while the reader thread runs, the app would crash.
     * Pin the DLL until process exit: the usual approach for WinTab providers. */
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)(void *)StartReader, &self);
    g_stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_thread = CreateThread(NULL, 0, Reader, NULL, 0, NULL);
    if (g_thread) SetThreadPriority(g_thread, THREAD_PRIORITY_HIGHEST);
}

/* ---------- Packet layout (in lcPktData order) ---------- */

static int Packed(WTPKT data)
{
    int n = 0;
    if (data & PK_CONTEXT) n += sizeof(HCTX);
    if (data & PK_STATUS) n += sizeof(UINT);
    if (data & PK_TIME) n += sizeof(DWORD);
    if (data & PK_CHANGED) n += sizeof(WTPKT);
    if (data & PK_SERIAL_NUMBER) n += sizeof(UINT);
    if (data & PK_CURSOR) n += sizeof(UINT);
    if (data & PK_BUTTONS) n += sizeof(DWORD);
    if (data & PK_X) n += sizeof(LONG);
    if (data & PK_Y) n += sizeof(LONG);
    if (data & PK_Z) n += sizeof(LONG);
    if (data & PK_NORMAL_PRESSURE) n += sizeof(UINT);
    if (data & PK_TANGENT_PRESSURE) n += sizeof(UINT);
    if (data & PK_ORIENTATION) n += sizeof(ORIENTATION);
    if (data & PK_ROTATION) n += sizeof(ROTATION);
    return n;
}

#define PUT(T, v) do { T tmp_ = (T)(v); memcpy(o, &tmp_, sizeof(T)); o += sizeof(T); } while (0)

/* Write the internal packet in the app's requested layout. prev: the previously delivered packet, for relative mode. */
static void Write(CTX *c, const PKT *p, const PKT *prev, unsigned char *o)
{
    WTPKT d = c->lc.lcPktData, m = c->lc.lcPktMode;
    if (d & PK_CONTEXT) PUT(HCTX, ToH(c));
    if (d & PK_STATUS) PUT(UINT, p->status);
    if (d & PK_TIME) PUT(DWORD, p->time);
    if (d & PK_CHANGED) PUT(WTPKT, p->changed);
    if (d & PK_SERIAL_NUMBER) PUT(UINT, p->serial);
    if (d & PK_CURSOR) PUT(UINT, p->cursor);
    if (d & PK_BUTTONS) {
        if (m & PK_BUTTONS) {   /* relative: LOWORD button number, HIWORD TBN_* */
            DWORD was = prev ? prev->buttons : 0, diff = was ^ p->buttons, out = 0;
            int b;
            for (b = 0; b < 32; b++) if (diff & (1u << b)) { out = MAKELONG(b, (p->buttons & (1u << b)) ? TBN_DOWN : TBN_UP); break; }
            PUT(DWORD, out);
        } else PUT(DWORD, p->buttons);
    }
    if (d & PK_X) PUT(LONG, (m & PK_X) && prev ? p->x - prev->x : p->x);
    if (d & PK_Y) PUT(LONG, (m & PK_Y) && prev ? p->y - prev->y : p->y);
    if (d & PK_Z) PUT(LONG, 0);
    if (d & PK_NORMAL_PRESSURE) PUT(UINT, (m & PK_NORMAL_PRESSURE) && prev ? p->npress - prev->npress : p->npress);
    if (d & PK_TANGENT_PRESSURE) PUT(UINT, 0);
    if (d & PK_ORIENTATION) { memcpy(o, &p->ori, sizeof(ORIENTATION)); o += sizeof(ORIENTATION); }
    if (d & PK_ROTATION) { ROTATION r = { 0, 0, 0 }; memcpy(o, &r, sizeof r); o += sizeof r; }
}

/* ---------- WTInfo ---------- */

static UINT CopyOut(LPVOID out, const void *src, UINT n) { if (out) memcpy(out, src, n); return n; }

static UINT Str(LPVOID out, const WCHAR *s, BOOL wide)
{
    if (wide) return CopyOut(out, s, (UINT)(wcslen(s) + 1) * sizeof(WCHAR));
    else {
        char buf[128];
        int n = WideCharToMultiByte(CP_ACP, 0, s, -1, buf, sizeof buf, NULL, NULL);
        return CopyOut(out, buf, (UINT)n);
    }
}

static UINT Axis(LPVOID out, LONG mn, LONG mx, UINT units, double res)
{
    AXIS a; a.axMin = mn; a.axMax = mx; a.axUnits = units; a.axResolution = (FIX32)(res * 65536.0);
    return CopyOut(out, &a, sizeof a);
}

static UINT Info(UINT cat, UINT idx, LPVOID out, BOOL wide)
{
    UINT u; DWORD dw; BYTE b; WORD w;
    switch (cat) {
    case 0:
        return 512;   /* buffer needed for the largest category (large enough) */

    case WTI_INTERFACE:
        switch (idx) {
        case IFC_WINTABID: return Str(out, L"Divit WinTab (Samsung S Pen)", wide);
        case IFC_SPECVERSION: w = 0x0104; return CopyOut(out, &w, sizeof w);
        case IFC_IMPLVERSION: w = 0x0100; return CopyOut(out, &w, sizeof w);
        case IFC_NDEVICES: u = 1; return CopyOut(out, &u, sizeof u);
        case IFC_NCURSORS: u = NCURSORS; return CopyOut(out, &u, sizeof u);
        case IFC_NCONTEXTS: u = MAX_CTX; return CopyOut(out, &u, sizeof u);
        case IFC_CTXOPTIONS: u = CXO_SYSTEM | CXO_PEN | CXO_MESSAGES | CXO_CSRMESSAGES | CXO_MARGIN | CXO_MGNINSIDE; return CopyOut(out, &u, sizeof u);
        case IFC_CTXSAVESIZE: u = sizeof(LOGCONTEXTW); return CopyOut(out, &u, sizeof u);
        case IFC_NEXTENSIONS: case IFC_NMANAGERS: u = 0; return CopyOut(out, &u, sizeof u);
        }
        return 0;

    case WTI_STATUS:
        switch (idx) {
        case STA_CONTEXTS: { int i; u = 0; for (i = 0; i < MAX_CTX; i++) if (g_ctx[i].used) u++; return CopyOut(out, &u, sizeof u); }
        case STA_SYSCTXS: case STA_MANAGERS: u = 0; return CopyOut(out, &u, sizeof u);
        case STA_PKTRATE: u = 360; return CopyOut(out, &u, sizeof u);
        case STA_PKTDATA: dw = SUPPORTED_PKTDATA; return CopyOut(out, &dw, sizeof dw);
        case STA_SYSTEM: u = TRUE; return CopyOut(out, &u, sizeof u);
        case STA_BUTTONUSE: case STA_SYSBTNUSE: dw = 0x3; return CopyOut(out, &dw, sizeof dw);
        }
        return 0;

    case WTI_DEFCONTEXT: case WTI_DEFSYSCTX: case WTI_DDCTXS: case WTI_DSCTXS: {
        LOGCONTEXTW lc;
        DefaultContext(&lc, cat == WTI_DEFSYSCTX || cat == WTI_DSCTXS);
        if (idx == 0) {
            if (wide) return CopyOut(out, &lc, sizeof lc);
            else { LOGCONTEXTA a; ToA(&lc, &a); return CopyOut(out, &a, sizeof a); }
        }
        /* Single field: CTX_* indexes start at 1; after the name every field is 4 bytes, in order. */
        if (idx == 1) return wide ? CopyOut(out, lc.lcName, sizeof lc.lcName) : (UINT)(WideCharToMultiByte(CP_ACP, 0, lc.lcName, -1, out, out ? LCNAMELEN : 0, NULL, NULL));
        if (idx >= 2 && idx <= 2 + (sizeof(LOGCONTEXTW) - sizeof lc.lcName) / 4 - 1)
            return CopyOut(out, (char *)&lc + sizeof lc.lcName + (idx - 2) * 4, 4);
        return 0;
    }

    case WTI_DEVICES:
        switch (idx) {
        case DVC_NAME: return Str(out, L"Divit (Samsung S Pen)", wide);
        case DVC_HARDWARE: u = HWC_HARDPROX | HWC_PHYSID_CURSORS; return CopyOut(out, &u, sizeof u);
        case DVC_NCSRTYPES: u = NCURSORS; return CopyOut(out, &u, sizeof u);
        case DVC_FIRSTCSR: u = 0; return CopyOut(out, &u, sizeof u);
        case DVC_PKTRATE: u = 360; return CopyOut(out, &u, sizeof u);
        case DVC_PKTDATA: dw = SUPPORTED_PKTDATA; return CopyOut(out, &dw, sizeof dw);
        case DVC_PKTMODE: dw = 0; return CopyOut(out, &dw, sizeof dw);
        case DVC_CSRDATA: dw = 0; return CopyOut(out, &dw, sizeof dw);
        case DVC_XMARGIN: case DVC_YMARGIN: case DVC_ZMARGIN: { int z = 0; return CopyOut(out, &z, sizeof z); }
        case DVC_X: return Axis(out, 0, TAB_MAX, TU_CENTIMETERS, (TAB_MAX + 1) / SurfaceCm(OFF_SURFACE_W, TAB_W_CM));
        case DVC_Y: return Axis(out, 0, TAB_MAX, TU_CENTIMETERS, (TAB_MAX + 1) / SurfaceCm(OFF_SURFACE_H, TAB_H_CM));
        case DVC_Z: return Axis(out, 0, 0, TU_NONE, 0);
        case DVC_NPRESSURE: return Axis(out, 0, PRESS_MAX, TU_NONE, 0);
        case DVC_TPRESSURE: return Axis(out, 0, 0, TU_NONE, 0);
        case DVC_ORIENTATION: {
            AXIS a[3] = { { 0, 3599, TU_CIRCLE, 3600u << 16 }, { -900, 900, TU_CIRCLE, 3600u << 16 }, { 0, 0, TU_CIRCLE, 3600u << 16 } };
            return CopyOut(out, a, sizeof a);
        }
        case DVC_ROTATION: { AXIS a[3]; ZeroMemory(a, sizeof a); return CopyOut(out, a, sizeof a); }
        case DVC_PNPID: return Str(out, L"DIVIT0001", wide);
        }
        return 0;

    default:
        if (cat >= WTI_CURSORS && cat < WTI_CURSORS + NCURSORS) {
            UINT csr = cat - WTI_CURSORS;
            switch (idx) {
            case CSR_NAME: return Str(out, csr == CSR_PEN ? L"Divit pen" : csr == CSR_ERASER ? L"Divit eraser" : L"Divit (unused)", wide);
            case CSR_ACTIVE: u = csr != 0; return CopyOut(out, &u, sizeof u);
            case CSR_PKTDATA: dw = SUPPORTED_PKTDATA; return CopyOut(out, &dw, sizeof dw);
            case CSR_BUTTONS: b = 2; return CopyOut(out, &b, sizeof b);
            case CSR_BUTTONBITS: b = 2; return CopyOut(out, &b, sizeof b);
            case CSR_BTNNAMES: {
                static const WCHAR names[] = L"Tip\0Side button\0";
                if (wide) return CopyOut(out, names, sizeof names);
                else { char buf[64]; int n = WideCharToMultiByte(CP_ACP, 0, names, sizeof names / sizeof(WCHAR), buf, sizeof buf, NULL, NULL); return CopyOut(out, buf, (UINT)n); }
            }
            case CSR_BUTTONMAP: { BYTE m[32]; int i; for (i = 0; i < 32; i++) m[i] = (BYTE)i; return CopyOut(out, m, sizeof m); }
            case CSR_SYSBTNMAP: { BYTE m[32]; ZeroMemory(m, sizeof m); return CopyOut(out, m, sizeof m); }
            case CSR_NPBUTTON: b = 0; return CopyOut(out, &b, sizeof b);
            case CSR_NPBTNMARKS: { UINT mk[2] = { 0, 1 }; return CopyOut(out, mk, sizeof mk); }
            case CSR_NPRESPONSE: { UINT r[2] = { 0, PRESS_MAX }; return CopyOut(out, r, sizeof r); }
            case CSR_TPBUTTON: b = 0xFF; return CopyOut(out, &b, sizeof b);
            case CSR_TPBTNMARKS: { UINT mk[2] = { 0, 0 }; return CopyOut(out, mk, sizeof mk); }
            case CSR_TPRESPONSE: return 0;
            case CSR_PHYSID: dw = 0x0D1F1700u + csr; return CopyOut(out, &dw, sizeof dw);
            case CSR_MODE: u = csr; return CopyOut(out, &u, sizeof u);
            case CSR_MINPKTDATA: dw = PK_X | PK_Y | PK_BUTTONS; return CopyOut(out, &dw, sizeof dw);
            case CSR_MINBUTTONS: b = 1; return CopyOut(out, &b, sizeof b);
            case CSR_CAPABILITIES: u = 0; return CopyOut(out, &u, sizeof u);
            case CSR_TYPE: u = csr == CSR_ERASER ? 0x080A : csr == CSR_PEN ? 0x0802 : 0x0006; return CopyOut(out, &u, sizeof u);
            }
        }
        return 0;
    }
}

/* ---------- Exported API ---------- */

UINT WINAPI WTInfoW(UINT cat, UINT idx, LPVOID out) { UINT r; EnterCriticalSection(&g_cs); r = Info(cat, idx, out, TRUE); LeaveCriticalSection(&g_cs); return r; }
UINT WINAPI WTInfoA(UINT cat, UINT idx, LPVOID out) { UINT r; EnterCriticalSection(&g_cs); r = Info(cat, idx, out, FALSE); LeaveCriticalSection(&g_cs); return r; }

static HCTX Open(HWND hwnd, const LOGCONTEXTW *lc, BOOL enable)
{
    int i;
    CTX *c = NULL;
    EnterCriticalSection(&g_cs);
    for (i = 0; i < MAX_CTX; i++) if (!g_ctx[i].used) { c = &g_ctx[i]; break; }
    if (!c) { LeaveCriticalSection(&g_cs); return NULL; }
    ZeroMemory(c, sizeof *c);
    c->used = TRUE;
    c->enabled = enable;
    c->hwnd = hwnd;
    c->lc = *lc;
    c->lc.lcPktData &= SUPPORTED_PKTDATA;
    if (c->lc.lcMsgBase == 0) c->lc.lcMsgBase = WT_DEFBASE;
    c->lc.lcStatus = enable ? CXS_ONTOP : CXS_DISABLED;
    c->qcap = 8;
    c->q = (PKT *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(PKT) * c->qcap);
    c->curCursor = CSR_PEN;
    Post(c, 1, (WPARAM)ToH(c), c->lc.lcStatus);
    LeaveCriticalSection(&g_cs);
    StartReader();
    return ToH(c);
}

HCTX WINAPI WTOpenW(HWND hwnd, LPLOGCONTEXTW lc, BOOL enable) { return lc ? Open(hwnd, lc, enable) : NULL; }
HCTX WINAPI WTOpenA(HWND hwnd, LPLOGCONTEXTA lc, BOOL enable) { LOGCONTEXTW w; if (!lc) return NULL; ToW(lc, &w); return Open(hwnd, &w, enable); }

BOOL WINAPI WTClose(HCTX h)
{
    CTX *c;
    EnterCriticalSection(&g_cs);
    c = FromH(h);
    if (c) {
        Post(c, 2, (WPARAM)h, c->lc.lcStatus);
        if (c->q) HeapFree(GetProcessHeap(), 0, c->q);
        ZeroMemory(c, sizeof *c);
    }
    LeaveCriticalSection(&g_cs);
    return c != NULL;
}

/* Remove/copy the oldest n packets from the queue. */
static int Take(CTX *c, int max, LPVOID out, BOOL remove)
{
    int n = 0, size = Packed(c->lc.lcPktData);
    unsigned char *o = (unsigned char *)out;
    const PKT *prev = NULL;
    while (n < max && n < c->qcount) {
        const PKT *p = &c->q[(c->qhead + n) % c->qcap];
        if (o) { Write(c, p, prev, o); o += size; }
        prev = p;
        n++;
    }
    if (remove) { c->qhead = (c->qhead + n) % (c->qcap ? c->qcap : 1); c->qcount -= n; }
    return n;
}

int WINAPI WTPacketsGet(HCTX h, int max, LPVOID out)
{
    int n = 0; CTX *c;
    EnterCriticalSection(&g_cs);
    if ((c = FromH(h)) != NULL) n = Take(c, max, out, TRUE);
    LeaveCriticalSection(&g_cs);
    return n;
}

int WINAPI WTPacketsPeek(HCTX h, int max, LPVOID out)
{
    int n = 0; CTX *c;
    EnterCriticalSection(&g_cs);
    if ((c = FromH(h)) != NULL) n = Take(c, max, out, FALSE);
    LeaveCriticalSection(&g_cs);
    return n;
}

/* Return the packet with the given serial; remove it and older ones from the queue. */
BOOL WINAPI WTPacket(HCTX h, UINT serial, LPVOID out)
{
    BOOL found = FALSE; CTX *c; int i;
    EnterCriticalSection(&g_cs);
    if ((c = FromH(h)) != NULL) {
        for (i = 0; i < c->qcount; i++) {
            const PKT *p = &c->q[(c->qhead + i) % c->qcap];
            if (p->serial == serial) {
                const PKT *prev = i > 0 ? &c->q[(c->qhead + i - 1) % c->qcap] : NULL;
                if (out) Write(c, p, prev, (unsigned char *)out);
                c->qhead = (c->qhead + i + 1) % c->qcap; c->qcount -= i + 1;
                found = TRUE;
                break;
            }
        }
    }
    LeaveCriticalSection(&g_cs);
    return found;
}

static int Range(HCTX h, UINT begin, UINT end, int max, LPVOID out, LPINT count, BOOL remove)
{
    int n = 0, first = -1, last = -1, i, size; CTX *c;
    unsigned char *o = (unsigned char *)out;
    EnterCriticalSection(&g_cs);
    if ((c = FromH(h)) != NULL) {
        size = Packed(c->lc.lcPktData);
        for (i = 0; i < c->qcount; i++) {
            UINT s = c->q[(c->qhead + i) % c->qcap].serial;
            if (s == begin) first = i;
            if (s == end) { last = i; break; }
        }
        if (first >= 0 && last >= first) {
            for (i = first; i <= last && n < max; i++, n++) {
                const PKT *p = &c->q[(c->qhead + i) % c->qcap];
                if (o) { Write(c, p, i > 0 ? &c->q[(c->qhead + i - 1) % c->qcap] : NULL, o); o += size; }
            }
            if (remove) { c->qhead = (c->qhead + first + n) % c->qcap; c->qcount -= first + n; }
        }
    }
    LeaveCriticalSection(&g_cs);
    if (count) *count = n;
    return n;
}

int WINAPI WTDataGet(HCTX h, UINT b, UINT e, int max, LPVOID out, LPINT n) { return Range(h, b, e, max, out, n, TRUE); }
int WINAPI WTDataPeek(HCTX h, UINT b, UINT e, int max, LPVOID out, LPINT n) { return Range(h, b, e, max, out, n, FALSE); }

BOOL WINAPI WTQueuePacketsEx(HCTX h, UINT *oldest, UINT *newest)
{
    BOOL ok = FALSE; CTX *c;
    EnterCriticalSection(&g_cs);
    if ((c = FromH(h)) != NULL && c->qcount > 0) {
        if (oldest) *oldest = c->q[c->qhead].serial;
        if (newest) *newest = c->q[(c->qhead + c->qcount - 1) % c->qcap].serial;
        ok = TRUE;
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

int WINAPI WTQueueSizeGet(HCTX h) { int n = 0; CTX *c; EnterCriticalSection(&g_cs); if ((c = FromH(h)) != NULL) n = c->qcap; LeaveCriticalSection(&g_cs); return n; }

BOOL WINAPI WTQueueSizeSet(HCTX h, int size)
{
    BOOL ok = FALSE; CTX *c; PKT *q;
    if (size <= 0 || size > 4096) return FALSE;
    EnterCriticalSection(&g_cs);
    if ((c = FromH(h)) != NULL && (q = (PKT *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(PKT) * size)) != NULL) {
        if (c->q) HeapFree(GetProcessHeap(), 0, c->q);
        c->q = q; c->qcap = size; c->qhead = c->qcount = 0;   /* per spec: the queue is flushed */
        ok = TRUE;
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

BOOL WINAPI WTEnable(HCTX h, BOOL enable)
{
    CTX *c; BOOL ok;
    EnterCriticalSection(&g_cs);
    c = FromH(h); ok = c != NULL;
    if (c) {
        c->enabled = enable;
        c->lc.lcStatus = enable ? (c->lc.lcStatus & ~CXS_DISABLED) : (c->lc.lcStatus | CXS_DISABLED);
        if (!enable) { c->qhead = c->qcount = 0; c->inProx = FALSE; }
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

BOOL WINAPI WTOverlap(HCTX h, BOOL toTop)
{
    CTX *c; BOOL ok;
    EnterCriticalSection(&g_cs);
    c = FromH(h); ok = c != NULL;
    if (c) {
        c->lc.lcStatus = toTop ? (c->lc.lcStatus | CXS_ONTOP) : (c->lc.lcStatus & ~CXS_ONTOP);
        Post(c, 4, (WPARAM)h, c->lc.lcStatus);
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

BOOL WINAPI WTGetW(HCTX h, LPLOGCONTEXTW lc) { CTX *c; BOOL ok; EnterCriticalSection(&g_cs); c = FromH(h); ok = c && lc; if (ok) *lc = c->lc; LeaveCriticalSection(&g_cs); return ok; }
BOOL WINAPI WTGetA(HCTX h, LPLOGCONTEXTA lc) { CTX *c; BOOL ok; EnterCriticalSection(&g_cs); c = FromH(h); ok = c && lc; if (ok) ToA(&c->lc, lc); LeaveCriticalSection(&g_cs); return ok; }

static BOOL Set(HCTX h, const LOGCONTEXTW *lc)
{
    CTX *c; BOOL ok;
    EnterCriticalSection(&g_cs);
    c = FromH(h); ok = c && lc;
    if (ok) {
        UINT status = c->lc.lcStatus;
        c->lc = *lc;
        c->lc.lcStatus = status;
        c->lc.lcPktData &= SUPPORTED_PKTDATA;
        if (c->lc.lcMsgBase == 0) c->lc.lcMsgBase = WT_DEFBASE;
        c->qhead = c->qcount = 0; c->haveLast = FALSE;
        Post(c, 3, (WPARAM)h, status);
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

BOOL WINAPI WTSetW(HCTX h, LPLOGCONTEXTW lc) { return Set(h, lc); }
BOOL WINAPI WTSetA(HCTX h, LPLOGCONTEXTA lc) { LOGCONTEXTW w; if (!lc) return FALSE; ToW(lc, &w); return Set(h, &w); }

BOOL WINAPI WTConfig(HCTX h, HWND hwnd) { (void)h; (void)hwnd; return FALSE; }
BOOL WINAPI WTExtGet(HCTX h, UINT ext, LPVOID data) { (void)h; (void)ext; (void)data; return FALSE; }
BOOL WINAPI WTExtSet(HCTX h, UINT ext, LPVOID data) { (void)h; (void)ext; (void)data; return FALSE; }
BOOL WINAPI WTSave(HCTX h, LPVOID buf) { return WTGetW(h, (LPLOGCONTEXTW)buf); }
HCTX WINAPI WTRestore(HWND hwnd, LPVOID buf, BOOL enable) { return buf ? Open(hwnd, (const LOGCONTEXTW *)buf, enable) : NULL; }

/* Manager API: not supported. Empty bodies so that apps importing them still load. */
HMGR WINAPI WTMgrOpen(HWND hwnd, UINT base) { (void)hwnd; (void)base; return NULL; }
BOOL WINAPI WTMgrClose(HMGR m) { (void)m; return FALSE; }
BOOL WINAPI WTMgrContextEnum(HMGR m, FARPROC f, LPARAM l) { (void)m; (void)f; (void)l; return FALSE; }
HWND WINAPI WTMgrContextOwner(HMGR m, HCTX h) { (void)m; (void)h; return NULL; }
HCTX WINAPI WTMgrDefContext(HMGR m, BOOL sys) { (void)m; (void)sys; return NULL; }
HCTX WINAPI WTMgrDefContextEx(HMGR m, UINT dev, BOOL sys) { (void)m; (void)dev; (void)sys; return NULL; }
UINT WINAPI WTMgrDeviceConfig(HMGR m, UINT dev, HWND hwnd) { (void)m; (void)dev; (void)hwnd; return 0; }
BOOL WINAPI WTMgrExt(HMGR m, UINT ext, LPVOID d) { (void)m; (void)ext; (void)d; return FALSE; }
BOOL WINAPI WTMgrCsrEnable(HMGR m, UINT c, BOOL e) { (void)m; (void)c; (void)e; return FALSE; }
BOOL WINAPI WTMgrCsrButtonMap(HMGR m, UINT c, LPBYTE a, LPBYTE b) { (void)m; (void)c; (void)a; (void)b; return FALSE; }
BOOL WINAPI WTMgrCsrPressureBtnMarks(HMGR m, UINT c, DWORD a, DWORD b) { (void)m; (void)c; (void)a; (void)b; return FALSE; }
BOOL WINAPI WTMgrCsrPressureResponse(HMGR m, UINT c, UINT *a, UINT *b) { (void)m; (void)c; (void)a; (void)b; return FALSE; }
BOOL WINAPI WTMgrCsrExt(HMGR m, UINT c, UINT e, LPVOID d) { (void)m; (void)c; (void)e; (void)d; return FALSE; }

/* The Divit installer's "this is our DLL" check: another driver's wintab32.dll is never overwritten or deleted. */
UINT WINAPI DivitWinTabVersion(void) { return 1; }

/* ---------- DLL lifecycle ---------- */

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)inst;
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(inst);
        InitializeCriticalSection(&g_cs);
        break;
    case DLL_PROCESS_DETACH:
        if (g_stop) SetEvent(g_stop);
        /* If the process is exiting (reserved != NULL) the other threads are already stopped; leave without taking the lock. */
        if (reserved == NULL) {
            if (g_thread) { WaitForSingleObject(g_thread, 200); CloseHandle(g_thread); }
            Detach();
            if (g_shm) UnmapViewOfFile((LPCVOID)g_shm);
            if (g_map) CloseHandle(g_map);
            if (g_event) CloseHandle(g_event);
            DeleteCriticalSection(&g_cs);
        } else {
            Detach();
        }
        break;
    }
    return TRUE;
}
