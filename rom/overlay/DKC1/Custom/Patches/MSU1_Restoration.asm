; Donkey Kong Country classic 27-track MSU-1 support, ported to USA v1.0.
;
; This mode uses the established one-based DKC mapping used by restoration
; packs: MSU track = original DKC music ID + 1. Music is handled by MSU-1
; while the original SPC engine remains active for sound effects.

!DKC1_MSU1_Busy = $0610
!DKC1_MSU1_Track = $0612

; Silence SPC music without disabling the SPC/SFX engine.
assert read2($CAA9E5) == $D401, "Unexpected USA v1.0 SPC music-control bytes"
org $CAA9E5
	db $00,$6F

; Intercept music selection at the USA v1.0 equivalent of Rev. 2 $CAB1D2.
assert read4($CAB1CD) == $180A4C85, "Unexpected USA v1.0 music-selection code"
org $CAB1CD
	JSL.l DKC1_MSU1_RestorationSelectTrack

; Poll MSU readiness from NMI before the stock $4210 acknowledgement.
assert read1($C0A971) == $E2 && read1($C0A972) == $20 && read2($C0A973) == $10AD, "Unexpected USA v1.0 NMI code"
org $C0A971
	JSL.l DKC1_MSU1_RestorationPoll
	NOP

; Bank $FB is unused in the original 4 MiB ROM and does not overlap the
; widescreen helpers in bank $CA.
org $FBF800

DKC1_MSU1_RestorationSelectTrack:
	STA.b $4C
	STA.w !DKC1_MSU1_Track
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
	LDA.w !DKC1_MSU1_Track
	INC                            ; DKC IDs are zero-based; PCM IDs are not
	STA.w !DKC1_MSU1_Track
	STA.w $2004
	STZ.w $2005
	LDA.w #$0001
	STA.w !DKC1_MSU1_Busy
	PLA
	CLC
	RTL

DKC1_MSU1_RestorationPoll:
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
	AND.b #$08                    ; selected track is missing
	BNE.b .Missing
	STZ.w !DKC1_MSU1_Busy
	LDA.w !DKC1_MSU1_Track
	TAX
	LDA.b #$FF
	STA.w $2006
	LDA.l DKC1_MSU1_RestorationLoopTable,x
	STA.w $2007
.Restore:
	PLP
	BRA.b .ReturnToNMI

.Missing:
	STZ.w !DKC1_MSU1_Busy         ; fail closed instead of polling forever
	BRA.b .Restore

org $FBFA00
DKC1_MSU1_RestorationLoopTable:
	; Indexed by the one-based DKC music ID. $03 = play+repeat, $01 = play.
	db $03,$03,$03,$03,$03,$03,$03,$03,$03,$03,$03,$01,$03,$03,$03,$03
	db $01,$03,$01,$01,$03,$03,$03,$03,$03,$03,$01,$01

warnpc $FBFA20
