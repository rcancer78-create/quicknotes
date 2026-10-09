using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace QuickNotes.App.Models;

public enum DateFilterType
{
    Today,
    Week
}

public abstract class SearchCondition
{
    public abstract bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy);
    public abstract Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy);
}

public sealed class TagCondition : SearchCondition
{
    public string TagName { get; }
    public int TagId { get; }

    public TagCondition(string tagName, int tagId)
    {
        TagName = tagName;
        TagId = tagId;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        var ids = tagHierarchy.TryGetValue(TagId, out var set) ? set : new HashSet<int> { TagId };
        return note.NoteTags.Any(nt => !nt.IsSuppressed && ids.Contains(nt.TagId));
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        var ids = tagHierarchy.TryGetValue(TagId, out var set) ? set : new HashSet<int> { TagId };
        return n => n.NoteTags.Any(nt => !nt.IsSuppressed && ids.Contains(nt.TagId));
    }
}

public sealed class CreatedCondition : SearchCondition
{
    public DateFilterType Type { get; }

    public CreatedCondition(DateFilterType type)
    {
        Type = type;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        if (Type == DateFilterType.Today)
        {
            var today = now.Date;
            var tomorrow = today.AddDays(1);
            return note.CreatedAt >= today && note.CreatedAt < tomorrow;
        }
        else
        {
            var cutoff = now.Date.AddDays(-7);
            return note.CreatedAt >= cutoff;
        }
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        if (Type == DateFilterType.Today)
        {
            var today = now.Date;
            var tomorrow = today.AddDays(1);
            return n => n.CreatedAt >= today && n.CreatedAt < tomorrow;
        }
        else
        {
            var cutoff = now.Date.AddDays(-7);
            return n => n.CreatedAt >= cutoff;
        }
    }
}

public sealed class UpdatedCondition : SearchCondition
{
    public DateFilterType Type { get; }

    public UpdatedCondition(DateFilterType type)
    {
        Type = type;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        if (Type == DateFilterType.Week)
        {
            var cutoff = now.Date.AddDays(-7);
            return note.UpdatedAt >= cutoff;
        }
        else
        {
            var today = now.Date;
            var tomorrow = today.AddDays(1);
            return note.UpdatedAt >= today && note.UpdatedAt < tomorrow;
        }
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        if (Type == DateFilterType.Week)
        {
            var cutoff = now.Date.AddDays(-7);
            return n => n.UpdatedAt >= cutoff;
        }
        else
        {
            var today = now.Date;
            var tomorrow = today.AddDays(1);
            return n => n.UpdatedAt >= today && n.UpdatedAt < tomorrow;
        }
    }
}

public sealed class SourceCondition : SearchCondition
{
    public string Term { get; }

    public SourceCondition(string term)
    {
        Term = term ?? string.Empty;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        if (string.IsNullOrWhiteSpace(Term))
        {
            return false;
        }

        return ContainsIgnoreCase(note.SourceProcessName, Term)
               || ContainsIgnoreCase(note.SourceWindowTitle, Term)
               || ContainsIgnoreCase(note.SourceUrl, Term);
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        var term = Term;
        return n =>
            (n.SourceProcessName != null && n.SourceProcessName.ToLower().Contains(term.ToLower())) ||
            (n.SourceWindowTitle != null && n.SourceWindowTitle.ToLower().Contains(term.ToLower())) ||
            (n.SourceUrl != null && n.SourceUrl.ToLower().Contains(term.ToLower()));
    }

    private static bool ContainsIgnoreCase(string? value, string term)
        => !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}

public sealed class UrlCondition : SearchCondition
{
    public string Term { get; }

    public UrlCondition(string term)
    {
        Term = term ?? string.Empty;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return !string.IsNullOrWhiteSpace(Term)
               && !string.IsNullOrEmpty(note.SourceUrl)
               && note.SourceUrl.Contains(Term, StringComparison.OrdinalIgnoreCase);
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        var term = Term;
        return n => n.SourceUrl != null && n.SourceUrl.ToLower().Contains(term.ToLower());
    }
}

public sealed class UntaggedCondition : SearchCondition
{
    public bool Expected { get; }

    public UntaggedCondition(bool expected = true)
    {
        Expected = expected;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        var hasTags = note.NoteTags.Any(nt => !nt.IsSuppressed);
        return Expected ? !hasTags : hasTags;
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        if (Expected)
        {
            return n => !n.NoteTags.Any(nt => !nt.IsSuppressed);
        }
        else
        {
            return n => n.NoteTags.Any(nt => !nt.IsSuppressed);
        }
    }
}

public sealed class AndCondition : SearchCondition
{
    public SearchCondition Left { get; }
    public SearchCondition Right { get; }

    public AndCondition(SearchCondition left, SearchCondition right)
    {
        Left = left;
        Right = right;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return Left.Matches(note, now, tagHierarchy) && Right.Matches(note, now, tagHierarchy);
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return Left.ToExpression(now, tagHierarchy).And(Right.ToExpression(now, tagHierarchy));
    }
}

public sealed class OrCondition : SearchCondition
{
    public SearchCondition Left { get; }
    public SearchCondition Right { get; }

    public OrCondition(SearchCondition left, SearchCondition right)
    {
        Left = left;
        Right = right;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return Left.Matches(note, now, tagHierarchy) || Right.Matches(note, now, tagHierarchy);
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return Left.ToExpression(now, tagHierarchy).Or(Right.ToExpression(now, tagHierarchy));
    }
}

public sealed class NotCondition : SearchCondition
{
    public SearchCondition Inner { get; }

    public NotCondition(SearchCondition inner)
    {
        Inner = inner;
    }

    public override bool Matches(Note note, DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return !Inner.Matches(note, now, tagHierarchy);
    }

    public override Expression<Func<Note, bool>> ToExpression(DateTime now, IReadOnlyDictionary<int, HashSet<int>> tagHierarchy)
    {
        return Inner.ToExpression(now, tagHierarchy).Not();
    }
}

internal static class PredicateBuilder
{
    public static Expression<Func<T, bool>> And<T>(
        this Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "n");
        var leftBody = new ReplaceParameterVisitor(left.Parameters[0], parameter).Visit(left.Body);
        var rightBody = new ReplaceParameterVisitor(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(leftBody, rightBody), parameter);
    }

    public static Expression<Func<T, bool>> Or<T>(
        this Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "n");
        var leftBody = new ReplaceParameterVisitor(left.Parameters[0], parameter).Visit(left.Body);
        var rightBody = new ReplaceParameterVisitor(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(leftBody, rightBody), parameter);
    }

    public static Expression<Func<T, bool>> Not<T>(
        this Expression<Func<T, bool>> expr)
    {
        var parameter = Expression.Parameter(typeof(T), "n");
        var body = new ReplaceParameterVisitor(expr.Parameters[0], parameter).Visit(expr.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.Not(body), parameter);
    }

    private sealed class ReplaceParameterVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParam;
        private readonly ParameterExpression _newParam;

        public ReplaceParameterVisitor(ParameterExpression oldParam, ParameterExpression newParam)
        {
            _oldParam = oldParam;
            _newParam = newParam;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return ReferenceEquals(node, _oldParam) ? _newParam : base.VisitParameter(node);
        }
    }
}
