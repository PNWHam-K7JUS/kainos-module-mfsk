# Kainos Olivia / MFSK

> **Experimental.** It passes its own self-test (sending to itself through noise), but it hasn't been tested on the air against fldigi yet. Reports are welcome on the Kainos GitHub.

A Kainos module for **Olivia**, **Contestia** and **MFSK** keyboard chat, coded and timed as fldigi does, so it works
with fldigi and the other programs that have them.

| Family | Modes | What it is |
|---|---|---|
| Olivia | 4/125 to 64/2000 (tones / bandwidth in Hz) | Each character spread over a whole block of symbols with a Walsh code, so it reads signals well below the noise and through fading and static. Slow: 8/250 and 8/500 are the usual ones, 32/1000 for contests |
| Contestia | 4/125 to 64/2000 | Olivia's code with 6-bit characters (upper case, figures, some punctuation): about twice as fast for the same tones and width, a little less sensitive |
| MFSK | MFSK4, 8, 11, 16, 22, 31, 32, 64, 64L, 128, 128L | One tone at a time with THOR's error correction (K = 7 and an interleaver) and MFSK varicode. MFSK16 is the common one; the L modes interleave much deeper, for long fades |

Install it in Kainos from **Setup > Modules**, then open it from the **Digi Modes** menu (**Olivia / MFSK**). The mode
button opens a menu with a submenu for each family.

## Receiving

- **Click a signal on the waterfall** to read it. The click snaps to the signal's centre.
  - Olivia and Contestia find the signal for themselves within 4 tones either way, and say how far off it is.
  - MFSK needs the click close: AFC (Options) then follows any drift.
- Olivia's text arrives a few blocks behind the signal. The receiver waits to see which timing and tuning stays
  strongest, so the first characters can take several seconds to appear.
- **SQL** (squelch) shows text only from a signal strong enough: off, low, medium, high, very high. Low (as it comes)
  keeps noise quiet and still reads Olivia at about -12 dB in 2.5 kHz.
- **Rev** turns the tones the other way up, for a station on the other sideband. LSB / DIGL is allowed for already.

## Sending

- Type in the box and press **TX** (Ctrl+T). The box goes out, then whatever you type, as you type it.
  - **RX** (Ctrl+R) goes back to receive once everything typed has gone.
  - **Esc** stops at once.
- **F1 – F8** send the messages under them (right-click one to change it).
  - They fill in `<MYCALL>`, `<MYNAME>`, `<MYGRID>`, `<CALL>`, `<NAME>`, `<RST>` and `<MODE>`.
  - `<RX>` goes back to receive when it's reached.
- **Olivia and Contestia transmissions** start and end with fldigi's start/stop tones (the band's two edges, which
  mark the signal on the waterfall).
  - Kainos sends a block of idle characters first, and a few at the end, so the receiver catches the first and last
    of the text.
  - Contestia sends upper case only.
- **MFSK transmissions** start and end as fldigi's do (CR STX CR, then CR EOT CR, and the flush).
- **Tune** sends a steady tone for up to 30 s. Set the transmit level (Options) and drive for full power with the ALC
  barely moving.

## Logging

**Log QSO** writes an ADIF log in the module's data folder:

- MODE OLIVIA, with SUBMODE OLIVIA 8/500 (and so on).
- MODE CONTESTI.
- MODE MFSK, with SUBMODE MFSK16 (and so on).

It also tells logging programs, the same way WSJT-X does (UDP "Logged ADIF" to 127.0.0.1:2237 by default). Log4OM,
N1MM+, HRD, DXKeeper, GridTracker and the Kainos Logbook all pick it up.

Not included yet: MFSK's picture mode.

## Building

`.\build.ps1` builds it, runs the self-test and packages `dist\kainos-mfsk-<version>.zip`. The self-test covers:

- Olivia's block code and the MFSK varicode.
- Every mode sent and received: clean, with noise and mistuned.
- The other sideband and 44.1 kHz.
- Noise alone printing nothing.
- The transmit path.

GPLv3 or later. Copyright (C) 2026 Justin Cron K7JUS. Olivia's code follows Pawel Jalocha's, as fldigi has it.
