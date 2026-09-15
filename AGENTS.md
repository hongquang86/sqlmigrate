# QUY TẮC LÀM VIỆC (BẮT BUỘC TUÂN THỦ)

File này là quy tắc bắt buộc, áp dụng cho **MỌI** phiên làm việc trong dự án `MigrateSQL`.
Nếu xung đột giữa yêu cầu của người dùng và file này, cần làm rõ với người dùng trước khi thực hiện.

## 1. Ngôn ngữ
- Toàn bộ **chú thích (comment) trong code** phải bằng **tiếng Việt**.
- Toàn bộ **label, thông báo, tiêu đề trên giao diện ứng dụng** phải bằng **tiếng Việt**.
- Tên biến, hàm, class, file, commit: dùng **tiếng Anh chuẩn** (đặt tên rõ nghĩa).
- Tài liệu (README, hướng dẫn) ưu tiên tiếng Việt.

## 2. Quy trình làm việc
- **Luôn luôn trình bày phân tích và roadmap trước, chờ người dùng duyệt rồi mới bắt tay vào viết code/đổi code.**
- Khi yêu cầu thay đổi phạm vi hoặc thay đổi kiến trúc lớn, phải dừng lại, phân tích tác động và xin phê duyệt.
- Không tự ý thực hiện các thay đổi ngoài những gì đã được duyệt.

## 3. Nền tảng giao diện
- Ứng dụng chạy dạng **GUI** với công nghệ **Windows Forms** (tận dụng sức mạnh của Windows và SQL Server).
- Không xây dựng giao diện dòng lệnh (CLI) làm giao diện chính.
- Toàn bộ logic nghiệp vụ được tách ở lớp `SqlMigrator.Core` để dễ kiểm thử.

## 4. Bảo mật
- **Toàn bộ chuỗi kết nối và thông tin đăng nhập của server nguồn và đích phải được mã hóa và bảo mật**:
  - Lưu trữ: mã hóa bằng DPAPI (`ProtectedData`) ở phạm vi người dùng hiện tại, không lưu mật khẩu dạng văn bản thuần (plaintext).
  - Truyền tải: connection strings luôn bật mã hóa kết nối (`Encrypt=True` / `Force Encryption`) khi có thể.
  - Không bao giờ in chuỗi kết nối, mật khẩu, hoặc secret ra log, console, hoặc giao diện.
  - Mật khẩu nhập trên giao diện dùng kiểm soát dạng mật khẩu (mask) và `SecureString` khi xử lý.
- Mọi câu lệnh SQL dùng tham số hóa (parameterized queries) và hàm bọc tên đối tượng hợp lệ (`QUOTENAME` tương đương), tuyệt đối không nối chuỗi tên đối tượng.

## 5. Chất lượng code
- Code rõ ràng, chuẩn chỉnh, nhất quán.
- Tuân thủ SOLID, dùng Dependency Injection cho khả năng kiểm thử.
- Chia module: SchemaExtractor, SchemaBuilder, DataCopier, ConstraintManager, Logger, Security.
- Kiểm thử bằng xUnit hoặc NUnit cho các thành phần lõi.
- Mọi thay đổi phải chạy được `dotnet build` và kiểm thử trước khi kết thúc phiên nếu có thể.

## 6. Nhật ký phiên làm việc (12/09/2026 — Pha 3 hoàn thiện)
- Phạm vi: hoàn thiện Pha 3 (mover chéo SQL Server ↔ PostgreSQL ↔ SQLite) + sửa UI
  tab Sao lưu/Khôi phục và ConnectionEditor. Không đụng Reconcile, không mở Pha 4+.
- Core: `MigrationGuard` mở full cặp relational; `PostgresEndpoint.ListDatabasesAsync`;
  mover generic nên chiều ngược (PG/SQLite → SQL) chạy chung một đường code.
