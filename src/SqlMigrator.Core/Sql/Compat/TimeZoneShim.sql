/* ============================================================================
   Shim AT TIME ZONE cho SQL Server 2014 (chuyển đổi múi giờ bằng bảng offset).
   - Nhận SQL_VARIANT để phân biệt datetime thường (gắn offset) và datetimeoffset
     (chuyển thẳng), mô phỏng 2 dạng dùng của AT TIME ZONE.
   - Bảng offset lưu GIỜ CHUẨN (standard time). Vùng có DST (giờ mùa hè) sẽ lệch
     trong mùa DST — rule rewrite đã cảnh báo rõ giới hạn này trong báo cáo.
     Các múi không DST (UTC, GMT, SE Asia/China/Tokyo...) chuyển chính xác 100%.
   - Zone lạ không có trong bảng → lỗi to rõ ràng (không trả NULL lặng lẽ).
   - Chỉ dùng cú pháp có từ SQL 2014 trở xuống. Chạy lại nhiều lần an toàn.
   ============================================================================ */
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'Compat')
    EXEC('CREATE SCHEMA Compat');
GO
IF OBJECT_ID(N'Compat.ConvertTimeZone', N'FN') IS NOT NULL DROP FUNCTION Compat.ConvertTimeZone;
GO
IF OBJECT_ID(N'Compat.TimeZoneOffset', N'U') IS NOT NULL DROP TABLE Compat.TimeZoneOffset;
GO

CREATE TABLE Compat.TimeZoneOffset(
    zone SYSNAME NOT NULL PRIMARY KEY,
    utc_offset_minutes INT NOT NULL
);
GO

/* Offset giờ chuẩn (phút). Vùng có DST dùng offset mùa đông (xem lưu ý DST ở trên). */
INSERT INTO Compat.TimeZoneOffset(zone, utc_offset_minutes) VALUES
    (N'UTC', 0),
    (N'GMT Standard Time', 0),
    (N'Greenwich Standard Time', 0),
    (N'Morocco Standard Time', 0),
    (N'GMT', 0),
    (N'W. Europe Standard Time', 60),
    (N'Central European Standard Time', 60),
    (N'Romance Standard Time', 60),
    (N'Central Europe Standard Time', 60),
    (N'E. Europe Standard Time', 120),
    (N'Egypt Standard Time', 120),
    (N'South Africa Standard Time', 120),
    (N'Arabic Standard Time', 180),
    (N'Russian Standard Time', 180),
    (N'Iran Standard Time', 210),
    (N'Arabian Standard Time', 240),
    (N'Afghanistan Standard Time', 270),
    (N'West Asia Standard Time', 300),
    (N'India Standard Time', 330),
    (N'Central Asia Standard Time', 360),
    (N'SE Asia Standard Time', 420),
    (N'China Standard Time', 480),
    (N'Singapore Standard Time', 480),
    (N'W. Australia Standard Time', 480),
    (N'Tokyo Standard Time', 540),
    (N'Korea Standard Time', 540),
    (N'AUS Eastern Standard Time', 600),
    (N'Central Pacific Standard Time', 660),
    (N'New Zealand Standard Time', 720),
    (N'Hawaiian Standard Time', -600),
    (N'Alaskan Standard Time', -540),
    (N'Pacific Standard Time', -480),
    (N'Mountain Standard Time', -420),
    (N'Central Standard Time', -360),
    (N'Central America Standard Time', -360),
    (N'Eastern Standard Time', -300),
    (N'SA Pacific Standard Time', -300),
    (N'Atlantic Standard Time', -240),
    (N'SA Eastern Standard Time', -180),
    (N'Azores Standard Time', -60),
    (N'Cape Verde Standard Time', -60);
GO

/* @toZone NULL = gắn múi giờ (@fromZone) vào datetime thường.
   @toZone có giá trị = chuyển từ @fromZone sang @toZone. Luôn trả datetimeoffset. */
CREATE FUNCTION Compat.ConvertTimeZone(
    @v SQL_VARIANT,
    @fromZone SYSNAME,
    @toZone SYSNAME
)
RETURNS DATETIMEOFFSET
AS
BEGIN
    IF @v IS NULL RETURN NULL;

    DECLARE @o1 INT, @o2 INT;
    SELECT @o1 = utc_offset_minutes FROM Compat.TimeZoneOffset WHERE zone = @fromZone;
    IF @o1 IS NULL
    BEGIN
        -- Zone lạ: lỗi to rõ ràng (trong hàm không RAISERROR được nên dùng chia 0).
        DECLARE @zero INT = 0;
        RETURN 1 / @zero;
    END

    DECLARE @base NVARCHAR(60) = CONVERT(NVARCHAR(60), SQL_VARIANT_PROPERTY(@v, N'BaseType'));

    -- Đầu vào đã có offset: chuyển thẳng sang múi đích (đích NULL = giữ nguyên).
    IF @base = N'datetimeoffset'
    BEGIN
        IF @toZone IS NULL RETURN CONVERT(DATETIMEOFFSET, @v);
        SELECT @o2 = utc_offset_minutes FROM Compat.TimeZoneOffset WHERE zone = @toZone;
        IF @o2 IS NULL
        BEGIN
            DECLARE @zero2 INT = 0;
            RETURN 1 / @zero2;
        END
        RETURN SWITCHOFFSET(CONVERT(DATETIMEOFFSET, @v), @o2);
    END

    -- Đầu vào datetime thường: gắn offset nguồn, rồi chuyển nếu có đích.
    DECLARE @d2 DATETIME2;
    IF @base IN (N'datetime', N'datetime2', N'smalldatetime', N'date')
        SET @d2 = CONVERT(DATETIME2, @v);
    ELSE
    BEGIN
        DECLARE @zero3 INT = 0;
        RETURN 1 / @zero3;
    END
    DECLARE @dto DATETIMEOFFSET = TODATETIMEOFFSET(@d2, @o1);
    IF @toZone IS NULL RETURN @dto;
    SELECT @o2 = utc_offset_minutes FROM Compat.TimeZoneOffset WHERE zone = @toZone;
    IF @o2 IS NULL
    BEGIN
        DECLARE @zero4 INT = 0;
        RETURN 1 / @zero4;
    END
    RETURN SWITCHOFFSET(@dto, @o2);
END
GO
