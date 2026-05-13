# Mitsubishi Q-Series PLC Communication Guide

## Overview
This Windows Forms application provides Ethernet communication with Mitsubishi Q-series PLCs using the ActUtlType COM object from the Mitsubishi library suite.

## Setup Instructions

### 1. Prerequisites
- Windows Forms project targeting .NET Framework 4.8
- Mitsubishi ActUtlDataLogging64 Control (v5.0)
- Mitsubishi ActUType64 Control (v1.0)
- Network connection to your Mitsubishi Q-series PLC

### 2. Configuration
Before connecting, update the PLC IP address in `Form1.cs`:

```csharp
private string plcIPAddress = "192.168.1.100"; // Change to your PLC's IP address
private int plcPort = 2000; // Default MELSEC Ethernet port
```

## Device Naming Conventions

The Mitsubishi Q-series PLC uses standardized device names:

### Data Registers (D)
- `D0` - `D32767` (16-bit signed integer)
- `D32768` - `D65535` (32-bit values when paired)
- Example: `D100`, `D1000`

### Relays (M)
- `M0` - `M32767` (Individual relay bits)
- Example: `M0`, `M100`, `M8191`

### Digital Output (Y)
- `Y0` - `Y255` (Output relay)
- Example: `Y0`, `Y10`

### Digital Input (X)
- `X0` - `X377` (Input relay)
- Example: `X0`, `X10`

### Timers (T)
- `T0` - `T511` (Timer preset and current values)
- Example: `T0`, `T100`

### Counters (C)
- `C0` - `C511` (Counter preset and current values)
- Example: `C0`, `C100`

## Usage Examples

### Basic Connection
```csharp
// The connection is managed through the Form's Connect button
// Click "Connect" button in the UI to establish connection
```

### Reading Values
1. Enter device name (e.g., `D100`, `M50`, `Y0`)
2. Click "Read" button
3. The value will appear in the Value field

### Writing Values
1. Enter device name (e.g., `D100`, `M50`, `Y0`)
2. Enter value to write
3. Click "Write" button
4. Confirmation message will appear

### Programmatic Access
```csharp
// Read a value
object value = ReadValueFromPLC("D100");

// Write a value
WriteValueToPLC("D100", 42);

// Write a relay bit
WriteValueToPLC("M100", 1); // Turn ON
WriteValueToPLC("M100", 0); // Turn OFF
```

## Class Reference

### PLCCommunication Class

#### Constructor
```csharp
public PLCCommunication(string ipAddress, int port = 2000)
```

#### Properties
- `IsConnected` - Returns true if connected to PLC
- `IPAddress` - PLC IP address
- `Port` - Ethernet port (default: 2000)

#### Methods

##### Connect()
```csharp
public bool Connect()
```
Establishes connection to the PLC. Returns true on success.

##### Disconnect()
```csharp
public bool Disconnect()
```
Closes connection to the PLC. Returns true on success.

##### ReadDevice(string deviceName, int count = 1)
```csharp
public object ReadDevice(string deviceName, int count = 1)
```
Reads one or more values from a device.
- `deviceName`: Device identifier (e.g., "D100")
- `count`: Number of consecutive values to read
- Returns: The value(s) read from the device

##### WriteDevice(string deviceName, object value)
```csharp
public bool WriteDevice(string deviceName, object value)
```
Writes a value to a device.
- `deviceName`: Device identifier
- `value`: Value to write (can be int, double, or string)
- Returns: true on success

##### ReadDeviceBlock(string deviceName, int count)
```csharp
public object ReadDeviceBlock(string deviceName, int count)
```
Reads a block of consecutive values starting at the specified device.

##### WriteDeviceBlock(string deviceName, object[] values)
```csharp
public bool WriteDeviceBlock(string deviceName, object[] values)
```
Writes an array of values starting at the specified device.

##### GetErrorMessage(int errorCode)
```csharp
public string GetErrorMessage(int errorCode)
```
Returns a human-readable error message for a given error code.

## Error Codes Reference

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Command failed |
| 2 | Connection failed |
| 3 | Invalid parameter |
| 4 | Device name error |
| 5 | Capacity exceeded |
| 6 | Device data error |
| 7 | Station number error |
| 8 | No connection |
| 9 | Timeout |
| 10 | Protocol error |

## Network Configuration

### PLC Network Setup
1. Ensure PLC has Ethernet interface enabled
2. Configure PLC IP address (typically 192.168.1.100 or similar)
3. Ensure firewall allows communication on port 2000
4. Test connectivity with ping before running application

### Windows Firewall
1. Allow the application through Windows Firewall
2. Or add rule: `netsh advfirewall firewall add rule name="PLC Comm" dir=out action=allow program="your_app.exe"`

## Troubleshooting

### Connection Fails
- Verify PLC IP address is correct
- Check network cable connection
- Ping the PLC IP address to verify connectivity
- Ensure port 2000 is not blocked by firewall
- Check PLC is powered on and Ethernet module is active

### Read/Write Fails
- Verify device name is correct (check PLC documentation)
- Ensure PLC is in "Run" mode (not Stop mode)
- Check device number is within valid range
- Verify data type matches device type

### COM Object Not Found
- Reinstall Mitsubishi ActUtlType libraries
- Ensure ActUtlType is registered: `regsvr32 ActUtlType.dll`
- Check Windows Registry for ActUtlType ProgID

## Best Practices

1. **Error Handling**: Always wrap PLC operations in try-catch blocks
2. **Connection Management**: Disconnect when application closes
3. **Device Names**: Use uppercase for device names (D100, not d100)
4. **Batch Operations**: Use block read/write for multiple consecutive values
5. **Timeout Handling**: Implement timeout mechanisms for production code
6. **Logging**: Log all PLC communication errors for diagnostics

## Example: Reading Multiple Consecutive Values

```csharp
// Read 10 values starting from D100
object[] values = ReadValueFromPLC("D100") as object[];
for (int i = 0; i < values.Length; i++)
{
    Console.WriteLine($"D{100+i} = {values[i]}");
}
```

## Example: Writing to Multiple Relays

```csharp
// Turn on relays M0 through M9
object[] bitValues = new object[10];
for (int i = 0; i < 10; i++)
{
    bitValues[i] = 1;
}
WriteDeviceBlock("M0", bitValues);
```

## Support & Resources

- Mitsubishi Electric Website: https://www.mitsubishielectric.com/
- ActUtlType Documentation: Included with Mitsubishi software package
- PLC Device Name Reference: Check PLC model manual
- Network Troubleshooting: Use Mitsubishi's GX Developer or GX Works2 tools

## License & Notes

- This code uses COM interop for Mitsubishi PLC communication
- Ensure all Mitsubishi software licenses are valid
- Test thoroughly before deploying to production systems
- Implement appropriate safety mechanisms for production use
