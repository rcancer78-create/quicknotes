using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QuickNotes.Tools.Performance;

public sealed class PresentMonParseResult
{
    public bool ProcessNotFound { get; init; }
    public bool Malformed { get; init; }
    public bool Empty { get; init; }
    public bool HeaderOnly { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int DataRowCount { get; init; }
    public int DisplayedFrameCount { get; init; }
    public int DroppedCount { get; init; }
    public int AllowsTearingCount { get; init; }
    public bool VariableRefreshLikely { get; init; }
    public string PresentModeSample { get; init; } = string.Empty;
    public IReadOnlyList<double> DisplayIntervalsMs { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> PresentIntervalsMs { get; init; } = Array.Empty<double>();
}

public static class PresentMonCsvParser
{
    public static PresentMonParseResult ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new PresentMonParseResult
            {
                Empty = true,
                Reason = "PresentMon CSV path is missing or the file does not exist."
            };
        }

        string text = File.ReadAllText(path);
        return Parse(text);
    }

    public static PresentMonParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PresentMonParseResult
            {
                Empty = true,
                Reason = "PresentMon output is empty."
            };
        }

        if (LooksLikeProcessNotFound(text))
        {
            return new PresentMonParseResult
            {
                ProcessNotFound = true,
                Reason = "PresentMon reported that the target process was not found. No presentation frames were captured."
            };
        }

        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        if (lines.Length == 0)
        {
            return new PresentMonParseResult
            {
                Empty = true,
                Reason = "PresentMon output is empty."
            };
        }

        int headerIndex = FindHeaderIndex(lines);
        if (headerIndex < 0)
        {
            return new PresentMonParseResult
            {
                Malformed = true,
                Reason = "PresentMon output has no CSV header with Application and a frame-time column."
            };
        }

        string[] header = SplitCsv(lines[headerIndex]);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
        {
            string name = header[i].Trim().Trim('"');
            if (name.Length > 0 && !map.ContainsKey(name))
            {
                map[name] = i;
            }
        }

        if (!map.ContainsKey("Application")
            || (!HasAny(map, "MsBetweenDisplayChange", "msBetweenDisplayChange")
                && !HasAny(map, "MsBetweenPresents", "msBetweenPresents")))
        {
            return new PresentMonParseResult
            {
                Malformed = true,
                Reason = "PresentMon CSV header is missing Application or frame-time columns."
            };
        }

        var display = new List<double>();
        var presents = new List<double>();
        int dropped = 0;
        int tearing = 0;
        int dataRows = 0;
        string presentMode = string.Empty;

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            string[] cells = SplitCsv(line);
            if (cells.Length < 2)
            {
                return new PresentMonParseResult
                {
                    Malformed = true,
                    Reason = "PresentMon CSV contains a malformed data row."
                };
            }

            dataRows++;
            if (TryGetDouble(map, cells, "MsBetweenPresents", "msBetweenPresents", out double presentMs) && presentMs > 0)
            {
                presents.Add(presentMs);
            }

            bool rowDropped = TryGetInt(map, cells, "Dropped", out int droppedFlag) && droppedFlag != 0;
            if (rowDropped)
            {
                dropped++;
            }

            if (TryGetInt(map, cells, "AllowsTearing", out int tear) && tear != 0)
            {
                tearing++;
            }

            if (string.IsNullOrEmpty(presentMode))
            {
                presentMode = GetCell(map, cells, "PresentMode");
            }

            if (TryGetDouble(map, cells, "MsBetweenDisplayChange", "msBetweenDisplayChange", out double displayMs)
                && displayMs > 0
                && !rowDropped)
            {
                display.Add(displayMs);
            }
        }

        if (dataRows == 0)
        {
            return new PresentMonParseResult
            {
                HeaderOnly = true,
                Reason = "PresentMon CSV has a header but no frame rows (process not found or capture produced no presents)."
            };
        }

        bool vrr = DetectVariableRefresh(display, tearing, dataRows, presentMode);
        return new PresentMonParseResult
        {
            DataRowCount = dataRows,
            DisplayedFrameCount = display.Count,
            DroppedCount = dropped,
            AllowsTearingCount = tearing,
            VariableRefreshLikely = vrr,
            PresentModeSample = presentMode,
            DisplayIntervalsMs = display,
            PresentIntervalsMs = presents
        };
    }

    private static bool DetectVariableRefresh(
        IReadOnlyList<double> displayMs,
        int tearing,
        int dataRows,
        string presentMode)
    {
        bool tearingMajority = dataRows > 0 && tearing * 2 >= dataRows;
        bool independentFlip = presentMode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)
            && tearing > 0;
        if (displayMs.Count < 8)
        {
            return tearingMajority || independentFlip;
        }

        var summary = PerformanceMetricsCalculator.ComputeSummary(displayMs);
        bool highSpread = summary.Mean > 0 && summary.StdDev / summary.Mean >= 0.20;
        return tearingMajority || independentFlip || highSpread;
    }

    private static bool LooksLikeProcessNotFound(string text)
    {
        return text.Contains("process not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not find process", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to find process", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no matching process", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindHeaderIndex(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Contains("Application", StringComparison.OrdinalIgnoreCase)
                && (line.Contains("MsBetween", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("ProcessID", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool HasAny(Dictionary<string, int> map, params string[] names)
    {
        foreach (string name in names)
        {
            if (map.ContainsKey(name))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetCell(Dictionary<string, int> map, string[] cells, string name)
    {
        if (!map.TryGetValue(name, out int index) || index < 0 || index >= cells.Length)
        {
            return string.Empty;
        }

        return cells[index].Trim().Trim('"');
    }

    private static bool TryGetDouble(Dictionary<string, int> map, string[] cells, string nameA, string nameB, out double value)
    {
        value = 0;
        if (TryGetDouble(map, cells, nameA, out value))
        {
            return true;
        }

        return !string.IsNullOrEmpty(nameB) && TryGetDouble(map, cells, nameB, out value);
    }

    private static bool TryGetDouble(Dictionary<string, int> map, string[] cells, string name, out double value)
    {
        value = 0;
        string raw = GetCell(map, cells, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt(Dictionary<string, int> map, string[] cells, string name, out int value)
    {
        value = 0;
        string raw = GetCell(map, cells, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    internal static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }
}
