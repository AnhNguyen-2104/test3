# G-code Z-Axis Fix - G1 Có Z Phải Nội Suy 3 Trục Và Có Dừng

## Vấn Đề

**Hiện tượng**:
1. Chạy xong dòng 26 bị lỗi
2. G0/G1 có Z không nội suy 3 trục
3. G1 có Z không có dừng (Continuous Positioning)

**Ví dụ từ file G-code**:
```
25  G1 X19.336 Y5.025 ; MOVE XY          ← 2-axis, OK
26  G1 X20.287 Y5.123 ; MOVE XY          ← 2-axis, OK
27  G1 X22.722 Y8.062 Z10 ; MOVE XYZ     ← 3-axis, PHẢI dừng
28  G1 X49.536 Y40.436 Z10 ; MOVE XYZ    ← 3-axis, PHẢI dừng
29  G1 Z10 ; MOVE Z AXIS                 ← 3-axis, PHẢI dừng
30  G1 X49.195 Y41.386 ; MOVE XY         ← 2-axis, OK
```

**Nguyên nhân**:
- Code cũ: G1 luôn dùng "Line" (2-axis), không kiểm tra Z
- G1 có Z phải dùng "Linear3" (3-axis) + Continuous Positioning
- Chuyển giữa 2-axis ↔ 3-axis phải có dừng

## Giải Pháp

### 1. Kiểm Tra Z Cho G1

**Trước đây**:
```csharp
// G0 Rapid: prefix "Rapid3" → Linear3 (Da.2=0x15)
string motionPrefix = primIsRapid ? "Rapid3" : "Line";
```

**Sau khi sửa**:
```csharp
// Xác định motion type:
// - G0 Rapid: luôn "Rapid3" (3-axis)
// - G1 có Z ≠ 0: "Linear3" (3-axis)
// - G1 không Z: "Line" (2-axis)
string motionPrefix;
if (primIsRapid)
{
    motionPrefix = "Rapid3";
}
else
{
    // G1: kiểm tra Z
    bool hasZ = Math.Abs(endZ) > 1e-9;
    motionPrefix = hasZ ? "Linear3" : "Line";
}
```

**Logic**:
- G0: luôn "Rapid3" (3-axis)
- G1 có Z ≠ 0: "Linear3" (3-axis)
- G1 không Z (Z=0): "Line" (2-axis)

### 2. Thêm Quy Tắc Post-Processing

**Quy tắc mới**:
```
5. G01 có Z (Linear3) cũng phải Continuous Positioning — không Continuous Path
6. Row trước/sau Linear3 phải là Continuous Positioning
```

**Code**:
```csharp
// Quy tắc 5: G01 có Z (Linear3) cũng phải Continuous Positioning
if (currIsLinear3)
{
    result[i].MotionType = result[i].MotionType
        .Replace("(Continuous Path)", "(Continuous Positioning)");
}

// ...

// Quy tắc 6: row trước Linear3 phải dừng
if (nextIsLinear3) mustStop = true;

// Quy tắc 6: row sau Linear3 phải dừng
if (currIsLinear3 && !nextIsLinear3) mustStop = true;
```

## Kết Quả Sau Khi Sửa

### Ví Dụ Mã Chạy

**File G-code**:
```
25  G1 X19.336 Y5.025
26  G1 X20.287 Y5.123
27  G1 X22.722 Y8.062 Z10
28  G1 X49.536 Y40.436 Z10
29  G1 Z10
30  G1 X49.195 Y41.386
```

**Process Table (Motion Type)**:
```
25  Line (Continuous Path)                    ← 2-axis, không dừng
26  Line (Continuous Positioning)             ← 2-axis, dừng (trước Linear3)
27  Linear3 (Continuous Positioning)          ← 3-axis, dừng
28  Linear3 (Continuous Positioning)          ← 3-axis, dừng
29  Linear3 (Continuous Positioning)          ← 3-axis, dừng (trước Line)
30  Line (Continuous Path)                    ← 2-axis, không dừng
```

**Mã lệnh chạy (Da.1)**:
```
25  Da.1 = 0x03 (Continuous Path)             ← không dừng
26  Da.1 = 0x01 (Continuous Positioning)      ← dừng có giảm tốc
27  Da.1 = 0x01 (Continuous Positioning)      ← dừng có giảm tốc
28  Da.1 = 0x01 (Continuous Positioning)      ← dừng có giảm tốc
29  Da.1 = 0x01 (Continuous Positioning)      ← dừng có giảm tốc
30  Da.1 = 0x03 (Continuous Path)             ← không dừng
```

**Mã điều khiển (Da.2)**:
```
25  Da.2 = 0x0A (ABS_Linear2)                 ← 2-axis
26  Da.2 = 0x0A (ABS_Linear2)                 ← 2-axis
27  Da.2 = 0x15 (ABS_Linear3)                 ← 3-axis
28  Da.2 = 0x15 (ABS_Linear3)                 ← 3-axis
29  Da.2 = 0x15 (ABS_Linear3)                 ← 3-axis
30  Da.2 = 0x0A (ABS_Linear2)                 ← 2-axis
```

