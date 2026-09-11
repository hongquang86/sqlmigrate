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