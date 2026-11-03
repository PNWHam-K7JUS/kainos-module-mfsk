/*  OliviaCode.cs

Olivia's and Contestia's block code, as Pawel Jalocha wrote it (fldigi's pj_mfsk.h). A block carries one character
for each bit of a symbol (n characters for 2^n tones). Each character picks one of 64 (Contestia 32) Walsh functions,
+ or -, which is 64 (32) bits long: one bit of each symbol, the character's bit rotating through the symbol's bits from
one symbol to the next. Each is scrambled by a fixed code, shifted 13 (Contestia 5) bits per character, so a steady
signal or a carrier doesn't look like a character. The receiver undoes the scrambling and takes the strongest output
of a Hadamard transform: the character, and how sure it is.

Olivia's characters are 7 bits (8-bit ones go as 127 then the low 7 bits); Contestia's are 6: upper case, figures and
some punctuation, space, CR and backspace.

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Mfsk
{
    internal static class Walsh
    {
        public static int Gray(int b) { return b ^ (b >> 1); }
        public static int Binary(int g) { g ^= g >> 4; g ^= g >> 2; g ^= g >> 1; return g; }

        // the forward fast Hadamard transform, as fldigi's (its pairs: (b + a, b - a))
        public static void Fht(double[] d, int len)
        {
            for (int step = 1; step < len; step *= 2)
                for (int p = 0; p < len; p += 2 * step)
                    for (int q = p; q - p < step; q++)
                    {
                        double a = d[q], b = d[q + step];
                        d[q] = b + a;
                        d[q + step] = b - a;
                    }
        }

        public static void Ifht(int[] d, int len)
        {
            for (int step = len / 2; step > 0; step /= 2)
                for (int p = 0; p < len; p += 2 * step)
                    for (int q = p; q - p < step; q++)
                    {
                        int a = d[q], b = d[q + step];
                        d[q] = a - b;
                        d[q + step] = a + b;
                    }
        }
    }

    internal sealed class OliviaEncoder
    {
        private const ulong CodeOlivia = 0xE257E6D0291574ECUL, CodeContestia = 0xEDB88320UL;
        public readonly int BitsPerSymbol, SymbolsPerBlock;
        private readonly bool _contestia;
        private readonly ulong _code;
        private readonly int[] _fht;
        public readonly int[] Output;           // the block's symbols (before Gray coding)

        public OliviaEncoder(int bitsPerSymbol, bool contestia)
        {
            BitsPerSymbol = bitsPerSymbol;
            _contestia = contestia;
            _code = contestia ? CodeContestia : CodeOlivia;
            SymbolsPerBlock = contestia ? 32 : 64;
            _fht = new int[SymbolsPerBlock];
            Output = new int[SymbolsPerBlock];
        }

        // Contestia's 6-bit alphabet
        public static int ContestiaCode(int c)
        {
            if (c >= 'a' && c <= 'z') c += 'A' - 'a';
            if (c == ' ') return 59;
            if (c == '\r') return 60;
            if (c == '\n' || c == 0) return 0;
            if (c >= 33 && c <= 90) return c - 32;
            if (c == 8) return 61;
            return '?' - 32;
        }

        public static int ContestiaChar(int code)
        {
            if (code == 0) return 0;
            if (code == 59) return ' ';
            if (code == 60) return '\r';
            if (code == 61) return 8;
            return code + 32;
        }

        private void character(int c)
        {
            int spb = SymbolsPerBlock;
            c = _contestia ? ContestiaCode(c) : c & (2 * spb - 1);
            Array.Clear(_fht, 0, spb);
            if (c < spb) _fht[c] = 1; else _fht[c - spb] = -1;
            Walsh.Ifht(_fht, spb);
        }

        private void scramble(int offset)
        {
            int wrap = SymbolsPerBlock - 1, bit = offset & wrap;
            for (int t = 0; t < SymbolsPerBlock; t++)
            {
                if ((_code & (1UL << bit)) != 0) _fht[t] = -_fht[t];
                bit = (bit + 1) & wrap;
            }
        }

        // a block of BitsPerSymbol characters to SymbolsPerBlock symbols
        public void Encode(int[] chars)
        {
            int shift = _contestia ? 5 : 13;
            Array.Clear(Output, 0, SymbolsPerBlock);
            for (int f = 0; f < BitsPerSymbol; f++)
            {
                character(chars[f]);
                scramble(f * shift);
                int rot = 0;
                for (int t = 0; t < SymbolsPerBlock; t++)
                {
                    if (_fht[t] < 0)
                    {
                        int bit = f + rot;
                        if (bit >= BitsPerSymbol) bit -= BitsPerSymbol;
                        Output[t] |= 1 << bit;
                    }
                    if (++rot >= BitsPerSymbol) rot -= BitsPerSymbol;
                }
            }
        }
    }

    // One soft decoder: the last block's worth of soft bits, decoded afresh at each symbol (the receiver keeps one for
    // each frequency offset and half-symbol timing, and believes the best)
    internal sealed class OliviaDecoder
    {
        private const ulong CodeOlivia = 0xE257E6D0291574ECUL, CodeContestia = 0xEDB88320UL;
        private readonly int _bits, _spb, _len;
        private readonly bool _contestia;
        private readonly ulong _code;
        private readonly double[] _in, _fht;
        private int _ptr;
        public double Signal, NoiseEnergy;
        public readonly int[] Chars;

        public OliviaDecoder(int bitsPerSymbol, bool contestia)
        {
            _bits = bitsPerSymbol;
            _contestia = contestia;
            _code = contestia ? CodeContestia : CodeOlivia;
            _spb = contestia ? 32 : 64;
            _len = _spb * _bits;
            _in = new double[_len];
            _fht = new double[_spb];
            Chars = new int[_bits];
        }

        public void Reset() { Array.Clear(_in, 0, _len); _ptr = 0; }

        public void Input(double[] sym)
        {
            for (int f = 0; f < _bits; f++) _in[_ptr++] = sym[f];
            if (_ptr >= _len) _ptr -= _len;
        }

        private void character(int f)
        {
            int ptr = _ptr, rot = f, wrap = _spb - 1;
            int bit = (f * (_contestia ? 5 : 13)) & wrap;
            for (int t = 0; t < _spb; t++)
            {
                double v = _in[ptr + rot];
                if ((_code & (1UL << bit)) != 0) v = -v;
                _fht[t] = v;
                bit = (bit + 1) & wrap;
                if (++rot >= _bits) rot -= _bits;
                ptr += _bits;
                if (ptr >= _len) ptr -= _len;
            }
            Walsh.Fht(_fht, _spb);
            double peak = 0, sq = 0;
            int pos = 0;
            for (int t = 0; t < _spb; t++)
            {
                double s = _fht[t];
                sq += s * s;
                if (Math.Abs(s) > Math.Abs(peak)) { peak = s; pos = t; }
            }
            int c = pos;
            if (peak < 0) c += _spb;
            sq -= peak * peak;
            Chars[f] = _contestia ? OliviaEncoder.ContestiaChar(c) : c;
            NoiseEnergy += sq / (_spb - 1);
            Signal += Math.Abs(peak);
        }

        public void Process()
        {
            Signal = 0;
            NoiseEnergy = 0;
            for (int f = 0; f < _bits; f++) character(f);
            Signal /= _bits;
            NoiseEnergy /= _bits;
        }
    }

    // fldigi's three-pole low-pass (LowPass3_Filter)
    internal struct LowPass3
    {
        public double Out1, Out2, Output;
        public void Process(double inp, double weight, double feedback = 0.1)
        {
            weight *= 2.0;
            double d1 = inp - Out1, d12 = Out1 - Out2, d23 = Out2 - Output;
            Out1 += d1 * weight;
            Out2 += d12 * weight;
            d23 *= weight;
            Output += d23;
            Out2 += d23 * feedback;
        }
    }
}
