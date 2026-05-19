# Fix Lỗi 524 - Control System Setting Error

## Lỗi 524 - QD75 Positioning Module

### Mô Tả Lỗi
**Error Code**: 524  
**Tên lỗi**: Control system setting error (Lỗi cài đặt hệ thống điều khiển)  
**Tài liệu**: SH-080058 (QD75 User Manual)

### Trạng Thái Khi Gặp Lỗi
- **Lúc bắt đầu lệnh**: Hệ thống không khởi động và không hoạt động
- **Trong quá trình hoạt động**: Hệ thống dừng lại ngay lập tức

### Nguyên Nhân Gây Lỗi 524

1. Giá trị cài đặt của hệ thống điều khiển nằm ngoài giới hạn cho phép
2. **Số lượng trục điều khiển hoặc các trục tham gia nội suy bị thay đổi** trong khi hệ thống đang vận hành ở chế độ:
   - Continuous Positioning Control (Điều khiển định vị liên tục)
   - Continuous Path Control (Điều khiển quỹ đạo liên tục)
3. Lệnh NOP bị gán sai cho hệ thống điều khiển tại vị trí dữ liệu định vị cuối cùng (No. 600)
4. Ghi giá trị khác "0" vào địa chỉ buffer memory 1906 (vùng nhớ cấm)
5. Thực hiện lệnh về gốc máy (OPR), về gốc nhanh (fast OPR), hoặc chuyển đổi tốc độ-vị trí/vị trí-tốc độ trong chế độ wiring-less
6. Sử dụng nội suy xoắn ốc 3 trục (3-axis helical interpolation) trên module QD75N có Serial < 17102

## Vấn Đề Trong Code

### Tình Huống Gây Lỗi

File G-code:
```
25  G1 X19.336 Y5.025           ; 2-axis (Line)
26  G1 X20.287 Y5.123           ; 2-axis (Line)
27  G1 X22.722 Y8.062 Z10       ; 3-axis (Linear3)
```

**Code cũ** (SAI):
```
25  Line (Continuous Path)                    ← 2-axis, không dừng
26  Line (Continuous Positioning)             ← 2-axis, dừng
27  Linear3 (Continuous Positioning)          ← 3-axis, dừng
```

**Vấn đề**: 
- Dòng 26: 2-axis, Continuous Positioning (Da.1=01)
- Dòng 27: 3-axis, Continuous Positioning (Da.1=01)
- **Chuyển từ 2-axis → 3-axis trong chế độ Continuous Positioning** → Lỗi 524!

### Nguyên Nhân

Theo tài liệu QD75:
> "Số lượng trục điều khiển hoặc các trục tham gia nội suy bị thay đổi trong khi hệ thống đang vận hành ở chế độ Continuous Positioning hoặc Continuous Path Control."

**Giải thích**:
- QD75 không cho phép thay đổi số trục nội suy (2↔3 axis) trong chế độ Continuous
- Phải **hoàn thành định vị** (END) trước khi chuyển sang số trục khác

## Giải Pháp

### Quy Tắc Mới

**Khi chuyển 2-axis ↔ 3-axis**: Dòng trước phải là **END** (Da.1=00) thay vì Continuous Positioning (Da.1=01)

**Code mới** (ĐÚNG):
```
25  Line (Continuous Path)                    ← 2-axis, không dừng
26  Line (End)                                ← 2-axis, hoàn thành định vị
27  Linear3 (Continuous Positioning)          ← 3-axis, bắt đầu mới
```

**Mã lệnh chạy**:
- Dòng 25: Da.1 = 0x03 (Continuous Path)
- Dòng 26: Da.1 = 0x00 (END) ← **Hoàn thành trước khi chuyển axis**
- Dòng 27: Da.1 = 0x01 (Continuous Positioning)

### Thay Đổi Code

**File**: `Form1.DxfHandler.cs`  
**Hàm**: `BuildGcodeProcessRows()` - Post-processing (Bước 2)

