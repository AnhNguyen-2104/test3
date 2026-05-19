# QD75 Positioning Identifier — Quy tắc chuyển G-code → Buffer PLC

## Tài liệu tham chiếu
- File CSV chuẩn: `E:\D\NEW\melsec_q_abs_identifier.csv`
- Manual: SH-080058 (QD75/LD75 Positioning Module)
- Code mẫu: `MelsecQ` (class PositioningIdentifier, BufferMemory)

---

## 1. Cấu trúc Positioning Identifier (16-bit word)

```
Bit 15-8 : Da.2 — Control System (loại nội suy)
Bit 7-6  : Da.4 — Deceleration time No. (0–3)
Bit 5-4  : Da.3 — Acceleration time No. (0–3)
Bit 3-2  : Da.5 — Partner Axis (0=Axis1, 1=Axis2, 2=Axis3, 3=Axis4)
Bit 1-0  : Da.1 — Operation Pattern (0=End, 1=Cont.Pos, 3=Cont.Path)
```

---

## 2. Bảng mã Da.2 (Control System) — CHỈ dùng ABS

| G-code | Loại nội suy | Da.2 (Hex) | Da.2 (Dec) | Mô tả |
|--------|-------------|-----------|-----------|-------|
| G00 không Z | Linear 2-axis | 0x0A | 10 | ABS Linear 2 |
| G01 không Z | Linear 2-axis | 0x0A | 10 | ABS Linear 2 |
| G00 có Z | Linear 3-axis | 0x15 | 21 | ABS Linear 3 |
| G01 có Z | Linear 3-axis | 0x15 | 21 | ABS Linear 3 |
| G02 | Circular CW | 0x0F | 15 | ABS Circular Right (center-point) |
| G03 | Circular CCW | 0x10 | 16 | ABS Circular Left (center-point) |

---

## 3. Bảng Da.1 (Operation Pattern)

| Giá trị | Tên | Khi nào dùng |
|---------|-----|-------------|
| 0 | Positioning Complete (End) | Dòng cuối cùng của chương trình |
| 1 | Continuous Positioning | Dừng có tăng/giảm tốc tại điểm. Dùng khi chuyển Da.2 |
| 3 | Continuous Path | Chạy liên tục không dừng. Chỉ khi Da.2 giống nhau liên tiếp |

---

## 4. Da.5 (Partner Axis) — LUÔN = Axis2 (giá trị 1)

Tất cả các loại nội suy trong dự án này đều dùng Da.5 = Axis2:
- Linear 2-axis: Da.5 = Axis2
- Linear 3-axis: Da.5 = Axis2
- Circular CW/CCW: Da.5 = Axis2

---

## 5. Bảng Identifier hoàn chỉnh (Da.3=0, Da.4=0, Da.5=Axis2)

| Loại | Da.1 | Decimal | Hex | Binary |
|------|------|---------|-----|--------|
| Linear 2-axis, End | 0 | 2564 | 0x0A04 | 0000101000000100 |
| Linear 2-axis, Cont.Pos | 1 | 2565 | 0x0A05 | 0000101000000101 |
| Linear 2-axis, Cont.Path | 3 | 2567 | 0x0A07 | 0000101000000111 |
| Linear 3-axis, End | 0 | 5380 | 0x1504 | 0001010100000100 |
| Linear 3-axis, Cont.Pos | 1 | 5381 | 0x1505 | 0001010100000101 |
| Linear 3-axis, Cont.Path | 3 | 5383 | 0x1507 | 0001010100000111 |
| Circular CW, End | 0 | 3844 | 0x0F04 | 0000111100000100 |
| Circular CW, Cont.Pos | 1 | 3845 | 0x0F05 | 0000111100000101 |
| Circular CW, Cont.Path | 3 | 3847 | 0x0F07 | 0000111100000111 |
| Circular CCW, End | 0 | 4100 | 0x1004 | 0001000000000100 |
| Circular CCW, Cont.Pos | 1 | 4101 | 0x1005 | 0001000000000101 |
| Circular CCW, Cont.Path | 3 | 4103 | 0x1007 | 0001000000000111 |

---

## 6. Quy tắc chuyển G-code → MotionType (per-line)

### 6.1 Xác định 2-axis hay 3-axis

**KHÔNG dùng pre-scan toàn file.** Mỗi lệnh tự quyết định:

```
Nếu lệnh G0/G1 có Z thay đổi (Z lệnh này ≠ Z lệnh trước):
  → 3-axis (Linear3 / Rapid3) — Da.2 = 0x15
Nếu lệnh G0/G1 KHÔNG có Z thay đổi (Z giữ nguyên modal):
  → 2-axis (Line) — Da.2 = 0x0A
G02/G03:
  → LUÔN 2-axis Circular (Da.2 = 0x0F / 0x10), bất kể file có Z hay không
```

### 6.2 Xác định Da.1 (Operation Pattern)

```
Dòng cuối chương trình:
  → End (Da.1 = 0)
Da.2 thay đổi giữa dòng hiện tại và dòng tiếp theo:
  → Continuous Positioning (Da.1 = 1)
Da.2 giống nhau liên tiếp:
  → Continuous Path (Da.1 = 3)
```

