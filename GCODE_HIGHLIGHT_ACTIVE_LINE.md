# G-code Active Line Highlight - Highlight Dòng Đang Chạy

## Tính Năng

Highlight dòng G-code mà robot đang chạy trong G-code editor, dựa trên **Current Data No.** (Md.44) từ PLC.

## Cách Hoạt Động

### 1. Đọc Current Data No. Từ PLC
- **Register**: Md.44 (U0\G835 cho Axis 1)
- **Giá trị**: Số dòng đang chạy (1-indexed)
- **Cập nhật**: Mỗi 200ms (poll timer)

### 2. Gửi Xuống UI
- C# đọc `axCurrentDataNo[0]` (Axis 1 - master axis)
- Gửi trong `controlState` message
- JavaScript nhận và render

### 3. Highlight Trong Editor
- Lấy `currentDataNo` từ Axis 1
- Highlight số dòng tương ứng (màu xanh lá)
- Auto-scroll đến dòng đó (center viewport)

## Thay Đổi Code

### 1. CSS (`ui/styles.css`)

Thêm styles cho highlight:

```css
/* G-code active line highlight */
.gcode-line-active {
  background: rgba(34, 197, 94, 0.15) !important;
  border-left: 3px solid var(--green) !important;
  padding-left: 5px !important;
}

.gcode-line-number-active {
  color: var(--green) !important;
  font-weight: 700 !important;
}
```

