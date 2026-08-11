; Donkey Kong Country gameplay widescreen prototype for SuperZSNES.
;
; SuperZSNES renders seven extra 8-pixel columns on each side (368x224),
; then the 358x224 output window crops five pixels per side. The ROM keeps
; DKC's original 128-pixel camera target while expanding the streamed map,
; camera bounds, object activation window, and sprite culling symmetrically.

!DKC1_WideMargin = $0038             ; 56 pixels / 7 tiles per side
!DKC1_WideInitialBackstep = $0170    ; 368 pixels / 46 columns; preserves the final camera position
!DKC1_WideRightEdge = $0138          ; 256 + 56 pixels
!DKC1_Wide_EnableCameraPatches = !TRUE
!DKC1_Wide_EnableInitialFillPatches = !TRUE
!DKC1_Wide_EnableMovingTilemapHooks = !TRUE
!DKC1_Wide_EnableSpriteCullingPatches = !TRUE
!DKC1_Wide_EnableObjectActivationPatches = !TRUE
!DKC1_Wide_EnableType5ChildRetry = !TRUE
!DKC1_Wide_EnableBananaFormationCameraFix = !TRUE
!DKC1_Wide_EnableBananaFormationCoverage = !TRUE
!DKC1_Wide_EnablePlayerEndpointRangeFix = !TRUE
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
; Standard path: fill the full 368-pixel internal render width. The loop must
; advance by exactly the same distance as the initial camera backstep, or level
; setup leaves the real camera shifted and exposes columns that were never
; initialized. The helper uses +368 during this loop, so the 46 uploads cover
; world X=0 through X=360 and finish back at the original camera position.
org $809EC5
	dw !DKC1_WideInitialBackstep
org $809ED7
	dw $002E

; Alternate path retains its original one extra column while preserving the
; same final-camera invariant: 47 * 8 = $0178.
org $80C56F
	dw $0178
org $80C57E
	dw $002F
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
; across the entire 368-pixel renderer sample.  The upload routine already DMAs
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
; Original ranges were [-48,303] and [-88,343]. Add 56 pixels to both sides.
org $BBA8DE
	dw $0068
org $BBA8E1
	dw $01D0
org $BBA90E
	dw $0090
org $BBA911
	dw $0220
endif

; ---------------------------------------------------------------------------
; Level object activation/spawn margins in bank $BD.

if !DKC1_Wide_EnableObjectActivationPatches == !TRUE
; cameraX-$20 becomes cameraX-$58 (32-pixel safety margin outside the new
; left edge), and each $140-wide activation span becomes $1B0.
org $BDF5AF : dw $0058
org $BDF5FD : dw $0058
org $BDF706 : dw $0058
org $BDF758 : dw $0058
org $BDF793 : dw $0058
org $BDF88F : dw $0058
org $BDF8DA : dw $0058
org $BDF9A7 : dw $0058
org $BDF9ED : dw $0058
org $BDFA36 : dw $0058
org $BDFAE0 : dw $0058
org $BDFB12 : dw $0058
org $BDFBAA : dw $0058
org $BDFCD1 : dw $0058
org $BDFD05 : dw $0058
org $BDFE96 : dw $0058
org $BDFF2A : dw $0058
org $BDFFB2 : dw $0058

org $BDF606 : dw $01B0
org $BDF711 : dw $01B0
org $BDF79E : dw $01B0
org $BDF89D : dw $01B0
org $BDF8E3 : dw $01B0
org $BDF9B8 : dw $01B0
org $BDF9F6 : dw $01B0
org $BDFA3F : dw $01B0
org $BDFAE9 : dw $01B0
org $BDFB1F : dw $01B0
org $BDFCDA : dw $01B0
org $BDFD0E : dw $01B0

; Two special right-side prefetch tests use cameraX+$120.
org $BDF596 : dw $0158
org $BDFB8F : dw $0158
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
; Banana formation presentation

if !DKC1_Wide_EnableBananaFormationCoverage == !TRUE
; Formation enumeration and clipping are private to the bank-$B8 banana
; renderer, so the general object/sprite widening does not affect them.  The
; final screen-X correction below moves every emitted banana left by $38; add
; the full $70 pixels of internal width here so candidates fill the added
; right side before that correction.  The effective post-correction clip is
; $0177-$0038=$013F, one tile beyond the 312-pixel wide right sample edge.
org $B8B91B
	ADC.w #$0170
org $B8B942
	ADC.w #$017F
org $B8BA11
	LDA.w #$0177
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
; vertically displaced after the 358x224 image is scaled to a PC display.
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
	; The normal initializer starts at widenedCameraX-368 and advances 46 times.
	; Adding 312 cancels the widened +56 minimum as well as the temporary
	; backstep, seeding world X=0..360 while the real camera still returns to
	; its original position after 46 * 8 pixels.
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
	; original extra column makes the matching full-width backstep/target $0178
	; (47 columns). Do not use the sign bit as the discriminator: long levels
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
	ADC.w #$0178
	RTL
.CheckBounds:
	; The boot/title code reuses these tile routines. Recompute the mode from
	; the current camera span on every upload so transitions and save states do
	; not depend on a persistent flag.
	LDA.w $1B25
	SEC
	SBC.w $1B23
	CMP.w #$0070
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
	CMP.w #$0070
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
	ADC.w #$0058
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
	CMP.w #$0070
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
	ADC.w #$0058
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	JSL.l $818CFB
	PLA
	STA.w !RAM_DKC1_Global_Layer1XPosLo
	RTL

DKC1_Wide_InitSpecialBounds:
	LDA.w #!DKC1_WideMargin
	STA.w $1B23
	LDA.w #$06C8                     ; $0700 - 56
	STA.w $1B25
	RTL

DKC1_Wide_AdjustCameraBounds:
	JSL.l $BCB052
	STA.b $76
	STY.b $78
	TYA
	SEC
	SBC.b $76
	CMP.w #$0070                     ; do not invert bounds in narrow rooms
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
	CMP.w #$0070
	BCC.b .Narrow
	PLA
	SEC
	SBC.w #!DKC1_WideMargin
	; Stock converts the sign bit to the packed OAM X-high bit. Mirror bit 8
	; into that sign bit for positive wide-screen coordinates $0100-$0177.
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
	CMP.w #$0070
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
	CMP.w #$0070
	BCC.b .Narrow
	LDA.l $7E0B19,x
	CLC
	ADC.w #$0026                    ; x - ($12-$38): visible left limit -38
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
	CMP.w #$0070
	BCC.b .Narrow
	LDA.l $7E0B19,x
	SEC
	SBC.w #$0126                    ; $EE+$38: visible right limit 294
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
	CMP.w #$0070
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
	ADC.w #$0158
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
	SBC.w #$0058
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
