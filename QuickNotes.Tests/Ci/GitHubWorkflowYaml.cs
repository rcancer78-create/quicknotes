using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QuickNotes.Tests.Ci;

/// <summary>
/// Indent-based subset parser for GitHub Actions workflow YAML.
/// Comments are dropped, so presence checks cannot pass from commented-out keys or steps.
/// </summary>
internal static class GitHubWorkflowYaml
{
    public static WorkflowDocument Parse(string text)
    {
        List<Line> lines = Preprocess(text);
        int i = 0;
        YamlMapping root = ParseMapping(lines, ref i, parentIndent: -1);
        return WorkflowDocument.FromRoot(root);
    }

    private readonly struct Line
    {
        public Line(int indent, string content)
        {
            Indent = indent;
            Content = content;
        }

        public int Indent { get; }
        public string Content { get; }
    }

    private static List<Line> Preprocess(string text)
    {
        var result = new List<Line>();
        if (string.IsNullOrEmpty(text))
            return result;

        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0)
                continue;

            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
                indent++;

            if (indent < raw.Length && raw[indent] == '\t')
                throw new InvalidOperationException("Tab indentation is not supported in workflow YAML.");

            string content = StripInlineComment(raw.Substring(indent));
            if (content.Length == 0 || content[0] == '#')
                continue;

