using System.Text;

namespace Testy.Core;

public static class TestDataCsv
{
    /// <summary>Literal RFC-style CSV; no expression or formula execution.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text)
    {
        if (text.Length > 5 * 1024 * 1024) throw new InvalidDataException("Test data CSV exceeds 5 MiB.");
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        var quoted = false; var closed = false; var atStart = true;
        void Cell() { row.Add(cell.ToString()); cell.Clear(); closed = false; atStart = true; if (row.Count > 200) throw new InvalidDataException("CSV supports at most 200 columns."); }
        void Row() { Cell(); rows.Add(row); row = []; if (rows.Count > 201) throw new InvalidDataException("CSV supports at most 200 data rows."); }
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (quoted)
            {
                if (value == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { cell.Append('"'); index++; }
                    else { quoted = false; closed = true; }
                }
                else cell.Append(value);
                continue;
            }
            if (value == '"')
            {
                if (!atStart || closed) throw new InvalidDataException("A CSV quote must start a field or be doubled inside a quoted field.");
                quoted = true; atStart = false;
            }
            else if (value == ',') Cell();
            else if (value is '\r' or '\n') { if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++; Row(); }
            else
            {
                if (closed) throw new InvalidDataException("Unexpected text after a closing CSV quote.");
                cell.Append(value); atStart = false;
            }
        }
        if (quoted) throw new InvalidDataException("The final quoted CSV field is not closed.");
        if (row.Count != 0 || cell.Length != 0 || closed || !atStart) Row();
        if (rows.Count < 2) throw new InvalidDataException("CSV needs a header and at least one data row.");
        var headers = rows[0]; headers[0] = headers[0].TrimStart('\uFEFF');
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
            throw new InvalidDataException("CSV headers must be nonempty, unique parameter names.");
        foreach (var header in headers) TestValidator.ValidateId(header);
        var result = new List<IReadOnlyDictionary<string, string>>();
        foreach (var values in rows.Skip(1))
        {
            if (values.Count != headers.Count || values.Any(x => x.Length > 100000)) throw new InvalidDataException("Every CSV row must contain exactly the header columns within the value limit.");
            result.Add(headers.Select((header, index) => (header, value: values[index])).ToDictionary(x => x.header, x => x.value, StringComparer.Ordinal));
        }
        return result;
    }
}
