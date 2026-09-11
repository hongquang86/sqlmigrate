/* ============================================================================
   Shim DATEDIFF_BIG cho SQL Server 2014.
   - DATEDIFF thường trả về INT nên tràn số với giây/mili-giây/micro-giây/
     nano-giây trên khoảng lớn; hàm này tính đúng về BIGINT bằng cách tách
     ngày/giờ/phút (phần dư luôn vừa INT) rồi cộng dồn bằng BIGINT.
   - DATEDIFF chỉ nhận từ khóa datepart cố định nên mỗi đơn vị là một nhánh IF.
   - Chỉ dùng cú pháp có từ SQL 2014 trở xuống. Chạy lại nhiều lần an toàn.
   - Lưu ý hiệu năng: hàm vô hướng gọi theo từng dòng sẽ chậm hơn hàm hệ thống;
     đúng trước, nhanh sau.
   ============================================================================ */
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'Compat')
    EXEC('CREATE SCHEMA Compat');
GO
IF OBJECT_ID(N'Compat.DatediffBig', N'FN') IS NOT NULL DROP FUNCTION Compat.DatediffBig;
GO

CREATE FUNCTION Compat.DatediffBig(
    @part SYSNAME,
    @start DATETIME2,
    @end DATETIME2
)
RETURNS BIGINT
AS
BEGIN
    -- Đảo dấu để phần dư luôn dương (tách mốc lớn rồi cộng phần dư nhỏ).
    IF @end < @start RETURN -Compat.DatediffBig(@part, @end, @start);

    DECLARE @p NVARCHAR(20) = LOWER(@part);

    -- Đơn vị ngày trở lên: DATEDIFF INT không bao giờ tràn trong thực tế.
    IF @p IN (N'yy', N'yyyy', N'year')
        RETURN DATEDIFF(year, @start, @end);
    IF @p IN (N'q', N'qq', N'quarter')
        RETURN DATEDIFF(quarter, @start, @end);
    IF @p IN (N'm', N'mm', N'month')
        RETURN DATEDIFF(month, @start, @end);
    IF @p IN (N'dy', N'y', N'dayofyear')
        RETURN DATEDIFF(dayofyear, @start, @end);
    IF @p IN (N'd', N'dd', N'day')
        RETURN DATEDIFF(day, @start, @end);
    IF @p IN (N'wk', N'ww', N'week')
        RETURN DATEDIFF(week, @start, @end);
    IF @p IN (N'isowk', N'isoww')
        RETURN DATEDIFF(isowk, @start, @end);
    IF @p IN (N'hh', N'hour')
        RETURN DATEDIFF(hour, @start, @end);
    IF @p IN (N'mi', N'n', N'minute')
        RETURN DATEDIFF(minute, @start, @end);

    -- Giây: tách ngày (phần dư < 86400 vừa INT).
    IF @p IN (N's', N'ss', N'second')
    BEGIN
        DECLARE @days BIGINT = DATEDIFF(day, @start, @end);
        RETURN @days * 86400
             + DATEDIFF(second, DATEADD(day, CONVERT(INT, @days), @start), @end);
    END

    -- Mili-giây: tách ngày → giờ (mọi phần dư đều nhỏ, không bao giờ tràn INT).
    IF @p IN (N'ms', N'millisecond')
    BEGIN
        DECLARE @d1 BIGINT = DATEDIFF(day, @start, @end);
        DECLARE @b1 DATETIME2 = DATEADD(day, CONVERT(INT, @d1), @start);
        DECLARE @h1 INT = DATEDIFF(hour, @b1, @end);
        RETURN @d1 * 86400000
             + CONVERT(BIGINT, @h1) * 3600000
             + DATEDIFF(millisecond, DATEADD(hour, @h1, @b1), @end);
    END

    -- Micro-giây: tách ngày → giờ → phút (phần dư < 60 triệu vừa INT).
    IF @p IN (N'mcs', N'microsecond')
    BEGIN
        DECLARE @d2 BIGINT = DATEDIFF(day, @start, @end);
        DECLARE @b2 DATETIME2 = DATEADD(day, CONVERT(INT, @d2), @start);
        DECLARE @h2 INT = DATEDIFF(hour, @b2, @end);
        DECLARE @b2b DATETIME2 = DATEADD(hour, @h2, @b2);
        DECLARE @m2 INT = DATEDIFF(minute, @b2b, @end);
        RETURN @d2 * 86400000000
             + CONVERT(BIGINT, @h2) * 3600000000
             + CONVERT(BIGINT, @m2) * 60000000
             + DATEDIFF(microsecond, DATEADD(minute, @m2, @b2b), @end);
    END

    -- Nano-giây: tách ngày → giờ → phút → giây (phần dư < 1 tỷ vừa INT).
    -- datetime2 có độ phân giải 100ns nên kết quả là bội số của 100, đúng như gốc.
    IF @p IN (N'ns', N'nanosecond')
    BEGIN
        DECLARE @d3 BIGINT = DATEDIFF(day, @start, @end);
        DECLARE @b3 DATETIME2 = DATEADD(day, CONVERT(INT, @d3), @start);
        DECLARE @h3 INT = DATEDIFF(hour, @b3, @end);
        DECLARE @b3b DATETIME2 = DATEADD(hour, @h3, @b3);
        DECLARE @m3 INT = DATEDIFF(minute, @b3b, @end);
        DECLARE @b3c DATETIME2 = DATEADD(minute, @m3, @b3b);
        DECLARE @s3 INT = DATEDIFF(second, @b3c, @end);
        RETURN @d3 * 86400000000000
             + CONVERT(BIGINT, @h3) * 3600000000000
             + CONVERT(BIGINT, @m3) * 60000000000
             + CONVERT(BIGINT, @s3) * 1000000000
             + DATEDIFF(nanosecond, DATEADD(second, @s3, @b3c), @end);
    END

    -- Đơn vị lạ: lỗi to rõ ràng (trong hàm không RAISERROR được nên dùng chia 0).
    DECLARE @zero INT = 0;
    RETURN 1 / @zero;
END
GO
