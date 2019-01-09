﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     Rewrites collection navigations into subqueries, e.g.:
    ///     customers.Select(c => c.Order.OrderDetails.Where(...)) => customers.Select(c => orderDetails.Where(od => od.OrderId == c.Order.Id).Where(...))
    /// </summary>
    public class CollectionNavigationRewritingExpressionVisitor : LinqQueryExpressionVisitorBase
    {
        private ParameterExpression _sourceParameter;
        private List<(List<INavigation> from, List<string> to)> _transparentIdentifierAccessorMapping;
        private List<(List<string> path, IEntityType entityType)> _entityTypeAccessorMapping;
        private List<NavigationPathNode> _foundNavigationPaths;

        private MethodInfo _listExistsMethodInfo = typeof(List<>).GetMethods().Where(m => m.Name == nameof(List<int>.Exists)).Single();

        public CollectionNavigationRewritingExpressionVisitor(
            ParameterExpression sourceParameter,
            List<(List<INavigation> from, List<string> to)> transparentIdentifierAccessorMapping,
            List<(List<string> path, IEntityType entityType)> entityTypeAccessorMapping,
            List<NavigationPathNode> foundNavigationPaths)
        {
            _sourceParameter = sourceParameter;
            _transparentIdentifierAccessorMapping = transparentIdentifierAccessorMapping;
            _entityTypeAccessorMapping = entityTypeAccessorMapping;
            _foundNavigationPaths = foundNavigationPaths;
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            // don't touch Include
            // this is temporary, new nav expansion happens to early at the moment
            if (methodCallExpression.IsIncludeMethod())
            {
                return methodCallExpression;
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableSelectManyMethodInfo))
            {
                return methodCallExpression;
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableSelectManyWithResultOperatorMethodInfo))
            {
                var newResultSelector = Visit(methodCallExpression.Arguments[2]);

                return newResultSelector != methodCallExpression.Arguments[2]
                    ? methodCallExpression.Update(methodCallExpression.Object, new[] { methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], newResultSelector })
                    : methodCallExpression;
            }

            if (methodCallExpression.Method.Name == nameof(List<int>.Exists)
                && methodCallExpression.Method.DeclaringType.IsGenericType
                && methodCallExpression.Method.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var anyLambda = Expression.Lambda(
                    ((LambdaExpression)methodCallExpression.Arguments[0]).Body,
                    ((LambdaExpression)methodCallExpression.Arguments[0]).Parameters[0]);

                var result = Expression.Call(
                    EnumerableAnyPredicate.MakeGenericMethod(methodCallExpression.Method.DeclaringType.GetGenericArguments()[0]),
                    methodCallExpression.Object,
                    anyLambda);

                return Visit(result);
            }

            // List<T>.Exists(predicate) -> Enumerable.Any<T>(source, predicate)

            // collection.Contains
            // list.Exists

            return base.VisitMethodCall(methodCallExpression);
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            var binding = NavigationPropertyBinder.BindNavigationProperties(memberExpression, _sourceParameter, _transparentIdentifierAccessorMapping, _entityTypeAccessorMapping);
            if (binding.navigations.Any()
                && binding.navigations.Last() is INavigation navigation
                && navigation.IsCollection())
            {
                var collectionNavigationElementType = navigation.ForeignKey.DeclaringEntityType.ClrType;
                var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

                var outerExpression = memberExpression.Expression;

                var outerKeyAccess = CreateKeyAccessExpression(
                    outerExpression,
                    navigation.ForeignKey.PrincipalKey.Properties);

                var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.GenerateParameterName());
                var innerKeyAccess = CreateKeyAccessExpression(
                    innerParameter,
                    navigation.ForeignKey.Properties);

                var predicate = Expression.Lambda(
                    CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                        outerKeyAccess,
                        innerKeyAccess,
                        outerExpression,
                        binding.root,
                        binding.navigations),
                    innerParameter);

                return Expression.Call(
                    QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
                    entityQueryable,
                    predicate);
            }

            var newExpression = Visit(memberExpression.Expression);
            if (newExpression != memberExpression.Expression)
            {
                if (memberExpression.Member.Name == nameof(List<int>.Count))
                {
                    // TODO: what about custom collection?????????? - how do we get type argument there
                    var countMethod = QueryableCountMethodInfo.MakeGenericMethod(newExpression.Type.GetGenericArguments()[0]);
                    var result = Expression.Call(instance: null, countMethod, newExpression);

                    return result;
                }
                else
                {
                    return memberExpression.Update(newExpression);
                }
            }

            return memberExpression;
        }

        private static Expression CreateKeyAccessExpression(
            Expression target, IReadOnlyList<IProperty> properties, bool addNullCheck = false)
            => properties.Count == 1
                ? CreatePropertyExpression(target, properties[0], addNullCheck)
                : Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        properties
                            .Select(p => Expression.Convert(CreatePropertyExpression(target, p, addNullCheck), typeof(object)))
                            .Cast<Expression>()
                            .ToArray()));

        private static Expression CreatePropertyExpression(Expression target, IProperty property, bool addNullCheck)
        {
            var propertyExpression = target.CreateEFPropertyExpression(property, makeNullable: false);

            var propertyDeclaringType = property.DeclaringType.ClrType;
            if (propertyDeclaringType != target.Type
                && target.Type.GetTypeInfo().IsAssignableFrom(propertyDeclaringType.GetTypeInfo()))
            {
                if (!propertyExpression.Type.IsNullableType())
                {
                    propertyExpression = Expression.Convert(propertyExpression, propertyExpression.Type.MakeNullable());
                }

                return Expression.Condition(
                    Expression.TypeIs(target, propertyDeclaringType),
                    propertyExpression,
                    Expression.Constant(null, propertyExpression.Type));
            }

            return addNullCheck
                ? new NullConditionalExpression(target, propertyExpression)
                : propertyExpression;
        }

        private static Expression CreateKeyComparisonExpressionForCollectionNavigationSubquery(
            Expression outerKeyExpression,
            Expression innerKeyExpression,
            Expression colectionRootExpression,
            Expression navigationRootExpression,
            IEnumerable<INavigation> navigations)
        {
            if (outerKeyExpression.Type != innerKeyExpression.Type)
            {
                if (outerKeyExpression.Type.IsNullableType())
                {
                    Debug.Assert(outerKeyExpression.Type.UnwrapNullableType() == innerKeyExpression.Type);

                    innerKeyExpression = Expression.Convert(innerKeyExpression, outerKeyExpression.Type);
                }
                else
                {
                    Debug.Assert(innerKeyExpression.Type.IsNullableType());
                    Debug.Assert(innerKeyExpression.Type.UnwrapNullableType() == outerKeyExpression.Type);

                    outerKeyExpression = Expression.Convert(outerKeyExpression, innerKeyExpression.Type);
                }
            }

            var outerNullProtection
                = Expression.NotEqual(
                    colectionRootExpression,
                    Expression.Constant(null, colectionRootExpression.Type));

            return new NullSafeEqualExpression(
                outerNullProtection,
                Expression.Equal(outerKeyExpression, innerKeyExpression));
        }
    }

    ///// <summary>
    /////     Rewrites collection navigations into subqueries, e.g.:
    /////     customers.Select(c => c.Order.OrderDetails.Where(...)) => customers.Select(c => orderDetails.Where(od => od.OrderId == c.Order.Id).Where(...))
    ///// </summary>
    //public class CollectionNavigationRewritingExpressionVisitor : LinqQueryExpressionVisitorBase
    //{
    //    private readonly IModel _model;

    //    public CollectionNavigationRewritingExpressionVisitor(IModel model)
    //    {
    //        _model = model;
    //    }

    //    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    //    {
    //        // don't touch Include
    //        // this is temporary, new nav expansion happens to early at the moment
    //        if (methodCallExpression.IsIncludeMethod())
    //        {
    //            return methodCallExpression;
    //        }

    //        if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableSelectManyMethodInfo))
    //        {
    //            return methodCallExpression;
    //        }

    //        if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableSelectManyWithResultOperatorMethodInfo))
    //        {
    //            var newResultSelector = Visit(methodCallExpression.Arguments[2]);

    //            return newResultSelector != methodCallExpression.Arguments[2]
    //                ? methodCallExpression.Update(methodCallExpression.Object, new[] { methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], newResultSelector })
    //                : methodCallExpression;
    //        }

    //        return base.VisitMethodCall(methodCallExpression);
    //    }

    //    protected override Expression VisitMember(MemberExpression memberExpression)
    //    {
    //        // TODO: fix the entity type accessor mapping here!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
    //        var binding = NavigationPropertyBinder.BindNavigationProperties(memberExpression, rootParameter: null /*_model*/, entityTypeAccessorMapping: null);
    //        if (binding.navigations.Any()
    //            && binding.navigations.Last() is INavigation navigation
    //            && navigation.IsCollection())
    //        {
    //            var collectionNavigationElementType = navigation.ForeignKey.DeclaringEntityType.ClrType;
    //            var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

    //            var outerExpression = memberExpression.Expression;

    //            var outerKeyAccess = CreateKeyAccessExpression(
    //                outerExpression,
    //                navigation.ForeignKey.PrincipalKey.Properties);

    //            var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.Name.ToLower().Substring(0, 1));
    //            var innerKeyAccess = CreateKeyAccessExpression(
    //                innerParameter,
    //                navigation.ForeignKey.Properties);

    //            var predicate = Expression.Lambda(
    //                CreateKeyComparisonExpressionForCollectionNavigationSubquery(
    //                    outerKeyAccess,
    //                    innerKeyAccess,
    //                    outerExpression,
    //                    binding.root,
    //                    binding.navigations),
    //                innerParameter);

    //            return Expression.Call(
    //                QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
    //                entityQueryable,
    //                predicate);
    //        }

    //        return base.VisitMember(memberExpression);
    //    }












    //    protected override Expression VisitConstant(ConstantExpression constantExpression)
    //    {
    //        if (constantExpression.Value != null
    //            && constantExpression.Value.GetType().IsGenericType
    //            && constantExpression.Value.GetType().GetGenericTypeDefinition() == typeof(EntityQueryable<>))
    //        {
    //            var elementType = constantExpression.Value.GetType().GetGenericArguments()[0];
    //            var entityType = _model.FindEntityType(elementType);

    //            var entityTypeAccessorMapping = new List<(List<string> path, IEntityType entityType)>();
    //            entityTypeAccessorMapping.Add((new List<string>(), entityType));

    //            var result = new NavigationExpansionExpression(
    //                operand: constantExpression,
    //                firstSelectorParameter: null,
    //                currentParameter: null,
    //                transparentIdentifierAccessorMapping: new List<(List<string> from, List<string> to)>(),
    //                entityTypeAccessorMapping: entityTypeAccessorMapping,
    //                appliedSelector: null,
    //                pendingSelector: null,
    //                foundNavigations: new List<NavigationPathNode>(),
    //                finalProjectionPath: new List<string>(),
    //                returnType: constantExpression.Type);

    //            return result;
    //        }

    //        return base.VisitConstant(constantExpression);
    //    }

    //    //protected override Expression VisitExtension(Expression extensionExpression)
    //    //{
    //    //    if (extensionExpression is NavigationBindingExpression navigationBindingExpression
    //    //        && navigationBindingExpression.Navigations.Last() is INavigation navigation
    //    //        && navigation.IsCollection())
    //    //    {
    //    //        var collectionNavigationElementType = navigation.ForeignKey.DeclaringEntityType.ClrType;
    //    //        var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

    //    //        // unwrap top level expression - since we are inside nav NavigationPropertyBindingExpression the top level will either be member expression or EF.Property
    //    //        var outerExpression = (navigationBindingExpression.Operand as MemberExpression)?.Expression
    //    //            ?? (navigationBindingExpression.Operand as MethodCallExpression).Arguments[0];

    //    //        var outerNavigationBinding = new NavigationBindingExpression(
    //    //            outerExpression,
    //    //            navigationBindingExpression.Root,
    //    //            navigationBindingExpression.Navigations.Take(navigationBindingExpression.Navigations.Count - 1));

    //    //        var outerKeyAccess = CreateKeyAccessExpression(
    //    //            outerNavigationBinding,
    //    //            navigation.ForeignKey.PrincipalKey.Properties);

    //    //        var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.Name.ToLower().Substring(0, 1));
    //    //        var innerKeyAccess = CreateKeyAccessExpression(
    //    //            innerParameter,
    //    //            navigation.ForeignKey.Properties);

    //    //        var predicate = Expression.Lambda(
    //    //            CreateKeyComparisonExpressionForCollectionNavigationSubquery(
    //    //                outerKeyAccess,
    //    //                innerKeyAccess,
    //    //                outerNavigationBinding,
    //    //                navigationBindingExpression.Root,
    //    //                navigationBindingExpression.Navigations),
    //    //            innerParameter);

    //    //        return Expression.Call(
    //    //            QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
    //    //            entityQueryable,
    //    //            predicate);
    //    //    }

    //    //    return extensionExpression;
    //    //}

    //    private static Expression CreateKeyAccessExpression(
    //        Expression target, IReadOnlyList<IProperty> properties, bool addNullCheck = false)
    //        => properties.Count == 1
    //            ? CreatePropertyExpression(target, properties[0], addNullCheck)
    //            : Expression.New(
    //                AnonymousObject.AnonymousObjectCtor,
    //                Expression.NewArrayInit(
    //                    typeof(object),
    //                    properties
    //                        .Select(p => Expression.Convert(CreatePropertyExpression(target, p, addNullCheck), typeof(object)))
    //                        .Cast<Expression>()
    //                        .ToArray()));

    //    private static Expression CreatePropertyExpression(Expression target, IProperty property, bool addNullCheck)
    //    {
    //        var propertyExpression = target.CreateEFPropertyExpression(property, makeNullable: false);

    //        var propertyDeclaringType = property.DeclaringType.ClrType;
    //        if (propertyDeclaringType != target.Type
    //            && target.Type.GetTypeInfo().IsAssignableFrom(propertyDeclaringType.GetTypeInfo()))
    //        {
    //            if (!propertyExpression.Type.IsNullableType())
    //            {
    //                propertyExpression = Expression.Convert(propertyExpression, propertyExpression.Type.MakeNullable());
    //            }

    //            return Expression.Condition(
    //                Expression.TypeIs(target, propertyDeclaringType),
    //                propertyExpression,
    //                Expression.Constant(null, propertyExpression.Type));
    //        }

    //        return addNullCheck
    //            ? new NullConditionalExpression(target, propertyExpression)
    //            : propertyExpression;
    //    }

    //    private static Expression CreateKeyComparisonExpressionForCollectionNavigationSubquery(
    //        Expression outerKeyExpression,
    //        Expression innerKeyExpression,
    //        Expression colectionRootExpression,
    //        Expression navigationRootExpression,
    //        IEnumerable<INavigation> navigations)
    //    {
    //        if (outerKeyExpression.Type != innerKeyExpression.Type)
    //        {
    //            if (outerKeyExpression.Type.IsNullableType())
    //            {
    //                Debug.Assert(outerKeyExpression.Type.UnwrapNullableType() == innerKeyExpression.Type);

    //                innerKeyExpression = Expression.Convert(innerKeyExpression, outerKeyExpression.Type);
    //            }
    //            else
    //            {
    //                Debug.Assert(innerKeyExpression.Type.IsNullableType());
    //                Debug.Assert(innerKeyExpression.Type.UnwrapNullableType() == outerKeyExpression.Type);

    //                outerKeyExpression = Expression.Convert(outerKeyExpression, innerKeyExpression.Type);
    //            }
    //        }

    //        var outerNullProtection
    //            = Expression.NotEqual(
    //                colectionRootExpression,
    //                Expression.Constant(null, colectionRootExpression.Type));

    //        return new NullSafeEqualExpression(
    //            outerNullProtection,
    //            Expression.Equal(outerKeyExpression, innerKeyExpression));
    //    }
    //}
}