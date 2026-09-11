# SQL Migrator

Công cụ **di chuyển database SQL Server** (migrate) với giao diện **Windows Forms**.
Ưu điểm nổi bật: giữ nguyên cấu trúc (bảng, khóa, index, view, proc, function, trigger,
sequence...), sao chép dữ liệu theo thứ tự phụ thuộc khóa ngoại, hỗ trợ di chuyển giữa các
phiên bản SQL Server khác nhau, bảo mật toàn bộ thông tin đăng nhập.

## Tính năng

- **Một form tổng thể**: nhập cả server nguồn + đích, tùy chọn, chọn bảng và theo dõi nhật ký
  real-time ngay trên cùng một màn hình — trực quan, không cần bước wizard.
- **Ba chế độ di chuyển**:
  - *Toàn bộ* — cấu trúc + dữ liệu.
  - *Chỉ cấu trúc* — schema (bảng, khóa, index, view, proc, function, trigger, sequence, quyền).
  - *Chỉ dữ liệu* — khi schema đích đã có sẵn.
- **Kiểm tra tương thích phiên bản**: cảnh báo đối tượng không hỗ trợ trên đích, tự đánh dấu
  bảng không thể di chuyển để không làm hỏng tiến trình.
- **Đổ dữ liệu nhanh**: `SqlBulkCopy`, thứ tự bảng theo khóa ngoại, tùy chọn vô hiệu hóa
  khóa ngoại/CHECK/trigger/index trong lúc đổ rồi bật lại sau khi xong.
- **Chọn bảng bằng checkbox**: bỏ chọn bảng nào là bảng đó được loại trừ; không gõ thủ công.
- **Nhật ký real-time**: khung nhật ký kiểu console ngay trong ứng dụng, tự động ghi file
  theo cuộc di chuyển, nút xuất nhật ký.
- **Quản lý profile kết nối**: nhiều profile được mã hóa bằng **DPAPI** và tải lại một chạm.

## Bảo mật

- Mật khẩu và file profile được mã hóa bằng **DPAPI** (`ProtectedData`, phạm vi người dùng
  hiện tại) — không bao giờ lưu văn bản thuần trên đĩa.
- Chuỗi kết nối luôn bật mã hóa truyền tải (`Encrypt=True`), `Persist Security Info=False`,
  và **không bao giờ được in ra nhật ký hay giao diện**.
- Ô mật khẩu trên giao diện dùng chế độ che dấu (mask).
- Mọi câu lệnh SQL đều tham số hóa; tên đối tượng luôn đi qua `QUOTENAME` tương đương
  (`Quoting`) — tuyệt đối không nối chuỗi tên đối tượng.

## Yêu cầu

- Windows 10/11 (WinForms + DPAPI).
- [.NET SDK 8+](https://dotnet.microsoft.com/download/dotnet/8.0).
  Nếu không có quyền admin, cài bản **user-scope** vào `%USERPROFILE%\.dotnet` —
  các script tự động nhận diện.
- SQL Server nguồn và đích (bản 2008 trở lên cho trải nghiệm tốt nhất).

## Build & chạy

```powershell
# Build toàn bộ solution
.\scripts\build.ps1

# Build rồi chạy unit test
.\scripts\build.ps1 -RunTests
```

Chạy ứng dụng:

```powershell
dotnet run --project .\src\SqlMigrator.UI.WinForms
```

File thực thi: `src\SqlMigrator.UI.WinForms\bin\Release\net8.0-windows\SqlMigrator.exe`.

## Chạy thử với database mẫu

1. Mở `scripts\setup-test-databases.sql` trong SSMS/sqlcmd, chạy để tạo
   `MigrateSQL_Source` (schema + 5 khách hàng + 5 đơn hàng, có FK/index/trigger/view/proc/sequence)
   và `MigrateSQL_Dest` (trống).
2. Mở ứng dụng, trang "Server nguồn": nhập server, nạp danh sách database, chọn
   `MigrateSQL_Source`. Làm tương tự bên "Server đích" với `MigrateSQL_Dest`
   (tích *Tạo database đích nếu chưa tồn tại* nếu muốn tự tạo).
3. Nạp danh sách bảng bằng nút *"Nạp danh sách bảng từ nguồn"*, bỏ chọn bảng cần loại trừ.
4. Bấm **"Bắt đầu di chuyển"** và theo dõi nhật ký. Cuối cùng so sánh schema + dữ liệu
   giữa hai database.

## Cấu trúc mã nguồn

```
src/
  SqlMigrator.Core/            # Logic nghiệp vụ, tách độc lập để dễ kiểm thử
    Models/                    # MigrationOptions, SchemaModel, SchemaObjects, Enums
    Interfaces/                # Hợp đồng các module
    Services/
      SchemaExtractor.cs       # Đọc cấu trúc nguồn qua SMO
      SchemaBuilder.cs         # Chạy lệnh tạo cấu trúc trên đích
      DataCopier.cs            # Bulk copy dữ liệu, sắp thứ tự theo khóa ngoại
      ConstraintManager.cs     # Bật/tắt ràng buộc, trigger, index
      VersionCompatibilityChecker.cs  # Phân tích khác biệt phiên bản
      MigrationOrchestrator.cs # Điều phối toàn pipeline
      SqlMigratorLogger.cs     # Nhật ký console + file
    Security/
      DpapiDataProtector.cs    # Mã hóa DPAPI
      ConnectionProfile.cs     # Hồ sơ kết nối (mật khẩu đã mã hóa)
      ConnectionProfileStore.cs# Lưu profile mã hóa toàn phần
      SecureConnectionStringBuilder.cs  # Dựng chuỗi kết nối an toàn
  SqlMigrator.UI.WinForms/     # Giao diện Windows Forms (một form tổng thể)
tests/
  SqlMigrator.Tests/           # Unit test (xUnit) cho Core + Security
```

## Kiểm thử

```powershell
dotnet test .\SqlMigrator.sln -c Release
```

Bộ test bao gồm: bọc tên đối tượng (`Quoting`), kiểm duyệt `MigrationOptions`,
vòng đời profile và chống plaintext (`SecurityTests`), bộ lọc cột khi sao chép
(`TableSchema.GetDataColumns`) — ví dụ loại cột computed và rowversion.

## Ứng dụng cần tài khoản gì?

- Tài khoản dùng cho server nguồn cần quyền đọc schema (database viewer / db_datareader).
- Server đích cần quyền `db_ddladmin` (tạo schema) + `db_datareader`, `db_datawriter`
  (đổ dữ liệu); nếu tạo database cần thêm quyền CREATE DATABASE.

## Giới hạn hiện tại

- Không di chuyển: edge case SMO không hỗ trợ, đối tượng CLR khi đích thiếu assembly,
  bảng filetable/temporal/external không được copy dữ liệu (có cảnh báo).
- Chưa có giao diện lập lịch; mỗi lần di chuyển là một lần thao tác trực tiếp.