/* ============================================================================
   Shim SESSION_CONTEXT / sp_set_session_context cho SQL Server 2014.
   - SESSION_CONTEXT gốc là key-value theo session; shim dùng bảng thường khóa
     theo @@SPID nên tương đương trong cùng một session/kết nối.
   - Kiểu trả về SQL_VARIANT giống gốc (so sánh = với số/chuỗi vẫn đúng).
   - Cờ @read_only của sp_set_session_context gốc KHÔNG mô phỏng được
     (luôn cho ghi đè) — rule rewrite đã cảnh báo điều này.
   - Dọn rác SPID đã chết khi có quyền VIEW SERVER STATE; thiếu quyền thì bỏ qua.
   - Chỉ dùng cú pháp có từ SQL 2014 trở xuống. Chạy lại nhiều lần an toàn.
   ============================================================================ */
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'Compat')
    EXEC('CREATE SCHEMA Compat');
GO
IF OBJECT_ID(N'Compat.SessionContext_Set', N'P') IS NOT NULL DROP PROCEDURE Compat.SessionContext_Set;
GO
IF OBJECT_ID(N'Compat.SessionContext_Get', N'FN') IS NOT NULL DROP FUNCTION Compat.SessionContext_Get;
GO
IF OBJECT_ID(N'Compat.SessionData', N'U') IS NOT NULL DROP TABLE Compat.SessionData;
GO

CREATE TABLE Compat.SessionData(
    spid INT NOT NULL,
    [key] SYSNAME NOT NULL,
    value SQL_VARIANT NULL,
    updated DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Compat_SessionData PRIMARY KEY(spid, [key])
);
GO

/* Đọc: thiếu key trả NULL giống SESSION_CONTEXT gốc. */
CREATE FUNCTION Compat.SessionContext_Get(@key SYSNAME)
RETURNS SQL_VARIANT
AS
BEGIN
    DECLARE @v SQL_VARIANT;
    SELECT @v = value FROM Compat.SessionData
     WHERE spid = @@SPID AND [key] = @key;
    RETURN @v;
END
GO

/* Ghi: luôn cho ghi đè (không có read_only). Key NULL thì không làm gì. */
CREATE PROCEDURE Compat.SessionContext_Set(
    @key SYSNAME,
    @value SQL_VARIANT
)
AS
BEGIN
    SET NOCOUNT ON;
    IF @key IS NULL RETURN;
    -- Dọn hàng của session đã chết (cần VIEW SERVER STATE; lỗi thì bỏ qua).
    BEGIN TRY
        DELETE FROM Compat.SessionData
         WHERE spid NOT IN (SELECT session_id FROM sys.dm_exec_sessions);
    END TRY
    BEGIN CATCH
    END CATCH
    UPDATE Compat.SessionData
       SET value = @value, updated = SYSUTCDATETIME()
     WHERE spid = @@SPID AND [key] = @key;
    IF @@ROWCOUNT = 0
        INSERT INTO Compat.SessionData(spid, [key], value)
        VALUES(@@SPID, @key, @value);
END
GO
