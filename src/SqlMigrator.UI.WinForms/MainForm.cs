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
        public MainForm(MigrateTabPage migrateTab, BackupRestoreTabPage backupTab)
        {
            Text = "SQL Management Tools — Bộ công cụ quản lý Database Server";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1120, 760);
            Size = new Size(1280, 900);
            Font = new Font("Segoe UI", 9.25F);
            Icon = MigrateTabPage.LoadAppIcon();

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(220, 34);
            tabs.DrawItem += DrawMainTab;
            tabs.TabPages.Add(new TabPage("Di chuyển (Migrate)") { Controls = { migrateTab } });
            tabs.TabPages.Add(new TabPage("Sao lưu / Khôi phục") { Controls = { backupTab } });
            tabs.TabPages.Add(BuildPlaceholderTab(
                "Quản trị Service",
                "Chức năng quản trị service/database đang phát triển.\r\n"
                + "Hiện tại hãy dùng công cụ quản trị của từng hệ CSDL."));
            Controls.Add(tabs);
        }

        /// <summary>
        /// Vẽ thẻ tab rõ nét: tab đang chọn nền xanh thép + chữ trắng đậm,
        /// tab còn lại nền xám nhạt + chữ tối (mặc định WinForms quá mờ nhạt).
        /// </summary>
        private static void DrawMainTab(object? sender, DrawItemEventArgs e)
        {
            if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count)
                return;
            var selected = e.Index == tabs.SelectedIndex;
            var back = selected ? Color.FromArgb(30, 100, 170) : Color.FromArgb(225, 232, 240);
            var fore = selected ? Color.White : Color.FromArgb(40, 55, 70);
            using var bg = new SolidBrush(back);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text,
                new Font("Segoe UI", 10F, selected ? FontStyle.Bold : FontStyle.Regular),
                e.Bounds, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
