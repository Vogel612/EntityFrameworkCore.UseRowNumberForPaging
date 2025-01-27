#if NET9_0_OR_GREATER
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EntityFrameworkCore.UseRowNumberForPaging;

internal class Offset2RowNumberConvertVisitor(
    Expression root,
    ISqlExpressionFactory sqlExpressionFactory,
    SqlAliasManager sqlAliasManager
) : ExpressionVisitor
{
    private static readonly MethodInfo GenerateOuterColumnAccessor;

    static Offset2RowNumberConvertVisitor()
    {
        var method = typeof(SelectExpression).GetMethod("GenerateOuterColumn", BindingFlags.NonPublic | BindingFlags.Instance);
        if (!typeof(ColumnExpression).IsAssignableFrom(method?.ReturnType))
        {
            throw new InvalidOperationException("SelectExpression.GenerateOuterColumn() was not found");
        }
        GenerateOuterColumnAccessor = method;
    }


    private readonly Expression root = root;
    private readonly ISqlExpressionFactory sqlExpressionFactory = sqlExpressionFactory;
    private readonly SqlAliasManager sqlAliasManager = sqlAliasManager;

    protected override Expression VisitExtension(Expression node)
    {
        if (node is ShapedQueryExpression shapedQueryExpression)
        {
            return shapedQueryExpression.Update(Visit(shapedQueryExpression.QueryExpression), Visit(shapedQueryExpression.ShaperExpression));
        }
        if (node is SelectExpression se)
        {
            node = VisitSelect(se);
        }
        return base.VisitExtension(node);
    }

    private Expression VisitSelect(SelectExpression selectExpression)
    {
        var oldOffset = selectExpression.Offset;
        if (oldOffset == null)
            return selectExpression;
        var oldLimit = selectExpression.Limit;
        var oldOrderings = selectExpression.Orderings;
        var newOrderings = oldOrderings.Count > 0 && (oldLimit != null || selectExpression == root)
            ? oldOrderings.ToList()
            : [];
        // Change SelectExpression
        selectExpression = selectExpression.Update(projections: selectExpression.Projection.ToList(),
                                                    tables: selectExpression.Tables.ToList(),
                                                    predicate: selectExpression.Predicate,
                                                    groupBy: selectExpression.GroupBy.ToList(),
                                                    having: selectExpression.Having,
                                                    orderings: newOrderings,
                                                    limit: null,
                                                    offset: null);
        var rowOrderings = oldOrderings.Count != 0 ? oldOrderings
            : [new OrderingExpression(new SqlFragmentExpression("(SELECT 1)"), true)];

        // restore sql alias manager in updated expression
        typeof(SelectExpression)
            .GetField("_sqlAliasManager", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(selectExpression, sqlAliasManager);

        selectExpression.PushdownIntoSubquery();

        var subQuery = (SelectExpression)selectExpression.Tables[0];
        var projection = new RowNumberExpression([], rowOrderings, oldOffset.TypeMapping);
        var left = GenerateOuterColumnAccessor.Invoke(
            subQuery,
            [
                subQuery.Alias,
                projection,
                sqlAliasManager.GenerateTableAlias("row"),
            ]) as ColumnExpression;
        selectExpression.ApplyPredicate(sqlExpressionFactory.GreaterThan(left!, oldOffset));

        if (oldLimit != null)
        {
            if (oldOrderings.Count == 0)
            {
                selectExpression.ApplyPredicate(sqlExpressionFactory.LessThanOrEqual(left, sqlExpressionFactory.Add(oldOffset, oldLimit)));
            }
            else
            {
                selectExpression.ApplyLimit(oldLimit);
            }
        }
        return selectExpression;
    }
}
#endif