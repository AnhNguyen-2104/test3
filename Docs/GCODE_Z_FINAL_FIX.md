# G-code Z-Axis Final Fix - Tất Cả G0/G1 Có Z Phải Nội Suy 3 Trục + Có Dừng

## Quy Tắc Đúng

### G0/G1 Có Z
- **Nội suy 3 trục**: Dùng Linear3 (Da.2=0x15) hoặc Rapid3
- **Có dừng**: Luôn Continuous Positioning (Da.1=0x01)
- **Áp dụng cho**: Cả điểm start và điểm giữa path

### G0/G1 Không Z
- **Nội suy 2 trục**: Dùng Line (Da.2=0x0A)
- **Có dừng hoặc không**: Tùy vị trí (Continuous Path hoặc Continuous Positioning)

## Ví Dụ

### File G-code
```
25  G1 X19.336 Y5.025           ; không Z
26  G1 X20.287 Y5.123           ; không Z
27  G1 X22.722 Y8.062 Z10       ; có Z
28  G1 X49.536 Y40.436 Z10      ; có Z
29  G1 Z10                      ; có Z
30  G1 X49.195 Y41.386          ; không Z
```

### Kết Quả Mong Muốn
```
25  Line (Continuous Path)                    ← 2-axis, không dừng ✅
26  Line (Continuous Positioning)             ← 2-axis, dừng (trước Linear3) ✅
27  Linear3 (Continuous Positioning)          ← 3-axis, dừng ✅
28  Linear3 (Continuous Positioning)          ← 3-axis, dừng ✅
29  Linear3 (Continuous Positioning)          ← 3-axis, dừng ✅
30  Line (Continuous Path)                    ← 2-axis, không dừng ✅
```

## Thay Đổi Code

### 1. Điểm Start Của Path (Dòng 935-975)

**Vấn đề**: Điểm start luôn dùng "Line" cho G1, không kiểm tra Z

**Trước đây**:
```csharp
string startMotion = startIsRapid
    ? "Rapid3 (Continuous Positioning)"
    : ((isFirstPath && onlyPath)
        ? "Line (Continuous Path)"
        : "Line (Continuous Positioning)");
```

**Sau khi sửa**:
```csharp
// Xác định motion prefix cho start point:
// - G0: luôn "Rapid3" (3-axis)
// - G1 có Z: "Linear3" (3-axis)
// - G1 không Z: "Line" (2-axis)
string startPrefix;
if (startIsRapid)
{
    startPrefix = "Rapid3";
}
else
{
    bool hasZ = Math.Abs(startZ) > 1e-9;
    startPrefix = hasZ ? "Linear3" : "Line";
}

// Xác định suffix (Continuous Path hoặc Continuous Positioning)
string startSuffix = (isFirstPath && onlyPath)
    ? " (Continuous Path)"
    : " (Continuous Positioning)";

string startMotion = startPrefix + startSuffix;
```

### 2. Điểm Giữa Path (Dòng 977-1010)

**Đã sửa trước đó** - Kiểm tra Z cho mỗi point:
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

### 3. Post-Processing (Dòng 1030-1090)

**Đã sửa trước đó** - 2 bước xử lý:

**Bước 1**: Xử lý current row
```csharp
// Quy tắc 1: G00 luôn Continuous Positioning
if (currIsRapid)
{
    result[i].MotionType = result[i].MotionType
        .Replace("(Continuous Path)", "(Continuous Positioning)");
}

// Quy tắc 5: G01 có Z (Linear3) cũng phải Continuous Positioning
if (currIsLinear3)
{
    result[i].MotionType = result[i].MotionType
        .Replace("(Continuous Path)", "(Continuous Positioning)");
}
```

**Bước 2**: Xử lý row trước/sau
```csharp
// Quy tắc 2: row trước G00 phải dừng
if (nextIsRapid) mustStop = true;

// Quy tắc 3: chuyển loại interpolation (2-axis ↔ 3-axis)
if (curr3Axis != next3Axis) mustStop = true;

// Quy tắc 4: row sau G00 phải dừng
if (currIsRapid && !nextIsRapid) mustStop = true;

// Quy tắc 6: row trước Linear3 phải dừng
if (nextIsLinear3) mustStop = true;

// Quy tắc 6: row sau Linear3 phải dừng
if (currIsLinear3 && !nextIsLinear3) mustStop = true;
```

## Tại Sao Cần Sửa Điểm Start?

### Vấn Đề
- Code cũ chỉ kiểm tra Z cho **điểm giữa path**
- **Điểm start path** luôn dùng "Line" cho G1, không kiểm tra Z
- Nếu điểm start có Z → sai mã lệnh → PLC báo lỗi

### Ví Dụ Lỗi
```
Path 1:
  Start: G1 X10 Y20 Z5    ← có Z, nhưng code cũ dùng "Line" (2-axis) → SAI!
  Point: G1 X30 Y40 Z5    ← có Z, code mới dùng "Linear3" (3-axis) → ĐÚNG
```

### Sau Khi Sửa
```
Path 1:
  Start: G1 X10 Y20 Z5    ← có Z, dùng "Linear3" (3-axis) → ĐÚNG ✅
  Point: G1 X30 Y40 Z5    ← có Z, dùng "Linear3" (3-axis) → ĐÚNG ✅
```

## Quy Tắc Post-Processing Đầy Đủ

### Cho G-code (6 Quy Tắc)

