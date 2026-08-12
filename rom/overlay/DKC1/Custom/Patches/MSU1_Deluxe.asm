; Donkey Kong Country Deluxe MSU-1 support, ported to USA v1.0.
;
; The public Deluxe patch was authored for USA Rev. 2. Its music policy and
; 60-track mapping are retained here, but its four ROM hooks are relocated to
; the byte-equivalent v1.0 instructions used by this project. Music is handled
; by MSU-1 while the original SPC engine remains active for sound effects.

!DKC1_MSU1_LevelOrEntrance = $0100
!DKC1_MSU1_MapThemeCounter = $0102
!DKC1_MSU1_CustomTrack = $0104
!DKC1_MSU1_Busy = $0610
!DKC1_MSU1_OriginalTrack = $0612
!DKC1_MSU1_MapThemeCountMinusOne = $07

; Capture the low-byte entrance/level identifier used by the Deluxe table.
; USA v1.0 equivalent of the Rev. 2 hook at $80829E.
assert read1($80829B) == $29 && read2($80829C) == $00FF && read1($80829E) == $85 && read1($80829F) == $3E, "Unexpected USA v1.0 entrance-load code"
org $80829B
	JSL.l DKC1_MSU1_CaptureLevel
	NOP

; Silence SPC music without disabling the SPC/SFX engine. USA v1.0 equivalent
; of the Rev. 2 embedded-SPC bytes at $CAA9EA.
assert read2($CAA9E5) == $D401, "Unexpected USA v1.0 SPC music-control bytes"
org $CAA9E5
	db $00,$6F

; Intercept music selection. USA v1.0 equivalent of Rev. 2 $CAB1D2.
assert read4($CAB1CD) == $180A4C85, "Unexpected USA v1.0 music-selection code"
org $CAB1CD
	JSL.l DKC1_MSU1_SelectTrack

; Poll MSU readiness from NMI before the stock $4210 acknowledgement. USA
; v1.0 equivalent of Rev. 2 $C0A97F.
assert read1($C0A971) == $E2 && read1($C0A972) == $20 && read2($C0A973) == $10AD, "Unexpected USA v1.0 NMI code"
org $C0A971
	JSL.l DKC1_MSU1_Poll
	NOP

; Bank $FB is unused in the original 4 MiB ROM and does not overlap the
; widescreen helpers in bank $CA.
org $FBF800

DKC1_MSU1_SelectTrack:
	STA.b $4C
	STA.w !DKC1_MSU1_OriginalTrack
	ASL
	PHA
	LDA.w $2002
	AND.w #$00FF
	CMP.w #$0053                    ; low byte of "MSU1" signature
	BEQ.b .MSUPresent
	PLA
	CLC
	RTL

.MSUPresent:
	STZ.w $2006                    ; silence/stop before selecting
	LDA.w !DKC1_MSU1_OriginalTrack
	INC
	STA.w !DKC1_MSU1_OriginalTrack ; DKC IDs are zero-based; PCM IDs are not
	JSR.w DKC1_MSU1_ChooseDeluxeTrack
	STA.w $2004
	STZ.w $2005
	LDA.w #$0001
	STA.w !DKC1_MSU1_Busy
	PLA
	CLC
	RTL

DKC1_MSU1_Poll:
	JSR.w DKC1_MSU1_AdvanceMapTheme
	PHA
	LDA.w !DKC1_MSU1_Busy
	AND.w #$00FF
	CMP.w #$0001
	BEQ.b .PollStatus
.ReturnToNMI:
	PLA
	SEP.b #$20
	LDA.w $4210
	RTL

.PollStatus:
	PHP
	SEP.b #$30
	BIT.w $2000
	BVS.b .Restore                ; device is still loading the selected PCM
	LDA.w $2000
	AND.b #$08                    ; track missing
	BNE.b .TryOriginalTrack
	STZ.w !DKC1_MSU1_Busy
	LDA.w !DKC1_MSU1_OriginalTrack
	TAX
	LDA.b #$FF
	STA.w $2006
	LDA.l DKC1_MSU1_LoopTable,x
	STA.w $2007