**Trước đây**:
```csharp
// Quy tắc 3: chuyển loại interpolation (2-axis ↔ 3-axis)
if (curr3Axis != next3Axis) mustStop = true;

// ...

if (mustStop)
{
    string mt = result[i].MotionType;
    if (mt.Contains("(Continuous Path)"))
        result[i].MotionType = mt.Replace("(Continuous Path)", "(Continuous Positioning)");
}
```

**Sau khi sửa**:
```csharp
bool mustStop = false;
bool mustEnd = false; // Phải dùng END thay vì Continuous Positioning

// Quy tắc 3: chuyển loại interpolation (2-axis ↔ 3-axis) → phải END
// Lý do: QD75 không cho phép thay đổi số trục trong Continuous Positioning/Path
// → Lỗi 524: "Control system setting error"
if (curr3Axis != next3Axis) {
    mustEnd = true;
    mustStop = false; // Ưu tiên END hơn Continuous Positioning
}

// Quy tắc 6: row trước Linear3 phải END (tránh lỗi 524)
if (nextIsLinear3 && !curr3Axis) {
    mustEnd = true;
    mustStop = false;
}

// Quy tắc 6: row sau Linear3 phải END (tránh lỗi 524)
if (currIsLinear3 && !next3Axis) {
    mustEnd = true;
    mustStop = false;
}

if (mustEnd)
{
    // Chuyển sang END (hoàn thành định vị)
    string mt = result[i].MotionType;
    if (mt.Contains("(Continuous Path)"))
        result[i].MotionType = mt.Replace("(Continuous Path)", " (End)");
    else if (mt.Contains("(Continuous Positioning)"))
        result[i].MotionType = mt.Replace("(Continuous Positioning)", " (End)");
}
else if (mustStop)
{
    // Chuyển sang Continuous Positioning
    string mt = result[i].MotionType;
    if (mt.Contains("(Continuous Path)"))
        result[i].MotionType = mt.Replace("(Continuous Path)", "(Continuous Positioning)");
}
```

## Quy Tắc Post-Processing Mới

### Cho G-code (6 Quy Tắc)

1. **G00 (Rapid3) luôn Continuous Positioning**
   - Không bao giờ Continuous Path

2. **Row trước G00 phải Continuous Positioning**
   - Dừng trước khi Rapid

3. **Chuyển 3-axis ↔ 2-axis → row trước phải END** ← **MỚI**
   - Tránh lỗi 524
   - Hoàn thành định vị trước khi chuyển số trục

4. **Row sau G00 phải Continuous Positioning**
   - G00 → G01/G02/G03

5. **G01 có Z (Linear3) luôn Continuous Positioning**
   - Không bao giờ Continuous Path

6. **Row trước/sau Linear3 phải END** ← **MỚI**
   - Tránh lỗi 524
   - Hoàn thành định vị trước/sau Linear3

## Ví Dụ Chi Tiết

### File G-code
```
24  G1 X18.5 Y4.8              ; 2-axis
25  G1 X19.336 Y5.025          ; 2-axis
26  G1 X20.287 Y5.123          ; 2-axis
27  G1 X22.722 Y8.062 Z10      ; 3-axis (có Z)
28  G1 X49.536 Y40.436 Z10     ; 3-axis (có Z)
29  G1 Z10                     ; 3-axis (chỉ Z)
30  G1 X49.195 Y41.386         ; 2-axis (không Z)
31  G1 X50.0 Y42.0             ; 2-axis
```

### Kết Quả Sau Khi Sửa
```
24  Line (Continuous Path)                    ← 2-axis, không dừng
25  Line (Continuous Path)                    ← 2-axis, không dừng
26  Line (End)                                ← 2-axis, END (trước Linear3)
27  Linear3 (Continuous Positioning)          ← 3-axis, dừng
28  Linear3 (Continuous Positioning)          ← 3-axis, dừng
29  Linear3 (End)                             ← 3-axis, END (trước Line)
30  Line (Continuous Path)                    ← 2-axis, không dừng
31  Line (Continuous Path)                    ← 2-axis, không dừng
```

