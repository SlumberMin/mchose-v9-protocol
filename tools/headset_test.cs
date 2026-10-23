// Headset protocol probe: which write path actually changes the device colour?
// The 0x07 query is supposed to answer with the current RGB, so we can verify
// objectively instead of relying on eyes.
using System;
using System.Collections.Generic;
using System.Threading;
using PrismSync;

internal static class HeadsetTest
{
    private static HidDevice _dev;

    private static void Main()
    {
        List<HidDeviceInfo> list = HidEnum.Find(0x3837, 0x6008);
        HidDeviceInfo info = null;
        for (int i = 0; i < list.Count; i++) if (list[i].UsagePage == 0xFF00) info = list[i];
        if (info == null) { Console.WriteLine("headset not found"); return; }
        _dev = HidDevice.Open(info);
        Console.WriteLine("open ok, in=" + info.InputReportByteLength + " out=" + info.OutputReportByteLength);
        Drain();
        Console.WriteLine("before: " + Query());

        SetColor("A SetOutputReport(65)", 0, new byte[] { 0x20, 0x00, 0xFF }, true);
        Console.WriteLine("after A: " + Query());

        SetColor("B WriteFile(65)", 0, new byte[] { 0xFF, 0x00, 0x20 }, false);
        Console.WriteLine("after B: " + Query());

        SetColor("C WriteFile(64, no leading 0)", 0, new byte[] { 0x00, 0xFF, 0x40 }, false);
        Console.WriteLine("after C: " + Query());

        SetColor("D SetOutputReport(64)", 0, new byte[] { 0x40, 0xFF, 0x00 }, true);
        Console.WriteLine("after D: " + Query());

        // status query itself via both paths
        SendRaw(true, BuildQuery());
        Console.WriteLine("query via SetOutputReport: " + ReadAll());
        SendRaw(false, BuildQuery());
        Console.WriteLine("query via WriteFile: " + ReadAll());

        _dev.Dispose();
    }

    private static void SetColor(string label, byte mode, byte[] rgb, bool viaReport)
    {
        byte[] wire = BuildWire(0x27, mode, rgb);
        byte[] buf = new byte[65];
        Array.Copy(wire, 0, buf, 1, 64);
        bool ok = viaReport ? _dev.SetOutputReport(buf, 65) : _dev.Write(buf, 65);
        Console.WriteLine(label + " -> " + ok);
        Drain();
        Thread.Sleep(180);
    }

    private static byte[] BuildQuery()
    {
        byte[] b = new byte[65];
        b[0] = 0x00;
        b[1] = 0xAA;
        b[2] = 0x07;
        return b;
    }

    private static byte[] BuildWire(byte cmd, byte mode, byte[] rgb)
    {
        byte[] p = new byte[64];
        p[0] = 0xAA;
        p[1] = cmd;
        p[5] = 0x0A;
        p[6] = 0x01;
        p[7] = mode;
        p[8] = 0x05;
        p[9] = 0x00;
        p[10] = 0x03;
        p[12] = rgb[0]; p[13] = rgb[1]; p[14] = rgb[2];
        return p;
    }

    private static void SendRaw(bool viaReport, byte[] buf)
    {
        if (viaReport) _dev.SetOutputReport(buf, 65);
        else _dev.Write(buf, 65);
        Thread.Sleep(80);
    }

    private static void Drain()
    {
        byte[] tmp = new byte[128];
        for (int i = 0; i < 24; i++)
        {
            int n = _dev.Read(tmp, 2);
            if (n <= 0) break;
        }
    }

    private static string Query()
    {
        byte[] buf = BuildQuery();
        SendRaw(true, buf);
        string a = ReadAll();
        SendRaw(false, buf);
        string b = ReadAll();
        return "rep[" + a + "] file[" + b + "]";
    }

    private static string ReadAll()
    {
        byte[] tmp = new byte[128];
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < 6; i++)
        {
            int n = _dev.Read(tmp, 120);
            if (n <= 0) break;
            for (int j = 0; j < n; j++) sb.Append(tmp[j].ToString("X2")).Append(' ');
            if (n > 14) sb.Append(" rgb=").Append(tmp[12]).Append(',').Append(tmp[13]).Append(',').Append(tmp[14]);
            sb.Append(" | ");
        }
        return sb.ToString();
    }
}
