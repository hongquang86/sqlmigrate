using System;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Dựng câu lệnh tạo nhóm file (filegroup) ROWS trên database đích.
    /// Thuần dựng chuỗi (không chạm database) nên dễ kiểm thử; thực thi do
    /// <see cref="DatabaseProvisioner"/> đảm nhiệm. Chỉ ghi đích.
    /// </summary>
    public static class FilegroupSqlBuilder
    {
        /// <summary>
        /// Câu ALTER DATABASE tạo filegroup + file dữ liệu nếu chưa có.
        /// Chạy được nhiều lần (có guard IF NOT EXISTS). File .ndf đặt dưới
        /// thư mục dữ liệu đã cho.
        /// </summary>
        public static string BuildEnsureFilegroupSql(string dbName, string filegroup, string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dbName))
                throw new ArgumentException("Thiếu tên database đích.", nameof(dbName));
            if (string.IsNullOrWhiteSpace(filegroup))
                throw new ArgumentException("Thiếu tên filegroup.", nameof(filegroup));
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("Thiếu thư mục dữ liệu.", nameof(dataDirectory));

            var dir = dataDirectory.Trim().TrimEnd('\\', '/');
            var logicalName = dbName.Trim() + "_" + filegroup.Trim();
            var filePath = dir + "\\" + logicalName + ".ndf";

            string Id(string n) => Quoting.QuoteIdentifier(n);
            string Lit(string s) => "N'" + s.Replace("'", "''") + "'";

            return "IF NOT EXISTS (SELECT 1 FROM "
                + Id(dbName.Trim()) + ".sys.filegroups WHERE name = " + Lit(filegroup.Trim()) + ")\n"
                + "BEGIN\n"
                + "    ALTER DATABASE " + Id(dbName.Trim()) + " ADD FILEGROUP " + Id(filegroup.Trim()) + ";\n"
                + "    ALTER DATABASE " + Id(dbName.Trim()) + " ADD FILE (NAME = " + Lit(logicalName)
                + ", FILENAME = " + Lit(filePath) + ") TO FILEGROUP " + Id(filegroup.Trim()) + ";\n"
                + "END;";
        }
    }
}
