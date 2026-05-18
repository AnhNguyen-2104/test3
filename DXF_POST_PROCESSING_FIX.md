# DXF Post-Processing Fix - Mã Lệnh Chạy Đúng

## Vấn Đề
Mã lệnh chạy (Da.1 - operation pattern) của file DXF không đúng, không chạy được trên PLC.

**Nguyên nhân**: DXF thiếu post-processing logic so với GCODE.

## Giải Pháp

### Trước Đây (Chỉ 1 Quy Tắc)
DXF chỉ có 1 quy tắc post-processing:
- Chuyển giữa 3-axis ↔ 2-axis → row trước phải là Continuous Positioning

### Sau Khi Sửa (3 Quy Tắc Đầy Đủ)
DXF giờ có 3 quy tắc post-processing giống GCODE:

1. **Chuyển giữa 3-axis (Linear3) ↔ 2-axis (Line/Arc)**
   - Row trước phải là Continuous Positioning
   - Đảm bảo PLC dừng có giảm tốc khi chuyển loại interpolation

2. **Chuyển giữa Line ↔ Arc (cả 2 chiều)**
   - Row trước phải là Continuous Positioning
   - Tránh giật cục hoặc sai quỹ đạo khi chuyển từ đường thẳng sang cung tròn

3. **Row cuối path (trước nhảy sang path mới)**
   - Đã được gán Continuous Positioning từ trước
   - Đảm bảo PLC dừng trước khi nhảy sang đoạn đứt quãng

## Mã Lệnh Chạy (Da.1 - Operation Pattern)

### Giá Trị Da.1 (b1-b0)
- `00` = **Positioning Complete (End)** - Kết thúc chương trình
- `01` = **Continuous Positioning** - Dừng có tăng/giảm tốc tại điểm
- `11` = **Continuous Path** - Chạy không dừng, liên tục

### Khi Nào Dùng Gì?

#### Continuous Path (0x03)
- Điểm giữa path (cùng loại chuyển động)
- Ví dụ: Line → Line, Arc → Arc

#### Continuous Positioning (0x01)
- Điểm cuối path (trước nhảy sang path mới)
- Chuyển giữa 3-axis ↔ 2-axis
- Chuyển giữa Line ↔ Arc
- Điểm start path (có đoạn đứt quãng)

#### End (0x00)
- Điểm cuối cùng của toàn bộ chương trình

## Ví Dụ Mã Chạy DXF

### File DXF Có 2 Path (Không Có Z)
```
Path 1:
  Point 1: Line (Continuous Positioning)     ← Start path 1
  Point 2: Line (Continuous Path)            ← Giữa path
  Point 3: Line (Continuous Positioning)     ← Cuối path 1, trước nhảy

Path 2:
  Point 4: Line (Continuous Positioning)     ← Start path 2
  Point 5: Arc CW (Continuous Positioning)   ← Chuyển Line→Arc
  Point 6: Arc CW (End)                      ← Cuối cùng
```

### File DXF Có Z (3-Axis)
```
Path 1:
  Point 1: Linear3 (Continuous Positioning)  ← Start, Z=zDown
  Point 2: Linear3 (Continuous Path)         ← Giữa, Z=zDown
  Point 3: Linear3 (Continuous Positioning)  ← Cuối, Z=zSafe (nhấc lên)

Path 2:
  Point 4: Linear3 (Continuous Positioning)  ← Start, Z=zSafe
  Point 5: Linear3 (Continuous Path)         ← Giữa, Z=zDown
  Point 6: Linear3 (End)                     ← Cuối, Z=zDown
```

### File DXF Có Line + Arc
```
Path 1:
  Point 1: Line (Continuous Positioning)     ← Start
  Point 2: Line (Continuous Path)            ← Line → Line
  Point 3: Line (Continuous Positioning)     ← Line → Arc (phải dừng)
  Point 4: Arc CW (Continuous Path)          ← Arc → Arc
  Point 5: Arc CW (Continuous Positioning)   ← Arc → Line (phải dừng)
  Point 6: Line (End)                        ← Cuối
```

## Thay Đổi Code

### File: `Form1.DxfHandler.cs`
**Hàm**: `BuildDxfProcessRows()` (dòng 869-891)

