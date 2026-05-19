# QD75 Positioning Identifier — Quy tắc chuyển G-code → Buffer PLC

## Tài liệu tham chiếu
- File CSV chuẩn: `E:\D\NEW\melsec_q_abs_identifier.csv`
- Manual: SH-080058 (QD75/LD75 Positioning Module)

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

## 2. Chương trình hiện tại: TẤT CẢ là nội suy 2 trục — Z bị bỏ qua

Chương trình hiện tại **CHỈ dùng nội suy 2 trục (X-Y)**. Tham số Z trong G-code bị bỏ qua hoàn toàn.

| G-code | Da.2 (Hex) | Da.2 (Dec) | Mô tả |
|--------|-----------|-----------|-------|
| G00 | 0x0A | 10 | ABS Linear 2-axis |
| G01 | 0x0A | 10 | ABS Linear 2-axis |
| G02 | 0x0F | 15 | ABS Circular CW (center-point) |
| G03 | 0x10 | 16 | ABS Circular CCW (center-point) |

---

## 3. Bảng Da.1 (Operation Pattern)

| Giá trị | Tên | Khi nào dùng |
|---------|-----|-------------|
| 0 | Positioning Complete (End) | Dòng cuối cùng của chương trình |
| 1 | Continuous Positioning | Khi Da.2 thay đổi giữa 2 dòng liên tiếp (Line↔Arc) |
| 3 | Continuous Path | Khi Da.2 giống nhau liên tiếp |

---

## 4. Da.5 (Partner Axis) — LUÔN = Axis2 (giá trị 1)

Tất cả các loại nội suy đều dùng Da.5 = Axis2 (bit 3-2 = 01).

---

## 5. Bảng Identifier hoàn chỉnh (Da.3=0, Da.4=0, Da.5=Axis2)

| Loại | Da.1 | Decimal | Hex |
|------|------|---------|-----|
| Linear 2-axis, End | 0 | 2564 | 0x0A04 |
| Linear 2-axis, Cont.Pos | 1 | 2565 | 0x0A05 |
| Linear 2-axis, Cont.Path | 3 | 2567 | 0x0A07 |
| Circular CW, End | 0 | 3844 | 0x0F04 |
| Circular CW, Cont.Pos | 1 | 3845 | 0x0F05 |
| Circular CW, Cont.Path | 3 | 3847 | 0x0F07 |
| Circular CCW, End | 0 | 4100 | 0x1004 |
| Circular CCW, Cont.Pos | 1 | 4101 | 0x1005 |
| Circular CCW, Cont.Path | 3 | 4103 | 0x1007 |

---

## 6. Quy tắc chuyển G-code → MotionType

### 6.1 Tất cả G0/G1/G2/G3 đều là 2-axis. Z bị bỏ qua.

```
G00 → "Line" (Da.2 = 0x0A) — rapid speed từ UI
G01 → "Line" (Da.2 = 0x0A) — speed từ F
G02 → "Arc CW" (Da.2 = 0x0F) — 1 dòng duy nhất với tọa độ đích + tâm cung
G03 → "Arc CCW" (Da.2 = 0x10) — 1 dòng duy nhất với tọa độ đích + tâm cung
```

### 6.2 Xác định Da.1 (Operation Pattern)

```
Dòng cuối chương trình:
  → End (Da.1 = 0)
Da.2 thay đổi giữa dòng hiện tại và dòng tiếp theo (Line↔Arc):
  → Continuous Positioning (Da.1 = 1)
Da.2 giống nhau liên tiếp:
  → Continuous Path (Da.1 = 3)
```

### 6.3 M Code

M code KHÔNG modal — chỉ áp dụng cho dòng hiện tại:
- `G00 X120 Y30 M03;` → M=3 gán vào điểm (120, 30)
- `G00 X30 Y20;\nM03` → M=3 gán vào điểm trước đó (30, 20)

### 6.4 Parser: Lệnh đầu tiên KHÔNG được skip

Parser tạo primitive cho lệnh đầu tiên (di chuyển từ gốc 0,0 đến điểm đích).
KHÔNG tạo start row thừa — chỉ dùng end points trong vòng lặp.