- UI Migrate: `RunCrossEngineMigrationAsync`, `NormalizeSqliteProfile`, probe mật khẩu
  chỉ trong bộ nhớ; UI Backup: layout 2 cột đúng, panel Dock=Top để scroll thật,
  nút "Chọn…" duyệt disk server (nguồn/đích), sao lưu multi-DB checkbox + tên file
  `<DB>_yyyyMMdd_HHmmss[_Diff].bak`; `ConnectionEditor`: Hệ CSDL textbox hiển thị
  engine detect + tự điền Port, auto-detect cả Connect DB lẫn Kiểm tra kết nối.
- Kiểm chứng: `dotnet build` 0 Warning/0 Error; `dotnet test` 376/376 pass
  (khóa chiều ngược trong `CanonicalDdlTests`); publish + installer + ClickToRun zip.
- Quy ước: mọi commit/push chỉ khi user yêu cầu tường minh.

## 7. Nhật ký phiên làm việc (13/09/2026 — Pha 4+5+6)
- Pha 4a (MySQL/MariaDB 2 chiều): `MySqlEndpoint` full `IDbEndpoint`
  (information_schema, chunk keyset, multi-row INSERT 500 dòng/lệnh),
  `ParseMySql`/`EmitMySql`/`ConvertTo` nhánh MySql, `ForMySql`, guard mở MySQL,
  UI liệt kê DB + Tạo DB utf8mb4.
- Pha 4b (Manage v1): tab Quản trị Service mới (`ManageTabPage`) thay placeholder;
  `IManageProvider` + SQL/MySQL provider (DB/size/session/disk + KILL + xác nhận
  state đổi qua `ManageVerify`); kill luôn có hộp xác nhận.
- Pha 5: `RoutineGuideService` (liệt kê routine + ma trận hướng dẫn viết lại theo
  đích, nhóm 4 tab Quản trị); Manage đọc SQLite (`SqliteManageProvider` + integrity)
  và MongoDB (`MongoManageProvider`: dbStats/currentOp, chỉ đọc).
- Pha 6 (MongoDB 2 chiều): `MongoSurvey` (suy schema từ mẫu, hỗn hợp→chuỗi, lồng→JSON),
  `MongoEndpoint` (keyset đúng thứ tự BSON, InsertMany), `EmitMongo`, `ConvertFor/To`
  nhánh Mongo, guard mở full; `MongoDumpService` (binary cấu hình, không log secret)
  + nhóm 5 tab Quản trị.
- Kiểm chứng: `dotnet build` 0 Warning/0 Error; `dotnet test` 431/431 pass; publish +
  installer + ClickToRun zip. Chưa smoke test server thật (MySQL/Mongo) — đợi user.
- Không đụng Reconcile theo yêu cầu user.

## 8. Nhật ký phiên làm việc (13/09/2026 — Bổ sung vận hành A+B+C, commit 3ba01fd)
- Phạm vi: 3 nhóm bổ sung trong tab Quản trị + Sao lưu được user duyệt ("Duyệt tất cả").
  Không đụng Reconcile, không mở Pha 7. Commit `3ba01fd` đã push (A+B+C + tests).
- A (lập lịch backup): `BackupJobModels`/`BackupJobStore`/`BackupJobRunner`/
  `TaskSchedulerService` + `BackupScheduleDialog` (nút "Lập lịch..." trong
  BackupRestoreTabPage); headless `SqlMigrator.exe --run-backup <jobId>` qua schtasks
  (folder "SqlMigrator", tên `<tên> [8 ký tự đầu id]`, Daily/Minute(giờ*60)/Weekly /D);
  job nhúng copy profile (DPAPI), tên file `<DB>_yyyyMMdd_HHmmss[_Diff].bak` trả exit
  code 0/1, log riêng theo ngày.
- B (vận hành): `ServiceControlService` (sc.exe Start/Stop/Restart local/remote,
  ParseState mã STATE, preset MSSQL/MySQL/PG/Mongo + tên tự gõ); `QueryRunnerService`
  (tối đa 5000 dòng, SQLite nạp thủ công qua reader, Mongo tắt + ghi chú); nhóm 6+7.
