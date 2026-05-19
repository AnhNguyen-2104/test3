# G-code Line Numbers - Hiển Thị Số Dòng

## Mục Đích
Thêm số dòng (line numbers) cho G-code editor để dễ debug và theo dõi lệnh.

## Thay Đổi

### 1. HTML Structure (`ui/index.html`)

**Trước đây**: Textarea đơn giản
```html
<textarea id="gcode-textarea" ...></textarea>
```

**Sau khi sửa**: Wrapper với line numbers bên trái
```html
<div style="flex:1; display:flex; ...">
  <!-- Line numbers column -->
  <div id="gcode-line-numbers" 
       style="padding:8px 8px 8px 12px; 
              background:rgba(0,0,0,0.2); 
              color:var(--muted); 
              font-family:monospace; 
              font-size:12px; 
              line-height:1.5; 
              text-align:right; 
              user-select:none; 
              overflow:hidden; 
              min-width:45px; 
              border-right:1px solid var(--border);">
  </div>
  
  <!-- G-code textarea -->
  <textarea id="gcode-textarea" 
            style="flex:1; 
                   resize:none; 
                   background:transparent; 
                   ...
                   line-height:1.5; 
                   overflow-y:auto;">
  </textarea>
</div>
```

**Đặc điểm**:
- Line numbers: `text-align:right`, `user-select:none`, `overflow:hidden`
- Textarea: `background:transparent`, `line-height:1.5` (khớp với line numbers)
- Cả 2 dùng `font-family:monospace`, `font-size:12px`, `line-height:1.5`

### 2. JavaScript Logic (`ui/app.js`)

#### a. Global Function
```javascript
// Global function to update G-code line numbers
function updateGcodeLineNumbers() {
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  if (!gcodeTa || !gcodeLineNumbers) return;
  
  const lines = gcodeTa.value.split('\n');
  const lineCount = lines.length;
  let html = '';
  for (let i = 1; i <= lineCount; i++) {
    html += i + '\n';
  }
  gcodeLineNumbers.textContent = html;
}
```

**Vị trí**: Đặt ở global scope (sau khai báo `state`, trước `window.app`)

#### b. Scroll Sync
```javascript
// Sync scroll between line numbers and textarea
if (gcodeTa && gcodeLineNumbers) {
  gcodeTa.addEventListener('scroll', () => {
    gcodeLineNumbers.scrollTop = gcodeTa.scrollTop;
  });
}
```

**Mục đích**: Đồng bộ scroll giữa line numbers và textarea

#### c. Update on Input
```javascript
gcodeTa.addEventListener("input", function() {
  // ... uppercase logic ...
  
  // Update line numbers on input
  updateGcodeLineNumbers();
  
  // ... preview logic ...
});
```

**Khi nào**: Mỗi khi user gõ hoặc paste vào textarea

#### d. Update on Load
```javascript
if (isGcode) {
  // ...
  if (gcodeTextarea && gcodeTextarea._lastRaw !== state.dxf.rawText) {
    if (document.activeElement !== gcodeTextarea) {
      gcodeTextarea.value = state.dxf.rawText || "";
      // Update line numbers when G-code is loaded
      updateGcodeLineNumbers();
    }
    gcodeTextarea._lastRaw = state.dxf.rawText;
  }
}
```

**Khi nào**: Khi load file G-code mới hoặc switch giữa DXF/GCODE

## Tính Năng

### 1. Hiển Thị Số Dòng
- Số dòng bắt đầu từ 1
- Căn phải (right-aligned)
- Màu muted (không chói mắt)
- Background tối hơn textarea một chút

### 2. Đồng Bộ Scroll
- Khi scroll textarea → line numbers scroll theo
- Luôn khớp với dòng code đang xem

### 3. Tự Động Cập Nhật
- Khi gõ → cập nhật ngay
- Khi paste → cập nhật ngay
- Khi load file → cập nhật ngay
- Khi thêm/xóa dòng → cập nhật ngay

### 4. Không Thể Select
- `user-select:none` → không thể copy số dòng
- Chỉ copy được code, không copy số dòng

## Ví Dụ Hiển Thị

```
┌─────┬────────────────────────────┐
│  1  │ G90                        │
│  2  │ G21                        │
│  3  │ G0 X10 Y20 Z5              │
│  4  │ G1 X30 Y40 F1000           │
│  5  │ G2 X50 Y60 I10 J10         │
│  6  │ M2                         │
│     │                            │
└─────┴────────────────────────────┘
```

## Lợi Ích

### 1. Debug Dễ Dàng
- Biết chính xác lệnh nào đang chạy
- Dễ tìm lỗi theo số dòng
- Dễ so sánh với log PLC

### 2. Theo Dõi Tiến Trình
- Biết đang ở dòng nào trong file
- Dễ nhảy đến dòng cụ thể
- Dễ đếm số lệnh

### 3. Chuyên Nghiệp
- Giống các code editor thực sự
- Dễ đọc, dễ theo dõi
- Trải nghiệm người dùng tốt hơn

## Build & Test

### Build
```powershell
msbuild DACDT_2026.sln /p:Configuration=Debug /p:Platform="Any CPU"
```
✅ **Kết quả**: Build thành công

### Test
1. Chạy app
2. Mở file G-code hoặc tạo mới
3. Kiểm tra:
   - ✅ Số dòng hiển thị bên trái
   - ✅ Scroll đồng bộ
   - ✅ Gõ text → số dòng cập nhật
   - ✅ Paste text → số dòng cập nhật
   - ✅ Thêm/xóa dòng → số dòng cập nhật
   - ✅ Không thể select số dòng

## Lưu Ý

### CSS Variables
- `var(--muted)`: Màu text muted (xám nhạt)
- `var(--border)`: Màu border
- `var(--bg-panel)`: Màu background panel

### Line Height
- **Quan trọng**: `line-height:1.5` phải giống nhau giữa line numbers và textarea
- Nếu không khớp → số dòng sẽ lệch với code

### Performance
- `textContent` thay vì `innerHTML` → nhanh hơn, an toàn hơn
- Chỉ update khi cần thiết (check `_lastRaw`)

### Browser Compatibility
- Dùng `textContent` → IE9+
- Dùng `addEventListener` → IE9+
- Dùng CSS variables → modern browsers

## Tương Lai

### Có Thể Thêm
1. **Highlight dòng hiện tại**: Khi click vào dòng → highlight
2. **Breakpoint**: Click vào số dòng → đặt breakpoint
3. **Error indicator**: Hiển thị icon lỗi bên cạnh số dòng
4. **Fold code**: Thu gọn/mở rộng block code

### Không Nên Thêm
- ❌ Syntax highlighting: Quá phức tạp, dùng library như CodeMirror/Monaco thì tốt hơn
- ❌ Autocomplete: Cần parser G-code phức tạp
- ❌ Linting: Cần validate G-code syntax

---

**Ngày**: 2026-05-18  
**Người thực hiện**: Kiro AI Assistant  
**File thay đổi**: 
- `ui/index.html` (G-code editor structure)
- `ui/app.js` (line numbers logic)