---

## 7. Cấu trúc Buffer Memory (10 word/point)

| Offset | Kích thước | Nội dung | Ghi chú |
|--------|-----------|----------|---------|
| 0 | 16-bit | Positioning Identifier (Da.1~Da.5) | |
| 1 | 16-bit | M Code | |
| 2 | 16-bit | Dwell Time (ms) | |
| 3 | 16-bit | (Reserved) | |
| 4-5 | 32-bit | Command Speed | ×100 (mm/min → QD75 units) |
| 6-7 | 32-bit | Positioning Address (Da.6) | ×10000 (mm → 0.1µm) |
| 8-9 | 32-bit | Arc Address (Da.7) | Tọa độ tâm cung ABS (chỉ Circular) |

### Buffer base address:
- Axis 1 (X master): G2000
- Axis 2 (Y slave): G8000

### Axis 1 (Master) ghi đầy đủ:
- Identifier, M code, Dwell, Speed, Pos X, Arc center X

### Axis 2 (Slave) ghi:
- Identifier (khớp master)
- Pos Y (Da.6)
- Arc center Y (Da.7, nếu Circular)

---

## 8. Ví dụ G-code → Buffer PLC

```gcode
G00 X30 Y20 Z20;
G01 X80 Y70 F2000;
G02 X80 Y70 I10 J30;
G01 X100 Y10;
```

### Kết quả (Z bị bỏ qua, tất cả 2-axis):

| # | G-code | MotionType | Identifier | Da.1 | Speed | Pos X | Arc X |
|---|--------|-----------|-----------|------|-------|-------|-------|
| 1 | G00 X30 Y20 Z20 | Line (Cont.Path) | 2567 | 3 | rapidSpeed | 300000 | — |
| 2 | G01 X80 Y70 F2000 | Line (Cont.Pos) | 2565 | 1 | 200000 | 800000 | — |
| 3 | G02 X80 Y70 I10 J30 | Arc CW (Cont.Pos) | 3845 | 1 | 200000 | 800000 | 400000 |
| 4 | G01 X100 Y10 | Line (End) | 2564 | 0 | 200000 | 1000000 | — |

### Giải thích Da.1:
- Point 1 → Point 2: cùng Da.2 (0x0A = Line) → Point 1 = Cont.Path (3)
- Point 2 → Point 3: Da.2 thay đổi (Line→Arc) → Point 2 = Cont.Pos (1)
- Point 3 → Point 4: Da.2 thay đổi (Arc→Line) → Point 3 = Cont.Pos (1)
- Point 4: cuối chương trình → End (0)

### Tọa độ tâm cung (Arc center):
- G02 I10 J30: center X = startX + I = 80 + 10 = 90 → nhưng thực tế parser tính từ start point trước G02
- Tâm cung là tọa độ ABS, KHÔNG phải offset I/J trực tiếp

---

## 9. Quy tắc bắt buộc

1. **Tất cả G0/G1/G2/G3 đều là 2-axis.** Z bị bỏ qua hoàn toàn.
2. **G02/G03 gửi 1 dòng duy nhất** với tọa độ đích + tọa độ tâm cung. KHÔNG chia thành nhiều đoạn Linear.
3. **Da.5 LUÔN = Axis2** (giá trị 1).
4. **Chuyển Line↔Arc** → Continuous Positioning (Da.1=1).
5. **Cùng Da.2 liên tiếp** → Continuous Path (Da.1=3).
6. **Dòng cuối** → End (Da.1=0).
7. **Tọa độ tâm cung** là ABS: centerX = startX + I, centerY = startY + J.
8. **Lệnh đầu tiên** KHÔNG được skip — parser tạo primitive từ gốc (0,0).
9. **Tọa độ** ×10000 (0.1µm). **Speed** ×100.
10. **M code KHÔNG modal** — chỉ áp dụng cho dòng có M hoặc dòng trước nếu M đứng riêng.
11. **Không có lỗi 524** vì tất cả đều 2-axis, không chuyển 2↔3 axis.
