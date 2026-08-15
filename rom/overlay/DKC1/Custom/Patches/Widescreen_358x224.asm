; Donkey Kong Country gameplay widescreen patch for SuperZSNES.
;
; The default profile renders seven extra 8-pixel columns per side (368x224)
; and crops five guard pixels per side to 358x224. The optional 16:9 profile
; renders nine columns per side (400x224) and crops one guard pixel per side
; to 398x224. Both profiles retain DKC's original 128-pixel camera target and
; expand streaming, bounds, activation, sprite culling, bananas, and endpoint
; movement symmetrically.

if defined("Define_DKC1_Widescreen16x9")
!DKC1_WideMargin = $0048             ; 72 pixels / 9 tiles per side
!DKC1_WideInitialBackstep = $0190    ; 400 pixels / 50 columns
!DKC1_WideInitialColumnCount = $0032
!DKC1_WideAlternateBackstep = $0198  ; one extra 8-pixel column
!DKC1_WideAlternateColumnCount = $0033
else
!DKC1_WideMargin = $0038             ; 56 pixels / 7 tiles per side
!DKC1_WideInitialBackstep = $0170    ; 368 pixels / 46 columns
!DKC1_WideInitialColumnCount = $002E
!DKC1_WideAlternateBackstep = $0178  ; one extra 8-pixel column
!DKC1_WideAlternateColumnCount = $002F
endif

; Asar performs textual define expansion, so this base name deliberately does
; not end in "Span" (which would collide with the derived *Span defines).
!DKC1_WideTotalExtension = (!DKC1_WideMargin*2)
!DKC1_WideRightEdge = $0100+!DKC1_WideMargin
!DKC1_WideSpecialUpper = $0700-!DKC1_WideMargin
!DKC1_WideRowSecondBias = $0090-!DKC1_WideMargin
!DKC1_WideSpriteCull1Left = $0030+!DKC1_WideMargin
!DKC1_WideSpriteCull1Span = $0160+!DKC1_WideTotalExtension
!DKC1_WideSpriteCull2Left = $0058+!DKC1_WideMargin
!DKC1_WideSpriteCull2Span = $01B0+!DKC1_WideTotalExtension
!DKC1_WideObjectLeftSafety = $0020+!DKC1_WideMargin
!DKC1_WideObjectActivationSpan = $0140+!DKC1_WideTotalExtension
!DKC1_WideObjectRightPrefetch = $0120+!DKC1_WideMargin
!DKC1_WidePlayerLeftProbe = !DKC1_WideMargin-$0012
!DKC1_WidePlayerRightProbe = $00EE+!DKC1_WideMargin
!DKC1_Wide_EnableCameraPatches = !TRUE
!DKC1_Wide_EnableInitialFillPatches = !TRUE
!DKC1_Wide_EnableMovingTilemapHooks = !TRUE
!DKC1_Wide_EnableSpriteCullingPatches = !TRUE
!DKC1_Wide_EnableObjectActivationPatches = !TRUE
!DKC1_Wide_EnableType5ChildRetry = !TRUE
!DKC1_Wide_EnableBananaFormationCameraFix = !TRUE
!DKC1_Wide_EnableBananaFormationCoverage = !TRUE
!DKC1_Wide_EnablePlayerEndpointRangeFix = !TRUE
!DKC1_Wide_EnableKRoolArenaBoundsFix = !TRUE
!DKC1_Wide_EnableKRoolTilemapFillFix = !TRUE
!DKC1_Wide_EnableSynchronizedBananas = !FALSE

; ---------------------------------------------------------------------------
; Camera bounds

if !DKC1_Wide_EnableCameraPatches == !TRUE
org $809E32
	JSL.l DKC1_Wide_AdjustCameraBounds
	NOP
	NOP
	NOP
	NOP
	NOP
	NOP
	NOP

; Special camera initialization path with hard-coded bounds. This path starts
; its tile fill immediately, so set the activation marker before that loop.
org $80C501
	JSL.l DKC1_Wide_InitSpecialBounds
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA
endif

; ---------------------------------------------------------------------------
; Initial tilemap fill

if !DKC1_Wide_EnableInitialFillPatches == !TRUE
; Standard path: fill the complete internal render width. The loop must
; advance by exactly the same distance as the initial camera backstep, or level
; setup leaves the real camera shifted and exposes columns that were never
; initialized. The helper uses the full-width right edge during this loop, so
; every 8-pixel column is seeded and the final camera position is preserved.
org $809EC5
	dw !DKC1_WideInitialBackstep
