/*  OliviaRx.cs

An Olivia / Contestia receiver, as fldigi's (Pawel Jalocha's MFSK_Receiver in pj_mfsk.h), at 8000 samples a second:

  1. The input processor: 16-symbol overlapping FFT windows, each bin brought to the average around it (a carrier or a
     strong neighbour is pulled down to the noise), then back to audio with its peaks clipped. It's what lets Olivia
     print under a carrier or through static.
  2. The demodulator: an FFT a symbol long every half step (each symbol is seen at two timings), the energy of each
     bin across the tones and a margin either side.
  3. A soft decoder for every timing (two a symbol, a block's worth) and every frequency offset (the margin, in half
     tones) on each half step: the decoder that has been strongest over the last few blocks is believed, and its
     block printed if it stands far enough above the noise. So the receiver finds the signal's timing and tuning for
     itself, within the margin (8 bins: 4 tones either way).

The text comes a few blocks behind the signal (the decision waits to see which decoder stays strongest).

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace Kainos.Mfsk
{
    internal sealed class OliviaRx : ModemRx
    {
        public const int SyncMargin = 8, SyncIntegLen = 4;
        private const int Slices = 2;

        private readonly int _n, _step, _bits, _tones, _spb, _firstCarrier, _margin, _width, _offsets, _phases;
        private readonly bool _contestia;
        private readonly InputProcessor _ip;
        private readonly double[] _ipOut;
        private int _ipFill;
        private readonly float[] _ipIn;

        // demodulator
        private readonly double[] _tap, _shape, _re, _im;
        private int _tapPtr;
        private readonly double[][] _energy = { null, null };
        private bool _rev;

        // decoders and sync
        private readonly OliviaDecoder[] _dec;
        private readonly LowPass3[][] _syncSignal, _syncNoise;
        private readonly ulong[][][] _pipe;     // [phase][row][offset]: the decoded block, packed a byte a character
        private readonly double[][][] _pipeSnr; // and that decoder's own S/N for it then
        private readonly int[] _pipeRow;
        public static double BlockSnrMin = 3.7;     // a block printed must stand this far above the noise itself (Contestia's
                                                    // blocks are half as long: their S/N reads 1/sqrt 2 as high)
        public static Action<double, double> BlockSnrSeen;  // (self-test) the own S/N of each block that passes the squelch
        private int _phase;
        private double _bestSignal, _snr;
        private int _bestPhase, _bestOffset;
        private readonly double[] _sym;
        private bool _escape;
        private readonly VaricodeReader _text = new VaricodeReader();

        public double SyncSnr { get { return _snr; } }

        public OliviaRx(MfskMode mode, double frequency) : base(mode, frequency)
        {
            _contestia = mode.Contestia;
            _n = mode.SymbolLen;
            _step = _n / 2;
            _bits = mode.Bits;
            _tones = mode.Tones;
            _spb = mode.BlockSymbols;
            double fc = _contestia ? mode.NominalBw / 2.0 : mode.NominalBw * (1.0 - 0.5 / _tones) / 2.0;
            // fldigi: (frequency -/+ fc) / 500 * (SymbolLen / 16), truncated, + 1 (the tones up, or down from there less 2)
            _firstCarrier = (int)((_n / 16) * ((frequency - fc) / 500.0)) + 1;
            _firstCarrierRev = (int)((_n / 16) * ((frequency + fc) / 500.0)) + 1;
            _margin = SyncMargin;
            int low = Math.Min(_firstCarrier, _firstCarrierRev - 2 - 2 * (_tones - 1));
            if (_margin > low) _margin = Math.Max(0, low);
            _width = (_tones * 2 - 1) + 2 * _margin;
            _offsets = 2 * _margin + 1;
            _phases = Slices * _spb;

            _ip = new InputProcessor(16 * _n);
            _ipIn = new float[16 * _n];
            _ipOut = new double[16 * _n];

            _tap = new double[_n];
            _shape = new double[_n];
            for (int t = 0; t < _n; t++) _shape[t] = (1 - Math.Cos(2 * Math.PI * t / _n)) / _n;
            _re = new double[_n]; _im = new double[_n];
            _energy[0] = new double[_width];
            _energy[1] = new double[_width];

            _dec = new OliviaDecoder[Slices * _offsets];
            for (int i = 0; i < _dec.Length; i++) _dec[i] = new OliviaDecoder(_bits, _contestia);
            _syncSignal = new LowPass3[_phases][];
            _syncNoise = new LowPass3[_phases][];
            _pipe = new ulong[_phases][][];
            _pipeSnr = new double[_phases][][];
            _pipeRow = new int[_phases];
            for (int p = 0; p < _phases; p++)
            {
                _syncSignal[p] = new LowPass3[_offsets];
                _syncNoise[p] = new LowPass3[_offsets];
                _pipe[p] = new ulong[SyncIntegLen][];
                _pipeSnr[p] = new double[SyncIntegLen][];
                for (int r = 0; r < SyncIntegLen; r++) { _pipe[p][r] = new ulong[_offsets]; _pipeSnr[p][r] = new double[_offsets]; }
            }
            _sym = new double[_bits];
        }

        private readonly int _firstCarrierRev;

        public override void Process(float[] a, int count)
        {
            _rev = Reverse;
            for (int i = 0; i < count; i++)
            {
                _ipIn[_ipFill++] = a[i];
                if (_ipFill == _ipIn.Length)
                {
                    _ipFill = 0;
                    _ip.Process(_ipIn, _ipOut);
                    for (int s = 0; s < _ipOut.Length; s += _step) symbol(_ipOut, s);
                }
            }
        }

        // one step of audio (half a symbol): two FFTs, a quarter symbol apart
        private void symbol(double[] x, int at)
        {
            int q = _step / 2, wrap = _n - 1;
            for (int i = 0; i < q; i++) { _tap[_tapPtr] = x[at + i]; _tapPtr = (_tapPtr + 1) & wrap; }
            for (int t = 0; t < _n; t++) { _re[t] = _tap[_tapPtr] * _shape[t]; _tapPtr = (_tapPtr + 1) & wrap; }
            for (int i = q; i < _step; i++) { _tap[_tapPtr] = x[at + i]; _tapPtr = (_tapPtr + 1) & wrap; }
            for (int t = 0; t < _n; t++) { _im[t] = _tap[_tapPtr] * _shape[t]; _tapPtr = (_tapPtr + 1) & wrap; }
            Fft.Transform(_re, _im);
            // two real FFTs from one complex: A = (X[k] + X*[N-k]) / 2, B = (X[k] - X*[N-k]) / 2j
            for (int i = 0; i < _width; i++)
            {
                int k = _rev ? _firstCarrierRev - 2 + _margin - i : _firstCarrier - _margin + i;
                if (k <= 0 || k >= _n / 2) { _energy[0][i] = _energy[1][i] = 0; continue; }
                int m = _n - k;
                double ar = (_re[k] + _re[m]) / 2, ai = (_im[k] - _im[m]) / 2;
                double br = (_im[k] + _im[m]) / 2, bi = -(_re[k] - _re[m]) / 2;
                _energy[0][i] = ar * ar + ai * ai;
                _energy[1][i] = br * br + bi * bi;
            }
            for (int slice = 0; slice < Slices; slice++) decode(slice);
        }

        private void softDecode(int slice, int offset)
        {
            double[] e = _energy[slice];
            Array.Clear(_sym, 0, _bits);
            double total = 0;
            int at = _margin + offset;
            for (int i = 0; i < _tones; i++)
            {
                int s = Walsh.Binary(i);
                double v = e[at + 2 * i];
                v *= v;
                total += v;
                for (int b = 0; b < _bits; b++) _sym[b] += (s & (1 << b)) != 0 ? -v : v;
            }
            if (total > 0) for (int b = 0; b < _bits; b++) _sym[b] /= total;
        }

        private void decode(int slice)
        {
            LowPass3[] noise = _syncNoise[_phase], signal = _syncSignal[_phase];
            ulong[] row = _pipe[_phase][_pipeRow[_phase]];
            double[] rowSnr = _pipeSnr[_phase][_pipeRow[_phase]];
            int bestOff = 0;
            double best = 0, w = 1.0 / SyncIntegLen;
            for (int o = 0; o < _offsets; o++)
            {
                softDecode(slice, o - _offsets / 2);
                OliviaDecoder d = _dec[slice * _offsets + o];
                d.Input(_sym);
                d.Process();
                ulong packed = 0;
                for (int f = _bits - 1; f >= 0; f--) packed = (packed << 8) | (byte)d.Chars[f];
                row[o] = packed;
                rowSnr[o] = d.NoiseEnergy > 0 ? d.Signal / Math.Sqrt(d.NoiseEnergy) : 0;
                noise[o].Process(d.NoiseEnergy, w);
                signal[o].Process(d.Signal, w);
                if (signal[o].Output > best) { best = signal[o].Output; bestOff = o; }
            }
            _pipeRow[_phase] = (_pipeRow[_phase] + 1) % SyncIntegLen;

            if (_phase == _bestPhase) { _bestSignal = best; _bestOffset = bestOff; }
            else if (best > _bestSignal) { _bestSignal = best; _bestPhase = _phase; _bestOffset = bestOff; }

            int dist = _phase - _bestPhase;
            if (dist < 0) dist += _phases;
            if (dist == _phases / 2)
            {
                // the noise as fldigi takes it: the next timing's first offset (out of step with the signal)
                double bn = Math.Sqrt(_syncNoise[(_phase + 1) % _phases][0].Output);
                _snr = bn <= 0 ? 0 : _bestSignal / bn;
                Metric = Math.Max(0, Math.Min(100, 10 * (_snr - 3)));
                FreqOffset = (_bestOffset - _offsets / 2) * (8000.0 / _n) * (_rev ? -1 : 1);
                if (Dcd)        // S/N at least 3 + squelch / 10 (fldigi: / 5); squelch 0 prints everything
                {
                    ulong block = _pipe[_bestPhase][_pipeRow[_bestPhase]][_bestOffset];
                    double own = _pipeSnr[_bestPhase][_pipeRow[_bestPhase]][_bestOffset];
                    BlockSnrSeen?.Invoke(own, _snr);
                    // the S/N above is the decoders' average over the last few blocks, so it stays up for a block or
                    // two after a signal ends: the block itself must be clear of the noise too
                    if (Squelch > 0 && own < BlockSnrMin * (_contestia ? 0.73 : 1)) block = 0;
                    StringBuilder sb = new StringBuilder();
                    for (int b = 0; b < _bits; b++) { put((int)(block & 0xFF), sb); block >>= 8; }
                    Emit(sb.ToString());
                }
                if (_snr > 100) _snr = 0;
            }
            if (++_phase >= _phases) _phase = 0;
        }

        // a received character: Olivia's 8-bit escape (127 then the low 7 bits), then as the other modes read bytes
        // (UTF-8 put together, CR / LF to a new line, other control codes dropped)
        private void put(int c, StringBuilder sb)
        {
            if (!_contestia)
            {
                if (_escape) { _escape = false; c += 128; }
                else if (c == 127) { _escape = true; return; }
            }
            if (c == 0) return;
            string s = _text.Byte(c);
            if (s != null) sb.Append(s);
        }
    }

    // fldigi's MFSK_InputProcessor: overlapping windows (each sqrt-raised-cosine, half overlapped), every bin's energy
    // brought to its neighbourhood's (peaks limited three times at 4x a 1/64-window average, then smoothed twice and
    // divided out), back to audio, peaks clipped at 2.5x RMS (twice)
    internal sealed class InputProcessor
    {
        private readonly int _len, _half, _specLen;
        private readonly double[] _inTap, _outTap, _win, _re, _im, _energy, _box;
        private int _inPtr, _outPtr;

        public InputProcessor(int windowLen)
        {
            _len = windowLen;
            _half = windowLen / 2;
            _specLen = windowLen / 2;
            _inTap = new double[_len];
            _outTap = new double[_len];
            _win = new double[_len];
            for (int i = 0; i < _len; i++) _win[i] = Math.Sqrt((1 - Math.Cos(2 * Math.PI * i / _len)) / 2);
            _re = new double[_len];
            _im = new double[_len];
            _energy = new double[_specLen];
            _box = new double[_len];
        }

        // a window's length of audio in, the same out (a window behind)
        public void Process(float[] input, double[] output)
        {
            half(input, 0, output, 0);
            half(input, _half, output, _half);
            limitOutput(output, 2.5);
            limitOutput(output, 2.5);
        }

        private void half(float[] input, int at, double[] output, int outAt)
        {
            int wrap = _len - 1;
            for (int i = 0; i < _half; i++) { _inTap[_inPtr] = input[at + i]; _inPtr = (_inPtr + 1) & wrap; }
            for (int t = 0; t < _len; t++) { _re[t] = _inTap[_inPtr] * _win[t]; _im[t] = 0; _inPtr = (_inPtr + 1) & wrap; }
            Fft.Transform(_re, _im);
            spectra();
            // back to time: the conjugate-symmetric spectrum, inverse FFT (conjugate, forward, conjugate, / N)
            _re[0] = 0; _im[0] = 0;
            _re[_specLen] = 0; _im[_specLen] = 0;
            for (int k = 1; k < _specLen; k++) { _re[_len - k] = _re[k]; _im[_len - k] = -_im[k]; }
            for (int k = 0; k < _len; k++) _im[k] = -_im[k];
            Fft.Transform(_re, _im);
            for (int t = 0; t < _len; t++)
            {
                _outTap[_outPtr] += _re[t] / _len * _win[t];
                _outPtr = (_outPtr + 1) & wrap;
            }
            for (int i = 0; i < _half; i++)
            {
                output[outAt + i] = _outTap[_outPtr];
                _outTap[_outPtr] = 0;
                _outPtr = (_outPtr + 1) & wrap;
            }
        }

        private void spectra()
        {
            for (int k = 0; k < _specLen; k++) _energy[k] = _re[k] * _re[k] + _im[k] * _im[k];
            limitPeaks(_len / 64, 4.0);
            limitPeaks(_len / 64, 4.0);
            limitPeaks(_len / 64, 4.0);
            average(_len / 96);
            average(_len / 64);
            for (int k = 0; k < _specLen; k++)
            {
                double c = _energy[k];
                if (c <= 0) continue;
                c = 1.0 / Math.Sqrt(c);
                _re[k] *= c; _im[k] *= c;
            }
        }

        // a moving sum over len bins (fldigi's BoxFilter)
        private void limitPeaks(int len, double threshold)
        {
            int maxF = 3 * (_specLen / 4), f, idx, ptr = 0;
            double sum = 0;
            Array.Clear(_box, 0, len);
            for (f = 0; f < len; f++) { sum += _energy[f] - _box[ptr]; _box[ptr] = _energy[f]; if (++ptr >= len) ptr = 0; }
            for (idx = len / 2; f < maxF; f++, idx++)
            {
                sum += _energy[f] - _box[ptr]; _box[ptr] = _energy[f]; if (++ptr >= len) ptr = 0;
                double s = _energy[idx], limit = sum / len * threshold;
                if (s > limit)
                {
                    double k = Math.Sqrt(limit / s);
                    _re[idx] *= k; _im[idx] *= k;
                    _energy[idx] = limit;
                }
            }
        }

        private void average(int len)
        {
            int maxF = 3 * (_specLen / 4), half = len / 2, f, idx, ptr = 0;
            double sum = 0, scale = 1.0 / len;
            Array.Clear(_box, 0, len);
            for (f = 0; f < len; f++) { sum += _energy[f] - _box[ptr]; _box[ptr] = _energy[f]; if (++ptr >= len) ptr = 0; }
            for (idx = 0; idx < half; idx++) _energy[idx] = sum * scale;
            for (; f < maxF; f++, idx++)
            {
                sum += _energy[f] - _box[ptr]; _box[ptr] = _energy[f]; if (++ptr >= len) ptr = 0;
                _energy[idx] = sum * scale;
            }
            for (; idx < _specLen; idx++) _energy[idx] = sum * scale;
        }

        private void limitOutput(double[] o, double threshold)
        {
            double rms = 0;
            for (int i = 0; i < _len; i++) rms += o[i] * o[i];
            double limit = Math.Sqrt(rms / _len) * threshold;
            for (int i = 0; i < _len; i++)
            {
                if (o[i] > limit) o[i] = limit;
                else if (o[i] < -limit) o[i] = -limit;
            }
        }
    }
}