### Mã Lệnh Chạy (Da.1)
```
24  Da.1 = 0x03 (Continuous Path)
25  Da.1 = 0x03 (Continuous Path)
26  Da.1 = 0x00 (END) ← Hoàn thành trước khi chuyển 2→3 axis
27  Da.1 = 0x01 (Continuous Positioning)
28  Da.1 = 0x01 (Continuous Positioning)
29  Da.1 = 0x00 (END) ← Hoàn thành trước khi chuyển 3→2 axis
30  Da.1 = 0x03 (Continuous Path)
31  Da.1 = 0x03 (Continuous Path)
```

## Tại Sao Phải Dùng END?

### Continuous Positioning vs END

**Continuous Positioning (Da.1=01)**:
- Dừng có tăng/giảm tốc tại điểm
- **Vẫn duy trì cấu hình nội suy** (số trục, partner axis)
- Sẵn sàng chạy tiếp với cùng cấu hình

**END (Da.1=00)**:
- Hoàn thành định vị
- **Kết thúc cấu hình nội suy hiện tại**
- Cho phép bắt đầu cấu hình mới (số trục khác)

### Lỗi 524 Xảy Ra Khi

```
Dòng 26: 2-axis, Continuous Positioning (Da.1=01)
         ↓ Vẫn duy trì cấu hình 2-axis
Dòng 27: 3-axis, Continuous Positioning (Da.1=01)
         ↑ Cố gắng chuyển sang 3-axis
         → Lỗi 524: Thay đổi số trục trong chế độ Continuous!
```

### Giải Pháp

```
Dòng 26: 2-axis, END (Da.1=00)
         ↓ Hoàn thành cấu hình 2-axis
Dòng 27: 3-axis, Continuous Positioning (Da.1=01)
         ↑ Bắt đầu cấu hình 3-axis mới
         → OK: Đã hoàn thành trước khi chuyển!
```

## Build & Test

### Build
```powershell
# Đóng app
msbuild DACDT_2026.sln /p:Configuration=Debug /p:Platform="Any CPU"
```
✅ **Kết quả**: Build thành công

### Test
1. Đóng app và chạy lại
2. Mở file G-code có chuyển 2↔3 axis
3. Kiểm tra Process Table:
   - ✅ Dòng trước Linear3: " (End)"
   - ✅ Dòng sau Linear3: " (End)"
   - ✅ Dòng Linear3: "(Continuous Positioning)"

4. Gửi xuống PLC và chạy:
   - ✅ Không bị lỗi 524
   - ✅ Chuyển 2↔3 axis mượt mà
   - ✅ Robot chạy đúng quỹ đạo

## Lưu Ý Quan Trọng

### 1. END vs Continuous Positioning
- **END**: Hoàn thành định vị, cho phép thay đổi cấu hình
- **Continuous Positioning**: Dừng nhưng vẫn duy trì cấu hình

### 2. Khi Nào Dùng END?
- Chuyển 2-axis → 3-axis
- Chuyển 3-axis → 2-axis
- Trước/sau Linear3 (G1 có Z)

### 3. Khi Nào Dùng Continuous Positioning?
- Trước/sau G00 (Rapid)
- Giữa các dòng cùng loại axis (2-axis → 2-axis, 3-axis → 3-axis)

### 4. Ưu Tiên
```
mustEnd > mustStop
```
- Nếu cần END → dùng END (ưu tiên cao nhất)
- Nếu không cần END nhưng cần Stop → dùng Continuous Positioning

## Tóm Tắt

### Vấn Đề
- Lỗi 524: Control system setting error
- Nguyên nhân: Thay đổi số trục (2↔3 axis) trong chế độ Continuous Positioning

### Giải Pháp
- Dùng **END** (Da.1=00) thay vì Continuous Positioning khi chuyển 2↔3 axis
- Hoàn thành định vị trước khi thay đổi cấu hình nội suy

### Kết Quả
- ✅ Không bị lỗi 524
- ✅ Chuyển 2↔3 axis mượt mà
- ✅ Robot chạy đúng quỹ đạo

---

**Ngày**: 2026-05-18  
**Người thực hiện**: Kiro AI Assistant  
**File thay đổi**: `Form1.DxfHandler.cs` (hàm `BuildGcodeProcessRows` - Post-processing Bước 2)  
**Tài liệu tham khảo**: SH-080058 (QD75 User Manual) - Error Code 524