**Trước**:
```csharp
// Chỉ 1 quy tắc
if (hasZ)
{
    for (int i = 0; i < result.Count; i++)
    {
        bool curr3Axis = result[i].MotionType.Contains("Linear3");
        if (i < result.Count - 1)
        {
            bool next3Axis = result[i + 1].MotionType.Contains("Linear3");
            bool mustStop = false;
            if (curr3Axis != next3Axis) mustStop = true;
            // ...
        }
    }
}
```

**Sau**:
```csharp
// 3 quy tắc đầy đủ
for (int i = 0; i < result.Count; i++)
{
    bool curr3Axis = result[i].MotionType.Contains("Linear3");
    bool currIsLine = result[i].MotionType.Contains("Line") || result[i].MotionType.Contains("Linear3");
    bool currIsArc = result[i].MotionType.Contains("Arc") || result[i].MotionType.Contains("Circle");

    if (i < result.Count - 1)
    {
        bool next3Axis = result[i + 1].MotionType.Contains("Linear3");
        bool nextIsLine = result[i + 1].MotionType.Contains("Line") || result[i + 1].MotionType.Contains("Linear3");
        bool nextIsArc = result[i + 1].MotionType.Contains("Arc") || result[i + 1].MotionType.Contains("Circle");

        bool mustStop = false;

        // Quy tắc 1: chuyển loại interpolation (2-axis ↔ 3-axis)
        if (curr3Axis != next3Axis) mustStop = true;

        // Quy tắc 2: chuyển giữa Line ↔ Arc (cả 2 chiều)
        if ((currIsLine && nextIsArc) || (currIsArc && nextIsLine)) mustStop = true;

        if (mustStop)
        {
            string mt = result[i].MotionType;
            if (mt.Contains("(Continuous Path)"))
                result[i].MotionType = mt.Replace("(Continuous Path)", "(Continuous Positioning)");
        }
    }
}
```

## Kiểm Tra

### Build Project
```powershell
msbuild DACDT_2026.sln /p:Configuration=Debug /p:Platform="Any CPU"
```
✅ **Kết quả**: Build thành công, không có lỗi compile.

### Test Với File DXF
1. Mở file DXF có nhiều path
2. Kiểm tra Process Table → cột "Motion Type"
3. Verify mã chạy:
   - Điểm giữa path: `(Continuous Path)` → Da.1 = 0x03
   - Điểm cuối path: `(Continuous Positioning)` → Da.1 = 0x01
   - Điểm cuối cùng: `(End)` → Da.1 = 0x00
   - Chuyển Line→Arc hoặc Arc→Line: row trước phải `(Continuous Positioning)`

4. Gửi xuống PLC và chạy thử
5. Kiểm tra PLC có chạy đúng quỹ đạo không

## Lưu Ý

### Khác Biệt DXF vs GCODE
- **DXF**: Không có G00/Rapid3 → không cần quy tắc về Rapid
- **GCODE**: Có G00/Rapid3 → cần 4 quy tắc (bao gồm quy tắc về Rapid)

### Tại Sao Cần Post-Processing?
QD75 Positioning Module yêu cầu:
- **Continuous Path (Da.1=11)**: Chỉ dùng khi cùng loại interpolation (2-axis hoặc 3-axis)
- **Continuous Positioning (Da.1=01)**: Phải dùng khi chuyển loại interpolation hoặc chuyển Line↔Arc
- Nếu không tuân thủ → PLC sẽ báo lỗi hoặc chạy sai quỹ đạo

### Tại Sao Thêm Quy Tắc Line↔Arc?
- Line (Da.2=0x0A): ABS_Linear2 - nội suy đường thẳng
- Arc (Da.2=0x0F/0x10): ABS_CircularRight/Left - nội suy cung tròn
- Chuyển giữa 2 loại này cần dừng có giảm tốc để PLC tính toán궤 đạo mới
- Nếu không dừng → giật cục, sai quỹ đạo tại điểm chuyển

## Kết Luận

✅ **Đã sửa**: DXF giờ có post-processing đầy đủ giống GCODE  
✅ **Đã build**: Không có lỗi compile  
⏳ **Cần test**: Test với file DXF thật trên PLC để verify mã chạy đúng

---

**Ngày**: 2026-05-18  
**Người thực hiện**: Kiro AI Assistant  
**File thay đổi**: `Form1.DxfHandler.cs` (hàm `BuildDxfProcessRows`)
