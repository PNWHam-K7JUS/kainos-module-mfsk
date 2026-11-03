/*  MfskTx.cs

The MFSK transmitter, as fldigi sends it, made a little at a time so text typed while sending goes straight out. Each
symbol is one tone (Gray coded), phase-continuous:

  - a third of the mode's preamble in zero bits (not the L modes), then CR STX CR
  - the text: MFSK varicode through the K = 7 encoder, each Bits coded bits through the interleaver to a symbol
  - while there's nothing to send: a NUL, a 1 and 32 zeros
  - at the end CR EOT CR, a 1 and the preamble's zeros, to flush the interleaver and the decoder at the other end

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace Kainos.Mfsk
{
    internal sealed class MfskTx : ModemTx
    {
        private readonly double _sps;               // samples a symbol at the transmit rate
        private double _phase, _step;

        private struct Sym { public int Tone; public bool Mark; public char C; }

        private readonly Queue<char> _text = new Queue<char>();
        private readonly Queue<Sym> _syms = new Queue<Sym>();
        private readonly ConvEncoder _enc = new ConvEncoder(ConvCode.PskR);
        private readonly Interleaver _il;
        private int _bits, _bitCount;
        private bool _started, _ended, _finishing;
        private long _out;
        private bool _mark;
        private char _markChar;
        private double _symPos;
        private bool _first = true;
        private int _fade = -1;

        public MfskTx(MfskMode mode, double frequency, bool reverse, int rate) : base(mode, frequency, reverse, rate)
        {
            _sps = rate * (double)mode.SymbolLen / mode.Rate;
            _il = new Interleaver(mode.Bits, mode.Interleave, false);
        }

        public override void Add(string s) { lock (_text) foreach (char c in s) _text.Enqueue(c); }
        public override int Queued { get { lock (_text) return _text.Count; } }
        public override void Finish() { _finishing = true; }
        public override bool Finishing { get { return _finishing; } }

        public override int Generate(float[] dst, int n)
        {
            int o = 0;
            while (o < n && !Done)
            {
                if (_fade >= 0)
                {
                    if (_fade == 0) { Done = true; break; }
                    double a = _fade / (Rate * 0.005);
                    dst[o++] = (float)(Math.Cos(_phase) * a);
                    _phase += _step;
                    _fade--;
                    continue;
                }
                if (_first || _symPos >= _sps)
                {
                    if (!_first) _symPos -= _sps;
                    _first = false;
                    if (!nextSymbol()) { _fade = (int)(Rate * 0.005); continue; }
                }
                double env = _out == 1 ? Math.Min(1, _symPos / (Rate * 0.005)) : 1;
                dst[o++] = (float)(Math.Cos(_phase) * env);
                _phase += _step;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                _symPos += 1;
            }
            return o;
        }

        private bool nextSymbol()
        {
            while (_syms.Count == 0)
            {
                if (!_started) { _started = true; preamble(); }
                else if (next()) { }
                else if (!_finishing) idle();
                else if (!_ended) { _ended = true; postamble(); }
                else return false;
            }
            Sym s = _syms.Dequeue();
            _out++;
            if (s.Mark) OnSent(s.C);
            int tone = Reverse ? Mode.Tones - 1 - s.Tone : s.Tone;
            double f = Frequency - (Mode.Tones - 1) * Mode.ToneSpacing / 2 + tone * Mode.ToneSpacing;
            _step = 2 * Math.PI * f / Rate;
            return true;
        }

        private void bit(int b)
        {
            int data = _enc.Encode(b);
            for (int i = 0; i < 2; i++)
            {
                _bits = (_bits << 1) | ((data >> i) & 1);
                if (++_bitCount == Mode.Bits)
                {
                    int sym = _il.Bits(_bits);
                    _syms.Enqueue(new Sym { Tone = Walsh.Gray(sym & (Mode.Tones - 1)), Mark = _mark, C = _markChar });
                    _mark = false;
                    _bits = _bitCount = 0;
                }
            }
        }

        private void sendChar(int c)
        {
            List<int> bits = new List<int>();
            MfskVaricode.Bits(c, bits);
            foreach (int b in bits) bit(b);
        }

        private void preamble()
        {
            if (Mode.Interleave < 100) for (int i = 0; i < Mode.Preamble / 3; i++) bit(0);       // not the L modes
            sendChar('\r'); sendChar(2); sendChar('\r');
        }

        private void idle()
        {
            sendChar(0);
            bit(1);
            for (int i = 0; i < 32; i++) bit(0);
        }

        private void postamble()
        {
            sendChar('\r'); sendChar(4); sendChar('\r');
            bit(1);
            for (int i = 0; i < Mode.Preamble; i++) bit(0);
            // what's left of a symbol, made up (fldigi drops it)
            while (_bitCount != 0) { _bits <<= 1; if (++_bitCount == Mode.Bits) { _syms.Enqueue(new Sym { Tone = Walsh.Gray(_il.Bits(_bits) & (Mode.Tones - 1)) }); _bits = _bitCount = 0; } }
        }

        // the next character's symbols (UTF-8 bytes for anything past ASCII): false if there's nothing to send
        private bool next()
        {
            char c;
            lock (_text)
            {
                if (_text.Count == 0) return false;
                c = _text.Dequeue();
            }
            if (c == '\r') return true;
            byte[] bytes = c == '\n' ? new byte[] { 13, 10 } : Encoding.UTF8.GetBytes(c.ToString());
            _mark = true;
            _markChar = c;
            foreach (byte b in bytes) sendChar(b);
            if (_mark) { _mark = false; OnSent(c); }       // its bits are waiting for a symbol
            return true;
        }
    }
}
