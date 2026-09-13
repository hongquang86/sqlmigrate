using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Lịch chạy backup tự động: hàng ngày / hàng giờ / hàng tuần.</summary>
    public enum BackupJobFrequency
    {
        Daily,
        Hourly,
        Weekly
    }

    /// <summary>
    /// Một job sao lưu tự động. Nhúng bản sao <see cref="Security.ConnectionProfile"/>
    /// (mật khẩu vẫn mã hóa DPAPI cùng máy/user) để chế độ headless chạy được mà
    /// không cần mở giao diện.
    /// </summary>
    public sealed class BackupJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public Security.ConnectionProfile Source { get; set; } = new Security.ConnectionProfile();
        public List<string> Databases { get; set; } = new List<string>();
        /// <summary>Thư mục lưu file .bak TRÊN SERVER.</summary>
        public string Folder { get; set; } = string.Empty;
        public bool FullBackup { get; set; } = true;
        public bool Compression { get; set; } = true;
        public BackupJobFrequency Frequency { get; set; } = BackupJobFrequency.Daily;
        /// <summary>Giờ chạy (Daily/Weekly), định dạng HH:mm.</summary>
        public string TimeOfDay { get; set; } = "01:00";
        /// <summary>Khoảng giờ (Hourly). Ngày trong tuần (Weekly): Sun..Sat.</summary>
        public int IntervalHours { get; set; } = 6;
        public string DayOfWeek { get; set; } = "SUN";
    }
}
