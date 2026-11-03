/*  MfskModes.cs

The modes, as fldigi defines them. Three families of multi-tone FSK with forward error correction:

  Olivia     2^n tones (4 - 64) in 125 - 2000 Hz. Each block of n characters (7 bits each) is spread by a Walsh
             (Hadamard) code over 64 symbols and scrambled, so every character is in every symbol. Very weak signals,
             deep fading, and tuning off by a few tones don't matter. Slow.
  Contestia  Olivia with 6-bit characters (upper case, figures, a little punctuation): blocks of 32 symbols, about
             twice as fast for the same tones and bandwidth.
  MFSK       16 tones (8 or 32 for some), one at a time a symbol: 4 coded bits a symbol (3 or 5), the same K = 7
             code and interleaver as THOR, and MFSK varicode. MFSK16 is the common one.

Olivia / Contestia: a symbol is SymbolLen samples at 8000 a second, the symbols SymbolLen / 2 apart (they overlap, each
shaped by a raised cosine), the tones 2 FFT bins (Bandwidth / Tones) apart.
MFSK: a symbol is SymbolLen samples at Rate (8000 or 11025), the tones Rate / SymbolLen apart.

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

namespace Kainos.Mfsk
{
    internal enum Family { Olivia, Contestia, Mfsk }

    internal sealed class MfskMode
    {
        public readonly string Name;        // as fldigi names it: "Olivia 8/500", "Contestia 16/1000", "MFSK16"
        public readonly Family Family;
        public readonly int Tones;
        public readonly int Rate;           // samples a second the mode is defined at
        public readonly int SymbolLen;      // samples a symbol at Rate (Olivia / Contestia: the FFT, twice the step)
        public readonly int Bits;           // bits a symbol
        // Olivia / Contestia
        public readonly int NominalBw;      // 125 - 2000
        // MFSK
        public readonly int Interleave;     // interleaver depth (Bits x Bits blocks)
        public readonly int Preamble;       // fldigi's "preamble" bits: zeros through the encoder at the start, and to flush at the end

        private MfskMode(string name, Family f, int tones, int rate, int symbolLen, int nominalBw = 0, int interleave = 0, int preamble = 0)
        {
            Name = name; Family = f; Tones = tones; Rate = rate; SymbolLen = symbolLen; NominalBw = nominalBw;
            Interleave = interleave; Preamble = preamble;
            int b = 0;
            while ((1 << b) < tones) b++;
            Bits = b;
        }

        private static MfskMode olivia(Family f, int tones, int bw)
        {
            int bits = 0;
            while ((1 << bits) < tones) bits++;
            int bwBits = 0;
            while ((125 << bwBits) < bw) bwBits++;
            int symbolLen = 1 << (bits + 7 - bwBits);
            return new MfskMode((f == Family.Olivia ? "Olivia " : "Contestia ") + tones + "/" + bw, f, tones, 8000, symbolLen, bw);
        }

        private static MfskMode mfsk(string name, int rate, int symbolLen, int tones, int interleave, int preamble)
        {
            return new MfskMode(name, Family.Mfsk, tones, rate, symbolLen, 0, interleave, preamble);
        }

        public bool Fec { get { return Family != Family.Mfsk; } }        // Olivia's block code (MFSK's is convolutional)
        public bool Contestia { get { return Family == Family.Contestia; } }

        // the step from one symbol to the next (Olivia's symbols overlap by half)
        public int Step { get { return Family == Family.Mfsk ? SymbolLen : SymbolLen / 2; } }
        public double Baud { get { return Rate / (double)Step; } }
        public double ToneSpacing { get { return Family == Family.Mfsk ? Rate / (double)SymbolLen : 2.0 * Rate / SymbolLen; } }
        // what it takes up on the waterfall
        public double Bandwidth { get { return Family == Family.Mfsk ? Tones * ToneSpacing : NominalBw; } }

        // Olivia / Contestia: symbols in a block, and characters a second
        public int BlockSymbols { get { return Contestia ? 32 : 64; } }
        public double CharsPerSecond { get { return Family == Family.Mfsk ? 0 : Bits * Baud / BlockSymbols; } }

        public static readonly MfskMode[] All = build();

        private static MfskMode[] build()
        {
            System.Collections.Generic.List<MfskMode> l = new System.Collections.Generic.List<MfskMode>();
            int[][] olv = { new[] { 4, 125 }, new[] { 4, 250 }, new[] { 4, 500 }, new[] { 4, 1000 }, new[] { 4, 2000 },
                            new[] { 8, 125 }, new[] { 8, 250 }, new[] { 8, 500 }, new[] { 8, 1000 }, new[] { 8, 2000 },
                            new[] { 16, 500 }, new[] { 16, 1000 }, new[] { 16, 2000 }, new[] { 32, 1000 }, new[] { 32, 2000 },
                            new[] { 64, 500 }, new[] { 64, 1000 }, new[] { 64, 2000 } };
            foreach (int[] m in olv) l.Add(olivia(Family.Olivia, m[0], m[1]));
            int[][] cnt = { new[] { 4, 125 }, new[] { 4, 250 }, new[] { 4, 500 }, new[] { 4, 1000 }, new[] { 4, 2000 },
                            new[] { 8, 125 }, new[] { 8, 250 }, new[] { 8, 500 }, new[] { 8, 1000 }, new[] { 8, 2000 },
                            new[] { 16, 250 }, new[] { 16, 500 }, new[] { 16, 1000 }, new[] { 16, 2000 }, new[] { 32, 1000 }, new[] { 32, 2000 },
                            new[] { 64, 500 }, new[] { 64, 1000 }, new[] { 64, 2000 } };
            foreach (int[] m in cnt) l.Add(olivia(Family.Contestia, m[0], m[1]));
            l.Add(mfsk("MFSK4", 8000, 2048, 32, 5, 107));
            l.Add(mfsk("MFSK8", 8000, 1024, 32, 5, 107));
            l.Add(mfsk("MFSK11", 11025, 1024, 16, 10, 107));
            l.Add(mfsk("MFSK16", 8000, 512, 16, 10, 107));
            l.Add(mfsk("MFSK22", 11025, 512, 16, 10, 107));
            l.Add(mfsk("MFSK31", 8000, 256, 8, 10, 107));
            l.Add(mfsk("MFSK32", 8000, 256, 16, 10, 107));
            l.Add(mfsk("MFSK64", 8000, 128, 16, 10, 180));
            l.Add(mfsk("MFSK64L", 8000, 128, 16, 400, 2500));
            l.Add(mfsk("MFSK128", 8000, 64, 16, 20, 214));
            l.Add(mfsk("MFSK128L", 8000, 64, 16, 800, 5000));
            return l.ToArray();
        }

        public static MfskMode ByName(string name)
        {
            foreach (MfskMode m in All) if (string.Equals(m.Name, name, System.StringComparison.OrdinalIgnoreCase)) return m;
            foreach (MfskMode m in All) if (string.Equals(m.Name.Replace(" ", "").Replace("/", "-"), (name ?? "").Replace(" ", "").Replace("/", "-"), System.StringComparison.OrdinalIgnoreCase)) return m;
            return ByName("Olivia 8/500");
        }

        // ADIF: MODE OLIVIA (submode OLIVIA 8/500...), CONTESTI (no submodes), MFSK (submode MFSK16...)
        public string AdifMode { get { return Family == Family.Olivia ? "OLIVIA" : Family == Family.Contestia ? "CONTESTI" : "MFSK"; } }
        public string AdifSubmode { get { return Family == Family.Olivia ? "OLIVIA " + Tones + "/" + NominalBw : Family == Family.Mfsk ? Name : ""; } }

        public override string ToString() { return Name; }
    }
}
