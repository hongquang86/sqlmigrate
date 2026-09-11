/*
    SQL Migrator — Kịch bản dựng database mẫu để chạy thử di chuyển.

    Tạo hai database:
      * MigrateSQL_Source — database nguồn chứa schema + dữ liệu mẫu.
      * MigrateSQL_Dest   — database đích BAN ĐẦU TRỐNG (không tồn tại hoặc tồn tại rỗng).

    Cách dùng: mở SSMS / sqlcmd và chạy toàn bộ script dưới tài khoản có quyền CREATE DATABASE.
    Lưu ý: script có thể chạy lặp lại (DROP + CREATE lại từ đầu) để reset môi trường test.
*/

SET NOCOUNT ON;

-- ============ 1. Chỉ dùng khi muốn reset toàn bộ ============
IF DB_ID('MigrateSQL_Dest') IS NOT NULL
BEGIN
    ALTER DATABASE [MigrateSQL_Dest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [MigrateSQL_Dest];
END

IF DB_ID('MigrateSQL_Source') IS NOT NULL
BEGIN
    ALTER DATABASE [MigrateSQL_Source] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [MigrateSQL_Source];
END

-- ============ 2. Database nguồn ============
CREATE DATABASE [MigrateSQL_Source];
GO
USE [MigrateSQL_Source];
GO

-- Schema phi hệ thống để kiểm tra việc di chuyển schema
CREATE SCHEMA [sales];
GO

-- Bảng khách hàng (có identity + khóa ngoại từ bảng đơn hàng)
CREATE TABLE [dbo].[Customers] (
    [CustomerId]      INT              IDENTITY(1,1) NOT NULL,
    [FullName]        NVARCHAR(200)    NOT NULL,
    [Email]           NVARCHAR(200)    NULL,
    [CreatedAt]       DATETIME2(3)     NOT NULL CONSTRAINT [DF_Customers_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [Balance]         DECIMAL(18,2)    NOT NULL CONSTRAINT [DF_Customers_Balance] DEFAULT (0),
    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([CustomerId])
);

CREATE TABLE [sales].[Orders] (
    [OrderId]        INT            IDENTITY(1,1) NOT NULL,
    [CustomerId]     INT            NOT NULL,
    [OrderNumber]    NVARCHAR(50)   NOT NULL,
    [OrderDate]      DATETIME2(3)   NOT NULL CONSTRAINT [DF_Orders_OrderDate] DEFAULT (SYSUTCDATETIME()),
    [TotalAmount]    DECIMAL(18,2)  NOT NULL,
    -- Cột computed để kiểm tra bỏ qua khi sao chép dữ liệu
    [TotalDisplay]   AS ('#' + [OrderNumber] + ' - ' + CONVERT(NVARCHAR(20), [TotalAmount])),
    [RowVersion]     ROWVERSION,
    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId]),
    CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId])
        REFERENCES [dbo].[Customers] ([CustomerId])
);

CREATE INDEX [IX_Orders_CustomerId] ON [sales].[Orders] ([CustomerId]);
CREATE INDEX [IX_Orders_OrderDate]  ON [sales].[Orders] ([OrderDate]);

-- Trigger mẫu trên bảng đơn hàng để kiểm tra tùy chọn vô hiệu hóa trigger
GO
CREATE OR ALTER TRIGGER [sales].[TR_Orders_Audit]
ON [sales].[Orders]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Chỉ ghi nhận, không làm gì phức tạp để việc test không bị nhiễu.
    DECLARE @rows INT = (
        SELECT COUNT(*) FROM inserted
    ) + (
        SELECT COUNT(*) FROM deleted
    );
    IF @rows > 0
        PRINT 'Đã thay đổi đơn hàng.';
END;
GO

-- Stored procedure mẫu
GO
CREATE OR ALTER PROCEDURE [sales].[GetOrdersByCustomer]
    @CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [OrderId], [OrderNumber], [OrderDate], [TotalAmount]
      FROM [sales].[Orders]
     WHERE [CustomerId] = @CustomerId;
END;
GO

-- Hàm mẫu
GO
CREATE OR ALTER FUNCTION [dbo].[Fn_ActiveOrdersCount] (@CustomerId INT)
RETURNS INT
AS
BEGIN
    RETURN (
        SELECT COUNT(*)
          FROM [sales].[Orders]
         WHERE [CustomerId] = @CustomerId
    );
END;
GO

-- View mẫu
GO
CREATE OR ALTER VIEW [sales].[vOrdersSummary] AS
    SELECT c.[CustomerId], c.[FullName], COUNT(o.[OrderId]) AS [OrderCount],
           ISNULL(SUM(o.[TotalAmount]), 0) AS [TotalSpent]
      FROM [dbo].[Customers] c
      LEFT JOIN [sales].[Orders] o ON o.[CustomerId] = c.[CustomerId]
     GROUP BY c.[CustomerId], c.[FullName];
GO

-- Sequence mẫu
GO
CREATE SEQUENCE [sales].[Seq_InvoiceNumber] AS INT START WITH 1000 INCREMENT BY 1;
GO

-- ============ 3. Dữ liệu mẫu (nguồn) ============
INSERT INTO [dbo].[Customers] ([FullName], [Email], [Balance])
VALUES
    (N'Nguyễn Văn An',    N'an.nguyen@example.com',   1250000),
    (N'Trần Thị Bích',    N'bich.tran@example.com',    400000),
    (N'Lê Hoàng Cường',   N'cuong.le@example.com',     300000),
    (N'Phạm Minh Dũng',   N'dung.pham@example.com',    990000),
    (N'Hồ Thị Em',        N'em.ho@example.com',        100000);
GO

INSERT INTO [sales].[Orders] ([CustomerId], [OrderNumber], [OrderDate], [TotalAmount])
VALUES
    (1, 'SO-2026-0001', '2026-01-05', 259900),
    (1, 'SO-2026-0002', '2026-02-11', 189000),
    (2, 'SO-2026-0003', '2026-02-18', 45000),
    (3, 'SO-2026-0004', '2026-03-02', 99000),
    (4, 'SO-2026-0005', '2026-03-27', 499000);
GO

-- ============ 4. Database đích (rỗng, chỉ tạo nền để chạy thử) ============
CREATE DATABASE [MigrateSQL_Dest];
GO

PRINT N'Hoàn tất. Sẵn sàng chạy SQL Migrator từ MigrateSQL_Source sang MigrateSQL_Dest.';