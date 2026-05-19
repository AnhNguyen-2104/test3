---
inclusion: auto
---

# QD75 Positioning — Quy tắc bắt buộc khi làm việc với G-code → PLC

Khi sửa code liên quan đến G-code parsing, process table, hoặc buffer write PLC, PHẢI tuân thủ:

## Tài liệu tham chiếu bắt buộc
- #[[GCODE_TO_PLC_POSITIONING_RULES.md]] — Bảng mã identifier + ví dụ đầy đủ
- File CSV chuẩn: `E:\D\NEW\melsec_q_abs_identifier.csv`

## Quy tắc cốt lõi (chương trình hiện tại)

1. **TẤT CẢ G0/G1/G2/G3 đều là nội suy 2 trục (X-Y). Z bị bỏ qua hoàn toàn.**
   - G00/G01 → Da.2 = 0x0A (ABS Linear 2-axis)
   - G02 → Da.2 = 0x0F (ABS Circular CW)
   - G03 → Da.2 = 0x10 (ABS Circular CCW)

2. **G02/G03 gửi 1 dòng duy nhất** với tọa độ đích + tọa độ tâm cung ABS. KHÔNG chia thành nhiều đoạn Linear.

3. **Da.5 LUÔN = Axis2** (giá trị 1, bit 3-2 = 01).

4. **Da.1 (Operation Pattern):**
   - Chuyển Line↔Arc → Continuous Positioning (Da.1 = 1)
   - Cùng Da.2 liên tiếp → Continuous Path (Da.1 = 3)
   - Dòng cuối → End (Da.1 = 0)

5. **Không có lỗi 524** vì tất cả đều 2-axis, không chuyển 2↔3 axis.

6. **Giá trị identifier chuẩn:**
   - Linear 2-axis End = 2564 (0x0A04)
   - Linear 2-axis Cont.Pos = 2565 (0x0A05)
   - Linear 2-axis Cont.Path = 2567 (0x0A07)
   - Circular CW Cont.Pos = 3845 (0x0F05)
   - Circular CCW Cont.Pos = 4101 (0x1005)

7. **Tọa độ tâm cung** là ABS: centerX = startX + I, centerY = startY + J.

8. **M code KHÔNG modal** — chỉ áp dụng cho dòng có M, hoặc gán vào dòng trước nếu M đứng riêng.

9. **Lệnh đầu tiên** KHÔNG được skip — parser tạo primitive từ gốc (0,0).

10. **Tọa độ** ×10000. **Speed** ×100.