1. **G00 (Rapid3) luôn Continuous Positioning**
   - Không bao giờ Continuous Path

2. **Row trước G00 phải Continuous Positioning**
   - Dừng trước khi Rapid

3. **Chuyển 3-axis ↔ 2-axis → row trước phải Continuous Positioning**
   - Line ↔ Linear3
   - Line ↔ Rapid3

4. **Row sau G00 phải Continuous Positioning**
   - G00 → G01/G02/G03

5. **G01 có Z (Linear3) luôn Continuous Positioning**
   - Không bao giờ Continuous Path

6. **Row trước/sau Linear3 phải Continuous Positioning**
   - Dừng trước và sau Linear3

## Mã Lệnh Chạy (Da.1 + Da.2)

### Dòng 25: G1 không Z
- **Motion Type**: Line (Continuous Path)
- **Da.1**: 0x03 (Continuous Path) - không dừng
- **Da.2**: 0x0A (ABS_Linear2) - 2-axis
- **Da.5**: 1 (Axis 2 là partner)

### Dòng 26: G1 không Z (trước Linear3)
- **Motion Type**: Line (Continuous Positioning)
- **Da.1**: 0x01 (Continuous Positioning) - dừng có giảm tốc
- **Da.2**: 0x0A (ABS_Linear2) - 2-axis
- **Da.5**: 1 (Axis 2 là partner)

### Dòng 27-29: G1 có Z
- **Motion Type**: Linear3 (Continuous Positioning)
- **Da.1**: 0x01 (Continuous Positioning) - dừng có giảm tốc
- **Da.2**: 0x15 (ABS_Linear3) - 3-axis
- **Da.5**: 0 (không cần partner, 3-axis tự nội suy)

### Dòng 30: G1 không Z (sau Linear3)
- **Motion Type**: Line (Continuous Path)
- **Da.1**: 0x03 (Continuous Path) - không dừng
- **Da.2**: 0x0A (ABS_Linear2) - 2-axis
- **Da.5**: 1 (Axis 2 là partner)

## File Thay Đổi

### `Form1.DxfHandler.cs`

**Hàm**: `BuildGcodeProcessRows()`

**Thay đổi 1** (Dòng 935-975): Kiểm tra Z cho điểm start
**Thay đổi 2** (Dòng 977-1010): Kiểm tra Z cho điểm giữa (đã có)
**Thay đổi 3** (Dòng 1030-1090): Post-processing 2 bước (đã có)

## Build & Test

### Build
```powershell
# Đóng app trước khi build
msbuild DACDT_2026.sln /p:Configuration=Debug /p:Platform="Any CPU"
```
✅ **Kết quả**: Build thành công

### Test
1. **Đóng app và chạy lại** (để load code mới)
2. Mở file G-code có Z
3. Kiểm tra Process Table:
   - ✅ G1 có Z (start point) → "Linear3 (Continuous Positioning)"
   - ✅ G1 có Z (mid point) → "Linear3 (Continuous Positioning)"
   - ✅ G1 không Z (trước Linear3) → "Line (Continuous Positioning)"
   - ✅ G1 không Z (sau Linear3) → "Line (Continuous Path)"

4. Gửi xuống PLC và chạy:
   - ✅ Không bị lỗi ở dòng 25-26
   - ✅ G1 có Z chạy đúng (3-axis)
   - ✅ Có dừng khi chuyển 2↔3 axis

## Lưu Ý Quan Trọng

### 1. Phải Đóng App Trước Khi Build
- App đang chạy → file .exe bị lock
- Build sẽ fail với lỗi MSB3027
- **Giải pháp**: Đóng app → Build → Chạy lại

### 2. Kiểm Tra Z
- Dùng `Math.Abs(Z) > 1e-9` thay vì `Z != 0`
- Tránh lỗi so sánh floating point
- `1e-9` = 0.000000001 (rất nhỏ, coi như 0)

### 3. Start Point vs Mid Point
- **Start point**: Điểm đầu tiên của primitive trong path
- **Mid point**: Các điểm còn lại
- **Cả 2 đều phải kiểm tra Z**

### 4. Continuous Positioning vs Continuous Path
- **Continuous Positioning (0x01)**: Dừng có tăng/giảm tốc
- **Continuous Path (0x03)**: Chạy không dừng, liên tục
- **End (0x00)**: Kết thúc chương trình

## Tóm Tắt

### Vấn Đề
- Dòng 25 chạy xong → dòng 26 bị lỗi
- G1 có Z không nội suy 3 trục
- Điểm start path không kiểm tra Z

### Giải Pháp
1. ✅ Kiểm tra Z cho **điểm start** path
2. ✅ Kiểm tra Z cho **điểm giữa** path
3. ✅ Post-processing 2 bước: current row → row trước/sau
4. ✅ G1 có Z luôn Linear3 + Continuous Positioning

### Kết Quả
- ✅ Dòng 25: Line (Continuous Path) - không dừng
- ✅ Dòng 26: Line (Continuous Positioning) - dừng
- ✅ Dòng 27-29: Linear3 (Continuous Positioning) - 3-axis + dừng
- ✅ Dòng 30: Line (Continuous Path) - không dừng

---

**Ngày**: 2026-05-18  
**Người thực hiện**: Kiro AI Assistant  
**File thay đổi**: `Form1.DxfHandler.cs` (hàm `BuildGcodeProcessRows`)  
**Lần sửa**: 3 (final fix - start point Z check)