org $809ED7
	dw !DKC1_WideInitialColumnCount

; Alternate path retains its original one extra column while preserving the
; same final-camera invariant with one additional 8-pixel column.
org $80C56F
	dw !DKC1_WideAlternateBackstep
org $80C57E
	dw !DKC1_WideAlternateColumnCount
endif

; Gang-Plank Galleon's boss entrance performs a second, private 65-column
; sweep after the normal level initializer.  That sweep deliberately fills
; every entry in the 64-column BG1 ring with stock +$0100 stream coordinates.
; It inherits the normal initializer's $1A5B=$0001 value, however, so the
; widescreen stream helper used to mistake it for another wide initializer
; and add the profile right edge.  The shifted 65-column sweep wrapped and
; replaced ring columns 0..6 with out-of-arena/map data.  Mark only this
; synchronous private loop, retain its stock selector, then restore $1A5B.
if !DKC1_Wide_EnableKRoolTilemapFillFix == !TRUE
org $809F70
	JSL.l DKC1_Wide_BeginKRoolTilemapFill
	NOP
	NOP

org $809F96
	JML.l DKC1_Wide_EndKRoolTilemapFill
endif

; ---------------------------------------------------------------------------
; Moving tilemap edges

; Each original selection block was 17 bytes. Replace it with one shared
; helper and preserve all following addresses.
; These routines are also used by DKC's boot/title tilemap construction.
; They remain opt-in until a reliable gameplay-state discriminator is known;
; forcing them globally prevents the intro from completing.
if !DKC1_Wide_EnableMovingTilemapHooks == !TRUE
org $818711
	JSL.l DKC1_Wide_GetStreamX
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA

org $818857
	JSL.l DKC1_Wide_GetStreamX
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA

org $8188BD
	JSL.l DKC1_Wide_GetStreamX
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA

org $818E06
	JSL.l DKC1_Wide_GetStreamX
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA

; Vertical scrolling rebuilds a complete tilemap row, but the stock builders
; only stage the 288-pixel native-width region.  During Jungle Hijinxs' entry
; pan that overwrites every visible row after the wide horizontal initializer,
; leaving both extended margins stale.  In wide levels, run each builder twice
; with overlapping horizontal biases so the 64-entry WRAM row ring is populated
; across the entire renderer sample. The upload routine already DMAs
; both 32-entry halves of that ring to the 64x32 BG map.
org $81890E
	JML.l DKC1_Wide_RowBuildStandard
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA

org $818CEF
	JML.l DKC1_Wide_RowBuildAlternate
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA
endif

; ---------------------------------------------------------------------------
; Main sprite draw culling

if !DKC1_Wide_EnableSpriteCullingPatches == !TRUE
; Original ranges were [-48,303] and [-88,343]. Add the profile margin to
; both sides while keeping the same outer safety region.
org $BBA8DE
	dw !DKC1_WideSpriteCull1Left
org $BBA8E1
	dw !DKC1_WideSpriteCull1Span
org $BBA90E
	dw !DKC1_WideSpriteCull2Left
org $BBA911
	dw !DKC1_WideSpriteCull2Span
endif

; ---------------------------------------------------------------------------
; Level object activation/spawn margins in bank $BD.

if !DKC1_Wide_EnableObjectActivationPatches == !TRUE
; Preserve the stock 32-pixel safety margin outside each widened edge.
org $BDF5AF : dw !DKC1_WideObjectLeftSafety
org $BDF5FD : dw !DKC1_WideObjectLeftSafety
org $BDF706 : dw !DKC1_WideObjectLeftSafety
org $BDF758 : dw !DKC1_WideObjectLeftSafety
org $BDF793 : dw !DKC1_WideObjectLeftSafety
org $BDF88F : dw !DKC1_WideObjectLeftSafety
org $BDF8DA : dw !DKC1_WideObjectLeftSafety
org $BDF9A7 : dw !DKC1_WideObjectLeftSafety
org $BDF9ED : dw !DKC1_WideObjectLeftSafety
org $BDFA36 : dw !DKC1_WideObjectLeftSafety
org $BDFAE0 : dw !DKC1_WideObjectLeftSafety
org $BDFB12 : dw !DKC1_WideObjectLeftSafety
org $BDFBAA : dw !DKC1_WideObjectLeftSafety
org $BDFCD1 : dw !DKC1_WideObjectLeftSafety
org $BDFD05 : dw !DKC1_WideObjectLeftSafety
org $BDFE96 : dw !DKC1_WideObjectLeftSafety
org $BDFF2A : dw !DKC1_WideObjectLeftSafety
org $BDFFB2 : dw !DKC1_WideObjectLeftSafety