            result.Add(new Line(indent, content));
        }

        return result;
    }

    private static string StripInlineComment(string content)
    {
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (c == '"' && !inSingle)
                inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble)
            {
                if (i == 0 || char.IsWhiteSpace(content[i - 1]))
                    return content.Substring(0, i).TrimEnd();
            }
        }

        return content.TrimEnd();
    }

    private static YamlMapping ParseMapping(List<Line> lines, ref int i, int parentIndent)
    {
        var map = new YamlMapping();
        if (i >= lines.Count)
            return map;

        int mapIndent = lines[i].Indent;
        if (mapIndent <= parentIndent)
            return map;

        while (i < lines.Count && lines[i].Indent == mapIndent && !lines[i].Content.StartsWith("- ", StringComparison.Ordinal))
        {
            SplitKeyValue(lines[i].Content, out string key, out string inline);
            i++;
            map.Set(key, ParseValue(lines, ref i, mapIndent, inline));
        }

        return map;
    }

    private static YamlNode ParseValue(List<Line> lines, ref int i, int keyIndent, string inline)
    {
        if (inline == "|" || inline == ">")
            return new YamlScalar(ParseMultiline(lines, ref i, keyIndent));

        if (inline.Length > 0)
            return new YamlScalar(Unquote(inline));

        if (i >= lines.Count || lines[i].Indent <= keyIndent)
            return new YamlScalar(string.Empty);

        if (lines[i].Content.StartsWith("- ", StringComparison.Ordinal))
            return ParseSequence(lines, ref i, keyIndent);

        return ParseMapping(lines, ref i, keyIndent);
    }

    private static string ParseMultiline(List<Line> lines, ref int i, int keyIndent)
    {
        var sb = new StringBuilder();
        while (i < lines.Count && lines[i].Indent > keyIndent)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(' ', lines[i].Indent - keyIndent - 2);
            sb.Append(lines[i].Content);
            i++;
        }

        return sb.ToString();
    }

    private static YamlSequence ParseSequence(List<Line> lines, ref int i, int parentIndent)
    {
        var seq = new YamlSequence();
        int seqIndent = lines[i].Indent;
        if (seqIndent <= parentIndent)
            return seq;

        while (i < lines.Count && lines[i].Indent == seqIndent && lines[i].Content.StartsWith("- ", StringComparison.Ordinal))
        {
            string rest = lines[i].Content.Substring(2).TrimStart();
            i++;
            if (rest.Length == 0)
            {
                seq.Items.Add(ParseValue(lines, ref i, seqIndent, string.Empty));
                continue;
            }

            if (!rest.Contains(':'))
            {
                seq.Items.Add(new YamlScalar(Unquote(rest)));
                continue;
            }

            SplitKeyValue(rest, out string firstKey, out string firstInline);
            var item = new YamlMapping();
            item.Set(firstKey, ParseValue(lines, ref i, seqIndent, firstInline));

            int itemKeyIndent = seqIndent + 2;
            while (i < lines.Count && lines[i].Indent >= itemKeyIndent && !lines[i].Content.StartsWith("- ", StringComparison.Ordinal))
            {
                if (lines[i].Indent != itemKeyIndent)
                    break;

                SplitKeyValue(lines[i].Content, out string key, out string inline);
                i++;
                item.Set(key, ParseValue(lines, ref i, itemKeyIndent, inline));
            }

            seq.Items.Add(item);
        }

        return seq;
    }

    private static void SplitKeyValue(string content, out string key, out string inline)
    {
        int colon = FindUnquotedColon(content);
        if (colon < 0)
            throw new InvalidOperationException("Expected 'key:' in workflow YAML: " + content);

        key = Unquote(content.Substring(0, colon).Trim());
        inline = content.Substring(colon + 1).Trim();
    }

    private static int FindUnquotedColon(string content)
    {
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (c == '"' && !inSingle)
                inDouble = !inDouble;
            else if (c == ':' && !inSingle && !inDouble)
                return i;
        }

        return -1;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    internal abstract class YamlNode
    {
        public virtual string AsScalar() => string.Empty;
        public virtual YamlMapping AsMapping() => new();
        public virtual YamlSequence AsSequence() => new();
        public bool IsMapping => this is YamlMapping;
        public bool IsSequence => this is YamlSequence;
    }

    internal sealed class YamlScalar : YamlNode
    {
        public YamlScalar(string value) => Value = value ?? string.Empty;
        public string Value { get; }
        public override string AsScalar() => Value;
    }

    internal sealed class YamlMapping : YamlNode
    {
        private readonly List<KeyValuePair<string, YamlNode>> _entries = new();

        public IReadOnlyList<KeyValuePair<string, YamlNode>> Entries => _entries;

        public void Set(string key, YamlNode value)
        {
            _entries.Add(new KeyValuePair<string, YamlNode>(key, value));
        }

        public bool TryGet(string key, out YamlNode node)
        {
            foreach (KeyValuePair<string, YamlNode> entry in _entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    node = entry.Value;
                    return true;
                }
            }

            node = new YamlScalar(string.Empty);
            return false;
        }

        public YamlNode Get(string key) => TryGet(key, out YamlNode node) ? node : new YamlScalar(string.Empty);

        public override YamlMapping AsMapping() => this;
    }

    internal sealed class YamlSequence : YamlNode
    {
        public List<YamlNode> Items { get; } = new();
        public override YamlSequence AsSequence() => this;
    }

    internal sealed class WorkflowDocument
    {
        public WorkflowDocument(
            IReadOnlyList<string> triggerKeys,
            bool hasPermissionsBlock,
            string? permissionsScalar,
            IReadOnlyDictionary<string, string> permissions,
            IReadOnlyDictionary<string, string> concurrency,
            IReadOnlyList<WorkflowJob> jobs)
        {
            TriggerKeys = triggerKeys;
            HasPermissionsBlock = hasPermissionsBlock;
            PermissionsScalar = permissionsScalar;
            Permissions = permissions;
            Concurrency = concurrency;
            Jobs = jobs;
        }

        public IReadOnlyList<string> TriggerKeys { get; }
        public bool HasPermissionsBlock { get; }
        public string? PermissionsScalar { get; }
        public IReadOnlyDictionary<string, string> Permissions { get; }
        public IReadOnlyDictionary<string, string> Concurrency { get; }
        public IReadOnlyList<WorkflowJob> Jobs { get; }

        public static WorkflowDocument FromRoot(YamlMapping root)
        {
            var triggers = new List<string>();
            if (root.TryGet("on", out YamlNode onNode))
            {
                if (onNode is YamlScalar scalar && !string.IsNullOrWhiteSpace(scalar.Value))
                    triggers.Add(scalar.Value);
                else
                {
                    foreach (KeyValuePair<string, YamlNode> entry in onNode.AsMapping().Entries)
                        triggers.Add(entry.Key);
                }
            }

            bool hasPermissions = root.TryGet("permissions", out YamlNode permNode);
            string? permScalar = null;
            var permMap = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hasPermissions)
            {
                if (permNode is YamlScalar ps)
                    permScalar = ps.Value;
                else
                {
                    foreach (KeyValuePair<string, YamlNode> entry in permNode.AsMapping().Entries)
                        permMap[entry.Key] = entry.Value.AsScalar();
                }
            }

            var concurrency = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGet("concurrency", out YamlNode concNode))
            {
                if (concNode is YamlScalar cs)
                    concurrency[""] = cs.Value;
                else
                {
                    foreach (KeyValuePair<string, YamlNode> entry in concNode.AsMapping().Entries)
                        concurrency[entry.Key] = entry.Value.AsScalar();
                }
            }

            var jobs = new List<WorkflowJob>();
            if (root.TryGet("jobs", out YamlNode jobsNode))
            {
                foreach (KeyValuePair<string, YamlNode> jobEntry in jobsNode.AsMapping().Entries)
                    jobs.Add(WorkflowJob.From(jobEntry.Key, jobEntry.Value.AsMapping()));
            }

            return new WorkflowDocument(triggers, hasPermissions, permScalar, permMap, concurrency, jobs);
        }
    }

    internal sealed class WorkflowJob
    {
        public WorkflowJob(
            string id,
            string? timeoutMinutes,
            string? environment,
            IReadOnlyList<WorkflowStep> steps)
        {
            Id = id;
            TimeoutMinutes = timeoutMinutes;
            Environment = environment;
            Steps = steps;
        }

        public string Id { get; }
        public string? TimeoutMinutes { get; }
        public string? Environment { get; }
        public string? Needs { get; private set; }
        public string? Condition { get; private set; }
        public IReadOnlyList<WorkflowStep> Steps { get; }

        public static WorkflowJob From(string id, YamlMapping map)
        {
            string? timeout = map.TryGet("timeout-minutes", out YamlNode t) ? t.AsScalar() : null;
            string? environment = null;
            if (map.TryGet("environment", out YamlNode envNode))
            {
                if (envNode is YamlScalar es)
                    environment = es.Value;
                else if (envNode.AsMapping().TryGet("name", out YamlNode nameNode))
                    environment = nameNode.AsScalar();
            }

            var steps = new List<WorkflowStep>();
            if (map.TryGet("steps", out YamlNode stepsNode))
            {
                foreach (YamlNode item in stepsNode.AsSequence().Items)
                    steps.Add(WorkflowStep.From(item.AsMapping()));
            }

            return new WorkflowJob(id, timeout, environment, steps)
            {
                Needs = map.TryGet("needs", out YamlNode needs) ? needs.AsScalar() : null,
                Condition = map.TryGet("if", out YamlNode condition) ? condition.AsScalar() : null
            };
        }
    }

    internal sealed class WorkflowStep
    {
        public WorkflowStep(
            string name,
            string? ifCondition,
            string? uses,
            string run,
            string? continueOnError,
            IReadOnlyDictionary<string, string> with,
            IReadOnlyDictionary<string, string> env)
        {
            Name = name;
            IfCondition = ifCondition;
            Uses = uses;
            Run = run;
            ContinueOnError = continueOnError;
            With = with;
            Env = env;
        }

        public string Name { get; }
        public string? IfCondition { get; }
        public string? Uses { get; }
        public string Run { get; }
        public string? ContinueOnError { get; }
        public IReadOnlyDictionary<string, string> With { get; }
        public IReadOnlyDictionary<string, string> Env { get; }

        public bool IsUploadArtifact =>
            Uses != null && Uses.StartsWith("actions/upload-artifact", StringComparison.Ordinal);

        public static WorkflowStep From(YamlMapping map)
        {
            var with = new Dictionary<string, string>(StringComparer.Ordinal);
            if (map.TryGet("with", out YamlNode withNode))
            {
                foreach (KeyValuePair<string, YamlNode> entry in withNode.AsMapping().Entries)
                    with[entry.Key] = entry.Value.AsScalar();
            }

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (map.TryGet("env", out YamlNode envNode))
            {
                foreach (KeyValuePair<string, YamlNode> entry in envNode.AsMapping().Entries)
                    env[entry.Key] = entry.Value.AsScalar();
            }

            return new WorkflowStep(
                map.Get("name").AsScalar(),
                map.TryGet("if", out YamlNode ifNode) ? ifNode.AsScalar() : null,
                map.TryGet("uses", out YamlNode usesNode) ? usesNode.AsScalar() : null,
                map.Get("run").AsScalar(),
                map.TryGet("continue-on-error", out YamlNode coe) ? coe.AsScalar() : null,
                with,
                env);
        }
    }

    internal static IReadOnlyList<string> SplitMultilinePaths(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<string>();

        return path
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
    }

    internal static bool IsAlwaysIf(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return false;

        string trimmed = condition.Trim();
        return trimmed.Equals("always()", StringComparison.Ordinal)
               || trimmed.Equals("${{ always() }}", StringComparison.Ordinal);
    }

    internal static int? ParsePositiveInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n;
        return null;
    }
}