### 6.3 Parser: Lệnh đầu tiên KHÔNG được skip

Parser phải tạo primitive cho lệnh đầu tiên (thường là G00).
Nếu lệnh đầu tiên là `G00 X30 Y20 Z20`, phải tạo primitive di chuyển từ (0,0,0) → (30,20,20).
KHÔNG được `continue` bỏ qua lệnh đầu tiên.

---

## 7. Cấu trúc Buffer Memory (10 word/point)

| Offset | Kích thước | Nội dung | Ghi chú |
|--------|-----------|----------|---------|
| 0 | 16-bit | Positioning Identifier (Da.1~Da.5) | Gộp bit theo layout trên |
| 1 | 16-bit | M Code (Da.10) | |
| 2 | 16-bit | Dwell Time ms (Da.9) | |
| 3 | 16-bit | (Reserved) | |
| 4-5 | 32-bit | Command Speed (Da.8) | Low word trước, High word sau |
| 6-7 | 32-bit | Positioning Address (Da.6) | Tọa độ đích (×10000 = 0.1µm) |
| 8-9 | 32-bit | Arc Address (Da.7) | Tọa độ tâm cung (chỉ dùng cho Circular) |

### Buffer base address:
- Axis 1 (X master): G2000
- Axis 2 (Y slave): G8000
- Axis 3 (Z): G14000

### Axis 2 (Slave) chỉ ghi:
- Offset 0: Identifier (khớp master — cần cho Circular)
- Offset 6-7: Da.6 Positioning Address Y
- Offset 8-9: Da.7 Arc Address Y (nếu Circular)

### Axis 3 (Z) chỉ ghi khi dòng là 3-axis (Da.2 = 0x15):
- Offset 0: Identifier (khớp master)
- Offset 6-7: Da.6 Positioning Address Z

---

## 8. Ví dụ G-code → Buffer PLC (đúng chuẩn)

```gcode
G00 X30 Y20 Z20;
G01 X80 Y70 F2000;
G02 X80 Y70 I10 J30;
G01 X100 Y10;
```

### Kết quả mong đợi (Axis 1 / X master — G2000+):

| Point | G-code | Identifier | Da.2 | Da.1 | Speed | Pos X | Arc X | Z |
|-------|--------|-----------|------|------|-------|-------|-------|---|
| 1 | G00 X30 Y20 Z20 | Rapid3 (End) | 0x15 | 0 (End) | rapidSpeed | 300000 | — | 200000 |
| 2 | G01 X80 Y70 F2000 | Line (Cont.Pos) | 0x0A | 1 (Cont.Pos) | 200000 | 800000 | — | — |
| 3 | G02 X80 Y70 I10 J30 | Arc CW (Cont.Pos) | 0x0F | 1 (Cont.Pos) | 200000 | 800000 | 800000 | — |
| 4 | G01 X100 Y10 | Line (End) | 0x0A | 0 (End) | 200000 | 1000000 | — | — |

### Giải thích Da.1:
- Point 1 → Point 2: **Số trục thay đổi** (3-axis → 2-axis) → Point 1 = **End (0)** — bắt buộc tránh lỗi 524
- Point 2 → Point 3: Cùng 2-axis, đổi Line→Arc → Point 2 = Cont.Pos (1)
- Point 3 → Point 4: Cùng 2-axis, đổi Arc→Line → Point 3 = Cont.Pos (1)
- Point 4: cuối chương trình → End (0)

---

## 9. Quy tắc KHÔNG BAO GIỜ vi phạm

1. **G02/G03 LUÔN là 2-axis Circular** (0x0F/0x10). KHÔNG dùng Helical. KHÔNG chia thành nhiều đoạn Linear.
2. **KHÔNG pre-scan toàn file** để quyết định 2/3-axis. Mỗi lệnh tự quyết định.
3. **Da.5 LUÔN = Axis2** (giá trị 1) cho tất cả các loại nội suy.
4. **LỖI 524 — Chuyển 2↔3 axis**: Khi số trục thay đổi, dòng TRƯỚC **PHẢI là End (Da.1=0)**. Continuous Positioning (Da.1=1) KHÔNG đủ — vẫn gây lỗi 524.
5. **Chuyển Da.2 cùng số trục** (Line↔Arc, đều 2-axis): Continuous Positioning (Da.1=1) là đủ.
6. **Continuous Path (Da.1=3)** chỉ được dùng khi Da.2 giống nhau liên tiếp.
7. **Dòng cuối** LUÔN là End (Da.1=0).
8. **Tọa độ tâm cung (Da.7)** là tọa độ tuyệt đối (ABS):
   - Tâm cung X = startX + I
   - Tâm cung Y = startY + J
9. **Lệnh đầu tiên** (thường G00) KHÔNG được skip — phải tạo primitive di chuyển từ gốc.
10. **Tọa độ** nhân ×10000 (đơn vị 0.1µm). Speed nhân ×100.