org $BDF606 : dw !DKC1_WideObjectActivationSpan
org $BDF711 : dw !DKC1_WideObjectActivationSpan
org $BDF79E : dw !DKC1_WideObjectActivationSpan
org $BDF89D : dw !DKC1_WideObjectActivationSpan
org $BDF8E3 : dw !DKC1_WideObjectActivationSpan
org $BDF9B8 : dw !DKC1_WideObjectActivationSpan
org $BDF9F6 : dw !DKC1_WideObjectActivationSpan
org $BDFA3F : dw !DKC1_WideObjectActivationSpan
org $BDFAE9 : dw !DKC1_WideObjectActivationSpan
org $BDFB1F : dw !DKC1_WideObjectActivationSpan
org $BDFCDA : dw !DKC1_WideObjectActivationSpan
org $BDFD0E : dw !DKC1_WideObjectActivationSpan

; Slipslide Ride and other levels with type-$09 vertical-section controllers
; use two private object-window checks. Their left margins are patched above at
; $BDFF2A/$BDFFB2, so their spans must grow by the same total extension. Leaving
; the stock $0140 span here moves the right edge 56 pixels left and can prevent
; the controller from activating the next rope/enemy section until a death or
; reload rebuilds its state.
org $BDFF38 : dw !DKC1_WideObjectActivationSpan
org $BDFFBF : dw !DKC1_WideObjectActivationSpan

; Two special right-side prefetch tests use cameraX+$120.
org $BDF596 : dw !DKC1_WideObjectRightPrefetch
org $BDFB8F : dw !DKC1_WideObjectRightPrefetch
endif

if !DKC1_Wide_EnableType5ChildRetry == !TRUE
; Type-$05 object groups allocate all of their children once, then mark the
; parent active even if the fixed normal-sprite/OAM pools were temporarily
; full.  The stock active-parent branch never revisits child records whose
; $192B slot stayed zero.  Wider activation makes that partial-allocation
; case reachable in ordinary play (Barrel Cannon Canyon group $5D loses its
; Zinger and the target barrel above Kong).  Route active wide groups back
; through the stock child loop; CODE_BDFC59 already skips nonzero children,
; so only missing records attempt allocation.  Narrow-mode behavior and the
; original inactive-parent path remain byte-for-byte stock after the hook.
org $BDFB76
	JML.l DKC1_Wide_Type5GroupSpawn
	db $EA,$EA,$EA,$EA,$EA,$EA
endif

; ---------------------------------------------------------------------------
; Player movement range at terminal camera bounds

if !DKC1_Wide_EnablePlayerEndpointRangeFix == !TRUE
; DKC clamps the active Kong to roughly X=18..238 relative to the camera at
; level endpoints.  Moving both camera bounds inward by 56 pixels without
; widening these probes makes the last 56 world pixels unreachable.  Jungle
; Hijinxs Bonus 1 demonstrates the failure exactly: Layer1X stops at $6BC8,
; the Kong stops at $6CB8, and the authored exit trigger at $6CD0 cannot be
; reached.  Keep the stock probes in narrow rooms and use -38..294 only when
; the camera span identifies widescreen gameplay.
org $BF86E7
	JSL.l DKC1_Wide_GetPlayerLeftBoundaryProbe
	db $EA,$EA,$EA

org $BF86FA
	JSL.l DKC1_Wide_GetPlayerRightBoundaryProbe
	db $EA,$EA,$EA
endif

; ---------------------------------------------------------------------------
; King K. Rool logical arena bounds

if !DKC1_Wide_EnableKRoolArenaBoundsFix == !TRUE
; K. Rool's movement and encounter-start helpers reuse the camera-bound words
; as fixed logical arena edges. Widescreen moves those camera bounds inward by
; the selected margin ($0038..$00C8 or $0048..$00B8), but the authored boss
; arena remains $0000..$0100. Feeding the narrowed render bounds into the AI
; clamps/wraps the boss early and delays one encounter transition by the left
; margin. These routines are private to K. Rool, so restore their exact stock
; logical constants without changing the widened camera or presentation.
org $B6E165 : CMP.w #$0000
org $B6E16F : CMP.w #$0100
org $B6E174 : LDA.w #$0000
org $B6E183 : LDA.w #$0100
org $B6E198 : LDA.w #$0000
org $B6E82B : SBC.w #$0000
endif

