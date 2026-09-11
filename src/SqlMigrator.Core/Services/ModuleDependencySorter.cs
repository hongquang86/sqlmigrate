using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Sắp xếp view/function/procedure theo phụ thuộc để tạo đối tượng gốc trước
    /// (VD: tạo vwCuocTamTinh trước VIE_CuocTamTinhHT). Thuật toán Kahn ổn định:
    /// giữa các node cùng "sẵn sàng" luôn ưu tiên thứ tự gốc nên nhóm không phụ
    /// thuộc nhau giữ nguyên vị trí tương đối. Vòng tròn phụ thuộc thì giữ thứ tự
    /// gốc + để lỗi hiện rõ (khâu fix retry sẽ thử lại nhiều vòng).
    /// Thuần logic (không chạm database) nên dễ kiểm thử.
    /// </summary>
    internal static class ModuleDependencySorter
    {
        /// <summary>
        /// Sắp xếp sao cho dependency đứng trước đối tượng phụ thuộc.
        /// dependencies: node → danh sách node nó tham chiếu trực tiếp.
        /// Chỉ cạnh mà cả 2 đầu đều trong names mới tính; còn lại bỏ qua.
        /// </summary>
        public static List<string> SortKeys(
            IReadOnlyList<string> names,
            IReadOnlyDictionary<string, List<string>> dependencies)
        {
            var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Count; i++)
            {
                if (!indexOf.ContainsKey(names[i]))
                    indexOf[names[i]] = i;
            }

            // Copy về dict không phân biệt hoa/thường để không phụ thuộc
            // comparer của dict bên gọi truyền vào.
            var depLookup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (dependencies != null)
            {
                foreach (var kv in dependencies)
                {
                    if (!depLookup.ContainsKey(kv.Key))
                        depLookup[kv.Key] = kv.Value ?? new List<string>();
                }
            }

            // Indegree = số dependency nằm trong tập đang xét (bỏ tự tham chiếu).
            var indegree = new int[names.Count];
            var children = new List<int>[names.Count];
            for (var i = 0; i < names.Count; i++)
                children[i] = new List<int>();

            for (var i = 0; i < names.Count; i++)
            {
                if (!depLookup.TryGetValue(names[i], out var deps) || deps == null)
                    continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dep in deps)
                {
                    if (string.IsNullOrWhiteSpace(dep) || !seen.Add(dep))
                        continue;
                    if (!indexOf.TryGetValue(dep, out var parent))
                        continue; // ngoài phạm vi (bảng, object hệ thống...) → không chặn
                    if (parent == i)
                        continue; // tự tham chiếu → bỏ qua
                    indegree[i]++;
                    children[parent].Add(i);
                }
            }

            // Kahn ổn định: luôn lấy node sẵn sàng có thứ tự gốc nhỏ nhất.
            var done = new bool[names.Count];
            var result = new List<string>(names.Count);
            while (result.Count < names.Count)
            {
                var next = -1;
                for (var i = 0; i < names.Count; i++)
                {
                    if (!done[i] && indegree[i] == 0)
                    {
                        next = i;
                        break;
                    }
                }

                if (next < 0)
                {
                    // Vòng tròn phụ thuộc: giữ thứ tự gốc cho phần còn lại.
                    for (var i = 0; i < names.Count; i++)
                    {
                        if (!done[i])
                        {
                            done[i] = true;
                            result.Add(names[i]);
                        }
                    }
                    break;
                }

                done[next] = true;
                result.Add(names[next]);
                foreach (var child in children[next])
                    indegree[child]--;
            }

            return result;
        }
    }
}
