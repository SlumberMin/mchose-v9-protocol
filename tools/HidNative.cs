// PrismSync native app - Win32 HID access (setupapi + hid.dll), no hidapi needed.
//
// Two behaviours are required by the hardware and were verified against the
// working Python implementation first:
//   * keyboard: WriteFile() with 64-byte reports that start with report id 9
//   * headset : HidD_SetOutputReport() with a 65-byte buffer ([0] + 64-byte wire
//               packet, report id 0xAA is inside the wire packet); reads and
//               writes must use the SAME handle and ACKs have to be drained or
//               the firmware silently stops accepting frames.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PrismSync
{
    public sealed class HidDeviceInfo
    {
        public string Path;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public string Product = "";
    }

    public sealed class HidDevice : IDisposable
    {
        private const int FILE_SHARE_READ = 1;
        private const int FILE_SHARE_WRITE = 2;
        private const int OPEN_EXISTING = 3;
        private const int FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;

        private IntPtr _handle = IntPtr.Zero;
        private IntPtr _readEvent = IntPtr.Zero;
        private IntPtr _writeEvent = IntPtr.Zero;
        private IntPtr _readOverlapped = IntPtr.Zero;
        private IntPtr _writeOverlapped = IntPtr.Zero;
        private IntPtr _readBuffer = IntPtr.Zero;
        private int _readBufferSize;
        private bool _disposed;

        public HidDeviceInfo Info { get; private set; }
        public bool IsOpen { get { return _handle != IntPtr.Zero; } }

        private HidDevice() { }

        public static HidDevice Open(HidDeviceInfo info)
        {
            IntPtr h = Native.CreateFileW(info.Path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "无法打开 HID 设备: " + info.Path);

            HidDevice d = new HidDevice();
            d._handle = h;
            d.Info = info;
            d._readEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
            d._writeEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
            d._readOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(OVERLAPPED)));
            d._writeOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(OVERLAPPED)));
            Marshal.StructureToPtr(new OVERLAPPED(), d._readOverlapped, false);
            Marshal.StructureToPtr(new OVERLAPPED(), d._writeOverlapped, false);
            d._readBufferSize = Math.Max(64, (int)info.InputReportByteLength) + 1;
            d._readBuffer = Marshal.AllocHGlobal(d._readBufferSize);
            return d;
        }

        /// <summary>WriteFile path: the buffer starts with the report id.</summary>
        public bool Write(byte[] data, int length)
        {
            if (_handle == IntPtr.Zero || data == null) return false;
            if (length > data.Length) length = data.Length;

            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(new OVERLAPPED(), _writeOverlapped, false);
                Marshal.WriteIntPtr(_writeOverlapped, IntPtr.Size * 3, _writeEvent); // hEvent at offset 24 on x64
                int written;
                bool ok = Native.WriteFile(_handle, pin.AddrOfPinnedObject(), length,
                                           out written, _writeOverlapped);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_IO_PENDING)
                    {
                        uint res = Native.WaitForSingleObject(_writeEvent, 2000);
                        if (res != WAIT_OBJECT_0) return false;
                        ok = Native.GetOverlappedResult(_handle, _writeOverlapped, out written, false);
                    }
                    else return false;
                }
                return ok;
            }
            finally
            {
                pin.Free();
                Native.ResetEvent(_writeEvent);
            }
        }

        /// <summary>HidD_SetOutputReport path (used by the headset: 65-byte buffer).</summary>
        public bool SetOutputReport(byte[] buffer, int length)
        {
            if (_handle == IntPtr.Zero || buffer == null) return false;
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return Native.HidD_SetOutputReport(_handle, pin.AddrOfPinnedObject(), length);
            }
            finally { pin.Free(); }
        }

        /// <summary>Overlapped read with timeout; returns bytes read or 0 on timeout/error.</summary>
        public int Read(byte[] dest, int timeoutMs)
        {
            if (_handle == IntPtr.Zero) return 0;
            Marshal.StructureToPtr(new OVERLAPPED(), _readOverlapped, false);
            Marshal.WriteIntPtr(_readOverlapped, IntPtr.Size * 3, _readEvent);
            Native.ResetEvent(_readEvent);
            int read;
            bool ok = Native.ReadFile(_handle, _readBuffer, _readBufferSize, out read, _readOverlapped);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return 0;
                uint res = Native.WaitForSingleObject(_readEvent, timeoutMs);
                if (res != WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(_handle, _readOverlapped);
                    Native.WaitForSingleObject(_readEvent, 100);
                    Native.GetOverlappedResult(_handle, _readOverlapped, out read, false);
                    Native.ResetEvent(_readEvent);
                    return 0;
                }
                if (!Native.GetOverlappedResult(_handle, _readOverlapped, out read, false)) return 0;
            }
            if (read <= 0) return 0;
            if (dest != null)
            {
                int n = Math.Min(read, dest.Length);
                Marshal.Copy(_readBuffer, dest, 0, n);
                return n;
            }
            return read;
        }

        /// <summary>Read and discard pending input reports (device ACKs).</summary>
        public int Drain(int maxReports, int timeoutMs)
        {
            int total = 0;
            for (int i = 0; i < maxReports; i++)
            {
                int n = Read(null, timeoutMs);
                if (n <= 0) break;
                total++;
            }
            return total;
        }

        public void Close()
        {
            if (_handle != IntPtr.Zero)
            {
                Native.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
            if (_readEvent != IntPtr.Zero) { Native.CloseHandle(_readEvent); _readEvent = IntPtr.Zero; }
            if (_writeEvent != IntPtr.Zero) { Native.CloseHandle(_writeEvent); _writeEvent = IntPtr.Zero; }
            if (_readOverlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_readOverlapped); _readOverlapped = IntPtr.Zero; }
            if (_writeOverlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_writeOverlapped); _writeOverlapped = IntPtr.Zero; }
            if (_readBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_readBuffer); _readBuffer = IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }

    public static class HidEnum
    {
        /// <summary>Enumerate HID interfaces matching VID/PID (usage page is filled in).</summary>
        public static List<HidDeviceInfo> Find(ushort vid, ushort pid)
        {
            List<HidDeviceInfo> result = new List<HidDeviceInfo>();
            foreach (HidDeviceInfo raw in EnumerateAll())
            {
                if (raw.VendorId == vid && raw.ProductId == pid) result.Add(raw);
            }
            return result;
        }

        public static List<HidDeviceInfo> EnumerateAll()
        {
            List<HidDeviceInfo> list = new List<HidDeviceInfo>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1) || set == IntPtr.Zero) return list;
            try
            {
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; ; i++)
                {
                    ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData))
                        break;
                    int needed;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(needed);
                    try
                    {
                        // cbSize on x64 is 8 (DWORD + padding), device path starts at +4.
                        Marshal.WriteInt32(detail, 8);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, needed, out needed, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        HidDeviceInfo info = Describe(path);
                        if (info != null) list.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        private static HidDeviceInfo Describe(string path)
        {
            IntPtr h = Native.CreateFileW(path, GENERIC_READ_CONST, 3 /*share read|write*/,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
            try
            {
                HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                if (!Native.HidD_GetAttributes(h, ref attr)) return null;

                HidDeviceInfo info = new HidDeviceInfo();
                info.Path = path;
                info.VendorId = attr.VendorID;
                info.ProductId = attr.ProductID;

                StringBuilder sb = new StringBuilder(256);
                if (Native.HidD_GetProductString(h, sb, 256)) info.Product = sb.ToString();

                IntPtr prep;
                if (Native.HidD_GetPreparsedData(h, out prep))
                {
                    try
                    {
                        HIDP_CAPS caps = new HIDP_CAPS();
                        caps.Reserved = new ushort[17];
                        // HIDP_STATUS_SUCCESS is 0x00110000 (not 0) - judging it by
                        // "!= 0 = failure" is the classic trap that made the vendor
                        // collections look like they had no caps at all.
                        Native.HidP_GetCaps(prep, ref caps);
                        if (caps.UsagePage != 0 || caps.OutputReportByteLength != 0)
                        {
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputReportByteLength = caps.InputReportByteLength;
                            info.OutputReportByteLength = caps.OutputReportByteLength;
                        }
                    }
                    finally { Native.HidD_FreePreparsedData(prep); }
                }
                return info;
            }
            finally { Native.CloseHandle(h); }
        }

        private const uint GENERIC_READ_CONST = 0x80000000;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public IntPtr hEvent;
    }

    internal static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attr);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr h, StringBuilder buf, int len);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr prep, ref HIDP_CAPS caps);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr prep);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(IntPtr h, IntPtr buffer, int length);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator,
            IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo,
            ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,
            ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, int detailSize,
            out int required, IntPtr devInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr security, int creation, int flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr handle, IntPtr buffer, int count,
            out int written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr handle, IntPtr buffer, int count,
            out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped,
            out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEvent(IntPtr security, bool manualReset,
            bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
    }
}
