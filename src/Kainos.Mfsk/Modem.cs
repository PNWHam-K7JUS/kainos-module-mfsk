/*  Modem.cs

What the engine needs of a receiver and a transmitter, whichever family the mode is (OliviaRx / OliviaTx, MfskRx /
MfskTx).

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Mfsk
{
    internal abstract class ModemRx
    {
        public readonly MfskMode Mode;
        public readonly double Frequency;   // the signal's centre (Hz, audio)
        public bool Reverse;                // the tones the other way up (the other sideband)
        public int Squelch = 10;            // the metric (0 - 100) text shows from; 0: always
        public double Metric;               // 0 - 100
        public double SnrDb;
        public double FreqOffset;           // Hz the signal was found off the frequency
        public bool Dcd { get { return Squelch <= 0 || Metric >= Squelch; } }
        public event Action<string> Text;   // decoded text (on the caller's thread)

        protected ModemRx(MfskMode mode, double frequency) { Mode = mode; Frequency = frequency; }

        // samples at the mode's rate
        public abstract void Process(float[] a, int count);

        protected void Emit(string s) { if (!string.IsNullOrEmpty(s)) Text?.Invoke(s); }

        public static ModemRx Create(MfskMode mode, double frequency)
        {
            if (mode.Family == Family.Mfsk) return new MfskRx(mode, frequency);
            return new OliviaRx(mode, frequency);
        }
    }

    internal abstract class ModemTx
    {
        public readonly MfskMode Mode;
        public readonly double Frequency;           // the signal's centre (Hz, audio)
        public readonly bool Reverse;
        protected readonly int Rate;                // the transmit audio's samples a second

        public bool Done { get; protected set; }
        public event Action<char> Sent;             // a character as it goes out (on the caller's thread)

        protected ModemTx(MfskMode mode, double frequency, bool reverse, int rate) { Mode = mode; Frequency = frequency; Reverse = reverse; Rate = rate; }

        public abstract void Add(string s);
        public abstract int Queued { get; }
        public abstract void Finish();
        public abstract bool Finishing { get; }
        // the next n samples of audio, amplitude up to 1; 0 when it's finished
        public abstract int Generate(float[] dst, int n);

        protected void OnSent(char c) { Sent?.Invoke(c); }

        public static ModemTx Create(MfskMode mode, double frequency, bool reverse, int rate)
        {
            if (mode.Family == Family.Mfsk) return new MfskTx(mode, frequency, reverse, rate);
            return new OliviaTx(mode, frequency, reverse, rate);
        }
    }
}