; ---------------------------------------------------------------------------
; Banana formation presentation

if !DKC1_Wide_EnableBananaFormationCoverage == !TRUE
; Formation enumeration and clipping are private to the bank-$B8 banana
; renderer, so the general object/sprite widening does not affect them.  The
; final screen-X correction below moves every emitted banana left by the
; selected margin. Add the complete internal width here so candidates fill
; both extensions before that correction.
org $B8B91B
	ADC.w #!DKC1_WideInitialBackstep
org $B8B942
	ADC.w #!DKC1_WideInitialBackstep+$000F
org $B8BA11
	LDA.w #!DKC1_WideInitialBackstep+$0007
endif

if !DKC1_Wide_EnableBananaFormationCameraFix == !TRUE
; Banana formations use a level-local camera offset rather than the normal
; sprite world-to-screen path. The widened lower camera bound is original+$38,
; so Layer1X-lowerBound is $38 too small and every banana is drawn 56 pixels
; too far right. Correct only the final per-tile screen X result, after the
; stock selection/clipping pass, so the candidate list and OAM budget remain
; unchanged. Positive wide-screen X can exceed 255, so the helper mirrors
; coordinate bit 8 into the sign bit consumed by the stock OAM packer.
org $B8BA67
	JSL.l DKC1_Wide_AdjustBananaScreenX
	db $EA,$EA

; The renderer has a second tile-chain loop for the alternate banana graphics
; bank. Apply the identical X correction there as well.
org $B8BACA
	JSL.l DKC1_Wide_AdjustBananaScreenX
	db $EA,$EA

; The pickup routine separately reloads the formation's authored X coordinate.
; Move that logical coordinate by the same amount as the OAM presentation so
; contact, collection, and the spawned pickup effect agree with what is drawn.
; Replacing only the four-byte long load leaves the stock STA $56 intact.
org $B8BB2E
	JSL.l DKC1_Wide_GetBananaCollisionX
endif

if !DKC1_Wide_EnableSynchronizedBananas == !TRUE
; DKC normally offsets the spin phase of each banana in a formation. The OAM
; anchors remain aligned, but poses with transparent top/bottom rows can look
; vertically displaced after the widescreen image is scaled to a PC display.
; $5C already holds the animated phase shared by the formation. Replace the
; later per-item phase calculation with that shared value, while preserving
; the original tile-bit mask and the following $2180 write at $B8BA88.
org $B8BA7A
	LDA.b $5C
	AND.b #$EE
	db $EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA,$EA
endif

; ---------------------------------------------------------------------------
; Shared helpers in documented free space.

org $CA6C61

DKC1_Wide_GetStreamX:
	; $0002 is a private, synchronous marker used only by Gang-Plank
	; Galleon's full-ring fill above.  That stock loop must use +$0100 for all
	; 65 writes; applying either widescreen initializer offset shifts and wraps
	; its last seven writes over the visible left extension.
	LDA.w $1A5B
	CMP.w #$0002
	BEQ.b .OriginalRight

	; The normal initializer starts at widenedCameraX minus the selected full
	; width. Adding the selected right edge cancels both the widened minimum and
	; temporary backstep, seeding every column while preserving the real camera.
	LDA.w $1A5B
	CMP.w #$0001
	BNE.b .CheckWideMode
	LDA.w $0A75
	CMP.w #$0008
	BNE.b .CheckWideMode
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	CLC
	ADC.w #!DKC1_WideRightEdge
	RTL
.CheckWideMode:
	; The alternate initializer uses $1A5B=0, an explicit +8 step, and a
	; temporary Layer1 X outside the forced special-init upper bound. Its
	; original extra column uses the matching alternate backstep/target. Do not
	; use the sign bit as the discriminator: long levels
	; such as Barrel Cannon Canyon legitimately cross $7FFF, and an initializer
	; can cross it too ($7FE9,$7FF1,$7FF9,$8001...). Comparing unsigned against
	; the current upper bound accepts all of those init positions while normal
	; camera interpolation remains inside the level's real bounds.
	LDA.w $0A75
	CMP.w #$0008
	BNE.b .CheckBounds
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	CMP.w $1B25
	BCC.b .CheckBounds
	BEQ.b .CheckBounds
	CMP.w $1A5E
	BEQ.b .CheckBounds
	CLC
	ADC.w #!DKC1_WideAlternateBackstep
	RTL
