using System.IO.Compression;
using System.Xml.Linq;

namespace Heart.Services;

/// <summary>
/// 极简只读 xlsx 读取器：只解析第一个工作表，返回按行排列的单元格文本（null = 空单元格），
/// 交由调用方按列语义解释。xlsx 本质是 zip + xml，这里只取需要的三份 part
/// （workbook / 共享字符串 / 工作表），因此不引入任何第三方包。
/// 覆盖导入需要的全部单元格类型：共享字符串、内联字符串、公式字符串、数值、布尔。
/// </summary>
internal static class XlsxLite
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>读取第一个工作表；不是合法 xlsx 时返回 null（由调用方走「导入失败」提示）。</summary>
    public static List<string?[]>? ReadFirstSheet(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var sheetPath = ResolveFirstSheetPath(zip);
            var entry = sheetPath is null ? null : zip.GetEntry(sheetPath);
            if (entry is null)
                return null;

            var shared = ReadSharedStrings(zip);
            using var sheetStream = entry.Open();
            var sheetData = XDocument.Load(sheetStream).Root?.Element(Main + "sheetData");
            if (sheetData is null)
                return null;

            var rows = new List<string?[]>();
            foreach (var row in sheetData.Elements(Main + "row"))
            {
                // 空单元格/被跳过的列一律补 null；单元格的 r 属性（如 "C7"）给出真实列号，
                // 缺 r 时按前一个单元格顺延
                var cells = new List<string?>();
                var column = -1;
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var reference = (string?)cell.Attribute("r");
                    var index = reference is null ? column + 1 : ColumnIndex(reference);
                    if (index < 0)
                        continue;
                    while (cells.Count < index)
                        cells.Add(null);
                    cells.Add(ReadCell(cell, shared));
                    column = index;
                }

                // 行号可能跳号（写出行省略时），按 r 属性落位，中间补空行
                var rowNumber = (int?)row.Attribute("r") ?? rows.Count + 1;
                if (rowNumber < 1)
                    rowNumber = rows.Count + 1;
                while (rows.Count < rowNumber - 1)
                    rows.Add(Array.Empty<string?>());
                if (rows.Count == rowNumber - 1)
                    rows.Add(cells.ToArray());
                else
                    rows[rowNumber - 1] = cells.ToArray();
            }

            return rows;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>workbook.xml 里第一个 sheet 对应的 part 路径（如 xl/worksheets/sheet1.xml）。</summary>
    private static string? ResolveFirstSheetPath(ZipArchive zip)
    {
        var workbook = zip.GetEntry("xl/workbook.xml");
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is not null && rels is not null)
        {
            using var workbookStream = workbook.Open();
            var id = XDocument.Load(workbookStream).Root?
                .Element(Main + "sheets")?
                .Elements(Main + "sheet")
                .FirstOrDefault()?
                .Attribute(Rel + "id")?.Value;

            if (id is not null)
            {
                using var relStream = rels.Open();
                var target = XDocument.Load(relStream).Root?
                    .Elements(PkgRel + "Relationship")
                    .FirstOrDefault(r => (string?)r.Attribute("Id") == id)?
                    .Attribute("Target")?.Value;

                if (!string.IsNullOrWhiteSpace(target))
                {
                    // Target 相对 xl/ 目录：可能写成 "worksheets/sheet1.xml" 或 "/xl/worksheets/sheet1.xml"
                    var normalized = target.Replace('\\', '/').TrimStart('/');
                    while (normalized.StartsWith("../", StringComparison.Ordinal))
                        normalized = normalized[3..];
                    if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                        normalized = "xl/" + normalized;
                    return normalized;
                }
            }
        }

        // 兜底：workbook.xml 缺失或结构异常时，直接取第一个工作表 part
        return zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?
            .FullName;
    }

    /// <summary>共享字符串表（xl/sharedStrings.xml）。富文本按 run 拼接，跳过日文注音 rPh。</summary>
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return result;

        using var stream = entry.Open();
        var root = XDocument.Load(stream).Root;
        if (root is null)
            return result;

        foreach (var si in root.Elements(Main + "si"))
        {
            var runs = si.Elements(Main + "t")
                .Concat(si.Elements(Main + "r").Elements(Main + "t"))
                .Select(t => t.Value);
            result.Add(string.Concat(runs));
        }
        return result;
    }

    /// <summary>单个单元格取文本：按 t 属性区分共享字符串 / 内联字符串 / 布尔 / 数值。</summary>
    private static string? ReadCell(XElement cell, List<string> shared)
    {
        switch ((string?)cell.Attribute("t"))
        {
            case "s":
            {
                var raw = cell.Element(Main + "v")?.Value;
                return int.TryParse(raw, out var index) && index >= 0 && index < shared.Count
                    ? shared[index]
                    : null;
            }
            case "inlineStr":
            {
                var inline = cell.Element(Main + "is");
                return inline is null
                    ? null
                    : string.Concat(inline.Descendants(Main + "t").Select(t => t.Value));
            }
            case "b":
                return cell.Element(Main + "v")?.Value == "1" ? "1" : "0";
            case "e":
                return null; // #N/A 之类的错误值按空处理
            default:
                // 数值（日期序列值也在此列，由调用方按列语义解释）与 t="str" 的公式结果
                return cell.Element(Main + "v")?.Value;
        }
    }

    /// <summary>单元格引用（如 "AB12"）→ 0 基列号；非法引用返回 -1。</summary>
    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')
                index = index * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z')
                index = index * 26 + (ch - 'a' + 1);
            else
                break;
        }
        return index - 1;
    }
}