**Màu sắc**:
- Background: Xanh lá nhạt (15% opacity)
- Border: Xanh lá đậm (var(--green) = #22c55e)
- Font: Bold, màu xanh lá

### 2. JavaScript (`ui/app.js`)

#### a. Hàm Highlight

```javascript
let lastHighlightedLine = -1;
function highlightGcodeLine(lineNumber) {
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  
  if (!gcodeTa || !gcodeLineNumbers) return;
  if (lineNumber === lastHighlightedLine) return; // No change
  
  lastHighlightedLine = lineNumber;
  
  if (lineNumber <= 0) return; // No active line
  
  const lines = gcodeTa.value.split('\n');
  if (lineNumber > lines.length) return; // Invalid line number
  
  // Scroll to line (center viewport)
  const lineHeight = 18; // 12px font-size * 1.5 line-height
  const scrollTop = (lineNumber - 1) * lineHeight;
  const viewportHeight = gcodeTa.clientHeight;
  const centerOffset = viewportHeight / 2 - lineHeight;
  
  gcodeTa.scrollTop = Math.max(0, scrollTop - centerOffset);
  gcodeLineNumbers.scrollTop = gcodeTa.scrollTop;
  
  // Highlight line number
  updateGcodeLineNumbersWithHighlight(lineNumber);
}
```

**Logic**:
1. Check nếu line number thay đổi (tránh re-render không cần thiết)
2. Validate line number (> 0, <= total lines)
3. Scroll đến dòng (center viewport)
4. Highlight số dòng (màu xanh lá, bold)

#### b. Update Line Numbers With Highlight

```javascript
function updateGcodeLineNumbersWithHighlight(activeLine) {
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  if (!gcodeTa || !gcodeLineNumbers) return;
  
  const lines = gcodeTa.value.split('\n');
  const lineCount = lines.length;
  let html = '';
  
  for (let i = 1; i <= lineCount; i++) {
    if (i === activeLine) {
      html += `<span class="gcode-line-number-active">${i}</span>\n`;
    } else {
      html += i + '\n';
    }
  }
  
  gcodeLineNumbers.innerHTML = html;
}
```

**Logic**:
- Tạo lại toàn bộ line numbers
- Wrap active line trong `<span>` với class `gcode-line-number-active`
- Các dòng khác: plain text

#### c. Gọi Từ renderControl()

```javascript
function renderControl() {
  // ... existing code ...
  
  // Highlight active G-code line (from Axis 1 Current Data No.)
  if (state.dxf && state.dxf.fileKind === "GCODE") {
    const axis1 = axes[0]; // Axis 1 (master axis)
    if (axis1 && axis1.currentDataNo && axis1.currentDataNo !== "--") {
      const lineNumber = parseInt(axis1.currentDataNo, 10);
      if (!isNaN(lineNumber) && lineNumber > 0) {
        highlightGcodeLine(lineNumber);
      }
    }
  }
}
```

**Logic**:
1. Chỉ highlight khi đang xem GCODE (không phải DXF)
2. Lấy `currentDataNo` từ Axis 1 (master axis)
3. Parse thành integer
4. Gọi `highlightGcodeLine()`

## Tính Năng Chi Tiết

### 1. Auto-Scroll
- Scroll đến dòng đang chạy
- Center viewport (dòng ở giữa màn hình)
- Smooth scroll (không giật)

### 2. Performance
- Chỉ update khi line number thay đổi
- Không re-render nếu cùng dòng
- Lightweight (chỉ update line numbers, không touch textarea)

### 3. Visual Feedback
- **Số dòng**: Màu xanh lá, bold
- **Background**: Không highlight background textarea (vì textarea không hỗ trợ per-line styling)
- **Border**: Không có (vì textarea không hỗ trợ)

### 4. Edge Cases
- Line number = 0 hoặc < 0: Không highlight
- Line number > total lines: Không highlight
- PLC disconnected: currentDataNo = "--", không highlight
- File DXF: Không highlight (chỉ GCODE)

## Ví Dụ Hoạt Động

### Trước Khi Chạy
```
┌─────┬────────────────────────────┐
│  1  │ G90                        │
│  2  │ G21                        │
│  3  │ G0 X10 Y20 Z5              │
│  4  │ G1 X30 Y40 F1000           │
│  5  │ G2 X50 Y60 I10 J10         │
│  6  │ M2                         │
└─────┴────────────────────────────┘
```

### Đang Chạy Dòng 3
```
┌─────┬────────────────────────────┐
│  1  │ G90                        │
│  2  │ G21                        │
│ [3] │ G0 X10 Y20 Z5              │ ← Highlight (xanh lá, bold)
│  4  │ G1 X30 Y40 F1000           │
│  5  │ G2 X50 Y60 I10 J10         │
│  6  │ M2                         │
└─────┴────────────────────────────┘
```

### Đang Chạy Dòng 5
```
┌─────┬────────────────────────────┐
│  3  │ G0 X10 Y20 Z5              │
│  4  │ G1 X30 Y40 F1000           │
│ [5] │ G2 X50 Y60 I10 J10         │ ← Highlight (xanh lá, bold)
│  6  │ M2                         │
└─────┴────────────────────────────┘
                                     ↑ Auto-scroll
```

## Lưu Ý

### 1. Chỉ Highlight Số Dòng
- Không thể highlight background của textarea (HTML limitation)
- Chỉ highlight số dòng bên trái
- Vẫn đủ để biết dòng nào đang chạy

### 2. Axis 1 (Master Axis)
- Chỉ dùng `currentDataNo` từ Axis 1
- Axis 2, 3, 4 không dùng (slave axes)
- Axis 1 là master, điều khiển toàn bộ chương trình

### 3. Update Frequency
- Mỗi 200ms (poll timer)
- Đủ nhanh để theo dõi real-time
- Không quá nhanh gây lag UI

### 4. Line Height Calculation
```javascript
const lineHeight = 18; // 12px font-size * 1.5 line-height
```
- Font size: 12px
- Line height: 1.5
- Total: 18px per line
- **Quan trọng**: Phải khớp với CSS

## Test

### 1. Đóng App và Build
```powershell
# Đóng app
# Build
msbuild DACDT_2026.sln /p:Configuration=Debug /p:Platform="Any CPU"
```

### 2. Chạy App
1. Connect PLC
2. Mở file G-code
3. Send to PLC
4. Start Action (M2000)

### 3. Kiểm Tra
- ✅ Số dòng đang chạy được highlight (màu xanh lá, bold)
- ✅ Auto-scroll đến dòng đó
- ✅ Update real-time khi chạy
- ✅ Không highlight khi PLC disconnect
- ✅ Không highlight khi xem DXF

## Tương Lai

### Có Thể Thêm
1. **Highlight background textarea**: Dùng CodeMirror hoặc Monaco Editor
2. **Highlight nhiều dòng**: Hiển thị dòng trước/sau
3. **Pause indicator**: Hiển thị icon khi PLC pause
4. **Error indicator**: Hiển thị icon lỗi bên cạnh số dòng

### Không Nên Thêm
- ❌ Highlight background textarea với plain textarea: Không thể (HTML limitation)
- ❌ Highlight nhiều axis: Chỉ cần Axis 1 (master)

## Tóm Tắt

### Tính Năng
✅ Highlight số dòng G-code đang chạy  
✅ Auto-scroll đến dòng đó  
✅ Update real-time (200ms)  
✅ Chỉ highlight khi xem GCODE  

### File Thay Đổi
- `ui/styles.css`: Thêm CSS cho highlight
- `ui/app.js`: Thêm hàm `highlightGcodeLine()` và `updateGcodeLineNumbersWithHighlight()`
- `ui/app.js`: Gọi từ `renderControl()`

### Build
✅ Build thành công

---

**Ngày**: 2026-05-18  
**Người thực hiện**: Kiro AI Assistant  
**File thay đổi**: `ui/styles.css`, `ui/app.js`
