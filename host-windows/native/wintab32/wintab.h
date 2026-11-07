/* SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Divit contributors
 *
 * WinTab 1.4 types and constants — rewritten from the published specification (LCS/Telegraphics, Wacom).
 * Only the part Divit implements.
 */
#pragma once
#include <windows.h>

typedef DWORD WTPKT;
typedef DWORD FIX32;
DECLARE_HANDLE(HCTX);
DECLARE_HANDLE(HMGR);

/* Messages */
#define WT_DEFBASE      0x7FF0
#define WT_MAXOFFSET    0xF
#define WT_PACKET(b)    ((b) + 0)
#define WT_CTXOPEN(b)   ((b) + 1)
#define WT_CTXCLOSE(b)  ((b) + 2)
#define WT_CTXUPDATE(b) ((b) + 3)
#define WT_CTXOVERLAP(b)((b) + 4)
#define WT_PROXIMITY(b) ((b) + 5)
#define WT_INFOCHANGE(b)((b) + 6)
#define WT_CSRCHANGE(b) ((b) + 7)

/* Packet fields (PK_*) — they appear in this order inside a packet */
#define PK_CONTEXT          0x0001
#define PK_STATUS           0x0002
#define PK_TIME             0x0004
#define PK_CHANGED          0x0008
#define PK_SERIAL_NUMBER    0x0010
#define PK_CURSOR           0x0020
#define PK_BUTTONS          0x0040
#define PK_X                0x0080
#define PK_Y                0x0100
#define PK_Z                0x0200
#define PK_NORMAL_PRESSURE  0x0400
#define PK_TANGENT_PRESSURE 0x0800
#define PK_ORIENTATION      0x1000
#define PK_ROTATION         0x2000

/* Information categories */
#define WTI_INTERFACE   1
#define   IFC_WINTABID      1
#define   IFC_SPECVERSION   2
#define   IFC_IMPLVERSION   3
#define   IFC_NDEVICES      4
#define   IFC_NCURSORS      5
#define   IFC_NCONTEXTS     6
#define   IFC_CTXOPTIONS    7
#define   IFC_CTXSAVESIZE   8
#define   IFC_NEXTENSIONS   9
#define   IFC_NMANAGERS     10
#define WTI_STATUS      2
#define   STA_CONTEXTS      1
#define   STA_SYSCTXS       2
#define   STA_PKTRATE       3
#define   STA_PKTDATA       4
#define   STA_MANAGERS      5
#define   STA_SYSTEM        6
#define   STA_BUTTONUSE     7
#define   STA_SYSBTNUSE     8
#define WTI_DEFCONTEXT  3
#define WTI_DEFSYSCTX   4
#define WTI_DDCTXS      400
#define WTI_DSCTXS      500
#define WTI_DEVICES     100
#define   DVC_NAME          1
#define   DVC_HARDWARE      2
#define   DVC_NCSRTYPES     3
#define   DVC_FIRSTCSR      4
#define   DVC_PKTRATE       5
#define   DVC_PKTDATA       6
#define   DVC_PKTMODE       7
#define   DVC_CSRDATA       8
#define   DVC_XMARGIN       9
#define   DVC_YMARGIN       10
#define   DVC_ZMARGIN       11
#define   DVC_X             12
#define   DVC_Y             13
#define   DVC_Z             14
#define   DVC_NPRESSURE     15
#define   DVC_TPRESSURE     16
#define   DVC_ORIENTATION   17
#define   DVC_ROTATION      18
#define   DVC_PNPID         19
#define WTI_CURSORS     200
#define   CSR_NAME          1
#define   CSR_ACTIVE        2
#define   CSR_PKTDATA       3
#define   CSR_BUTTONS       4
#define   CSR_BUTTONBITS    5
#define   CSR_BTNNAMES      6
#define   CSR_BUTTONMAP     7
#define   CSR_SYSBTNMAP     8
#define   CSR_NPBUTTON      9
#define   CSR_NPBTNMARKS    10
#define   CSR_NPRESPONSE    11
#define   CSR_TPBUTTON      12
#define   CSR_TPBTNMARKS    13
#define   CSR_TPRESPONSE    14
#define   CSR_PHYSID        15
#define   CSR_MODE          16
#define   CSR_MINPKTDATA    17
#define   CSR_MINBUTTONS    18
#define   CSR_CAPABILITIES  19
#define   CSR_TYPE          20
#define WTI_EXTENSIONS  300

/* Context options and states */
#define CXO_SYSTEM      0x0001
#define CXO_PEN         0x0002
#define CXO_MESSAGES    0x0004
#define CXO_CSRMESSAGES 0x0008
#define CXO_MGNINSIDE   0x4000
#define CXO_MARGIN      0x8000
#define CXS_DISABLED    0x0001
#define CXS_OBSCURED    0x0002
#define CXS_ONTOP       0x0004

/* Packet status */
#define TPS_PROXIMITY   0x0001
#define TPS_QUEUE_ERR   0x0002
#define TPS_MARGIN      0x0004
#define TPS_GRAB        0x0008
#define TPS_INVERT      0x0010

/* Relative button events */
#define TBN_NONE 0
#define TBN_UP   1
#define TBN_DOWN 2

/* Hardware capabilities */
#define HWC_INTEGRATED      0x0001
#define HWC_TOUCH           0x0002
#define HWC_HARDPROX        0x0004
#define HWC_PHYSID_CURSORS  0x0008

/* Units */
#define TU_NONE         0
#define TU_INCHES       1
#define TU_CENTIMETERS  2
#define TU_CIRCLE       3

typedef struct tagAXIS {
    LONG  axMin;
    LONG  axMax;
    UINT  axUnits;
    FIX32 axResolution;
} AXIS;

typedef struct tagORIENTATION { int orAzimuth; int orAltitude; int orTwist; } ORIENTATION;
typedef struct tagROTATION { int roPitch; int roRoll; int roYaw; } ROTATION;

#define LCNAMELEN 40

#define LOGCONTEXT_BODY \
    UINT  lcOptions; UINT lcStatus; UINT lcLocks; UINT lcMsgBase; UINT lcDevice; UINT lcPktRate; \
    WTPKT lcPktData; WTPKT lcPktMode; WTPKT lcMoveMask; \
    DWORD lcBtnDnMask; DWORD lcBtnUpMask; \
    LONG  lcInOrgX; LONG lcInOrgY; LONG lcInOrgZ; LONG lcInExtX; LONG lcInExtY; LONG lcInExtZ; \
    LONG  lcOutOrgX; LONG lcOutOrgY; LONG lcOutOrgZ; LONG lcOutExtX; LONG lcOutExtY; LONG lcOutExtZ; \
    FIX32 lcSensX; FIX32 lcSensY; FIX32 lcSensZ; \
    BOOL  lcSysMode; \
    int   lcSysOrgX; int lcSysOrgY; int lcSysExtX; int lcSysExtY; \
    FIX32 lcSysSensX; FIX32 lcSysSensY;

typedef struct tagLOGCONTEXTA { char  lcName[LCNAMELEN]; LOGCONTEXT_BODY } LOGCONTEXTA, *LPLOGCONTEXTA;
typedef struct tagLOGCONTEXTW { WCHAR lcName[LCNAMELEN]; LOGCONTEXT_BODY } LOGCONTEXTW, *LPLOGCONTEXTW;
