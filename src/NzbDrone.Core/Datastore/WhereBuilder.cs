using System;
using System.Linq.Expressions;
using Dapper;

namespace NzbDrone.Core.Datastore
{
    public abstract class WhereBuilder : ExpressionVisitor
    {
        public DynamicParameters Parameters { get; protected set; }

        protected override Expression VisitUnary(UnaryExpression expression)
        {
            if (expression.NodeType == ExpressionType.Not &&
                (expression.Type == typeof(bool) || expression.Type == typeof(bool?)))
            {
                Visit(Expression.Equal(expression.Operand, Expression.Constant(false, expression.Type)));
                return expression;
            }

            // Expression-tree casts wrap enum and nullable column comparisons.
            if (expression.NodeType == ExpressionType.Convert || expression.NodeType == ExpressionType.ConvertChecked)
            {
                return base.VisitUnary(expression);
            }

            throw new NotSupportedException($"Unary {expression.NodeType} on {expression.Type.Name} is not supported in a SQL where clause.");
        }
    }
}
