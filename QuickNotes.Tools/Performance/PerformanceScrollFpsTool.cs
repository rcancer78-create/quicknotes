using System;
using System.Globalization;
using System.IO;

namespace QuickNotes.Tools.Performance;

public sealed class ScrollFpsParseCliOptions
{
    public string? CsvPath { get; init; }
    public string? ReportPath { get; init; }
    public string ToolName { get; init; } = "PresentMon";
    public string ToolVersion { get; init; } = "unknown";
    public string? OsDescription { get; init; }
    public string? DisplayDescription { get; init; }
    public int? DisplayRefreshHz { get; init; }
    public bool OperatorConfirmsManualScroll { get; init; }
    public string? ParseError { get; init; }

    public bool HasParseError => !string.IsNullOrEmpty(ParseError);
}

public static class PerformanceScrollFpsTool
{
    public static readonly string UsageText =
        "Usage: QuickNotes.Tools.exe scroll-fps-parse --csv <presentmon.csv> --report <out.md> [options]"
        + Environment.NewLine
        + "Parses an existing PresentMon CSV. Does not launch QuickNotes, does not download tools, and does not use CompositionTarget.Rendering."
        + Environment.NewLine
        + "Options:" + Environment.NewLine
        + "  --tool-name <text>           Default PresentMon" + Environment.NewLine
        + "  --tool-version <text>" + Environment.NewLine
        + "  --os <text>" + Environment.NewLine
        + "  --display <text>" + Environment.NewLine
        + "  --display-hz <int>" + Environment.NewLine
        + "  --operator-confirms-scroll   Required for PASS; idle presents are not scroll proof";

    public static ScrollFpsParseCliOptions Parse(string[] args)
    {
        string? csv = null;
        string? report = null;
        string tool = "PresentMon";
        string version = "unknown";
        string? os = null;
        string? display = null;
        int? hz = null;
        bool confirm = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--csv", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                csv = args[++i];
            }
            else if (string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                report = args[++i];
            }
            else if (string.Equals(arg, "--tool-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                tool = args[++i];
            }
            else if (string.Equals(arg, "--tool-version", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                version = args[++i];
            }
            else if (string.Equals(arg, "--os", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                os = args[++i];
            }
            else if (string.Equals(arg, "--display", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                display = args[++i];
            }
            else if (string.Equals(arg, "--display-hz", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    return new ScrollFpsParseCliOptions { ParseError = "Invalid --display-hz." };
                }

                hz = parsed;
            }
            else if (string.Equals(arg, "--operator-confirms-scroll", StringComparison.OrdinalIgnoreCase))
            {
                confirm = true;
            }
            else if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
            {
                return new ScrollFpsParseCliOptions { ParseError = UsageText };
            }
            else
            {
                return new ScrollFpsParseCliOptions { ParseError = "Unknown argument: " + arg + Environment.NewLine + UsageText };
            }
        }

        if (string.IsNullOrWhiteSpace(csv) || string.IsNullOrWhiteSpace(report))
        {
            return new ScrollFpsParseCliOptions { ParseError = UsageText };
        }

        return new ScrollFpsParseCliOptions
        {
            CsvPath = csv,
            ReportPath = report,
            ToolName = tool,
            ToolVersion = version,
            OsDescription = os,
            DisplayDescription = display,
            DisplayRefreshHz = hz,
            OperatorConfirmsManualScroll = confirm
        };
    }

    public static int Run(string[] args)
    {
        ScrollFpsParseCliOptions options = Parse(args);
        if (options.HasParseError)
        {
            Console.Error.WriteLine(options.ParseError);
            return 1;
        }

        try
        {
            PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(options.CsvPath!);
            DisplayEnvironmentSnapshot env = DisplayEnvironmentProbe.Capture();
            var evaluation = ScrollFpsCaptureEvaluator.Evaluate(parsed, new ScrollFpsEvaluateOptions
            {
                OperatorConfirmsManualScroll = options.OperatorConfirmsManualScroll,
                UsedCompositionTarget = false,
                ToolName = options.ToolName,
                ToolVersion = options.ToolVersion,
                RawCsvPath = Path.GetFullPath(options.CsvPath!),
                OsDescription = options.OsDescription ?? env.OsDescription,
                DisplayDescription = options.DisplayDescription ?? env.DisplayDescription,
                DisplayRefreshHz = options.DisplayRefreshHz ?? (env.RefreshHz > 0 ? env.RefreshHz : null)
            });

            ScrollFpsPresentMonReportGenerator.Save(evaluation, options.ReportPath!);
            Console.WriteLine("QN_SCROLL_FPS_VERDICT:" + evaluation.Verdict.ToString().ToUpperInvariant());
            Console.WriteLine("QN_SCROLL_FPS_REPORT:" + Path.GetFullPath(options.ReportPath!));
            return evaluation.Verdict switch
            {
                ScrollFpsVerdict.Pass => 0,
                ScrollFpsVerdict.Fail => 3,
                _ => 2
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("scroll-fps-parse failed: " + ex.Message);
            return 1;
        }
    }
}
