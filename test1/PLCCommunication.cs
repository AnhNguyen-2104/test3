using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace test1
{
    public class PLCCommunication : IDisposable
    {
        private dynamic plcDevice;
        private bool isConnected = false;
        private readonly object commLock = new object();

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
                Type actUtlType = Type.GetTypeFromProgID("ActUtlType.ActUtlType");
                if (actUtlType == null) throw new Exception("MX Component chưa được cài đặt.");
                plcDevice = Activator.CreateInstance(actUtlType);
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
            object readValue = null;
            int result = plcDevice.ReadDevice(deviceName, count, ref readValue);
            if (result == 0) return readValue;
            throw new Exception($"Lỗi ReadDevice: {result}");
        }

        /// <summary>
        /// Read buffer memory (Un\Gx) using MX Component ReadBuffer if available.
        /// Returns an int[] of length 'count' with values read from the buffer.
        /// </summary>
        public int[] ReadBuffer(int startIO, int address, int count)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            try
            {
                int[] ints = new int[count];
                lock (commLock)
                {
                    for (int i = 0; i < count; i++)
                    {
                        string devName = $"U{startIO:X}\\G{address + i}";
                        int value = 0;
                        int result = plcDevice.GetDevice(devName, out value);
                        if (result != 0)
                        {
                            throw new Exception($"GetDevice {devName} failed: {result:X}");
                        }
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
        /// 2) Try plcDevice.ReadDevice (multi-word read) if available.
        /// 3) Fallback to plcDevice.GetDevice per sequential address.
        /// </summary>
        public int[] ReadDeviceRange(string deviceName, int count)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            // handle Un\Gx style device addresses first
            if (TryParseUDevicePath(deviceName, out int uNumber, out int gAddress))
            {
                // try ReadBuffer (preferred)
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
                            int value;
                            int res = plcDevice.GetDevice(path, out value);
                            if (res != 0)
                            {
                                throw new Exception($"GetDevice {path} returned {res}");
                            }
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

            // try ReadDevice (multi-word read) if available
            try
            {
                object readValue = null;
                int result = plcDevice.ReadDevice(deviceName, count, ref readValue);
                if (result == 0)
                {
                    if (readValue is Array arr)
                    {
                        int[] outArr = new int[arr.Length];
                        for (int i = 0; i < arr.Length; i++) outArr[i] = Convert.ToInt32(arr.GetValue(i), CultureInfo.InvariantCulture);
                        return outArr;
                    }
                    return new int[] { Convert.ToInt32(readValue, CultureInfo.InvariantCulture) };
                }
            }
            catch
            {
                // continue to fallback
            }

            // fallback: read each word individually via GetDevice using sequential device numbering
            try
            {
                var match = Regex.Match(deviceName.Trim(), "^(?<prefix>[A-Za-z]+)(?<address>\\d+)$");
                if (!match.Success)
                {
                    // single device name that isn't sequential - attempt GetDevice once
                    int singleVal;
                    int r = plcDevice.GetDevice(deviceName, out singleVal);
                    if (r == 0) return new int[] { singleVal };
                    throw new Exception($"GetDevice {deviceName} returned {r}");
                }

                string prefix = match.Groups["prefix"].Value;
                int address = int.Parse(match.Groups["address"].Value, CultureInfo.InvariantCulture);
                int[] resultArr = new int[count];
                for (int i = 0; i < count; i++)
                {
                    string path = prefix + (address + i).ToString(CultureInfo.InvariantCulture);
                    int val;
                    int res = plcDevice.GetDevice(path, out val);
                    if (res != 0)
                    {
                        throw new Exception($"GetDevice {path} returned {res}");
                    }
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
                int resLow = plcDevice.GetDevice(deviceName, out int low);
                if (resLow != 0) throw new Exception($"Lỗi GetDevice {deviceName}: {GetErrorMessage(resLow)}");

                int resHigh = plcDevice.GetDevice(nextWordDevice, out int high);
                if (resHigh != 0) throw new Exception($"Lỗi GetDevice {nextWordDevice}: {GetErrorMessage(resHigh)}");

                return (high << 16) | (low & 0xFFFF);
            }

            int value = 0;
            int result = plcDevice.GetDevice(deviceName, out value);
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
        /// Xử lý triệt để lỗi "Could not convert argument 0".
        /// </summary>
        public int WriteBuffer(int startIO, int address, short[] data)
        {
            if (!isConnected) throw new InvalidOperationException("Chưa kết nối PLC");
            if (data == null || data.Length == 0) throw new ArgumentException("Khong co du lieu de ghi.", nameof(data));
            try
            {
                return plcDevice.WriteBuffer(startIO, address, data.Length, ref data[0]);
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
                    short[] sData = new short[1];
                    int resRead = plcDevice.ReadBuffer(startIO, address, 1, out sData[0]);
                    if (resRead != 0) return resRead;

                    // Bước 2: Xóa 2 bit b0, b1 về 0 (AND với 0xFFFC ~ -4)
                    sData[0] = (short)(sData[0] & ~0x0003);

                    // Bước 3: Ghi giá trị mới (00, 01, 11)
                    sData[0] = (short)(sData[0] | (da1Value & 0x0003));

                    // Bước 4: Ghi xuống PLC
                    return plcDevice.WriteBuffer(startIO, address, 1, ref sData[0]);
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
                    short[] sData = new short[1];
                    int resRead = plcDevice.ReadBuffer(startIO, address, 1, out sData[0]);
                    if (resRead != 0) return resRead;

                    // Keep upper bits, replace only Positioning Identifier field (b0..b4).
                    sData[0] = (short)(sData[0] & ~0x001F);
                    sData[0] = (short)(sData[0] | (identifierValue & 0x001F));

                    return plcDevice.WriteBuffer(startIO, address, 1, ref sData[0]);
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
            return plcDevice.SetDevice2(devicePath, value);
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
            return plcDevice.SetDevice(devicePath, value);
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

            int result = plcDevice.SetDevice2(lowWordDevice, lowWord);
            if (result != 0) return result;

            return plcDevice.SetDevice2(highWordDevice, highWord);
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
            try { return plcDevice.GetErrorMessage(errorCode); }
            catch { return $"Mã lỗi: {errorCode}"; }
        }

        public void Dispose()
        {
            try { if (isConnected) Disconnect(); } catch { }
            plcDevice = null;
        }
    }
}
