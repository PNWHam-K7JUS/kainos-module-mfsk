/*  MfskModule.cs

Kainos Olivia / MFSK: Olivia, Contestia and MFSK keyboard chat inside Kainos, as a downloadable module (Setup > Modules).
Reads the signal clicked on the waterfall, and sends what's typed and the F1 - F8 messages. Works with fldigi.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

Copyright (C) 2026 Justin Cron K7JUS
*/

using Kainos.Modules;
using Kainos.Mfsk.UI;

namespace Kainos.Mfsk
{
    public class MfskModule : IKainosModule
    {
        private IKainosHost _host;
        private MfskEngine _engine;
        private MfskPane _view;
        private IKainosPane _pane;
        private IKainosMenuItem _menu;

        public void Start(IKainosHost host)
        {
            _host = host;
            _engine = new MfskEngine(host);
            _view = new MfskPane(host, _engine);
            _pane = host.CreatePane("Kainos Olivia / MFSK", _view);
            _pane.Closed += (s, e) => close();
            _view.PopToggled += () => _pane.Popped = !_pane.Popped;
            // one digital mode at a time: when another (FT8, RTTY, SSTV...) takes the receiver's audio, this one closes
            _engine.RxLost += () => host.BeginInvoke(() =>
            {
                if (!_pane.IsOpen) return;
                host.Log("another digital mode took the receiver's audio: closing");
                close();
            });
            _menu = host.AddMenuItem("Olivia / MFSK (experimental)", "Olivia, Contestia and MFSK (the Olivia / MFSK module): open / close. Experimental: not yet tested on the air.", () => { if (_pane.IsOpen) close(); else open(); });
            host.Log("Olivia / MFSK " + ModuleVersion + " started");
            if (host.GetSetting("open", "0") == "1") open();
        }

        internal static string ModuleVersion { get { System.Version v = typeof(MfskModule).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; } }

        private void open()
        {
            _pane.Open();
            _view.Showing = true;
            _engine.Listen();
            _engine.Start();
            _view.Restore();
            _menu.Checked = true;
            _host.SetSetting("open", "1");
        }

        private void close()
        {
            _engine.Stop();
            _pane.Close();
            _view.Showing = false;
            _view.ClearMarkers();
            _menu.Checked = false;
            _host.SetSetting("open", "0");
        }

        public void Stop()
        {
            if (_engine != null) _engine.Stop();
            if (_view != null) { _view.Showing = false; _view.ClearMarkers(); }
        }

        public void Dispose()
        {
            if (_view != null && !_view.IsDisposed) _view.Dispose();
        }
    }
}
