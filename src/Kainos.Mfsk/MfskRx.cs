/*  MfskRx.cs

An MFSK receiver, as fldigi's: the audio mixed down to around 0 Hz (centred on the frequency) and low-passed, then a
sliding DFT a symbol long on each tone. At each symbol's end the tones' strengths make soft bits (Gray decoded; a tone
that keeps winning however the signal changes, a carrier, is ignored), through the de-interleaver to the K = 7 Viterbi
decoder, read as MFSK varicode. The symbol clock follows where the last tone's bin peaked (it peaks when the DFT
exactly covers the tone); AFC follows the tone's phase from one sample to the next while a tone repeats.

Modes with an odd number of bits a symbol (MFSK4, 8 and 31) don't keep the code's bit pairs lined up with the symbols,
so two decoders a bit apart run, and the better is believed.

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Mfsk
{
    internal sealed class MfskRx : ModemRx
    {
        public bool Afc = true;
        private readonly int _n, _t, _bits, _rate;
        private readonly double _spacing;
        private double _f0, _ncoPhase;
        private readonly CpxFir _lpf;
        private readonly double[] _wRe, _wIm, _oscRe, _oscIm, _cRe, _cIm, _sRe, _sIm;
        private readonly double[] _xRe, _xIm;           // the last symbol's baseband samples
        private int _xPos, _renorm;
        private readonly float[] _pipe;                 // two symbols of every tone's magnitude
        private int _pipePos;
        private readonly double[] _prevRe, _prevIm;     // each tone's DFT a sample ago (for AFC)

        private int _syncCounter;
        private readonly double[] _synAvg = new double[8];
        private int _synIdx, _synCount, _synUpdates, _good;
        private int _cur, _prev1, _prev2;
        private bool _staticBurst;
        private double _afcMetric, _s2n;
        private readonly int[] _cwi;

        private readonly Viterbi _d1, _d2;
        private readonly Interleaver _il;
        private readonly MfskVaricodeReader _r1 = new MfskVaricodeReader(), _r2 = new MfskVaricodeReader();
        private int _symCounter, _pairPrev;
        private double _met1, _met2;
        private readonly byte[] _soft;

        public MfskRx(MfskMode mode, double frequency) : base(mode, frequency)
        {
            _n = mode.SymbolLen;
            _t = mode.Tones;
            _bits = mode.Bits;
            _rate = mode.Rate;
            _spacing = mode.ToneSpacing;
            _f0 = frequency;
            _lpf = new CpxFir(Fir.LowPass(127, Math.Min(0.45, (_t / 2.0 + 2) * _spacing / _rate)));
            _wRe = new double[_t]; _wIm = new double[_t];
            _oscRe = new double[_t]; _oscIm = new double[_t];
            _cRe = new double[_t]; _cIm = new double[_t];
            _sRe = new double[_t]; _sIm = new double[_t];
            _prevRe = new double[_t]; _prevIm = new double[_t];
            for (int k = 0; k < _t; k++)
            {
                double w = 2 * Math.PI * (k - (_t - 1) / 2.0) * _spacing / _rate;
                _wRe[k] = Math.Cos(w); _wIm[k] = -Math.Sin(w);
                _oscRe[k] = 1; _oscIm[k] = 0;
                _cRe[k] = Math.Cos(w * _n); _cIm[k] = Math.Sin(w * _n);
            }
            _xRe = new double[_n]; _xIm = new double[_n];
            _pipe = new float[2 * _n * _t];
            _syncCounter = _n;
            _cwi = new int[_t];
            _d1 = new Viterbi(ConvCode.PskR, 45);
            _d2 = new Viterbi(ConvCode.PskR, 45);
            _il = new Interleaver(_bits, mode.Interleave, true);
            _soft = new byte[_bits];
        }

        public double Tuned { get { return _f0; } }

        public override void Process(float[] a, int count)
        {
            double dph = 2 * Math.PI * _f0 / _rate;
            for (int i = 0; i < count; i++)
            {
                // to baseband: e^(-j w0 n), then the low-pass takes off the image at -2 w0
                Cpx z = new Cpx(a[i] * Math.Cos(_ncoPhase), -a[i] * Math.Sin(_ncoPhase));
                _ncoPhase += dph;
                if (_ncoPhase > 2 * Math.PI) _ncoPhase -= 2 * Math.PI;
                _lpf.Push(z);
                z = _lpf.Output();
                double oldRe = _xRe[_xPos], oldIm = _xIm[_xPos];
                _xRe[_xPos] = z.Re; _xIm[_xPos] = z.Im;
                if (++_xPos == _n) _xPos = 0;
                int row = _pipePos * _t;
                for (int k = 0; k < _t; k++)
                {
                    double oRe = _oscRe[k] * _wRe[k] - _oscIm[k] * _wIm[k];
                    double oIm = _oscRe[k] * _wIm[k] + _oscIm[k] * _wRe[k];
                    _oscRe[k] = oRe; _oscIm[k] = oIm;
                    // the sample a symbol back went in at osc * c
                    double pRe = oRe * _cRe[k] - oIm * _cIm[k], pIm = oRe * _cIm[k] + oIm * _cRe[k];
                    _prevRe[k] = _sRe[k]; _prevIm[k] = _sIm[k];
                    _sRe[k] += (z.Re * oRe - z.Im * oIm) - (oldRe * pRe - oldIm * pIm);
                    _sIm[k] += (z.Re * oIm + z.Im * oRe) - (oldRe * pIm + oldIm * pRe);
                    _pipe[row + k] = (float)Math.Sqrt(_sRe[k] * _sRe[k] + _sIm[k] * _sIm[k]);
                }
                if (++_renorm == 4096)
                {
                    _renorm = 0;
                    for (int k = 0; k < _t; k++)
                    {
                        double m = Math.Sqrt(_oscRe[k] * _oscRe[k] + _oscIm[k] * _oscIm[k]);
                        _oscRe[k] /= m; _oscIm[k] /= m;
                    }
                }
                if (--_syncCounter <= 0)
                {
                    _syncCounter = _n;
                    _cur = hardDecode();
                    softDecode();
                    synchronize();
                    afc();
                    evalSnr();
                    _prev2 = _prev1;
                    _prev1 = _cur;
                    dph = 2 * Math.PI * _f0 / _rate;
                }
                if (++_pipePos == 2 * _n) _pipePos = 0;
            }
        }

        private float mag(int age, int tone)
        {
            int p = _pipePos - age;
            if (p < 0) p += 2 * _n;
            return _pipe[p * _t + tone];
        }

        // the strongest tone (as received: Reverse is applied when it's read as a symbol)
        private int hardDecode()
        {
            double max = 0, avg = 0;
            int sym = 0, burst = 0, row = _pipePos * _t;
            for (int k = 0; k < _t; k++) avg += _pipe[row + k];
            avg /= _t;
            if (avg < 1e-20) avg = 1e-20;
            for (int k = 0; k < _t; k++)
            {
                double v = _pipe[row + k];
                if (v > max) { max = v; sym = k; }
                if (v > 2.0 * avg) burst++;
            }
            _staticBurst = burst == _t;
            _afcMetric = _staticBurst ? 0 : 0.95 * _afcMetric + 0.05 * (2 * max / avg);
            return sym;
        }

        private void softDecode()
        {
            const int CwiMax = 6;
            int row = _pipePos * _t;
            double sum = 0;
            for (int k = 0; k < _t; k++) if (_cwi[k] < CwiMax) sum += _pipe[row + k];
            double avg = sum / _t;
            if (sum < 1e-10) sum = 1e-10;
            // a tone that wins too many times running is a carrier, not the signal (never tone 0)
            for (int k = 1; k < _t; k++)
            {
                int kk = Reverse ? _t - 1 - k : k;
                if (kk == _cur) _cwi[kk]++; else _cwi[kk]--;
                if (_cwi[kk] < 0) _cwi[kk] = 0;
                if (_cwi[kk] > CwiMax) _cwi[kk] = CwiMax + 1;
            }
            double[] b = new double[_bits];
            for (int i = 0; i < _t; i++)
            {
                int j = Walsh.Binary(i);
                int k = Reverse ? _t - 1 - i : i;
                double m = _cwi[k] > CwiMax ? avg : k == _cur ? 2.0 * _pipe[row + k] : _pipe[row + k];
                for (int bit = 0; bit < _bits; bit++) b[bit] += (j & (1 << (_bits - bit - 1))) != 0 ? m : -m;
            }
            for (int i = 0; i < _bits; i++)
                _soft[i] = _staticBurst ? (byte)128 : (byte)Math.Max(0, Math.Min(255, 128.0 + b[i] / sum * 256.0));
            _il.Symbols(_soft);
            for (int i = 0; i < _bits; i++) decodeBit(_soft[i]);
        }

        // the soft bits in pairs to the decoder(s): with an even number a symbol only decoder 2, on the symbol's pairs
        private void decodeBit(int s)
        {
            int prev = _pairPrev;
            _pairPrev = s;
            _symCounter ^= 1;
            bool odd = (_bits & 1) == 1;
            string text = null;
            if (_symCounter == 1)
            {
                if (!odd) return;
                int bit = _d1.Decode(prev, s);
                _met1 += (_d1.Gain - _met1) / 50;
                if (bit < 0) return;
                string t = _r1.Bit(bit);
                if (_met1 >= _met2) { Metric = metric(_met1); text = t; }
            }
            else
            {
                int bit = _d2.Decode(prev, s);
                _met2 += (_d2.Gain - _met2) / 50;
                if (bit < 0) return;
                string t = _r2.Bit(bit);
                if (!odd || _met2 >= _met1) { Metric = metric(_met2); text = t; }
            }
            if (text != null && text != "\0" && Dcd) Emit(text);
        }

        // fldigi's (met - 60) / 2 reads noise as 10 - 40 depending on the tones (fewer tones fit noise better), so
        // here the decoder's gain on noise for the bits a symbol is taken off first: noise reads about 0, a good
        // signal about 100
        private double metric(double met)
        {
            double floor = _bits == 3 ? 136 : _bits == 4 ? 88 : 55;
            return Math.Max(0, Math.Min(100, (met - floor) * 100 / (254 - floor)));
        }

        // the symbol clock: where, over the last two symbols, the last tone's bin peaked; it should be a symbol ago
        private void synchronize()
        {
            _good = _staticBurst ? 0 : _good + 1;
            if (_staticBurst || _cur == _prev1 || _prev1 == _prev2) return;
            if (_synUpdates < 8 && _good < 3) return;
            double max = 0;
            int syn = -1;
            for (int i = 0; i < 2 * _n; i++)
            {
                int age = i == 0 ? 0 : 2 * _n - i;
                double v = mag(age, _prev1);
                if (v > max) { max = v; syn = i; }
            }
            // fast at first (each error halved), so the first characters aren't lost; then as fldigi (the last 8
            // averaged, a tone count's fraction of the error taken off)
            if (_synUpdates < 8)
            {
                _synUpdates++;
                _syncCounter += (int)Math.Floor((syn - _n) * 0.5 + 0.5);
                return;
            }
            _synAvg[_synIdx] = syn;
            _synIdx = (_synIdx + 1) % _synAvg.Length;
            if (_synCount < _synAvg.Length) _synCount++;
            double avg = 0;
            for (int i = 0; i < _synCount; i++) avg += _synAvg[i];
            avg /= _synCount;
            _syncCounter += (int)Math.Floor((avg - _n) / _t + 0.5);
        }

        // AFC: while a tone repeats, the turn of its DFT from one sample to the next is how far off it is
        private void afc()
        {
            if (!Afc || _staticBurst || !Dcd || _afcMetric < 3.0 || _cur != _prev1) return;
            double zRe = _prevRe[_cur] * _sRe[_cur] + _prevIm[_cur] * _sIm[_cur];
            double zIm = _prevRe[_cur] * _sIm[_cur] - _prevIm[_cur] * _sRe[_cur];
            double err = Math.Atan2(zIm, zRe) * _rate / (2 * Math.PI);
            if (Math.Abs(err) < _spacing / 4)
            {
                _f0 += err / 32;
                FreqOffset = _f0 - Frequency;
            }
        }

        private void evalSnr()
        {
            double sig = mag(0, _cur), noise = (_t - 1) * mag(0, _prev2);
            if (noise > 0) _s2n += (sig / noise - _s2n) / 64;
            SnrDb = _s2n > 0 ? 20 * Math.Log10(_s2n) : -30;
        }
    }
}