.CheckBounds:
	; The boot/title code reuses these tile routines. Recompute the mode from
	; the current camera span on every upload so transitions and save states do
	; not depend on a persistent flag.
	LDA.w $1B25
	SEC
	SBC.w $1B23
	CMP.w #!DKC1_WideTotalExtension
	BCS.b .Wide
.Original:
	LDA.w $0A75
	BPL.b .OriginalRight
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	RTL
.OriginalRight:
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	CLC
	ADC.w #$0100
	RTL
.Wide:
	LDA.w $0A75
	BPL.b .Right
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	SEC
	SBC.w #!DKC1_WideMargin
	RTL
.Right:
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	; Level entrances briefly move the camera from the old zero origin to the
	; widened minimum. Keep DKC's stock edge during that pre-bound motion so it
	; cannot overwrite the already initialized wide columns with transition
	; data. At/after the real bound, stream the full wide right edge.
	CMP.w $1B23
	BCS.b .WideRight
	CLC
	ADC.w #$0100
	RTL
.WideRight:
	CLC
	ADC.w #!DKC1_WideRightEdge
	RTL

DKC1_Wide_RowBuildStandard:
	; Preserve the original early-out before doing either pass.
	LDA.w !RAM_DKC1_Global_Layer1YPosLo
	AND.w #$FFF8
	CMP.w $08A7
	BNE.b .CheckWideMode
	RTL
.CheckWideMode:
	LDA.w $1B25
	SEC
	SBC.w $1B23
	CMP.w #!DKC1_WideTotalExtension
	BCS.b .Wide
	JML.l $81891A
.Wide:
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	PHA
	SEC
	SBC.w #!DKC1_WideMargin
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	JSL.l $81891A
	PLA
	PHA
	CLC
	ADC.w #!DKC1_WideRowSecondBias
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	JSL.l $81891A
	PLA
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	RTL

DKC1_Wide_RowBuildAlternate:
	; Same coverage expansion for the alternate map-data layout.
	LDA.w !RAM_DKC1_Global_Layer1YPosLo
	AND.w #$FFF8
	CMP.w $08A7
	BNE.b .CheckWideMode
	RTL
.CheckWideMode:
	LDA.w $1B25
	SEC
	SBC.w $1B23
	CMP.w #!DKC1_WideTotalExtension
	BCS.b .Wide
	JML.l $818CFB
.Wide:
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	PHA
	SEC
	SBC.w #!DKC1_WideMargin
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	JSL.l $818CFB
	PLA
	PHA
	CLC
	ADC.w #!DKC1_WideRowSecondBias
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	JSL.l $818CFB
	PLA
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	RTL

DKC1_Wide_InitSpecialBounds:
	LDA.w #!DKC1_WideMargin
	STA.w $1B23
	LDA.w #!DKC1_WideSpecialUpper
	STA.w $1B25
	RTL

if !DKC1_Wide_EnableKRoolTilemapFillFix == !TRUE
DKC1_Wide_BeginKRoolTilemapFill:
	; Reproduce the two overwritten stores, then mark this private sweep.
	STZ.w !RAM_DKC1_Global_Layer1YPosLo
	STZ.w $0897
	LDA.w #$0002
	STA.w $1A5B
	RTL

DKC1_Wide_EndKRoolTilemapFill:
	; This helper is reached by JML from a bank-$80 routine entered with JSR.
	; Restore state, then return to bank $80's existing RTS at $809F60; executing
	; RTS while PB is still $CA would resume at the right offset in the wrong
	; program bank.
	STZ.w !RAM_DKC1_Global_Layer1XPosLo
	LDA.w #$0001
	STA.w $1A5B
	JML.l $809F60
endif

DKC1_Wide_AdjustCameraBounds:
	JSL.l $BCB052
	STA.b $76
	STY.b $78
	TYA
	SEC
	SBC.b $76
	CMP.w #!DKC1_WideTotalExtension  ; do not invert bounds in narrow rooms
	BCC.b .KeepOriginal
	LDA.b $76
	CLC
	ADC.w #!DKC1_WideMargin
	STA.w $1B23
	LDA.b $78
	SEC
	SBC.w #!DKC1_WideMargin
	STA.w $1B25
	RTL

.KeepOriginal:
	LDA.b $76
	STA.w $1B23
	LDA.b $78
	STA.w $1B25
	RTL