- C (đối chiếu + lịch sử + file backup): `InventoryCompareService` (số dòng từng bảng
  2 bên, khác engine được nhờ canonical); `OpsLogStore` (300 entry, không secret,
  ghi kill/dump/restore/service/truy vấn/đối chiếu/xóa file); `ServerFolderService.
  GetChildFilesAsync` (xp_dirtree, cột 2 = file, chỉ xem — không xóa file server);
  BuildCompareGroup/BuildFileGroup/BuildOpsLogGroup + wire events + SetBusy + `_ = 
  RefreshOpsLogAsync()` khi khởi tạo ManageTabPage.
- Kiểm chứng: `dotnet build` 0 Warning/0 Error; `dotnet test` 459/459 pass; publish +
  installer `SQLMigrator_Setup_1.1.3.exe` + ClickToRun zip 73.1 MB (đã chứa A/B/C).
- Lưu ý: working tree clean sau commit 3ba01fd; vẫn chờ smoke test server thật
  (MySQL/MariaDB/MongoDB) do user hẹn.

## 9. Nhật ký phiên làm việc (14/09/2026 — Fix timeout bulk copy MSSQL→MSSQL, commit 51734f9)
- Báo cáo user: migrate MSSQL→MSSQL DB ~12GB, bảng 5 triệu dòng → copy lâu, log
  "session expire / time out". Nguyên nhân chính: ô "Chờ bulk copy (giây)" mặc định
  **0** nhưng chưa set Minimum/Value → `BulkCopyTimeoutSeconds = 0`; `DataCopier` chỉ
  set `bulk.BulkCopyTimeout` khi `> 0` nên `SqlBulkCopy` giữ **mặc định 30 giây** →
  chunk bảng lớn vượt 30s → SqlException timeout (-2, transient) → `SqlRetry` retry cả
  chunk 3 lần → chậm rồi fail. (Nhánh copy MSSQL→MSSQL đi qua `DataCopier` pipeline
  cổ điển, không phải `SqlServerEndpoint`/cross-engine.)
- Xử lý (user duyệt "1800 giây (30 phút)"): `DataCopier.cs` (2 chỗ keyset + fullscan)
  luôn `BulkCopyTimeout = _options.BulkCopyTimeoutSeconds` (0 = không giới hạn) +
  thêm `EffectiveBulkCopyTimeoutSeconds`; UI `MigrateTabPage` `Minimum=0, Value=1800`,
  nhãn "Chờ bulk copy (giây, 0 = không giới hạn):"; đổi nhãn "Dòng/chunk" →
  "Số dòng/chunk (0=tự chọn):"; test `DataCopier_BulkTimeoutGiáTrịHiệuDụng...`.
- Sự cố môi trường: giữa phiên phát hiện ~30 file bị ghi đè ngoài (MainForm 2300 dòng
  ảo, thiếu enum `DatabaseEngine`, AGENTS.md mất mục 6-8 là bản đúng nhất sau khi soi
  bằng clone sạch: `MainForm.cs` thật = shell 54 dòng host 3 tab, UI nằm hết trong
  `MigrateTabPage`). User duyệt khôi phục về `060f83a` (`git checkout -- .`) rồi
  re-apply fix. **Bài học: file "MainForm.cs 2300 dòng" giai đoạn trước là dữ liệu ghi
  đè ngoài, bản commit thật không có timeout trong MainForm.**
- Kiểm chứng: build 0 Warning/0 Error; test **461/461 pass**; publish lại installer
  `SQLMigrator_Setup_1.1.3.exe` (53.2 MB) + ClickToRun zip (73.1 MB).
- Working tree clean sau commit `51734f9` (push `060f83a..51734f9`); vẫn chờ smoke
  test server thật (MySQL/MariaDB/MongoDB) + test lại migrate 12GB với timeout mới.