﻿#if LESSTHAN_NET35

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Dynamic.Utils;
using System.Runtime.CompilerServices;

namespace System.Linq.Expressions
{
    public partial class Expression
    {
        /// <summary>
        ///     Creates an instance of <see cref="RuntimeVariablesExpression" />.
        /// </summary>
        /// <param name="variables">
        ///     An array of <see cref="ParameterExpression" /> objects to use to populate the
        ///     <see cref="RuntimeVariablesExpression.Variables" /> collection.
        /// </param>
        /// <returns>
        ///     An instance of <see cref="RuntimeVariablesExpression" /> that has the <see cref="NodeType" /> property equal
        ///     to <see cref="ExpressionType.RuntimeVariables" /> and the <see cref="RuntimeVariablesExpression.Variables" />
        ///     property set to the specified value.
        /// </returns>
        public static RuntimeVariablesExpression RuntimeVariables(params ParameterExpression[] variables)
        {
            return RuntimeVariables((IEnumerable<ParameterExpression>)variables);
        }

        /// <summary>
        ///     Creates an instance of <see cref="RuntimeVariablesExpression" />.
        /// </summary>
        /// <param name="variables">
        ///     A collection of <see cref="ParameterExpression" /> objects to use to populate the
        ///     <see cref="RuntimeVariablesExpression.Variables" /> collection.
        /// </param>
        /// <returns>
        ///     An instance of <see cref="RuntimeVariablesExpression" /> that has the <see cref="NodeType" /> property equal
        ///     to <see cref="ExpressionType.RuntimeVariables" /> and the <see cref="RuntimeVariablesExpression.Variables" />
        ///     property set to the specified value.
        /// </returns>
        public static RuntimeVariablesExpression RuntimeVariables(IEnumerable<ParameterExpression> variables)
        {
            ContractUtils.RequiresNotNull(variables, nameof(variables));

            var vars = Theraot.Collections.Extensions.AsArrayInternal(variables);
            for (var i = 0; i < vars.Length; i++)
            {
                ContractUtils.RequiresNotNull(vars[i], nameof(variables), i);
            }

            return new RuntimeVariablesExpression(vars);
        }
    }

    /// <inheritdoc />
    /// <summary>
    ///     An expression that provides runtime read/write access to variables.
    ///     Needed to implement "eval" in some dynamic languages.
    ///     Evaluates to an instance of <see cref="T:System.Collections.Generic.IList`1" /> when executed.
    /// </summary>
    [DebuggerTypeProxy(typeof(RuntimeVariablesExpressionProxy))]
    public sealed class RuntimeVariablesExpression : Expression
    {
        private readonly ParameterExpression[] _variables;
        private readonly ReadOnlyCollectionEx<ParameterExpression> _variablesAsReadOnlyCollection;

        internal RuntimeVariablesExpression(ParameterExpression[] variables)
        {
            _variables = variables;
            _variablesAsReadOnlyCollection = ReadOnlyCollectionEx.Create(_variables);
        }

        /// <inheritdoc />
        /// <summary>
        ///     Returns the node type of this Expression. Extension nodes should return
        ///     ExpressionType.Extension when overriding this method.
        /// </summary>
        /// <returns>The <see cref="T:System.Linq.Expressions.ExpressionType" /> of the expression.</returns>
        public override ExpressionType NodeType => ExpressionType.RuntimeVariables;

        /// <inheritdoc />
        /// <summary>
        ///     Gets the static type of the expression that this <see cref="T:System.Linq.Expressions.Expression" /> represents.
        /// </summary>
        /// <returns>The <see cref="T:System.Type" /> that represents the static type of the expression.</returns>
        public override Type Type => typeof(IRuntimeVariables);

        /// <summary>
        ///     The variables or parameters to which to provide runtime access.
        /// </summary>
        public ReadOnlyCollection<ParameterExpression> Variables => _variablesAsReadOnlyCollection;

        /// <summary>
        ///     Creates a new expression that is like this one, but using the
        ///     supplied children. If all of the children are the same, it will
        ///     return this expression.
        /// </summary>
        /// <param name="variables">The <see cref="Variables" /> property of the result.</param>
        /// <returns>This expression if no children changed, or an expression with the updated children.</returns>
        public RuntimeVariablesExpression Update(IEnumerable<ParameterExpression> variables)
        {
            if (variables != null && ExpressionUtils.SameElements(ref variables, _variables))
            {
                return this;
            }

            return RuntimeVariables(variables);
        }

        protected internal override Expression Accept(ExpressionVisitor visitor)
        {
            return visitor.VisitRuntimeVariables(this);
        }
    }
}

#endif