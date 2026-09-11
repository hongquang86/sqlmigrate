SQL Migrator - Công cụ di chuyển database SQL Server

Phiên bản 1.0.0

TÍNH NĂNG CHÍNH:
- Di chuyển cấu trúc (schema) và dữ liệu giữa các server SQL Server
- Hỗ trợ nâng/hạ phiên bản SQL Server (2008 -> 2022)
- Sao chép trigger cấp server, quyền truy cập, index, foreign key
- Đồng bộ dữ liệu tăng dần (Update New / Update Full) với mốc baseline
- Engine dữ liệu lớn: chunk keyset, song song, checkpoint hồi tục, RAM bounded

YÊU CẦU HỆ THỐNG:
- Windows 10/11 hoặc Windows Server 2016+
- .NET 8 Runtime (đã đóng gói trong file cài đặt - không cần cài riêng)
- SQL Server 2008 trở lên (nguồn và đích)
- Quyền sysadmin hoặc db_owner trên database nguồn/đích

HƯỚNG DẪN SỬ DỤNG:
1. Cấu hình server nguồn và đích (mật khẩu được mã hóa DPAPI)
2. Chọn database, chế độ di chuyển (Toàn bộ / Chỉ cấu trúc / Chỉ dữ liệu)
3. Chọn bảng cần di chuyển (mặc định chọn tất cả)
4. Tùy chọn engine dữ liệu lớn (luồng song song, đệm RAM, chunk keyset...)
5. Bấm "Kiểm tra trước khi chạy" để xem preflight và dữ liệu mới
6. Bấm "Bắt đầu di chuyển" hoặc "Cập nhật dữ liệu mới/đầy đủ"

CẤU HÌNH ENGINE DỮ LIỆU LỚN:
- Luồng song song (MaxParallelism): số bảng độc lập chạy cùng lúc (-1 = tự động)
- Đệm RAM (MaxBufferMB): ngân sách bộ nhớ tối đa cho engine (mặc định 128MB)
- Dòng/chunk (ChunkRowCount): số dòng mỗi đợt keyset (0 = tự chọn)
- Chunk keyset: đọc WHERE key > @mốc thay vì scan toàn bảng
- Snapshot reading: đọc không chặn ghi (RCSI/SNAPSHOT)
- Bật RCSI nguồn: tự bật READ_COMMITTED_SNAPSHOT nếu có quyền
- Chuyển recovery đích: tạm sang BULK_LOGGED khi đổ dữ liệu lớn
- Checkpoint hồi tục: ghi mốc chunk để tiếp tục khi bị đứt
- Ước lượng số dòng: dùng sys.partitions thay vì COUNT_BIG

THƯ MỤC DỮ LIỆU (LocalAppData):
- %LocalAppData%\SqlMigrator\baselines\ : mốc đồng bộ dữ liệu
- %LocalAppData%\SqlMigrator\transfers\ : checkpoint hồi tục

BẢO MẬT:
- Mật khẩu không bao giờ lưu dạng văn bản thuần
- Mã hóa DPAPI (ProtectedData) phạm vi người dùng hiện tại
- Không log chuỗi kết nối, mật khẩu, secret

GIẤY PHÉP: MIT License