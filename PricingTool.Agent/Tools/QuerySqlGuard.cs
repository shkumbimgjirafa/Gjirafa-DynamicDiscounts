using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PricingTool.Agent.Tools;

/// <summary>
/// The parse-level guardrail of <c>query_data</c>: exactly one SELECT statement (no INTO), reading only
/// <c>agent.*</c> views or its own CTEs, with no rowset functions that reach outside the database. The
/// database user it runs as (SELECT on schema agent only) is the second, independent wall.
/// </summary>
public static class QuerySqlGuard
{
    public const string AllowedSchema = "agent";

    public sealed record Verdict(bool Ok, string? Error)
    {
        public static readonly Verdict Accepted = new(true, null);
        public static Verdict Refused(string why) => new(false, why);
    }

    public static Verdict Check(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Verdict.Refused("Empty query.");

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql)) fragment = parser.Parse(reader, out errors);
        if (errors.Count > 0)
        {
            var e = errors[0];
            return Verdict.Refused($"The SQL does not parse (line {e.Line}, column {e.Column}): {e.Message}");
        }

        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1)
            return Verdict.Refused($"Exactly one statement is allowed; this has {statements.Count}. Send one SELECT per call.");
        if (statements[0] is not SelectStatement select)
            return Verdict.Refused($"Only SELECT is allowed; this is {Describe(statements[0])}.");
        if (select.Into is not null)
            return Verdict.Refused("SELECT ... INTO is not allowed: the query may only read.");

        var visitor = new ReferenceVisitor(
            select.WithCtesAndXmlNamespaces?.CommonTableExpressions.Select(c => c.ExpressionName.Value) ?? Enumerable.Empty<string>());
        select.Accept(visitor);
        return visitor.Problem is null ? Verdict.Accepted : Verdict.Refused(visitor.Problem);
    }

    private static string Describe(TSqlStatement s)
    {
        var name = s.GetType().Name;
        if (!name.EndsWith("Statement", StringComparison.Ordinal)) return name;
        var kind = name[..^"Statement".Length].ToUpperInvariant();
        return $"{("AEIOU".Contains(kind[0]) ? "an" : "a")} {kind} statement";
    }

    private sealed class ReferenceVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _ctes;
        public string? Problem { get; private set; }

        public ReferenceVisitor(IEnumerable<string> cteNames) =>
            _ctes = new HashSet<string>(cteNames, StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node) => CheckObject(node.SchemaObject);
        public override void Visit(SchemaObjectFunctionTableReference node) => CheckObject(node.SchemaObject);

        public override void Visit(OpenRowsetTableReference node) => Refuse("OPENROWSET");
        public override void Visit(OpenQueryTableReference node) => Refuse("OPENQUERY");
        public override void Visit(AdHocTableReference node) => Refuse("OPENDATASOURCE");
        public override void Visit(BulkOpenRowset node) => Refuse("OPENROWSET(BULK ...)");

        private void CheckObject(SchemaObjectName name)
        {
            if (Problem is not null) return;
            var full = string.Join('.', name.Identifiers.Select(i => i.Value));
            if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
                Problem = $"'{full}': other databases and servers are out of reach. Use the agent.* views.";
            else if (name.SchemaIdentifier is null)
            {
                if (!_ctes.Contains(name.BaseIdentifier.Value))
                    Problem = $"'{full}' needs its schema: only agent.* views (or your own CTEs) can be read.";
            }
            else if (!string.Equals(name.SchemaIdentifier.Value, AllowedSchema, StringComparison.OrdinalIgnoreCase))
                Problem = $"'{full}' is outside the agent schema: only agent.* views can be read.";
        }

        private void Refuse(string what) => Problem ??= $"{what} is not allowed: only agent.* views can be read.";
    }
}
