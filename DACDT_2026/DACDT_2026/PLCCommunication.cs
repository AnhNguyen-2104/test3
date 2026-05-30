using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace DACDT_2026
{
    public class PLCCommunication : IDisposable
    {
        private ActUtlTypeLib.ActUtlType plcDevice;
        private bool isConnected = false;
        private readonly object commLock = new object();

        // Thread-safe accessor — ném exception rõ ràng thay vì NullReferenceException
        private ActUtlTypeLib.ActUtlType Dev
        {
            get
            {
                var d = plcDevice;
                if (d == null) throw new InvalidOperationException("PLC device đã bị giải phóng.");
                return d;
            }
        }

        public string IPAddress { get; set; }
        public int Port { get; set; } = 2000;
        public int LogicalStationNumber { get; set; } = 0;
        public bool IsConnected => isConnected;

        public PLCCommunication(string ipAddress, int port = 2000, int logicalStationNumber = 0)
        {
            IPAddress = ipAddress;
            Port = port;
            LogicalStationNumber = logicalStationNumber;
            try
            {
                plcDevice = new ActUtlTypeLib.ActUtlType();
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi khởi tạo ActUtlType: {ex.Message}");
            }
        }

        public bool Connect()
        {
            try
            {
                if (isConnected) return true;
                plcDevice.ActLogicalStationNumber = LogicalStationNumber;
                int result = plcDevice.Open();
                if (result == 0)
                {
                    isConnected = true;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi kết nối PLC: {ex.Message}");
            }
        }

        public bool Disconnect()
        {
            try
            {
                if (!isConnected) return true;
                int result = plcDevice.Close();
                if (result == 0)
                {
                    isConnected = false;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi ngắt kết nối: {ex.Message}");
            }
        }

        public object ReadDevice(string deviceName, int count = 1)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            short[] arr = new short[count];
            int result = Dev.ReadDeviceBlock2(deviceName, count, out arr[0]);
            if (result == 0) return arr;
            throw new Exception($"Lỗi ReadDevice: {result}");
        }

        /// <summary>
        /// Read buffer memory (Un\Gx) using MX Component ReadDeviceBlock2 if available.
        /// Returns an int[] of length 'count' with values read from the buffer.
        /// </summary>
        public int[] ReadBuffer(int startIO, int address, int count)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            var dev = plcDevice;
            if (dev == null) throw new InvalidOperationException("PLC device đã bị giải phóng.");

            string startDevice = $"U{startIO:X}\\G{address}";

            // ── Thử ReadDeviceBlock2: 1 COM call cho toàn bộ mảng ───────────────
            try
            {
                short[] shorts = new short[count];
                int res = dev.ReadDeviceBlock2(startDevice, count, out shorts[0]);
                if (res == 0)
                {
                    int[] result = new int[count];
                    for (int i = 0; i < count; i++) result[i] = shorts[i];
                    return result;
                }
                // Nếu trả về lỗi thì fallback
            }
            catch
            {
                // ReadDeviceBlock2 không hỗ trợ U\G trên một số driver → fallback
            }

            // ── Fallback: GetDevice từng word ────────────────────────────────────
            try
            {
                int[] ints = new int[count];
                lock (commLock)
                {
                    for (int i = 0; i < count; i++)
                    {
                        string devName = $"U{startIO:X}\\G{address + i}";
                        int value = 0;
                        int result = dev.GetDevice(devName, out value);
                        if (result != 0)
                            throw new Exception($"GetDevice {devName} failed: {result:X}");
                        ints[i] = value;
                    }
                }
                return ints;
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi ReadBuffer: {GetInnermostMessage(ex)}");
            }
        }

        /// <summary>
        /// Read multiple consecutive device words and return them as int[].
        /// Tries several strategies:
        /// 1) If deviceName is Un\Gx, try ReadBuffer then fallback to per-word GetDevice.
        /// 2) Try Dev.ReadDeviceBlock2 (multi-word read) if available.
        /// 3) Fallback to Dev.GetDevice per sequential address.
        /// </summary>
        public int[] ReadDeviceRange(string deviceName, int count)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            var dev = plcDevice;
            if (dev == null) throw new InvalidOperationException("PLC device đã bị giải phóng.");

            // handle Un\Gx style device addresses first
            if (TryParseUDevicePath(deviceName, out int uNumber, out int gAddress))
            {
                try
                {
                    return ReadBuffer(uNumber, gAddress, count);
                }
                catch
                {
                    // fallback to per-word GetDevice on U\G paths
                    try
                    {
                        int[] arr = new int[count];
                        for (int i = 0; i < count; i++)
                        {
                            string path = $"U{uNumber}\\G{gAddress + i}";
                            int value = 0;
                            int res = dev.GetDevice(path, out value);
                            if (res != 0)
                                throw new Exception($"GetDevice {path} returned {res}");
                            arr[i] = value;
                        }
                        return arr;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Lỗi ReadDeviceRange (U device): {GetInnermostMessage(ex)}");
                    }
                }
            }

            // try ReadDeviceBlock2 (multi-word read) if available
            if (count > 0)
            {
                try
                {
                    short[] arr = new short[count];
                    int result = dev.ReadDeviceBlock2(deviceName, count, out arr[0]);
                    if (result == 0)
                    {
                        int[] outArr = new int[count];
                        for (int i = 0; i < count; i++) outArr[i] = arr[i];
                        return outArr;
                    }
                }
                catch
                {
                    // continue to fallback
                }
            }

            // fallback: read each word individually via GetDevice using sequential device numbering
            try
            {
                var match = Regex.Match(deviceName.Trim(), "^(?<prefix>[A-Za-z]+)(?<address>\\d+)$");
                if (!match.Success)
                {
                    int singleVal = 0;
                    int r = dev.GetDevice(deviceName, out singleVal);
                    if (r == 0) return new int[] { singleVal };
                    throw new Exception($"GetDevice {deviceName} returned {r}");
                }

                string prefix = match.Groups["prefix"].Value;
                int address = int.Parse(match.Groups["address"].Value, CultureInfo.InvariantCulture);
                int[] resultArr = new int[count];
                for (int i = 0; i < count; i++)
                {
                    string path = prefix + (address + i).ToString(CultureInfo.InvariantCulture);
                    int val = 0;
                    int res = dev.GetDevice(path, out val);
                    if (res != 0)
                        throw new Exception($"GetDevice {path} returned {res}");
                    resultArr[i] = val;
                }
                return resultArr;
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi ReadDeviceRange: {GetInnermostMessage(ex)}");
            }
        }

        public int ReadDeviceValue(string deviceName)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");

            // Nếu là buffer (U\G), đọc 2 words để thành 32-bit
            if (TryParseUDevicePath(deviceName, out int u, out int g))
            {
                try
                {
                    int[] buf = ReadBuffer(u, g, 2);
                    return (buf[1] << 16) | (buf[0] & 0xFFFF);
                }
                catch
                {
                    // Fallback to GetDevice
                }
            }

            // Nếu là thanh ghi D, đọc 2 words liên tiếp để ghép thành 32-bit
            if (deviceName.StartsWith("D", StringComparison.OrdinalIgnoreCase) && TryGetNextWordDevice(deviceName, out string nextWordDevice))
            {
                int resLow = Dev.GetDevice(deviceName, out int low);
                if (resLow != 0) throw new Exception($"Lỗi GetDevice {deviceName}: {GetErrorMessage(resLow)}");

                int resHigh = Dev.GetDevice(nextWordDevice, out int high);
                if (resHigh != 0) throw new Exception($"Lỗi GetDevice {nextWordDevice}: {GetErrorMessage(resHigh)}");

                return (high << 16) | (low & 0xFFFF);
            }

            int value = 0;
            int result = Dev.GetDevice(deviceName, out value);
            if (result == 0) return value;

            throw new Exception($"Lỗi GetDevice {deviceName}: {GetErrorMessage(result)}");
        }

        public void WriteDeviceValue(string deviceName, int value)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");

            int result = WriteInt32ToDevicePath(deviceName, value, out string method);
            if (result != 0)
            {
                throw new Exception($"Lỗi {method} {deviceName}: {GetErrorMessage(result)}");
            }
        }

        /// <summary>
        /// Ghi dữ liệu vào Buffer Memory module thông minh.
        /// Dùng WriteDeviceBlock2 để ghi toàn bộ mảng trong 1 COM call duy nhất.
        /// Fallback về SetDevice2 từng word nếu WriteDeviceBlock2 không hỗ trợ.
        /// </summary>
        public int WriteBuffer(int startIO, int address, short[] data)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            if (data == null || data.Length == 0) throw new ArgumentException("Khong co du lieu de ghi.", nameof(data));

            string startDevice = $"U{startIO:X}\\G{address}";

            // ── Thử WriteDeviceBlock2: 1 COM call cho toàn bộ mảng ──────────────
            try
            {
                int res = Dev.WriteDeviceBlock2(startDevice, data.Length, ref data[0]);
                if (res == 0) return 0;
                // Nếu trả về lỗi (không phải exception) thì fallback
            }
            catch
            {
                // WriteDeviceBlock2 không hỗ trợ U\G trên một số driver → fallback
            }

            // ── Fallback: SetDevice2 từng word ───────────────────────────────────
            try
            {
                int res = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    string devName = $"U{startIO:X}\\G{address + i}";
                    try
                    {
                        res = Dev.SetDevice2(devName, data[i]);
                    }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        res = Dev.SetDevice(devName, (int)data[i]);
                    }
                    if (res != 0) return res;
                }
                return res;
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi WriteBuffer: {GetInnermostMessage(ex)}");
            }
        }

        /// <summary>
        /// Sửa đổi 2 bit thấp nhất (Da.1) trong thanh ghi Positioning Identifier (16-bit)
        /// mà không làm hỏng Da.2 đến Da.5. (Ví dụ: U0\G2000)
        /// </summary>
        public int WriteOperationPatternToDevicePath(string devicePath, short da1Value, out string usedMethod)
        {
            usedMethod = "";
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            
            if (TryParseUDevicePath(devicePath, out int startIO, out int address))
            {
                usedMethod = "ReadBuffer -> Modify -> WriteBuffer";
                try
                {
                    // Bước 1: Đọc giá trị hiện tại
                    int val;
                    int resRead = Dev.GetDevice($"U{startIO:X}\\G{address}", out val);
                    if (resRead != 0) return resRead;

                    short current = (short)val;

                    // Bước 2: Xóa 2 bit b0, b1 về 0 (AND với 0xFFFC ~ -4)
                    current = (short)(current & ~0x0003);

                    // Bước 3: Ghi giá trị mới (00, 01, 11)
                    current = (short)(current | (da1Value & 0x0003));

                    // Bước 4: Ghi xuống PLC
                    try
                    {
                        return Dev.SetDevice2($"U{startIO:X}\\G{address}", current);
                    }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        return Dev.SetDevice($"U{startIO:X}\\G{address}", (int)current);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Lỗi WriteOperationPatternToDevicePath: {GetInnermostMessage(ex)}");
                }
            }
            throw new ArgumentException("Path must be a buffer address (U\\G).");
        }

        /// <summary>
        /// Update Positioning Identifier (lower 5 bits) without touching other bits in the same word.
        /// This is used for values like 0x000A, 0x000B, 0x000C, 0x000F...
        /// </summary>
        public int WritePositioningIdentifierToDevicePath(string devicePath, short identifierValue, out string usedMethod)
        {
            usedMethod = "";
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");

            if (TryParseUDevicePath(devicePath, out int startIO, out int address))
            {
                usedMethod = "ReadBuffer -> Modify (lower5) -> WriteBuffer";
                try
                {
                    int val;
                    int resRead = Dev.GetDevice($"U{startIO:X}\\G{address}", out val);
                    if (resRead != 0) return resRead;

                    short current = (short)val;

                    // Keep upper bits, replace only Positioning Identifier field (b0..b4).
                    current = (short)(current & ~0x001F);
                    current = (short)(current | (identifierValue & 0x001F));

                    try
                    {
                        return Dev.SetDevice2($"U{startIO:X}\\G{address}", current);
                    }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        return Dev.SetDevice($"U{startIO:X}\\G{address}", (int)current);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Lỗi WritePositioningIdentifierToDevicePath: {GetInnermostMessage(ex)}");
                }
            }
            throw new ArgumentException("Path must be a buffer address (U\\G).");
        }

        /// <summary>
        /// Ghi số 32-bit theo thứ tự Low Word -> High Word.
        /// Ví dụ: G2006 (L) và G2007 (H).
        /// </summary>
        /// 
        public int WriteInt32ToBuffer(int startIO, int address, int value)
        {
            short[] sData = new short[2];
            // Tách giá trị 32-bit thành 2 thanh ghi 16-bit
            sData[0] = (short)(value & 0xFFFF);         // Gửi vào G2006 (Low)
            sData[1] = (short)((value >> 16) & 0xFFFF); // Gửi vào G2007 (High)

            return WriteBuffer(startIO, address, sData);
        }

        /// <summary>
        /// Write a single 16-bit word to device path (especially U\G buffer address).
        /// </summary>
        public int WriteInt16ToDevicePath(string devicePath, short value, out string usedMethod)
        {
            usedMethod = "";
            if (string.IsNullOrWhiteSpace(devicePath))
                return -1;

            if (TryParseUDevicePath(devicePath, out int u, out int g))
            {
                usedMethod = "WriteBuffer x1 (16-bit)";
                try
                {
                    return WriteBuffer(u, g, new short[] { value });
                }
                catch
                {
                    // Fallback below
                }
            }

            usedMethod = "SetDevice2 (16-bit)";
            try
            {
                return Dev.SetDevice2(devicePath, value);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                usedMethod = "SetDevice (16-bit fallback)";
                return Dev.SetDevice(devicePath, (int)value);
            }
        }

        public int WriteInt32ToDevicePath(string devicePath, int value, out string usedMethod)
        {
            usedMethod = "";
            if (string.IsNullOrWhiteSpace(devicePath))
                return -1;

            if (TryParseUDevicePath(devicePath, out int u, out int g))
            {
                usedMethod = "WriteBuffer x2 (32-bit)";
                short lowWord = (short)(value & 0xFFFF);
                short highWord = (short)((value >> 16) & 0xFFFF);
                try
                {
                    return WriteBuffer(u, g, new short[] { lowWord, highWord });
                }
                catch
                {
                    // Fallback below
                }
            }

            // Chỉ ghi 32-bit cho thanh ghi D, W, R hoặc buffer U\G để tránh lỗi với Bit Device (M, X, Y)
            bool is32BitTarget = devicePath.StartsWith("D", StringComparison.OrdinalIgnoreCase) || 
                                 devicePath.StartsWith("W", StringComparison.OrdinalIgnoreCase) ||
                                 devicePath.StartsWith("R", StringComparison.OrdinalIgnoreCase) ||
                                 devicePath.StartsWith("U", StringComparison.OrdinalIgnoreCase);

            if (is32BitTarget && TryGetNextWordDevice(devicePath, out string nextWordDevice))
            {
                usedMethod = "SetDevice2 x2 (Low word -> High word)";
                return WriteInt32ByWords(devicePath, nextWordDevice, value);
            }

            usedMethod = "SetDevice";
            return Dev.SetDevice(devicePath, value);
        }

        private static bool TryParseUDevicePath(string devicePath, out int uNumber, out int gAddress)
        {
            uNumber = 0; gAddress = 0;
            string s = devicePath.Replace("\\\\", "\\").Trim();
            var m = Regex.Match(s, @"^U([0-9A-F]+)\\G(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uNumber)
                && int.TryParse(m.Groups[2].Value, out gAddress);
        }

        private int WriteInt32ByWords(string lowWordDevice, string highWordDevice, int value)
        {
            short lowWord = (short)(value & 0xFFFF);
            short highWord = (short)((value >> 16) & 0xFFFF);

            int result;
            try
            {
                result = Dev.SetDevice2(lowWordDevice, lowWord);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                result = Dev.SetDevice(lowWordDevice, (int)lowWord);
            }
            if (result != 0) return result;

            try
            {
                return Dev.SetDevice2(highWordDevice, highWord);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return Dev.SetDevice(highWordDevice, (int)highWord);
            }
        }



        private static bool TryGetNextWordDevice(string devicePath, out string nextWordDevice)
        {
            nextWordDevice = null;
            var match = Regex.Match(devicePath.Trim(), @"^(?<prefix>.*?)(?<address>\d+)$", RegexOptions.IgnoreCase);
            if (!match.Success) return false;

            if (!int.TryParse(match.Groups["address"].Value, out int address)) return false;

            nextWordDevice = match.Groups["prefix"].Value + (address + 1).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static string GetInnermostMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        public string GetErrorMessage(int errorCode)
        {
            return $"Mã lỗi: {errorCode}";
        }

        public void Dispose()
        {
            try { if (isConnected) Disconnect(); } catch { }
            plcDevice = null;
            isConnected = false;
        }
    }
}
