/*  Program.cs (self-test)

Checks the Olivia / Contestia / MFSK modems without a radio: Olivia's block code and the MFSK varicode, then every
mode sent and received (clean, with noise, mistuned, on the other sideband, at 44.1 kHz), noise alone printing
nothing, the speed of the heaviest, then the transmit path through the engine, and the window drawn with a signal
playing.

  Kainos.Mfsk.SelfTest.exe [--ui] [--quick]

This file is part of the Kainos Olivia / MFSK module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Kainos.Mfsk.SelfTest
{
    internal static class Program
    {
        private static int _fail;
        private static readonly Random Rnd = new Random(42);

        private static void check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) _fail++;
        }

        internal static double Gauss()
        {
            double u1 = 1.0 - Rnd.NextDouble(), u2 = Rnd.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        private const string Msg = "CQ CQ de K7JUS K7JUS pse K. The quick brown fox jumps over the lazy dog 0123456789";
        private const string Mid = "CQ CQ de K7JUS K7JUS pse K";
        private const string Short = "CQ de K7JUS K";

        // what each mode is tested with: a long text for the quick ones, a short one for the slow
        private static string textFor(MfskMode m)
        {
            if (m.Family == Family.Mfsk) return m.Baud < 10 ? Short : Msg;
            return m.CharsPerSecond < 1.5 ? Short : m.CharsPerSecond < 5 ? Mid : Msg;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            bool quick = args.Contains("--quick");
            if (args.Contains("--diag")) { diag(); return 0; }
            paneFixes();
            if (args.Contains("--pane")) return _fail == 0 ? 0 : 1;
            codes();
            foreach (MfskMode m in MfskMode.All)
            {
                if (quick && m.Name != "Olivia 8/500" && m.Name != "Contestia 8/250" && m.Name != "MFSK16") continue;
                string text = textFor(m);
                if (m.Contestia) text = text.ToUpperInvariant();
                double c0 = Math.Max(1000, m.Bandwidth / 2 + 400);
                string clean = loop(m, text, c0, c0, 30, 48000, false, false);
                check(clean.Contains(text), m.Name + " clean" + show(clean));
                bool slow = m.Family == Family.Mfsk ? m.Baud < 10 : m.CharsPerSecond < 1.5;
                if (slow) continue;                     // the slowest: clean is enough (they take minutes)
                // noise in 2.5 kHz: Olivia reads 31.25 baud (8/500, 32/1000) at -10 dB and needs more for the faster
                // ones (the signal's energy a symbol is less); Contestia 2 dB more; MFSK16 at about -6 dB
                double snr = m.Family == Family.Mfsk ? -6 + 10 * Math.Log10(m.Bandwidth / 280.0)
                           : (m.Contestia ? -8 : -10) + Math.Max(0, 10 * Math.Log10(m.Baud / 31.25));
                string noisy = loop(m, text, Math.Max(1500, c0), Math.Max(1500, c0), snr, 48000, false, false);
                check(noisy.Contains(text.Substring(text.Length / 3)), m.Name + " at " + snr.ToString("0") + " dB SNR in 2.5 kHz" + show(noisy));
                // Olivia finds a signal within 4 tones; MFSK's AFC follows a fraction of a tone
                double off = m.Family == Family.Mfsk ? 0.2 * m.ToneSpacing : 2.5 * m.ToneSpacing;
                // the wide ones: kept inside the audio, and inside the search (Olivia looks as many bins above as it can
                // below, and there's only as much room below as the lowest tone's frequency)
                off = Math.Min(off, Math.Min(3500 - (c0 + 200) - m.Bandwidth / 2, 0.8 * (c0 + 200 - m.Bandwidth / 2)));
                string tuned = loop(m, text, c0 + 200 + off, c0 + 200, 30, 48000, false, false);
                check(tuned.Contains(text), m.Name + " mistuned by " + off.ToString("0.#") + " Hz (" + (off / m.ToneSpacing).ToString("0.#") + " tones)" + show(tuned));
            }
            foreach (string name in new[] { "Olivia 8/500", "MFSK16" })
            {
                MfskMode m = MfskMode.ByName(name);
                string rev = loop(m, Mid, 1000, 1000, 30, 48000, true, true);
                check(rev.Contains(Mid), name + " reversed both ends" + show(rev));
                string wrong = loop(m, Mid, 1000, 1000, 30, 48000, true, false);
                check(!wrong.Contains(Mid.Substring(0, 12)), name + " reversed one end only: not read" + show(wrong));
            }
            string r44 = loop(MfskMode.ByName("MFSK22"), Msg, 900, 900, 30, 44100, false, false);
            check(r44.Contains(Msg), "MFSK22 (11025) at 44.1 kHz" + show(r44));
            string r44o = loop(MfskMode.ByName("Olivia 16/1000"), Mid, 1200, 1200, 30, 44100, false, false);
            check(r44o.Contains(Mid), "Olivia 16/1000 at 44.1 kHz" + show(r44o));
            const string Utf = "73 de Jörg, 5°C\nNext line";
            string utf = loop(MfskMode.ByName("Olivia 32/1000"), Utf, 1200, 1200, 30, 48000, false, false);
            check(utf.Contains(Utf), "Olivia 32/1000 UTF-8 and new lines" + show(utf));
            string utfM = loop(MfskMode.ByName("MFSK32"), Utf, 1000, 1000, 30, 48000, false, false);
            check(utfM.Contains(Utf), "MFSK32 UTF-8 and new lines" + show(utfM));
            string cnt = loop(MfskMode.ByName("Contestia 16/1000"), "cq test de k7jus, 599 001?\nNEXT", 1200, 1200, 30, 48000, false, false);
            check(cnt.Contains("CQ TEST DE K7JUS, 599 001?\nNEXT"), "Contestia 16/1000: lower case sent as upper, punctuation, new lines" + show(cnt));
            noiseOnly("Olivia 8/500");
            noiseOnly("Contestia 8/250");
            noiseOnly("MFSK16");
            speed("Olivia 64/2000");
            speed("Contestia 64/2000");
            speed("MFSK128L");
            bandwidth("Olivia 8/500");
            bandwidth("MFSK16");
            transmit("Olivia 32/1000");
            transmit("MFSK16");
            if (args.Contains("--ui")) ui();
            Console.WriteLine(_fail == 0 ? "All passed" : _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static string show(string s)
        {
            s = s.Replace("\n", "\\n").Replace("\b", "\\b");
            return "   [" + (s.Length > 120 ? s.Substring(0, 60) + " ... " + s.Substring(s.Length - 60) : s) + "]";
        }

        private static void codes()
        {
            // Olivia's and Contestia's block code: every character, through the encoder and decoder, with symbols wrong
            foreach (bool contestia in new[] { false, true })
                foreach (int bits in new[] { 2, 3, 5, 6 })
                {
                    OliviaEncoder enc = new OliviaEncoder(bits, contestia);
                    OliviaDecoder dec = new OliviaDecoder(bits, contestia);
                    int spb = enc.SymbolsPerBlock, bad = 0, wrong = 0, total = 0;
                    int[] chars = new int[bits];
                    string alphabet = contestia ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ,./?-=:;\r" : null;
                    for (int round = 0; round < 40; round++)
                    {
                        for (int i = 0; i < bits; i++) chars[i] = contestia ? alphabet[Rnd.Next(alphabet.Length)] : Rnd.Next(128);
                        enc.Encode(chars);
                        double[] sym = new double[bits];
                        for (int t = 0; t < spb; t++)
                        {
                            int s = enc.Output[t];
                            if (Rnd.NextDouble() < 0.1) { s = Rnd.Next(1 << bits); bad++; }     // a wrong tone
                            for (int b = 0; b < bits; b++) sym[b] = (s & (1 << b)) != 0 ? -1 : 1;
                            dec.Input(sym);
                        }
                        dec.Process();
                        for (int i = 0; i < bits; i++) { total++; if (dec.Chars[i] != chars[i]) wrong++; }
                    }
                    check(wrong == 0, (contestia ? "Contestia" : "Olivia") + " block code, " + (1 << bits) + " tones: " + total + " characters, " + bad + " of " + (40 * spb) + " symbols wrong, " + wrong + " misread");
                }
            bool ok = true;
            for (int b = 0; b < 256; b++)
            {
                List<int> bitsL = new List<int>();
                MfskVaricode.Bits(b, bitsL);
                if (bitsL[0] != 1 || string.Concat(bitsL).Contains("001") || MfskVaricode.Lookup(MfskVaricode.Codes[b]) != b) ok = false;
            }
            check(ok, "MFSK varicode: 256 codes, each starting with 1, none holding 001, each read back");
        }

        // the audio for some text (with a little silence either side)
        internal static float[] TxAudio(MfskMode m, string text, double hz, int rate, bool rev)
        {
            ModemTx tx = ModemTx.Create(m, hz, rev, rate);
            tx.Add(text);
            tx.Finish();
            List<float> a = new List<float>(new float[rate / 2]);
            float[] buf = new float[4096];
            int n;
            while ((n = tx.Generate(buf, buf.Length)) > 0) for (int i = 0; i < n; i++) a.Add(buf[i] * 0.5f);
            // after it: time for the receiver to catch up (Olivia prints a few blocks behind)
            double tail = m.Family == Family.Mfsk ? 8 / m.Baud : 3 * m.BlockSymbols / m.Baud;
            a.AddRange(new float[rate + (int)(tail * rate)]);
            return a.ToArray();
        }

        private static void addNoise(float[] a, double snrDb, int rate)
        {
            double ps = 0.5 * 0.5 * 0.5;        // a tone at amplitude 0.5
            double sigma = Math.Sqrt(ps / Math.Pow(10, snrDb / 10) * (rate / 2.0) / 2500);
            for (int i = 0; i < a.Length; i++) a[i] += (float)(Gauss() * sigma);
        }

        private static string receive(MfskMode m, float[] a, double rxHz, int rate, bool rxRev, int squelch = 0)
        {
            Resampler rs = new Resampler(m.Rate);
            ModemRx rx = ModemRx.Create(m, rxHz);
            rx.Squelch = squelch;
            rx.Reverse = rxRev;
            StringBuilder sb = new StringBuilder();
            rx.Text += s => sb.Append(s);
            float[] o = new float[1400];
            for (int p = 0; p < a.Length; p += 4800)
            {
                int len = Math.Min(4800, a.Length - p);
                float[] part = new float[len];
                Array.Copy(a, p, part, 0, len);
                int k = rs.Process(part, len, rate, o);
                rx.Process(o, k);
            }
            return sb.ToString();
        }

        // sends text and receives it: SNR (dB) in 2500 Hz
        private static string loop(MfskMode m, string text, double txHz, double rxHz, double snrDb, int rate, bool txRev, bool rxRev)
        {
            float[] a = TxAudio(m, text, txHz, rate, txRev);
            addNoise(a, snrDb, rate);
            // Olivia's squelch at its lowest step, as it comes; MFSK's off
            return receive(m, a, rxHz, rate, rxRev, m.Family == Family.Mfsk ? 0 : 10);
        }

        // the metric and S/N over time, for noise and for a weak signal
        private static void diag()
        {
            foreach (string name in new[] { "Olivia 8/500", "Contestia 8/250", "Olivia 32/1000", "Contestia 16/1000" })
                foreach (double snr in new[] { 99.0, -10.0, -12.0, -14.0 })
                {
                    MfskMode m = MfskMode.ByName(name);
                    List<double> own = new List<double>(), syn = new List<double>();
                    OliviaRx.BlockSnrSeen = (v, w) => { own.Add(v); syn.Add(w); };
                    double save = OliviaRx.BlockSnrMin;
                    OliviaRx.BlockSnrMin = 0;
                    float[] a = snr > 90 ? new float[48000 * 90] : TxAudio(m, Mid + Mid, 1500, 48000, false);
                    addNoise(a, snr > 90 ? 0 : snr, 48000);
                    string got = receive(m, a, 1500, 48000, false, 1);
                    OliviaRx.BlockSnrMin = save;
                    OliviaRx.BlockSnrSeen = null;
                    own.Sort(); syn.Sort();
                    Console.WriteLine(name + (snr > 90 ? " noise (squelch 1)" : " at " + snr + " dB") + ": " + own.Count + " blocks past the squelch, own S/N " +
                        (own.Count == 0 ? "-" : own[0].ToString("0.0") + " .. " + own[own.Count / 2].ToString("0.0") + " .. " + own[own.Count - 1].ToString("0.0") + ", sync S/N " + syn[0].ToString("0.0") + " .. " + syn[syn.Count / 2].ToString("0.0")) + show(got));
                }
            foreach (string name in new string[0])
                foreach (double snr in new[] { 99.0, -6.0, -12.0 })
                {
                    MfskMode m = MfskMode.ByName(name);
                    float[] a = snr > 90 ? new float[48000 * 30] : TxAudio(m, Msg + Msg, 1500, 48000, false);
                    addNoise(a, snr > 90 ? 0 : snr, 48000);
                    Resampler rs = new Resampler(m.Rate);
                    ModemRx rx = ModemRx.Create(m, 1500);
                    rx.Squelch = 0;
                    List<double> met = new List<double>(), sn = new List<double>();
                    float[] o = new float[1400];
                    for (int p = 0; p < a.Length; p += 4800)
                    {
                        int len = Math.Min(4800, a.Length - p);
                        float[] part = new float[len];
                        Array.Copy(a, p, part, 0, len);
                        rx.Process(o, rs.Process(part, len, 48000, o));
                        met.Add(rx.Metric); sn.Add(rx.SnrDb);
                    }
                    met.Sort(); sn.Sort();
                    Func<List<double>, string> q = l => l[l.Count / 10].ToString("0.0") + " / " + l[l.Count / 2].ToString("0.0") + " / " + l[l.Count * 9 / 10].ToString("0.0");
                    Console.WriteLine(name + (snr > 90 ? " noise" : " at " + snr + " dB") + ": metric 10/50/90% " + q(met) + "   S/N " + q(sn));
                }
        }

        // a minute of noise alone: the squelch as it comes keeps it quiet
        private static void noiseOnly(string name)
        {
            MfskMode m = MfskMode.ByName(name);
            float[] a = new float[48000 * 60];
            addNoise(a, 0, 48000);
            string got = receive(m, a, 1500, 48000, false, 10);
            check(got.Length <= 2, name + ": a minute of noise alone, squelch low: " + got.Length + " characters printed" + show(got));
        }

        // the heaviest keep up with time to spare
        private static void speed(string modeName)
        {
            MfskMode m = MfskMode.ByName(modeName);
            float[] a = TxAudio(m, Mid, 1500, 48000, false);
            Resampler rs = new Resampler(m.Rate);
            ModemRx rx = ModemRx.Create(m, 1500);
            float[] o = new float[a.Length / 4 + 8];
            int k = rs.Process(a, a.Length, 48000, o);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            rx.Process(o, k);
            double secs = a.Length / 48000.0, took = sw.Elapsed.TotalSeconds;
            check(took < secs / 4, modeName + ": " + secs.ToString("0.0") + " s of signal decoded in " + took.ToString("0.00") + " s");
        }

        // the signal stays within its bandwidth (and a tone either side)
        private static void bandwidth(string name)
        {
            MfskMode m = MfskMode.ByName(name);
            ModemTx tx = ModemTx.Create(m, 1500, false, 8000);
            tx.Add(Msg + " " + Msg);
            float[] a = new float[65536];
            int skip = 8000 * 2;            // past the start tones
            float[] all = new float[a.Length + skip];
            tx.Generate(all, all.Length);
            Array.Copy(all, skip, a, 0, a.Length);
            double[] re = new double[a.Length], im = new double[a.Length];
            for (int i = 0; i < a.Length; i++) re[i] = a[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (a.Length - 1)));
            Fft.Transform(re, im);
            double inside = 0, total = 0, half = m.Bandwidth / 2 + m.ToneSpacing;
            for (int b = 0; b < a.Length / 2; b++)
            {
                double p = re[b] * re[b] + im[b] * im[b], hz = b * 8000.0 / a.Length;
                total += p;
                if (Math.Abs(hz - 1500) <= half) inside += p;
            }
            check(inside / total > 0.99, name + "'s power within its " + m.Bandwidth.ToString("0") + " Hz (and a tone): " + (100 * inside / total).ToString("0.00") + " %");
        }

        // sending through the engine: MOX on, the audio fed, typed text added while sending, MOX off after; the audio
        // sent decodes to what was sent, and what was sent is echoed
        private static void transmit(string modeName)
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-mfsk-selftest");
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data-tx")) { ModeName = "DIGU", Speed = 8 };
            MfskEngine e = new MfskEngine(h) { TxLock = true, TxFreq = 1234, LevelDb = -6 };
            e.Mode = MfskMode.ByName(modeName);
            e.Start();
            string why = e.StartTx("CQ CQ de K7JUS ");
            Thread.Sleep(1000);
            bool moxOn = h.Mox;
            e.AddText("typed while sending\n");
            e.Finish();
            DateTime until = DateTime.UtcNow.AddSeconds(90);
            while (e.Sending && DateTime.UtcNow < until) Thread.Sleep(50);
            Thread.Sleep(300);
            StringBuilder echo = new StringBuilder();
            foreach (MfskEngine.TextPiece p in e.TakeText()) if (p.Tx) echo.Append(p.Text);
            e.Stop();
            float[] a;
            lock (h.TxAudio) a = h.TxAudio.ToArray();
            List<float> padded = new List<float>(a);
            padded.AddRange(new float[48000 * (int)(e.Mode.Family == Family.Mfsk ? 2 : 3 * e.Mode.BlockSymbols / e.Mode.Baud + 2)]);
            float[] pa = padded.ToArray();
            addNoise(pa, 30, 48000);
            string got = receive(e.Mode, pa, 1234, 48000, false, e.Mode.Family == Family.Mfsk ? 0 : 10);
            float peak = a.Length == 0 ? 0 : a.Max(v => Math.Abs(v));
            check(why == null && moxOn && !h.Mox, modeName + " TX: MOX on while sending, off after" + (why != null ? " (" + why + ")" : ""));
            check(got.Contains("CQ CQ de K7JUS typed while sending\n"), modeName + " TX: the audio sent decodes" + show(got));
            check(echo.ToString().Contains("CQ CQ de K7JUS typed while sending"), modeName + " TX: what's sent is echoed" + show(echo.ToString()));
            check(Math.Abs(peak - 0.501) < 0.03, modeName + " TX: peak " + peak.ToString("0.000") + " at -6 dB");
        }

        // ---- the pane: GitHub #86 (the marker while closed), #80 (<RX> / <TX> typed), #75 (scrolling, Clear) ----

        private static T field<T>(object o, string name) { return (T)o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(o); }
        private static void pump(int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }

        private static void paneFixes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-mfsk-panetest");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Form f = new Form { ClientSize = new Size(1000, 420), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data")) { Window = f, ModeName = "DIGU", Speed = 1 };
            f.Show();
            MfskModule mod = new MfskModule();
            mod.Start(h);
            pump(600);
            check(h.MarkerLog.Count == 0, "#86 closed: no marker on the panadapter (" + string.Join(" ", h.MarkerLog) + ")");
            h.Menus[0].Click();
            pump(400);
            check(h.MarkerLog.Count > 0 && h.MarkerLog[h.MarkerLog.Count - 1] != "0:none", "#86 open: the mode's marker shows");
            h.Menus[0].Click();
            pump(100);
            int after = h.MarkerLog.Count;
            pump(600);
            check(h.MarkerLog.Count == after && h.MarkerLog.Skip(after - 2).Any(m => m.EndsWith("none")), "#86 closed again: the marker cleared and not put back (" + string.Join(" ", h.MarkerLog.Skip(Math.Max(0, after - 3))) + ")");

            // #80: open, then type <TX> to start sending what's in the box
            h.Menus[0].Click();
            pump(300);
            Control pane = h.Panes[0].Content;
            MfskEngine e = field<MfskEngine>(pane, "_e");
            TextBox box = field<TextBox>(pane, "_txText");
            box.Text = "CQ de K7JUS ";
            box.AppendText("<TX>");
            pump(400);
            check(e.Sending && !box.Text.Contains("<TX>"), "#80 typed <TX>: sending, and the tag taken out of the box (" + box.Text + ")");
            int q0 = e.TxQueuedChars;
            box.AppendText("pse <R");
            pump(50);
            int q1 = e.TxQueuedChars;
            check(q1 - q0 == 4, "#80 a '<' that could be a tag is held back: 4 of 'pse <R' queued (" + (q1 - q0) + ")");
            box.AppendText("X>");
            pump(100);
            check(e.Finishing && !box.Text.Contains("<RX>"), "#80 typed <RX>: back to receive once sent, the tag out of the box (" + box.Text + ")");
            box.AppendText("x");        // finishing: nothing more goes
            e.Abort();
            pump(800);
            box.Text = "";
            // a '<' that isn't a tag goes out once it's clearly not one
            h.Menus[0].Click(); pump(100); h.Menus[0].Click(); pump(300);
            pane = h.Panes[0].Content; e = field<MfskEngine>(pane, "_e"); box = field<TextBox>(pane, "_txText");
            box.Text = "<TX>";
            pump(300);
            int qb = e.TxQueuedChars;
            box.AppendText("a<b");
            pump(50);
            check(e.Sending && e.TxQueuedChars - qb == 3, "#80 '<b' isn't a tag: sent (" + (e.TxQueuedChars - qb) + " of 3)");
            e.Abort();
            pump(800);

            // #75: following the newest text, and staying put while reading back
            RichTextBox rx = field<RichTextBox>(pane, "_rxText");
            System.Reflection.MethodInfo append = pane.GetType().GetMethod("append", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            rx.Clear();
            for (int i = 0; i < 200; i++) append.Invoke(pane, new object[] { "line " + i + " of the received text\n", false });
            Func<bool> endShowing = () => rx.GetPositionFromCharIndex(rx.TextLength).Y < rx.ClientSize.Height + rx.Font.Height;
            check(endShowing(), "#75 at the bottom: it follows the newest text");
            rx.Select(0, 0); rx.ScrollToCaret();
            int top = rx.GetCharIndexFromPosition(new Point(2, 2));
            for (int i = 0; i < 50; i++) append.Invoke(pane, new object[] { "more " + i + "\n", false });
            int top2 = rx.GetCharIndexFromPosition(new Point(2, 2));
            check(top2 == top && !endShowing(), "#75 scrolled up to read: it stays put as text comes in (first char shown " + top + " then " + top2 + ")");
            rx.Select(rx.TextLength, 0); rx.ScrollToCaret();
            append.Invoke(pane, new object[] { "back at the bottom\n", false });
            check(endShowing(), "#75 scrolled back down: it follows again");
            KButtonClick(pane, "Clear");
            check(rx.TextLength == 0, "#75 the Clear button clears the received text");

            h.Menus[0].Click();
            pump(200);
            mod.Stop();
            mod.Dispose();
            f.Close();
        }

        private static void KButtonClick(Control root, string text)
        {
            foreach (Control c in root.Controls)
                if (c.Text == text && c.GetType().Name == "KButton") { typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, new object[] { EventArgs.Empty }); return; }
            throw new Exception("no button " + text);
        }

        // the window with the module running and a signal playing; a screenshot saved
        private static void ui()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-mfsk-selftest");
            Form f = new Form { Text = "Kainos Olivia / MFSK self-test", ClientSize = new Size(1100, 420), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data-ui")) { Window = f, ModeName = "DIGU", Speed = 1 };
            h.Settings["open"] = "1";
            h.Settings["rxfreq"] = "1000";
            h.Settings["mode"] = "Olivia 32/1000";
            MfskModule mod = new MfskModule();
            f.Shown += (s, e) =>
            {
                mod.Start(h);
                h.Play(TxAudio(MfskMode.ByName("Olivia 32/1000"), "CQ CQ de K7JUS K7JUS pse K  The quick brown fox", 1000, 48000, false));
            };
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 26000 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Rectangle r = f.RectangleToScreen(f.ClientRectangle);
                using (Bitmap b = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    string png = Path.Combine(dir, "ui.png");
                    b.Save(png);
                    Console.WriteLine("screenshot: " + png);
                }
                mod.Stop();
                f.Close();
            };
            t.Start();
            Application.Run(f);
            mod.Dispose();
        }
    }
}
