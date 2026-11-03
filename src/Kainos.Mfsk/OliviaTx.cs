/*  OliviaTx.cs

The Olivia / Contestia transmitter, as fldigi sends it, made at the transmit rate directly:

  - the start tones: the band's two edges, low - high - low - high, a quarter second each (fldigi's "start/stop tones",
    on by default, which mark the signal's edges on the waterfall)
  - blocks of BitsPerSymbol characters (the first a NUL), each SymbolsPerBlock symbols; a block starts with what's
    typed so far, NULs making up the rest, and NUL blocks while there's nothing to send
  - at the end, a few blocks of NULs: the receiver prints a block a few blocks after it (it waits to see which timing
    and tuning stays strongest), so these bring the last of the text out at the other end instead of leaving it to
    the noise; then the tones again

Each symbol is a tone shaped by a raised cosine a symbol (SymbolLen) long, starting every half symbol, so each overlaps
the next by half; the phase jumps a quarter cycle either way between symbols, as fldigi does, to spread the spectrum.

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace Kainos.Mfsk
{
    internal sealed class OliviaTx : ModemTx
    {
        public const int FlushBlocks = 3;
        private const double ToneSecs = 0.256;      // each quarter of the start / stop tones (fldigi's SR4 at 8000)

        private readonly OliviaEncoder _enc;
        private readonly int _bits, _spb, _fc, _fcRev;
        private readonly double _binHz, _symSamples, _hopSamples;
        private readonly Queue<int> _q = new Queue<int>();          // bytes (Olivia) or characters to send
        private readonly Queue<char> _echo = new Queue<char>();     // what's shown as sent, a character per queued item (or \0)
        private readonly Random _rnd = new Random(7);
        private bool _finishing, _started;
        private int _symInBlock = -1, _flushLeft = -1;
        private int _toneStage;             // 0 start tones, 1 data, 2 stop tones, 3 done
        private long _sample;
        private double _toneStart;

        private struct Burst { public double Start, Freq, Phase; }
        private readonly List<Burst> _bursts = new List<Burst>();
        private double _nextStart, _lastFreq, _lastPhase;
        private bool _blocksDone;

        public OliviaTx(MfskMode mode, double frequency, bool reverse, int rate) : base(mode, frequency, reverse, rate)
        {
            _bits = mode.Bits;
            _spb = mode.BlockSymbols;
            _enc = new OliviaEncoder(_bits, mode.Contestia);
            int n = mode.SymbolLen;
            double fc = mode.Contestia ? mode.NominalBw / 2.0 : mode.NominalBw * (1.0 - 0.5 / mode.Tones) / 2.0;
            _fc = (int)((n / 16) * ((frequency - fc) / 500.0)) + 1;
            _fcRev = (int)((n / 16) * ((frequency + fc) / 500.0)) + 1;
            _binHz = 8000.0 / n;
            _symSamples = rate * (double)n / 8000;
            _hopSamples = _symSamples / 2;
            // fldigi starts with a NUL; a whole block of them gives the receiver a block to find the signal's timing
            // and tuning before the text
            lock (_q) for (int i = 0; i < _bits; i++) { _q.Enqueue(0); _echo.Enqueue('\0'); }
        }

        public override void Add(string s)
        {
            lock (_q)
                foreach (char c in s)
                {
                    if (c == '\r') continue;
                    if (Mode.Contestia)
                    {
                        _q.Enqueue(c == '\n' ? '\r' : c < 128 ? c : '?');
                        _echo.Enqueue(c);
                        continue;
                    }
                    // Olivia: 7-bit characters; others as UTF-8, each byte past 127 escaped (127, then its low 7 bits)
                    byte[] bytes = c == '\n' ? new byte[] { 13, 10 } : Encoding.UTF8.GetBytes(c.ToString());
                    bool first = true;
                    foreach (byte b in bytes)
                    {
                        if (b >= 128) { _q.Enqueue(127); _echo.Enqueue('\0'); _q.Enqueue(b & 127); }
                        else _q.Enqueue(b);
                        _echo.Enqueue(first ? c : '\0');
                        first = false;
                    }
                }
        }

        public override int Queued { get { lock (_q) return _q.Count; } }
        public override void Finish() { _finishing = true; }
        public override bool Finishing { get { return _finishing; } }

        public override int Generate(float[] dst, int n)
        {
            int o = 0;
            while (o < n && !Done)
            {
                if (_toneStage == 0 || _toneStage == 2)
                {
                    double t = (_sample - _toneStart) / Rate;
                    if (t >= 4 * ToneSecs)
                    {
                        if (_toneStage == 2) { _toneStage = 3; Done = true; break; }
                        _toneStage = 1;
                        _nextStart = _sample;
                        continue;
                    }
                    dst[o++] = (float)tones(t);
                    _sample++;
                    continue;
                }
                // data: start the symbols due by now
                while (!_blocksDone && _nextStart <= _sample) startSymbol();
                double v = 0;
                for (int i = _bursts.Count - 1; i >= 0; i--)
                {
                    Burst b = _bursts[i];
                    double tau = _sample - b.Start;
                    if (tau >= _symSamples) { _bursts.RemoveAt(i); continue; }
                    if (tau < 0) continue;
                    double env = 1 - Math.Cos(2 * Math.PI * tau / _symSamples);
                    v += env * Math.Cos(b.Phase + 2 * Math.PI * b.Freq * tau / Rate);
                }
                if (_blocksDone && _bursts.Count == 0) { _toneStage = 2; _toneStart = _sample; continue; }
                dst[o++] = (float)(v / 2);
                _sample++;
            }
            return o;
        }

        // the start / stop tones: the band's edges, alternating, each faded in and out over an eighth of its time
        private double tones(double t)
        {
            int part = (int)(t / ToneSecs);
            double tau = t - part * ToneSecs, edge = ToneSecs / 8;
            double env = tau < edge ? 0.5 * (1 - Math.Cos(Math.PI * tau / edge)) : tau > ToneSecs - edge ? 0.5 * (1 - Math.Cos(Math.PI * (ToneSecs - tau) / edge)) : 1;
            double lo = Frequency - Mode.NominalBw / 2.0, hi = Frequency + Mode.NominalBw / 2.0;
            bool low = (part % 2 == 0) ^ Reverse;
            return env * Math.Cos(2 * Math.PI * (low ? lo : hi) * tau);
        }

        private void startSymbol()
        {
            if (_symInBlock < 0 || _symInBlock >= _spb)
            {
                if (!nextBlock()) { _blocksDone = true; return; }
                _symInBlock = 0;
            }
            int tone = Walsh.Gray(_enc.Output[_symInBlock++]);
            int bin = Reverse ? _fcRev - 2 - 2 * tone : _fc + 2 * tone;
            double f = bin * _binHz;
            double phase = _started ? _lastPhase + 2 * Math.PI * _lastFreq * _hopSamples / Rate + (_rnd.Next(2) == 0 ? Math.PI / 2 : -Math.PI / 2) : 0;
            _started = true;
            _bursts.Add(new Burst { Start = _nextStart, Freq = f, Phase = phase });
            _lastFreq = f;
            _lastPhase = phase % (2 * Math.PI);
            _nextStart += _hopSamples;
        }

        // the next block's characters: what's queued (NULs making up the rest); false when it's time to stop
        private bool nextBlock()
        {
            int[] chars = new int[_bits];
            lock (_q)
            {
                if (_finishing && _q.Count == 0)
                {
                    if (_flushLeft < 0) _flushLeft = FlushBlocks;
                    if (_flushLeft-- == 0) return false;
                }
                for (int i = 0; i < _bits && _q.Count > 0; i++)
                {
                    chars[i] = _q.Dequeue();
                    char e = _echo.Dequeue();
                    if (e != '\0') OnSent(e);
                }
            }
            _enc.Encode(chars);
            return true;
        }
    }
}
