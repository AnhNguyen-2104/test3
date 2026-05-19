---
inclusion: auto
---

# QD75 Positioning — Quy tắc bắt buộc khi làm việc với G-code → PLC

Khi sửa code liên quan đến G-code parsing, process table, hoặc buffer write PLC, PHẢI tuân thủ:

## Tài liệu tham chiếu bắt buộc
- #[[GCODE_TO_PLC_POSITIONING_RULES.md]] — Bảng mã identifier đầy đủ + ví dụ
- File CSV chuẩn: `E:\D\NEW\melsec_q_abs_identifier.csv`

## Quy tắc cốt lõi

1. **G02/G03 LUÔN là 2-axis Circular** (Da.2 = 0x0F CW / 0x10 CCW). KHÔNG BAO GIỜ dùng Helical, KHÔNG chia thành nhiều đoạn Linear. Gửi 1 dòng duy nhất với tọa độ đích + tọa độ tâm cung.

2. **Per-line Z detection** — KHÔNG pre-scan toàn file:
   - G0/G1 có Z thay đổi (Z lệnh này ≠ Z lệnh trước) → 3-axis (Da.2 = 0x15)
   - G0/G1 không có Z thay đổi (Z modal giữ nguyên) → 2-axis (Da.2 = 0x0A)
   - G02/G03 → luôn 2-axis Circular (0x0F/0x10)

3. **Da.5 LUÔN = Axis2** (giá trị 1, bit 3-2 = 01) cho tất cả loại nội suy.

4. **Da.1 (Operation Pattern) — QUY TẮC LỖI 524:**
   - Chuyển 2↔3 axis (số trục thay đổi) → dòng trước PHẢI là **End (Da.1 = 0)** — bắt buộc, nếu không sẽ lỗi 524
   - Chuyển Da.2 cùng số trục (Line↔Arc, đều 2-axis) → Continuous Positioning (Da.1 = 1)
   - Da.2 giống nhau liên tiếp → Continuous Path (Da.1 = 3)
   - Dòng cuối → End (Da.1 = 0)

5. **Lệnh đầu tiên** (thường G00) KHÔNG được skip bởi parser. Phải tạo primitive di chuyển từ gốc (0,0,0).

6. **Giá trị identifier chuẩn** (verify bằng CSV):
   - Linear 2-axis End = 2564 (0x0A04)
   - Linear 2-axis Cont.Pos = 2565 (0x0A05)
   - Linear 2-axis Cont.Path = 2567 (0x0A07)
   - Linear 3-axis Cont.Pos = 5381 (0x1505)
   - Circular CW Cont.Pos = 3845 (0x0F05)
   - Circular CCW Cont.Pos = 4101 (0x1005)

7. **Tọa độ tâm cung** là ABS (tuyệt đối): centerX = startX + I, centerY = startY + J