if !DKC1_Wide_EnableBananaFormationCameraFix == !TRUE
DKC1_Wide_AdjustBananaScreenX:
	LDA.b $56
	CLC
	ADC.w $BD9C,y
	PHA
	LDA.l $7E1B25
	SEC
	SBC.l $7E1B23
	CMP.w #!DKC1_WideTotalExtension
	BCC.b .Narrow
	PLA
	SEC
	SBC.w #!DKC1_WideMargin
	; Stock converts the sign bit to the packed OAM X-high bit. Mirror bit 8
	; into that sign bit for positive wide-screen coordinates above $00FF.
	; The real 16-bit coordinate remains available to the low-byte OAM write.
	BIT.w #$0100
	BEQ.b .WideXReady
	ORA.w #$8000
.WideXReady:
	RTL
.Narrow:
	PLA
	RTL

DKC1_Wide_GetBananaCollisionX:
	LDA.l $7EC000,x
	PHA
	LDA.l $7E1B25
	SEC
	SBC.l $7E1B23
	CMP.w #!DKC1_WideTotalExtension
	BCC.b .Narrow
	PLA
	SEC
	SBC.w #!DKC1_WideMargin
	RTL
.Narrow:
	PLA
	RTL
endif

if !DKC1_Wide_EnablePlayerEndpointRangeFix == !TRUE
DKC1_Wide_GetPlayerLeftBoundaryProbe:
	LDA.l $7E1B25
	SEC
	SBC.l $7E1B23
	CMP.w #!DKC1_WideTotalExtension
	BCC.b .Narrow
	LDA.l $7E0B19,x
	CLC
	ADC.w #!DKC1_WidePlayerLeftProbe
	RTL
.Narrow:
	LDA.l $7E0B19,x
	SEC
	SBC.w #$0012
	RTL

DKC1_Wide_GetPlayerRightBoundaryProbe:
	LDA.l $7E1B25
	SEC
	SBC.l $7E1B23
	CMP.w #!DKC1_WideTotalExtension
	BCC.b .Narrow
	LDA.l $7E0B19,x
	SEC
	SBC.w #!DKC1_WidePlayerRightProbe
	RTL
.Narrow:
	LDA.l $7E0B19,x
	SEC
	SBC.w #$00EE
	RTL
endif

if !DKC1_Wide_EnableType5ChildRetry == !TRUE
DKC1_Wide_Type5GroupSpawn:
	; Reproduce the overwritten active-parent test.
	LDX.b $A4
	LDA.w $192B,x
	AND.w #$00FF
	BEQ.b .InactiveParent

	; Preserve stock behavior outside widened gameplay rooms.
	LDA.w $1B25
	SEC
	SBC.w $1B23
	CMP.w #!DKC1_WideTotalExtension
	BCC.b .AlreadyActive

	; Recover the first child pointer and apply the same widened range tests as
	; the original type-$05 creation path before attempting any retry.
	STY.b $76
	LDA.w $0006,y
	CLC
	ADC.w #$0008
	TAY
	STY.b $78
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	CLC
	ADC.w #!DKC1_WideObjectRightPrefetch
	CMP.w $0002,y
	BCC.b .AlreadyActive

.FindLastChild:
	LDA.w $0008,y
	BEQ.b .CheckLeftEdge
	TYA
	CLC
	ADC.w #$0008
	TAY
	BRA.b .FindLastChild

.CheckLeftEdge:
	LDA.w !RAM_DKC1_Global_Layer1XPosLo
	SEC
	SBC.w #!DKC1_WideObjectLeftSafety
	BPL.b .CompareLastChild
	CMP.w #$FC00
	BCC.b .CompareLastChild
	CMP.w $0002,y
	BCC.b .AlreadyActive
	BRA.b .PrepareChildLoop

.CompareLastChild:
	CMP.w $0002,y
	BCS.b .AlreadyActive

.PrepareChildLoop:
	; CODE_BDFBF5 expects the parent map index on the stack, $A4 adjusted to
	; the first child bookkeeping byte, and Y restored to the first record.
	; Its existing PLA/RTS then returns directly to the original scanner.
	LDA.b $A4
	PHA
	LDA.b $78
	SEC
	SBC.b $76
	LSR
	LSR
	LSR
	CLC
	ADC.b $A4
	STA.b $A4
	LDY.b $78
	JML.l $BDFBF5

.InactiveParent:
	JML.l $BDFB80

.AlreadyActive:
	JML.l $BDFB72
endif
