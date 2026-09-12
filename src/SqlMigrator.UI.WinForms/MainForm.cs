using System.Drawing;
using System.Windows.Forms;
using SqlMigrator.UI.Components;

namespace SqlMigrator.UI
{
    /// <summary>
    /// Cửa sổ chính (shell mỏng): thanh tab điều hướng các nhóm chức năng.
    /// Mọi nghiệp vụ di chuyển nằm nguyên trong <see cref="MigrateTabPage"/>.
    /// Tuân thủ bảo mật: không hiển thị chuỗi kết nối hay mật khẩu.
    /// </summary>
    public sealed class MainForm : Form
    {
        public MainForm(MigrateTabPage migrateTab)
        {
            Text = "SQL Management Tools — Bộ công cụ quản lý Database Server";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1120, 760);
            Size = new Size(1280, 900);
            Font = new Font("Segoe UI", 9.25F);
            Icon = MigrateTabPage.LoadAppIcon();

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(new TabPage("Di chuyển (Migrate)") { Controls = { migrateTab } });
            tabs.TabPages.Add(BuildPlaceholderTab(
                "Sao lưu / Khôi phục",
                "Chức năng sao lưu và khôi phục database đang phát triển.\r\n"
                + "Hiện tại hãy dùng công cụ sao lưu của từng hệ CSDL."));
            tabs.TabPages.Add(BuildPlaceholderTab(
                "Quản trị Service",
                "Chức năng quản trị service/database đang phát triển.\r\n"
                + "Hiện tại hãy dùng công cụ quản trị của từng hệ CSDL."));
            Controls.Add(tabs);
        }

        private static TabPage BuildPlaceholderTab(string title, string message)
        {
            var label = new Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                AutoSize = false
            };
            var group = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };
            group.Controls.Add(label);
            var page = new TabPage(title);
            page.Controls.Add(group);
            return page;
        }
    }
}