.Restore:
	PLP
	BRA.b .ReturnToNMI

.TryOriginalTrack:
	LDA.w !DKC1_MSU1_CustomTrack
	BEQ.b .Restore                ; the original PCM is missing too
	LDA.w !DKC1_MSU1_OriginalTrack
	STA.w $2004
	STZ.w $2005
	STZ.w !DKC1_MSU1_CustomTrack
	BRA.b .Restore

DKC1_MSU1_ChooseDeluxeTrack:
	PHX
	PHP
	SEP.b #$30
	CMP.b #$0A
	BCC.b .ReplaceLevelTheme
	CMP.b #$0E
	BEQ.b .ReplaceLevelTheme
	CMP.b #$14
	BEQ.b .ReplaceLevelTheme
	CMP.b #$16
	BEQ.b .ReplaceLevelTheme
	CMP.b #$0D
	BEQ.b .ChooseMapTheme
	BRA.b .UseOriginal

.ReplaceLevelTheme:
	LDX.w !DKC1_MSU1_LevelOrEntrance
	LDA.l DKC1_MSU1_ReplacementTable,x
	BEQ.b .UseOriginal
	INC.w !DKC1_MSU1_CustomTrack
	PLP
	PLX
	RTS

.UseOriginal:
	LDA.w !DKC1_MSU1_OriginalTrack
.UseOriginalAndClearFlag:
	PLP
	PLX
	STZ.w !DKC1_MSU1_CustomTrack
	RTS

.ChooseMapTheme:
	LDA.w !DKC1_MSU1_MapThemeCounter
	BEQ.b .OriginalMapTheme
	CLC
	ADC.b #$35                    ; counters 1..7 select tracks 54..60
	INC.w !DKC1_MSU1_CustomTrack
	BRA.b .ReturnMapTheme
.OriginalMapTheme:
	LDA.b #$0D
.ReturnMapTheme:
	PLP
	PLX
	RTS

DKC1_MSU1_AdvanceMapTheme:
	PHP
	SEP.b #$20
	LDA.w !DKC1_MSU1_MapThemeCounter
	CMP.b #!DKC1_MSU1_MapThemeCountMinusOne
	BEQ.b .Wrap
	INC.w !DKC1_MSU1_MapThemeCounter
	BRA.b .Done
.Wrap:
	STZ.w !DKC1_MSU1_MapThemeCounter
.Done:
	PLP
	RTS

DKC1_MSU1_CaptureLevel:
	AND.w #$00FF
	STA.b $3E
	STA.w !DKC1_MSU1_LevelOrEntrance
	RTL

org $FBFA00
DKC1_MSU1_LoopTable:
	; Indexed by the original one-based DKC music ID. Custom tracks inherit the
	; loop/one-shot behavior of the cue they replace.
	db $03,$03,$03,$03,$03,$03,$03,$03,$03,$03,$03,$01,$03,$03,$03,$03
	db $01,$03,$01,$01,$03,$03,$03,$03,$03,$03,$01,$01,$01,$03,$03,$03

org $FBFA30
DKC1_MSU1_ReplacementTable:
	; Indexed by the captured low-byte entrance/level identifier.
	db $00,$02,$00,$00,$00,$00,$00,$1F,$00,$00,$23,$00,$1C,$1E,$00,$00
	db $00,$00,$22,$00,$20,$00,$01,$1D,$21,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$27,$00,$06,$00,$00,$28,$00,$00,$00,$2E,$00,$00,$05,$2C
	db $2D,$03,$00,$00,$00,$00,$24,$00,$00,$00,$00,$00,$00,$00,$26,$00
	db $08,$2B,$07,$2A,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$0D,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$14,$0E,$00,$29,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$04
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$2F,$00
	db $35,$00,$00,$00,$00,$00,$00,$00,$00,$09,$00,$00,$00,$00,$25,$00
	db $16,$30,$32,$33,$34,$31,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00
	db $00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00,$00

warnpc $FBFB30