## Tại Sao Phải Dừng?

### 1. Chuyển Loại Interpolation
- 2-axis (Line) → 3-axis (Linear3): Phải dừng
- 3-axis (Linear3) → 2-axis (Line): Phải dừng
- PLC cần thời gian tính toán궤 đạo mới

### 2. Tránh Giật Cục
- Không dừng → PLC cố gắng nội suy liên tục
- Chuyển 2↔3 axis mà không dừng → giật cục, sai quỹ đạo

### 3. Đảm Bảo Chính Xác
- Dừng có giảm tốc → PLC dừng chính xác tại điểm
- Sau đó mới bắt đầu chuyển động mới

## Quy Tắc Post-Processing Đầy Đủ

### Cho G-code

1. **G00 (Rapid3) luôn Continuous Positioning**
   - Không bao giờ Continuous Path

2. **Row trước G00 phải Continuous Positioning**
   - Dừng trước khi Rapid

3. **Chuyển 3-axis ↔ 2-axis → row trước phải Continuous Positioning**
   - Line ↔ Linear3
   - Line ↔ Rapid3

4. **Row sau G00 phải Continuous Positioning**
   - G00 → G01/G02/G03

5. **G01 có Z (Linear3) luôn Continuous Positioning** ← **MỚI**
   - Không bao giờ Continuous Path

6. **Row trước/sau Linear3 phải Continuous Positioning** ← **MỚI**
   - Dừng trước và sau Linear3

## File Thay Đổi

### `Form1.DxfHandler.cs`

**Hàm**: `BuildGcodeProcessRows()`

**Dòng 967-995**: Kiểm tra Z cho G1
```csharp
// Xác định motion type:
// - G0 Rapid: luôn "Rapid3" (3-axis)
// - G1 có Z ≠ 0: "Linear3" (3-axis)
// - G1 không Z: "Line" (2-axis)
string motionPrefix;
if (primIsRapid)
{
    motionPrefix = "Rapid3";
}
else
{
    // G1: kiểm tra Z
    bool hasZ = Math.Abs(endZ) > 1e-9;
    motionPrefix = hasZ ? "Linear3" : "Line";
}
```

**Dòng 1020-1070**: Post-processing với quy tắc mới
```csharp
// Quy tắc 5: G01 có Z (Linear3) cũng phải Continuous Positioning
if (currIsLinear3)
{
    result[i].MotionType = result[i].MotionType
        .Replace("(Continuous Path)", "(Continuous Positioning)");
}

// Quy tắc 6: row trước Linear3 phải dừng
if (nextIsLinear3) mustStop = true;

// Quy tắc 6: row sau Linear3 phải dừng
if (currIsLinear3 && !nextIsLinear3) mustStop = true;
```

## Build & Test

### Build
```powershell
msbuild DACDT_2026.sln /p:Configuration=Debug /p:Platform="Any CPU"
```
✅ **Kết quả**: Build thành công

### Test
1. Mở file G-code có Z (ví dụ: dòng 27-29)
2. Kiểm tra Process Table:
   - ✅ G1 có Z → Motion Type = "Linear3 (Continuous Positioning)"
   - ✅ G1 không Z → Motion Type = "Line (Continuous Path)" hoặc "Line (Continuous Positioning)"
   - ✅ Row trước G1 có Z → phải "Continuous Positioning"
   - ✅ Row sau G1 có Z → phải "Continuous Positioning"

3. Gửi xuống PLC và chạy:
   - ✅ Không bị lỗi ở dòng 26
   - ✅ G1 có Z chạy đúng (3-axis interpolation)
   - ✅ Có dừng khi chuyển 2↔3 axis

## Lưu Ý

### Kiểm Tra Z
- Dùng `Math.Abs(endZ) > 1e-9` thay vì `endZ != 0`
- Tránh lỗi so sánh floating point

### Linear3 vs Rapid3
- **Rapid3**: G0, luôn dùng rapidSpeed (10000 mm/min)
- **Linear3**: G1 có Z, dùng F từ file (modal)

### Da.2 Control System
- **0x0A**: ABS_Linear2 (2-axis interpolation)
- **0x15**: ABS_Linear3 (3-axis interpolation)

### Da.5 Partner Axis
- Linear3 (3-axis): Da.5 = 0 (không cần partner)
- Line (2-axis): Da.5 = 1 (Axis 2 là partner của Axis 1)

## Tương Lai

### Có Thể Thêm
1. **G2/G3 có Z**: Arc với Z (helix)
2. **Optimize dừng**: Giảm số lần dừng không cần thiết
3. **Smooth transition**: Chuyển mượt hơn giữa 2↔3 axis

### Không Nên Thay Đổi
- ❌ Bỏ dừng khi chuyển 2↔3 axis: Sẽ giật cục, sai quỹ đạo
- ❌ G1 có Z dùng Continuous Path: PLC sẽ báo lỗi

---

**Ngày**: 2026-05-18  
**Người thực hiện**: Kiro AI Assistant  
**File thay đổi**: `Form1.DxfHandler.cs` (hàm `BuildGcodeProcessRows`)
