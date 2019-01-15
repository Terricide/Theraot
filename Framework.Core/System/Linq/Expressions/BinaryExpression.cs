#if LESSTHAN_NET35

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic.Utils;
using System.Reflection;
using Theraot.Collections.Specialized;
using Theraot.Reflection;
using static System.Linq.Expressions.CachedReflectionInfo;

namespace System.Linq.Expressions
{
    /// <summary>
    /// Represents an expression that has a binary operator.
    /// </summary>
    [DebuggerTypeProxy(typeof(BinaryExpressionProxy))]
    public class BinaryExpression : Expression
    {
        internal BinaryExpression(Expression left, Expression right)
        {
            Left = left;
            Right = right;
        }

        /// <summary>
        /// Gets a value that indicates whether the expression tree node can be reduced.
        /// </summary>
        public override bool CanReduce =>
            // Only OpAssignments are reducible.
            IsOpAssignment(NodeType);

        /// <summary>
        /// Gets the type conversion function that is used by a coalescing or compound assignment operation.
        /// </summary>
        public LambdaExpression Conversion => GetConversion();

        /// <summary>
        /// Gets a value that indicates whether the expression tree node represents a lifted call to an operator.
        /// </summary>
        public bool IsLifted
        {
            get
            {
                if (NodeType == ExpressionType.Coalesce || NodeType == ExpressionType.Assign)
                {
                    return false;
                }
                if (Left.Type.IsNullable())
                {
                    var method = GetMethod();
                    return method == null ||
                        !TypeUtils.AreEquivalent(method.GetParameters()[0].ParameterType.GetNonRefTypeInternal(), Left.Type);
                }
                return false;
            }
        }

        /// <summary>
        /// Gets a value that indicates whether the expression tree node represents a lifted call to an operator whose return type is lifted to a nullable type.
        /// </summary>
        public bool IsLiftedToNull => IsLifted && Type.IsNullable();

        /// <summary>
        /// Gets the left operand of the binary operation.
        /// </summary>
        public Expression Left { get; }

        /// <summary>
        /// Gets the implementing method for the binary operation.
        /// </summary>
        public MethodInfo Method => GetMethod();

        /// <summary>
        /// Gets the right operand of the binary operation.
        /// </summary>
        public Expression Right { get; }

        internal bool IsLiftedLogical
        {
            get
            {
                var left = Left.Type;
                var right = Right.Type;
                var method = GetMethod();
                var kind = NodeType;

                return
                    (kind == ExpressionType.AndAlso || kind == ExpressionType.OrElse) &&
                    TypeUtils.AreEquivalent(right, left) &&
                    left.IsNullable() &&
                    method != null &&
                    TypeUtils.AreEquivalent(method.ReturnType, left.GetNonNullable());
            }
        }

        internal bool IsReferenceComparison
        {
            get
            {
                var left = Left.Type;
                var right = Right.Type;
                var method = GetMethod();
                var kind = NodeType;

                return (kind == ExpressionType.Equal || kind == ExpressionType.NotEqual) &&
                    method == null && !left.IsValueType && !right.IsValueType;
            }
        }

        /// <summary>
        /// Reduces the binary expression node to a simpler expression.
        /// If CanReduce returns true, this should return a valid expression.
        /// This method is allowed to return another node which itself
        /// must be reduced.
        /// </summary>
        /// <returns>The reduced expression.</returns>
        public override Expression Reduce()
        {
            // Only reduce OpAssignment expressions.
            if (IsOpAssignment(NodeType))
            {
                switch (Left.NodeType)
                {
                    case ExpressionType.MemberAccess:
                        return ReduceMember();

                    case ExpressionType.Index:
                        return ReduceIndex();

                    default:
                        return ReduceVariable();
                }
            }
            return this;
        }

        /// <summary>
        /// Creates a new expression that is like this one, but using the
        /// supplied children. If all of the children are the same, it will
        /// return this expression.
        /// </summary>
        /// <param name="left">The <see cref="Left"/> property of the result.</param>
        /// <param name="conversion">The <see cref="Conversion"/> property of the result.</param>
        /// <param name="right">The <see cref="Right"/> property of the result.</param>
        /// <returns>This expression if no children changed, or an expression with the updated children.</returns>
        public BinaryExpression Update(Expression left, LambdaExpression conversion, Expression right)
        {
            if (left == Left && right == Right && conversion == Conversion)
            {
                return this;
            }
            if (IsReferenceComparison)
            {
                if (NodeType == ExpressionType.Equal)
                {
                    return ReferenceEqual(left, right);
                }

                return ReferenceNotEqual(left, right);
            }
            return MakeBinary(NodeType, left, right, IsLiftedToNull, Method, conversion);
        }

        internal static BinaryExpression Create(ExpressionType nodeType, Expression left, Expression right, Type type, MethodInfo method, LambdaExpression conversion)
        {
            Debug.Assert(nodeType != ExpressionType.Assign);
            if (conversion != null)
            {
                Debug.Assert(method == null && TypeUtils.AreEquivalent(type, right.Type) && nodeType == ExpressionType.Coalesce);
                return new CoalesceConversionBinaryExpression(left, right, conversion);
            }
            if (method != null)
            {
                return new MethodBinaryExpression(nodeType, left, right, type, method);
            }
            if (type == typeof(bool))
            {
                return new LogicalBinaryExpression(nodeType, left, right);
            }
            return new SimpleBinaryExpression(nodeType, left, right, type);
        }

        internal virtual LambdaExpression GetConversion() => null;

        internal virtual MethodInfo GetMethod() => null;

        //
        // For a user-defined type T which has op_False defined and L, R are
        // nullable, (L AndAlso R) is computed as:
        //
        // L.HasValue
        //     ? T.op_False(L.GetValueOrDefault())
        //         ? L
        //         : R.HasValue
        //             ? (T?)(T.op_BitwiseAnd(L.GetValueOrDefault(), R.GetValueOrDefault()))
        //             : null
        //     : null
        //
        // For a user-defined type T which has op_True defined and L, R are
        // nullable, (L OrElse R)  is computed as:
        //
        // L.HasValue
        //     ? T.op_True(L.GetValueOrDefault())
        //         ? L
        //         : R.HasValue
        //             ? (T?)(T.op_BitwiseOr(L.GetValueOrDefault(), R.GetValueOrDefault()))
        //             : null
        //     : null
        //
        //
        // This is the same behavior as VB. If you think about it, it makes
        // sense: it's combining the normal pattern for short-circuiting
        // operators, with the normal pattern for lifted operations: if either
        // of the operands is null, the result is also null.
        //
        internal Expression ReduceUserDefinedLifted()
        {
            Debug.Assert(IsLiftedLogical);

            var left = Parameter(Left.Type, "left");
            var right = Parameter(Right.Type, "right");
            var opName = NodeType == ExpressionType.AndAlso ? "op_False" : "op_True";
            var opTrueFalse = TypeUtils.GetBooleanOperator(Method.DeclaringType, opName);
            Debug.Assert(opTrueFalse != null);

            return Block(
                ArrayReadOnlyCollection.Create(left),
                ArrayReadOnlyCollection.Create<Expression>(
                    Assign(left, Left),
                    Condition(
                        Property(left, "HasValue"),
                        Condition(
                            Call(opTrueFalse, Call(left, "GetValueOrDefault", null)),
                            left,
                            Block(
                                ArrayReadOnlyCollection.Create(right),
                                ArrayReadOnlyCollection.Create<Expression>(
                                    Assign(right, Right),
                                    Condition(
                                        Property(right, "HasValue"),
                                        Convert(
                                            Call(
                                                Method,
                                                Call(left, "GetValueOrDefault", null),
                                                Call(right, "GetValueOrDefault", null)
                                            ),
                                            Type
                                        ),
                                        Constant(null, Type)
                                    )
                                )
                            )
                        ),
                        Constant(null, Type)
                    )
                )
            );
        }

        /// <summary>
        /// Dispatches to the specific visit method for this node type.
        /// </summary>
        protected internal override Expression Accept(ExpressionVisitor visitor)
        {
            return visitor.VisitBinary(this);
        }

        // Note: takes children in evaluation order, which is also the order
        // that ExpressionVisitor visits them. Having them this way reduces the
        // chances people will make a mistake and use an inconsistent order in
        // derived visitors.
        // Return the corresponding Op of an assignment op.
        private static ExpressionType GetBinaryOpFromAssignmentOp(ExpressionType op)
        {
            Debug.Assert(IsOpAssignment(op));
            switch (op)
            {
                case ExpressionType.AddAssign:
                    return ExpressionType.Add;

                case ExpressionType.AddAssignChecked:
                    return ExpressionType.AddChecked;

                case ExpressionType.SubtractAssign:
                    return ExpressionType.Subtract;

                case ExpressionType.SubtractAssignChecked:
                    return ExpressionType.SubtractChecked;

                case ExpressionType.MultiplyAssign:
                    return ExpressionType.Multiply;

                case ExpressionType.MultiplyAssignChecked:
                    return ExpressionType.MultiplyChecked;

                case ExpressionType.DivideAssign:
                    return ExpressionType.Divide;

                case ExpressionType.ModuloAssign:
                    return ExpressionType.Modulo;

                case ExpressionType.PowerAssign:
                    return ExpressionType.Power;

                case ExpressionType.AndAssign:
                    return ExpressionType.And;

                case ExpressionType.OrAssign:
                    return ExpressionType.Or;

                case ExpressionType.RightShiftAssign:
                    return ExpressionType.RightShift;

                case ExpressionType.LeftShiftAssign:
                    return ExpressionType.LeftShift;

                case ExpressionType.ExclusiveOrAssign:
                    return ExpressionType.ExclusiveOr;

                default:
                    throw ContractUtils.Unreachable;
            }
        }

        private static bool IsOpAssignment(ExpressionType op)
        {
            switch (op)
            {
                case ExpressionType.AddAssign:
                case ExpressionType.SubtractAssign:
                case ExpressionType.MultiplyAssign:
                case ExpressionType.AddAssignChecked:
                case ExpressionType.SubtractAssignChecked:
                case ExpressionType.MultiplyAssignChecked:
                case ExpressionType.DivideAssign:
                case ExpressionType.ModuloAssign:
                case ExpressionType.PowerAssign:
                case ExpressionType.AndAssign:
                case ExpressionType.OrAssign:
                case ExpressionType.RightShiftAssign:
                case ExpressionType.LeftShiftAssign:
                case ExpressionType.ExclusiveOrAssign:
                    return true;
            }
            return false;
        }

        private Expression ReduceIndex()
        {
            // left[a0, a1, ... aN] (op)= r
            //
            // ... is reduced into ...
            //
            // tempObj = left
            // tempArg0 = a0
            // ...
            // tempArgN = aN
            // tempValue = tempObj[tempArg0, ... tempArgN] (op) r
            // tempObj[tempArg0, ... tempArgN] = tempValue

            var index = (IndexExpression)Left;

            var vars = new ArrayBuilder<ParameterExpression>(index.ArgumentCount + 2);
            var builder = new ArrayBuilder<Expression>(index.ArgumentCount + 3);

            var tempObj = Variable(index.Object.Type, "tempObj");
            vars.UncheckedAdd(tempObj);
            builder.UncheckedAdd(Assign(tempObj, index.Object));

            var n = index.ArgumentCount;
            var tempArgs = new ArrayBuilder<Expression>(n);
            for (var i = 0; i < n; i++)
            {
                var arg = index.GetArgument(i);
                var tempArg = Variable(arg.Type, "tempArg" + i);
                vars.UncheckedAdd(tempArg);
                tempArgs.UncheckedAdd(tempArg);
                builder.UncheckedAdd(Assign(tempArg, arg));
            }

            var tempIndex = MakeIndex(tempObj, index.Indexer, tempArgs.ToArray());

            // tempValue = tempObj[tempArg0, ... tempArgN] (op) r
            var binaryOp = GetBinaryOpFromAssignmentOp(NodeType);
            Expression op = MakeBinary(binaryOp, tempIndex, Right, false, Method);
            var conversion = GetConversion();
            if (conversion != null)
            {
                op = Invoke(conversion, op);
            }
            var tempValue = Variable(op.Type, "tempValue");
            vars.UncheckedAdd(tempValue);
            builder.UncheckedAdd(Assign(tempValue, op));

            // tempObj[tempArg0, ... tempArgN] = tempValue
            builder.UncheckedAdd(Assign(tempIndex, tempValue));

            return Block(vars.ToArray(), builder.ToArray());
        }

        private Expression ReduceMember()
        {
            var member = (MemberExpression)Left;

            if (member.Expression == null)
            {
                // static member, reduce the same as variable
                return ReduceVariable();
            }

            // left.b (op)= r
            // ... is reduced into ...
            // temp1 = left
            // temp2 = temp1.b (op) r
            // temp1.b = temp2
            // temp2
            var temp1 = Variable(member.Expression.Type, "temp1");

            // 1. temp1 = left
            Expression e1 = Assign(temp1, member.Expression);

            // 2. temp2 = temp1.b (op) r
            var op = GetBinaryOpFromAssignmentOp(NodeType);
            Expression e2 = MakeBinary(op, MakeMemberAccess(temp1, member.Member), Right, false, Method);
            var conversion = GetConversion();
            if (conversion != null)
            {
                e2 = Invoke(conversion, e2);
            }
            var temp2 = Variable(e2.Type, "temp2");
            e2 = Assign(temp2, e2);

            // 3. temp1.b = temp2
            Expression e3 = Assign(MakeMemberAccess(temp1, member.Member), temp2);

            // 3. temp2
            Expression e4 = temp2;

            return Block(
                ArrayReadOnlyCollection.Create(temp1, temp2),
                ArrayReadOnlyCollection.Create(e1, e2, e3, e4)
            );
        }

        private Expression ReduceVariable()
        {
            // v (op)= r
            // ... is reduced into ...
            // v = v (op) r
            var op = GetBinaryOpFromAssignmentOp(NodeType);
            Expression r = MakeBinary(op, Left, Right, false, Method);
            var conversion = GetConversion();
            if (conversion != null)
            {
                r = Invoke(conversion, r);
            }
            return Assign(Left, r);
        }
    }

    public partial class Expression
    {
        #region Assign

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Assign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Assign(Expression left, Expression right)
        {
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            TypeUtils.ValidateType(left.Type, nameof(left), allowByRef: true, allowPointer: true);
            TypeUtils.ValidateType(right.Type, nameof(right), allowByRef: true, allowPointer: true);
            if (!left.Type.IsReferenceAssignableFromInternal(right.Type))
            {
                throw Error.ExpressionTypeDoesNotMatchAssignment(right.Type, left.Type);
            }
            return new AssignBinaryExpression(left, right);
        }

        #endregion Assign

        /// <summary>
        /// Creates a <see cref="BinaryExpression" />, given the left and right operands, by calling an appropriate factory method.
        /// </summary>
        /// <param name="binaryType">The ExpressionType that specifies the type of binary operation.</param>
        /// <param name="left">An Expression that represents the left operand.</param>
        /// <param name="right">An Expression that represents the right operand.</param>
        /// <returns>The BinaryExpression that results from calling the appropriate factory method.</returns>
        public static BinaryExpression MakeBinary(ExpressionType binaryType, Expression left, Expression right)
        {
            return MakeBinary(binaryType, left, right, liftToNull: false, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression" />, given the left and right operands, by calling an appropriate factory method.
        /// </summary>
        /// <param name="binaryType">The ExpressionType that specifies the type of binary operation.</param>
        /// <param name="left">An Expression that represents the left operand.</param>
        /// <param name="right">An Expression that represents the right operand.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <param name="method">A MethodInfo that specifies the implementing method.</param>
        /// <returns>The BinaryExpression that results from calling the appropriate factory method.</returns>
        public static BinaryExpression MakeBinary(ExpressionType binaryType, Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            return MakeBinary(binaryType, left, right, liftToNull, method, conversion: null);
        }

        ///
        /// <summary>
        /// Creates a <see cref="BinaryExpression" />, given the left and right operands, by calling an appropriate factory method.
        /// </summary>
        /// <param name="binaryType">The ExpressionType that specifies the type of binary operation.</param>
        /// <param name="left">An Expression that represents the left operand.</param>
        /// <param name="right">An Expression that represents the right operand.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <param name="method">A MethodInfo that specifies the implementing method.</param>
        /// <param name="conversion">A LambdaExpression that represents a type conversion function. This parameter is used if binaryType is Coalesce or compound assignment.</param>
        /// <returns>The BinaryExpression that results from calling the appropriate factory method.</returns>
        public static BinaryExpression MakeBinary(ExpressionType binaryType, Expression left, Expression right, bool liftToNull, MethodInfo method, LambdaExpression conversion)
        {
            switch (binaryType)
            {
                case ExpressionType.Add:
                    return Add(left, right, method);

                case ExpressionType.AddChecked:
                    return AddChecked(left, right, method);

                case ExpressionType.Subtract:
                    return Subtract(left, right, method);

                case ExpressionType.SubtractChecked:
                    return SubtractChecked(left, right, method);

                case ExpressionType.Multiply:
                    return Multiply(left, right, method);

                case ExpressionType.MultiplyChecked:
                    return MultiplyChecked(left, right, method);

                case ExpressionType.Divide:
                    return Divide(left, right, method);

                case ExpressionType.Modulo:
                    return Modulo(left, right, method);

                case ExpressionType.Power:
                    return Power(left, right, method);

                case ExpressionType.And:
                    return And(left, right, method);

                case ExpressionType.AndAlso:
                    return AndAlso(left, right, method);

                case ExpressionType.Or:
                    return Or(left, right, method);

                case ExpressionType.OrElse:
                    return OrElse(left, right, method);

                case ExpressionType.LessThan:
                    return LessThan(left, right, liftToNull, method);

                case ExpressionType.LessThanOrEqual:
                    return LessThanOrEqual(left, right, liftToNull, method);

                case ExpressionType.GreaterThan:
                    return GreaterThan(left, right, liftToNull, method);

                case ExpressionType.GreaterThanOrEqual:
                    return GreaterThanOrEqual(left, right, liftToNull, method);

                case ExpressionType.Equal:
                    return Equal(left, right, liftToNull, method);

                case ExpressionType.NotEqual:
                    return NotEqual(left, right, liftToNull, method);

                case ExpressionType.ExclusiveOr:
                    return ExclusiveOr(left, right, method);

                case ExpressionType.Coalesce:
                    return Coalesce(left, right, conversion);

                case ExpressionType.ArrayIndex:
                    return ArrayIndex(left, right);

                case ExpressionType.RightShift:
                    return RightShift(left, right, method);

                case ExpressionType.LeftShift:
                    return LeftShift(left, right, method);

                case ExpressionType.Assign:
                    return Assign(left, right);

                case ExpressionType.AddAssign:
                    return AddAssign(left, right, method, conversion);

                case ExpressionType.AndAssign:
                    return AndAssign(left, right, method, conversion);

                case ExpressionType.DivideAssign:
                    return DivideAssign(left, right, method, conversion);

                case ExpressionType.ExclusiveOrAssign:
                    return ExclusiveOrAssign(left, right, method, conversion);

                case ExpressionType.LeftShiftAssign:
                    return LeftShiftAssign(left, right, method, conversion);

                case ExpressionType.ModuloAssign:
                    return ModuloAssign(left, right, method, conversion);

                case ExpressionType.MultiplyAssign:
                    return MultiplyAssign(left, right, method, conversion);

                case ExpressionType.OrAssign:
                    return OrAssign(left, right, method, conversion);

                case ExpressionType.PowerAssign:
                    return PowerAssign(left, right, method, conversion);

                case ExpressionType.RightShiftAssign:
                    return RightShiftAssign(left, right, method, conversion);

                case ExpressionType.SubtractAssign:
                    return SubtractAssign(left, right, method, conversion);

                case ExpressionType.AddAssignChecked:
                    return AddAssignChecked(left, right, method, conversion);

                case ExpressionType.SubtractAssignChecked:
                    return SubtractAssignChecked(left, right, method, conversion);

                case ExpressionType.MultiplyAssignChecked:
                    return MultiplyAssignChecked(left, right, method, conversion);

                default:
                    throw Error.UnhandledBinary(binaryType, nameof(binaryType));
            }
        }

        internal static bool ParameterIsAssignable(ParameterInfo pi, Type argType)
        {
            var pType = pi.ParameterType;
            if (pType.IsByRef)
            {
                pType = pType.GetElementType();
            }
            return pType.IsReferenceAssignableFromInternal(argType);
        }

        private static BinaryExpression GetMethodBasedAssignOperator(ExpressionType binaryType, Expression left, Expression right, MethodInfo method, LambdaExpression conversion, bool liftToNull)
        {
            var b = GetMethodBasedBinaryOperator(binaryType, left, right, method, liftToNull);
            if (conversion == null)
            {
                // return type must be assignable back to the left type
                if (!left.Type.IsReferenceAssignableFromInternal(b.Type))
                {
                    throw Error.UserDefinedOpMustHaveValidReturnType(binaryType, b.Method.Name);
                }
            }
            else
            {
                // add the conversion to the result
                ValidateOpAssignConversionLambda(conversion, b.Left, b.Method, b.NodeType);
                b = new OpAssignMethodConversionBinaryExpression(b.NodeType, b.Left, b.Right, b.Left.Type, b.Method, conversion);
            }
            return b;
        }

        private static BinaryExpression GetMethodBasedBinaryOperator(ExpressionType binaryType, Expression left, Expression right, MethodInfo method, bool liftToNull)
        {
            Debug.Assert(method != null);
            ValidateOperator(method);
            var pms = method.GetParameters();
            if (pms.Length != 2)
            {
                throw Error.IncorrectNumberOfMethodCallArguments(method, nameof(method));
            }

            if (ParameterIsAssignable(pms[0], left.Type) && ParameterIsAssignable(pms[1], right.Type))
            {
                ValidateParamsWithOperandsOrThrow(pms[0].ParameterType, left.Type, binaryType, method.Name);
                ValidateParamsWithOperandsOrThrow(pms[1].ParameterType, right.Type, binaryType, method.Name);
                return new MethodBinaryExpression(binaryType, left, right, method.ReturnType, method);
            }
            // check for lifted call
            if (left.Type.IsNullable() && right.Type.IsNullable() &&
                ParameterIsAssignable(pms[0], left.Type.GetNonNullable()) &&
                ParameterIsAssignable(pms[1], right.Type.GetNonNullable()) &&
                method.ReturnType.IsValueType && !method.ReturnType.IsNullable())
            {
                if (method.ReturnType != typeof(bool) || liftToNull)
                {
                    return new MethodBinaryExpression(binaryType, left, right, method.ReturnType.GetNullable(), method);
                }

                return new MethodBinaryExpression(binaryType, left, right, typeof(bool), method);
            }
            throw Error.OperandTypesDoNotMatchParameters(binaryType, method.Name);
        }

        private static BinaryExpression GetUserDefinedAssignOperatorOrThrow(ExpressionType binaryType, string name, Expression left, Expression right, LambdaExpression conversion, bool liftToNull)
        {
            var b = GetUserDefinedBinaryOperatorOrThrow(binaryType, name, left, right, liftToNull);
            if (conversion == null)
            {
                // return type must be assignable back to the left type
                if (!left.Type.IsReferenceAssignableFromInternal(b.Type))
                {
                    throw Error.UserDefinedOpMustHaveValidReturnType(binaryType, b.Method.Name);
                }
            }
            else
            {
                // add the conversion to the result
                ValidateOpAssignConversionLambda(conversion, b.Left, b.Method, b.NodeType);
                b = new OpAssignMethodConversionBinaryExpression(b.NodeType, b.Left, b.Right, b.Left.Type, b.Method, conversion);
            }
            return b;
        }

        private static BinaryExpression GetUserDefinedBinaryOperator(ExpressionType binaryType, string name, Expression left, Expression right, bool liftToNull)
        {
            // try exact match first
            var method = GetUserDefinedBinaryOperator(binaryType, left.Type, right.Type, name);
            if (method != null)
            {
                return new MethodBinaryExpression(binaryType, left, right, method.ReturnType, method);
            }
            // try lifted call
            if (left.Type.IsNullable() && right.Type.IsNullable())
            {
                var nnLeftType = left.Type.GetNonNullable();
                var nnRightType = right.Type.GetNonNullable();
                method = GetUserDefinedBinaryOperator(binaryType, nnLeftType, nnRightType, name);
                if (method != null && method.ReturnType.IsValueType && !method.ReturnType.IsNullable())
                {
                    if (method.ReturnType != typeof(bool) || liftToNull)
                    {
                        return new MethodBinaryExpression(binaryType, left, right, method.ReturnType.GetNullable(), method);
                    }

                    return new MethodBinaryExpression(binaryType, left, right, typeof(bool), method);
                }
            }
            return null;
        }

        private static MethodInfo GetUserDefinedBinaryOperator(ExpressionType binaryType, Type leftType, Type rightType, string name)
        {
            // This algorithm is wrong, we should be checking for uniqueness and erroring if
            // it is defined on both types.
            Type[] types = { leftType, rightType };
            var nnLeftType = leftType.GetNonNullable();
            var nnRightType = rightType.GetNonNullable();
            var method = nnLeftType.GetStaticMethodInternal(name, types);
            if (method == null && !TypeUtils.AreEquivalent(leftType, rightType))
            {
                method = nnRightType.GetStaticMethodInternal(name, types);
            }

            if (IsLiftingConditionalLogicalOperator(leftType, rightType, method, binaryType))
            {
                method = GetUserDefinedBinaryOperator(binaryType, nnLeftType, nnRightType, name);
            }
            return method;
        }

        private static BinaryExpression GetUserDefinedBinaryOperatorOrThrow(ExpressionType binaryType, string name, Expression left, Expression right, bool liftToNull)
        {
            var b = GetUserDefinedBinaryOperator(binaryType, name, left, right, liftToNull);
            if (b != null)
            {
                var pis = b.Method.GetParameters();
                ValidateParamsWithOperandsOrThrow(pis[0].ParameterType, left.Type, binaryType, name);
                ValidateParamsWithOperandsOrThrow(pis[1].ParameterType, right.Type, binaryType, name);
                return b;
            }
            throw Error.BinaryOperatorNotDefined(binaryType, left.Type, right.Type);
        }

        private static bool IsLiftingConditionalLogicalOperator(Type left, Type right, MethodInfo method, ExpressionType binaryType)
        {
            return right.IsNullable() &&
                    left.IsNullable() &&
                    method == null &&
                    (binaryType == ExpressionType.AndAlso || binaryType == ExpressionType.OrElse);
        }

        private static bool IsNullComparison(Expression left, Expression right)
        {
            // If we have x==null, x!=null, null==x or null!=x where x is
            // nullable but not null, then this is treated as a call to x.HasValue
            // and is legal even if there is no equality operator defined on the
            // type of x.
            return IsNullConstant(left)
                ? !IsNullConstant(right) && right.Type.IsNullable()
                : IsNullConstant(right) && left.Type.IsNullable();
        }

        // Note: this has different meaning than ConstantCheck.IsNull
        // That function attempts to determine if the result of a tree will be
        // null at runtime. This function is used at tree construction time and
        // only looks for a ConstantExpression with a null Value. It can't
        // become "smarter" or that would break tree construction.
        private static bool IsNullConstant(Expression e)
        {
            return e is ConstantExpression c && c.Value == null;
        }

        private static bool IsValidLiftedConditionalLogicalOperator(Type left, Type right, ParameterInfo[] pms)
        {
            return TypeUtils.AreEquivalent(left, right) &&
                   right.IsNullable() &&
                   TypeUtils.AreEquivalent(pms[1].ParameterType, right.GetNonNullable());
        }

        private static void ValidateMethodInfo(MethodInfo method, string paramName)
        {
            if (method.ContainsGenericParameters)
            {
                throw method.IsGenericMethodDefinition ? Error.MethodIsGeneric(method, paramName) : Error.MethodContainsGenericParameters(method, paramName);
            }
        }

        private static void ValidateOperator(MethodInfo method)
        {
            Debug.Assert(method != null);
            ValidateMethodInfo(method, nameof(method));
            if (!method.IsStatic)
            {
                throw Error.UserDefinedOperatorMustBeStatic(method, nameof(method));
            }

            if (method.ReturnType == typeof(void))
            {
                throw Error.UserDefinedOperatorMustNotBeVoid(method, nameof(method));
            }
        }

        private static void ValidateParamsWithOperandsOrThrow(Type paramType, Type operandType, ExpressionType exprType, string name)
        {
            if (paramType.IsNullable() && !operandType.IsNullable())
            {
                throw Error.OperandTypesDoNotMatchParameters(exprType, name);
            }
        }

        private static void ValidateUserDefinedConditionalLogicOperator(ExpressionType nodeType, Type left, Type right, MethodInfo method)
        {
            ValidateOperator(method);
            var pms = method.GetParameters();
            if (pms.Length != 2)
            {
                throw Error.IncorrectNumberOfMethodCallArguments(method, nameof(method));
            }

            if (!ParameterIsAssignable(pms[0], left) && !(left.IsNullable() && ParameterIsAssignable(pms[0], left.GetNonNullable())))
            {
                throw Error.OperandTypesDoNotMatchParameters(nodeType, method.Name);
            }

            if (!ParameterIsAssignable(pms[1], right) && !(right.IsNullable() && ParameterIsAssignable(pms[1], right.GetNonNullable())))
            {
                throw Error.OperandTypesDoNotMatchParameters(nodeType, method.Name);
            }

            if (pms[0].ParameterType != pms[1].ParameterType)
            {
                throw Error.UserDefinedOpMustHaveConsistentTypes(nodeType, method.Name);
            }
            if (method.ReturnType != pms[0].ParameterType)
            {
                throw Error.UserDefinedOpMustHaveConsistentTypes(nodeType, method.Name);
            }
            if (IsValidLiftedConditionalLogicalOperator(left, right, pms))
            {
                left = left.GetNonNullable();
            }
            var declaringType = method.DeclaringType;
            if (declaringType == null)
            {
                throw Error.LogicalOperatorMustHaveBooleanOperators(nodeType, method.Name);
            }
            var opTrue = TypeUtils.GetBooleanOperator(declaringType, "op_True");
            var opFalse = TypeUtils.GetBooleanOperator(declaringType, "op_False");
            if (opTrue == null || opTrue.ReturnType != typeof(bool) ||
                opFalse == null || opFalse.ReturnType != typeof(bool))
            {
                throw Error.LogicalOperatorMustHaveBooleanOperators(nodeType, method.Name);
            }
            VerifyOpTrueFalse(nodeType, left, opFalse, nameof(method));
            VerifyOpTrueFalse(nodeType, left, opTrue, nameof(method));
        }

        private static void VerifyOpTrueFalse(ExpressionType nodeType, Type left, MethodInfo opTrue, string paramName)
        {
            var pmsOpTrue = opTrue.GetParameters();
            if (pmsOpTrue.Length != 1)
            {
                throw Error.IncorrectNumberOfMethodCallArguments(opTrue, paramName);
            }

            if (!ParameterIsAssignable(pmsOpTrue[0], left) && !(left.IsNullable() && ParameterIsAssignable(pmsOpTrue[0], left.GetNonNullable())))
            {
                throw Error.OperandTypesDoNotMatchParameters(nodeType, opTrue.Name);
            }

        }

        #region Equality Operators

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an equality comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Equal"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Equal(Expression left, Expression right)
        {
            return Equal(left, right, liftToNull: false, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an equality comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Equal"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.IsLiftedToNull"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Equal(Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                return GetEqualityComparisonOperator(ExpressionType.Equal, "op_Equality", left, right, liftToNull);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Equal, left, right, method, liftToNull);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an inequality comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.NotEqual"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression NotEqual(Expression left, Expression right)
        {
            return NotEqual(left, right, liftToNull: false, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an inequality comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.NotEqual"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.IsLiftedToNull"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression NotEqual(Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                return GetEqualityComparisonOperator(ExpressionType.NotEqual, "op_Inequality", left, right, liftToNull);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.NotEqual, left, right, method, liftToNull);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a reference equality comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Equal"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ReferenceEqual(Expression left, Expression right)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (TypeUtils.HasReferenceEquality(left.Type, right.Type))
            {
                return new LogicalBinaryExpression(ExpressionType.Equal, left, right);
            }
            throw Error.ReferenceEqualityNotDefined(left.Type, right.Type);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a reference inequality comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.NotEqual"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ReferenceNotEqual(Expression left, Expression right)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (TypeUtils.HasReferenceEquality(left.Type, right.Type))
            {
                return new LogicalBinaryExpression(ExpressionType.NotEqual, left, right);
            }
            throw Error.ReferenceEqualityNotDefined(left.Type, right.Type);
        }

        private static BinaryExpression GetEqualityComparisonOperator(ExpressionType binaryType, string opName, Expression left, Expression right, bool liftToNull)
        {
            // known comparison - numeric types, bools, object, enums
            if (left.Type == right.Type && (left.Type.IsNumeric() ||
                left.Type == typeof(object) ||
                left.Type.IsBool() ||
                left.Type.GetNonNullable().IsEnum))
            {
                if (left.Type.IsNullable() && liftToNull)
                {
                    return new SimpleBinaryExpression(binaryType, left, right, typeof(bool?));
                }

                return new LogicalBinaryExpression(binaryType, left, right);
            }
            // look for user defined operator
            var b = GetUserDefinedBinaryOperator(binaryType, opName, left, right, liftToNull);
            if (b != null)
            {
                return b;
            }
            if (TypeUtils.HasBuiltInEqualityOperator(left.Type, right.Type) || IsNullComparison(left, right))
            {
                if (left.Type.IsNullable() && liftToNull)
                {
                    return new SimpleBinaryExpression(binaryType, left, right, typeof(bool?));
                }

                return new LogicalBinaryExpression(binaryType, left, right);
            }
            throw Error.BinaryOperatorNotDefined(binaryType, left.Type, right.Type);
        }

        #endregion Equality Operators

        #region Comparison Expressions

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "greater than" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.GreaterThan"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression GreaterThan(Expression left, Expression right)
        {
            return GreaterThan(left, right, liftToNull: false, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "greater than" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.GreaterThan"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.IsLiftedToNull"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression GreaterThan(Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                return GetComparisonOperator(ExpressionType.GreaterThan, "op_GreaterThan", left, right, liftToNull);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.GreaterThan, left, right, method, liftToNull);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "less than" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LessThan"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "greater than or equal" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.GreaterThanOrEqual"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression GreaterThanOrEqual(Expression left, Expression right)
        {
            return GreaterThanOrEqual(left, right, liftToNull: false, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "greater than or equal" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.GreaterThanOrEqual"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.IsLiftedToNull"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression GreaterThanOrEqual(Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                return GetComparisonOperator(ExpressionType.GreaterThanOrEqual, "op_GreaterThanOrEqual", left, right, liftToNull);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.GreaterThanOrEqual, left, right, method, liftToNull);
        }

        public static BinaryExpression LessThan(Expression left, Expression right)
        {
            return LessThan(left, right, liftToNull: false, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "less than" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LessThan"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.IsLiftedToNull"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression LessThan(Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                return GetComparisonOperator(ExpressionType.LessThan, "op_LessThan", left, right, liftToNull);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.LessThan, left, right, method, liftToNull);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "less than or equal" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LessThanOrEqual"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression LessThanOrEqual(Expression left, Expression right)
        {
            return LessThanOrEqual(left, right, liftToNull: false, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a "less than or equal" numeric comparison.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="liftToNull">true to set IsLiftedToNull to true; false to set IsLiftedToNull to false.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LessThanOrEqual"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.IsLiftedToNull"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression LessThanOrEqual(Expression left, Expression right, bool liftToNull, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                return GetComparisonOperator(ExpressionType.LessThanOrEqual, "op_LessThanOrEqual", left, right, liftToNull);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.LessThanOrEqual, left, right, method, liftToNull);
        }

        private static BinaryExpression GetComparisonOperator(ExpressionType binaryType, string opName, Expression left, Expression right, bool liftToNull)
        {
            if (left.Type == right.Type && left.Type.IsNumeric())
            {
                if (left.Type.IsNullable() && liftToNull)
                {
                    return new SimpleBinaryExpression(binaryType, left, right, typeof(bool?));
                }

                return new LogicalBinaryExpression(binaryType, left, right);
            }
            return GetUserDefinedBinaryOperatorOrThrow(binaryType, opName, left, right, liftToNull);
        }

        #endregion Comparison Expressions

        #region Boolean Expressions

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a conditional AND operation that evaluates the second operand only if it has to.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AndAlso"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression AndAlso(Expression left, Expression right)
        {
            return AndAlso(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a conditional AND operation that evaluates the second operand only if it has to.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AndAlso"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AndAlso(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            Type returnType;
            if (method == null)
            {
                if (left.Type == right.Type)
                {
                    if (left.Type == typeof(bool))
                    {
                        return new LogicalBinaryExpression(ExpressionType.AndAlso, left, right);
                    }

                    if (left.Type == typeof(bool?))
                    {
                        return new SimpleBinaryExpression(ExpressionType.AndAlso, left, right, left.Type);
                    }
                }
                method = GetUserDefinedBinaryOperator(ExpressionType.AndAlso, left.Type, right.Type, "op_BitwiseAnd");
                if (method != null)
                {
                    ValidateUserDefinedConditionalLogicOperator(ExpressionType.AndAlso, left.Type, right.Type, method);
                    returnType = left.Type.IsNullable() && TypeUtils.AreEquivalent(method.ReturnType, left.Type.GetNonNullable()) ? left.Type : method.ReturnType;
                    return new MethodBinaryExpression(ExpressionType.AndAlso, left, right, returnType, method);
                }
                throw Error.BinaryOperatorNotDefined(ExpressionType.AndAlso, left.Type, right.Type);
            }
            ValidateUserDefinedConditionalLogicOperator(ExpressionType.AndAlso, left.Type, right.Type, method);
            returnType = left.Type.IsNullable() && TypeUtils.AreEquivalent(method.ReturnType, left.Type.GetNonNullable()) ? left.Type : method.ReturnType;
            return new MethodBinaryExpression(ExpressionType.AndAlso, left, right, returnType, method);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a conditional OR operation that evaluates the second operand only if it has to.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.OrElse"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression OrElse(Expression left, Expression right)
        {
            return OrElse(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a conditional OR operation that evaluates the second operand only if it has to.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.OrElse"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression OrElse(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            Type returnType;
            if (method == null)
            {
                if (left.Type == right.Type)
                {
                    if (left.Type == typeof(bool))
                    {
                        return new LogicalBinaryExpression(ExpressionType.OrElse, left, right);
                    }

                    if (left.Type == typeof(bool?))
                    {
                        return new SimpleBinaryExpression(ExpressionType.OrElse, left, right, left.Type);
                    }
                }
                method = GetUserDefinedBinaryOperator(ExpressionType.OrElse, left.Type, right.Type, "op_BitwiseOr");
                if (method != null)
                {
                    ValidateUserDefinedConditionalLogicOperator(ExpressionType.OrElse, left.Type, right.Type, method);
                    returnType = left.Type.IsNullable() && method.ReturnType == left.Type.GetNonNullable() ? left.Type : method.ReturnType;
                    return new MethodBinaryExpression(ExpressionType.OrElse, left, right, returnType, method);
                }
                throw Error.BinaryOperatorNotDefined(ExpressionType.OrElse, left.Type, right.Type);
            }
            ValidateUserDefinedConditionalLogicOperator(ExpressionType.OrElse, left.Type, right.Type, method);
            returnType = left.Type.IsNullable() && method.ReturnType == left.Type.GetNonNullable() ? left.Type : method.ReturnType;
            return new MethodBinaryExpression(ExpressionType.OrElse, left, right, returnType, method);
        }

        #endregion Boolean Expressions

        #region Coalescing Expressions

        /// <summary>
        /// Creates a <see cref="BinaryExpression" /> that represents a coalescing operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression" /> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Coalesce"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Coalesce(Expression left, Expression right)
        {
            return Coalesce(left, right, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression" /> that represents a coalescing operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="conversion">A LambdaExpression to set the Conversion property equal to.</param>
        /// <returns>A <see cref="BinaryExpression" /> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Coalesce"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/> and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Coalesce(Expression left, Expression right, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));

            if (conversion == null)
            {
                var resultType = ValidateCoalesceArgTypes(left.Type, right.Type);
                return new SimpleBinaryExpression(ExpressionType.Coalesce, left, right, resultType);
            }

            if (left.Type.IsValueType && !left.Type.IsNullable())
            {
                throw Error.CoalesceUsedOnNonNullType();
            }

            var delegateType = conversion.Type;
            Debug.Assert(typeof(MulticastDelegate).IsAssignableFrom(delegateType) && delegateType != typeof(MulticastDelegate));
            var method = delegateType.GetInvokeMethod();
            if (method.ReturnType == typeof(void))
            {
                throw Error.UserDefinedOperatorMustNotBeVoid(conversion, nameof(conversion));
            }
            var pms = method.GetParameters();
            Debug.Assert(pms.Length == conversion.ParameterCount);
            if (pms.Length != 1)
            {
                throw Error.IncorrectNumberOfMethodCallArguments(conversion, nameof(conversion));
            }
            // The return type must match exactly.
            // We could weaken this restriction and
            // say that the return type must be assignable to from
            // the return type of the lambda.
            if (!TypeUtils.AreEquivalent(method.ReturnType, right.Type))
            {
                throw Error.OperandTypesDoNotMatchParameters(ExpressionType.Coalesce, conversion.ToString());
            }
            // The parameter of the conversion lambda must either be assignable
            // from the erased or unerased type of the left hand side.
            if (!ParameterIsAssignable(pms[0], left.Type.GetNonNullable()) &&
                !ParameterIsAssignable(pms[0], left.Type))
            {
                throw Error.OperandTypesDoNotMatchParameters(ExpressionType.Coalesce, conversion.ToString());
            }
            return new CoalesceConversionBinaryExpression(left, right, conversion);
        }

        private static Type ValidateCoalesceArgTypes(Type left, Type right)
        {
            var leftStripped = left.GetNonNullable();
            if (left.IsValueType && !left.IsNullable())
            {
                throw Error.CoalesceUsedOnNonNullType();
            }

            if (left.IsNullable() && right.IsImplicitlyConvertibleToInternal(leftStripped))
            {
                return leftStripped;
            }

            if (right.IsImplicitlyConvertibleToInternal(left))
            {
                return left;
            }

            if (leftStripped.IsImplicitlyConvertibleToInternal(right))
            {
                return right;
            }

            throw Error.ArgumentTypesMustMatch();
        }

        #endregion Coalescing Expressions

        #region Arithmetic Expressions

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic addition operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Add"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Add(Expression left, Expression right)
        {
            return Add(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic addition operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Add"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Add(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.Add, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.Add, "op_Addition", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Add, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an addition assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression AddAssign(Expression left, Expression right)
        {
            return AddAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an addition assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AddAssign(Expression left, Expression right, MethodInfo method)
        {
            return AddAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an addition assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AddAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.AddAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.AddAssign, "op_Addition", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.AddAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an addition assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to
        /// <see cref="ExpressionType.AddAssignChecked"/> and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/>
        /// properties set to the specified values.
        /// </returns>
        public static BinaryExpression AddAssignChecked(Expression left, Expression right)
        {
            return AddAssignChecked(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an addition assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AddAssignChecked(Expression left, Expression right, MethodInfo method)
        {
            return AddAssignChecked(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an addition assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AddAssignChecked(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));

            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.AddAssignChecked, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.AddAssignChecked, "op_Addition", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.AddAssignChecked, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic addition operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddChecked"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression AddChecked(Expression left, Expression right)
        {
            return AddChecked(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic addition operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AddChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AddChecked(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.AddChecked, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.AddChecked, "op_Addition", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.AddChecked, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise AND operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.And"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression And(Expression left, Expression right)
        {
            return And(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise AND operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.And"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression And(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsIntegerOrBool())
                {
                    return new SimpleBinaryExpression(ExpressionType.And, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.And, "op_BitwiseAnd", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.And, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise AND assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AndAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression AndAssign(Expression left, Expression right)
        {
            return AndAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise AND assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AndAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AndAssign(Expression left, Expression right, MethodInfo method)
        {
            return AndAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise AND assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.AndAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression AndAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsIntegerOrBool())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.AndAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.AndAssign, "op_BitwiseAnd", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.AndAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic division operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Divide"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Divide(Expression left, Expression right)
        {
            return Divide(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic division operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Divide"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Divide(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.Divide, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.Divide, "op_Division", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Divide, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a division assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.DivideAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression DivideAssign(Expression left, Expression right)
        {
            return DivideAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a division assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.DivideAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression DivideAssign(Expression left, Expression right, MethodInfo method)
        {
            return DivideAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a division assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.DivideAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression DivideAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.DivideAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.DivideAssign, "op_Division", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.DivideAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise or logical XOR operation, using op_ExclusiveOr for user-defined types.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ExclusiveOr"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression ExclusiveOr(Expression left, Expression right)
        {
            return ExclusiveOr(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise or logical XOR operation, using op_ExclusiveOr for user-defined types.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ExclusiveOr"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ExclusiveOr(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsIntegerOrBool())
                {
                    return new SimpleBinaryExpression(ExpressionType.ExclusiveOr, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.ExclusiveOr, "op_ExclusiveOr", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.ExclusiveOr, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise or logical XOR assignment operation, using op_ExclusiveOr for user-defined types.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ExclusiveOrAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression ExclusiveOrAssign(Expression left, Expression right)
        {
            return ExclusiveOrAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise or logical XOR assignment operation, using op_ExclusiveOr for user-defined types.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ExclusiveOrAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ExclusiveOrAssign(Expression left, Expression right, MethodInfo method)
        {
            return ExclusiveOrAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise or logical XOR assignment operation, using op_ExclusiveOr for user-defined types.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ExclusiveOrAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ExclusiveOrAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsIntegerOrBool())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.ExclusiveOrAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.ExclusiveOrAssign, "op_ExclusiveOr", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.ExclusiveOrAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise left-shift operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LeftShift"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression LeftShift(Expression left, Expression right)
        {
            return LeftShift(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise left-shift operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LeftShift"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression LeftShift(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (IsSimpleShift(left.Type, right.Type))
                {
                    var resultType = GetResultTypeOfShift(left.Type, right.Type);
                    return new SimpleBinaryExpression(ExpressionType.LeftShift, left, right, resultType);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.LeftShift, "op_LeftShift", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.LeftShift, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise left-shift assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LeftShiftAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression LeftShiftAssign(Expression left, Expression right)
        {
            return LeftShiftAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise left-shift assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LeftShiftAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression LeftShiftAssign(Expression left, Expression right, MethodInfo method)
        {
            return LeftShiftAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise left-shift assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.LeftShiftAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression LeftShiftAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (IsSimpleShift(left.Type, right.Type))
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    var resultType = GetResultTypeOfShift(left.Type, right.Type);
                    return new SimpleBinaryExpression(ExpressionType.LeftShiftAssign, left, right, resultType);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.LeftShiftAssign, "op_LeftShift", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.LeftShiftAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic remainder operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Modulo"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Modulo(Expression left, Expression right)
        {
            return Modulo(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic remainder operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Modulo"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Modulo(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.Modulo, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.Modulo, "op_Modulus", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Modulo, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a remainder assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ModuloAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression ModuloAssign(Expression left, Expression right)
        {
            return ModuloAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a remainder assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ModuloAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ModuloAssign(Expression left, Expression right, MethodInfo method)
        {
            return ModuloAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a remainder assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ModuloAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression ModuloAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.ModuloAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.ModuloAssign, "op_Modulus", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.ModuloAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic multiplication operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Multiply"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Multiply(Expression left, Expression right)
        {
            return Multiply(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic multiplication operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Multiply"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Multiply(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.Multiply, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.Multiply, "op_Multiply", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Multiply, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a multiplication assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression MultiplyAssign(Expression left, Expression right)
        {
            return MultiplyAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a multiplication assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression MultiplyAssign(Expression left, Expression right, MethodInfo method)
        {
            return MultiplyAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a multiplication assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression MultiplyAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.MultiplyAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.MultiplyAssign, "op_Multiply", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.MultiplyAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a multiplication assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression MultiplyAssignChecked(Expression left, Expression right)
        {
            return MultiplyAssignChecked(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a multiplication assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression MultiplyAssignChecked(Expression left, Expression right, MethodInfo method)
        {
            return MultiplyAssignChecked(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a multiplication assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression MultiplyAssignChecked(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.MultiplyAssignChecked, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.MultiplyAssignChecked, "op_Multiply", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.MultiplyAssignChecked, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic multiplication operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyChecked"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression MultiplyChecked(Expression left, Expression right)
        {
            return MultiplyChecked(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic multiplication operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.MultiplyChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression MultiplyChecked(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.MultiplyChecked, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.MultiplyChecked, "op_Multiply", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.MultiplyChecked, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise OR operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Or"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Or(Expression left, Expression right)
        {
            return Or(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise OR operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Or"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Or(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsIntegerOrBool())
                {
                    return new SimpleBinaryExpression(ExpressionType.Or, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.Or, "op_BitwiseOr", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Or, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise OR assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.OrAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression OrAssign(Expression left, Expression right)
        {
            return OrAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise OR assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.OrAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression OrAssign(Expression left, Expression right, MethodInfo method)
        {
            return OrAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise OR assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.OrAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression OrAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsIntegerOrBool())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.OrAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.OrAssign, "op_BitwiseOr", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.OrAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents raising a number to a power.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Power"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Power(Expression left, Expression right)
        {
            return Power(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents raising a number to a power.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Power"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Power(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    method = MathPowDoubleDouble;
                    Debug.Assert(method != null);
                }
                else
                {
                    // VB uses op_Exponent, F# uses op_Exponentiation. This inconsistency is unfortunate, but we can
                    // test for either.
                    var name = "op_Exponent";
                    var b = GetUserDefinedBinaryOperator(ExpressionType.Power, name, left, right, liftToNull: true);
                    if (b == null)
                    {
                        name = "op_Exponentiation";
                        b = GetUserDefinedBinaryOperator(ExpressionType.Power, name, left, right, liftToNull: true);
                        if (b == null)
                        {
                            throw Error.BinaryOperatorNotDefined(ExpressionType.Power, left.Type, right.Type);
                        }
                    }

                    var pis = b.Method.GetParameters();
                    ValidateParamsWithOperandsOrThrow(pis[0].ParameterType, left.Type, ExpressionType.Power, name);
                    ValidateParamsWithOperandsOrThrow(pis[1].ParameterType, right.Type, ExpressionType.Power, name);
                    return b;
                }
            }

            return GetMethodBasedBinaryOperator(ExpressionType.Power, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents raising an expression to a power and assigning the result back to the expression.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.PowerAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression PowerAssign(Expression left, Expression right)
        {
            return PowerAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents raising an expression to a power and assigning the result back to the expression.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.PowerAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression PowerAssign(Expression left, Expression right, MethodInfo method)
        {
            return PowerAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents raising an expression to a power and assigning the result back to the expression.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.PowerAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression PowerAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                method = MathPowDoubleDouble;
                if (method == null)
                {
                    throw Error.BinaryOperatorNotDefined(ExpressionType.PowerAssign, left.Type, right.Type);
                }
            }
            return GetMethodBasedAssignOperator(ExpressionType.PowerAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise right-shift operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.RightShift"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression RightShift(Expression left, Expression right)
        {
            return RightShift(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an bitwise right-shift operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.RightShift"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression RightShift(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (IsSimpleShift(left.Type, right.Type))
                {
                    var resultType = GetResultTypeOfShift(left.Type, right.Type);
                    return new SimpleBinaryExpression(ExpressionType.RightShift, left, right, resultType);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.RightShift, "op_RightShift", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.RightShift, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise right-shift assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.RightShiftAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression RightShiftAssign(Expression left, Expression right)
        {
            return RightShiftAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise right-shift assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.RightShiftAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression RightShiftAssign(Expression left, Expression right, MethodInfo method)
        {
            return RightShiftAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a bitwise right-shift assignment operation.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.RightShiftAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression RightShiftAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (IsSimpleShift(left.Type, right.Type))
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    var resultType = GetResultTypeOfShift(left.Type, right.Type);
                    return new SimpleBinaryExpression(ExpressionType.RightShiftAssign, left, right, resultType);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.RightShiftAssign, "op_RightShift", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.RightShiftAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic subtraction operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Subtract"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression Subtract(Expression left, Expression right)
        {
            return Subtract(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic subtraction operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.Subtract"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression Subtract(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.Subtract, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.Subtract, "op_Subtraction", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.Subtract, left, right, method, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a subtraction assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractAssign"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression SubtractAssign(Expression left, Expression right)
        {
            return SubtractAssign(left, right, method: null, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a subtraction assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression SubtractAssign(Expression left, Expression right, MethodInfo method)
        {
            return SubtractAssign(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a subtraction assignment operation that does not have overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractAssign"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression SubtractAssign(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.SubtractAssign, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.SubtractAssign, "op_Subtraction", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.SubtractAssign, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a subtraction assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression SubtractAssignChecked(Expression left, Expression right)
        {
            return SubtractAssignChecked(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a subtraction assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression SubtractAssignChecked(Expression left, Expression right, MethodInfo method)
        {
            return SubtractAssignChecked(left, right, method, conversion: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents a subtraction assignment operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <param name="conversion">A <see cref="LambdaExpression"/> to set the <see cref="BinaryExpression.Conversion"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractAssignChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, <see cref="BinaryExpression.Method"/>,
        /// and <see cref="BinaryExpression.Conversion"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression SubtractAssignChecked(Expression left, Expression right, MethodInfo method, LambdaExpression conversion)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            RequiresCanWrite(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    // conversion is not supported for binary ops on arithmetic types without operator overloading
                    if (conversion != null)
                    {
                        throw Error.ConversionIsNotSupportedForArithmeticTypes();
                    }
                    return new SimpleBinaryExpression(ExpressionType.SubtractAssignChecked, left, right, left.Type);
                }
                return GetUserDefinedAssignOperatorOrThrow(ExpressionType.SubtractAssignChecked, "op_Subtraction", left, right, conversion, liftToNull: true);
            }
            return GetMethodBasedAssignOperator(ExpressionType.SubtractAssignChecked, left, right, method, conversion, liftToNull: true);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic subtraction operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractChecked"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression SubtractChecked(Expression left, Expression right)
        {
            return SubtractChecked(left, right, method: null);
        }

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents an arithmetic subtraction operation that has overflow checking.
        /// </summary>
        /// <param name="left">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="right">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <param name="method">A <see cref="MethodInfo"/> to set the <see cref="BinaryExpression.Method"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.SubtractChecked"/>
        /// and the <see cref="BinaryExpression.Left"/>, <see cref="BinaryExpression.Right"/>, and <see cref="BinaryExpression.Method"/> properties set to the specified values.
        /// </returns>
        public static BinaryExpression SubtractChecked(Expression left, Expression right, MethodInfo method)
        {
            ExpressionUtils.RequiresCanRead(left, nameof(left));
            ExpressionUtils.RequiresCanRead(right, nameof(right));
            if (method == null)
            {
                if (left.Type == right.Type && left.Type.IsArithmetic())
                {
                    return new SimpleBinaryExpression(ExpressionType.SubtractChecked, left, right, left.Type);
                }
                return GetUserDefinedBinaryOperatorOrThrow(ExpressionType.SubtractChecked, "op_Subtraction", left, right, liftToNull: true);
            }
            return GetMethodBasedBinaryOperator(ExpressionType.SubtractChecked, left, right, method, liftToNull: true);
        }

        private static Type GetResultTypeOfShift(Type left, Type right)
        {
            if (!left.IsNullable() && right.IsNullable())
            {
                // lift the result type to Nullable<T>
                return typeof(Nullable<>).MakeGenericType(left);
            }
            return left;
        }

        private static bool IsSimpleShift(Type left, Type right)
        {
            return left.IsInteger()
                && right.GetNonNullable() == typeof(int);
        }

        private static void ValidateOpAssignConversionLambda(LambdaExpression conversion, Expression left, MethodInfo method, ExpressionType nodeType)
        {
            var delegateType = conversion.Type;
            Debug.Assert(typeof(MulticastDelegate).IsAssignableFrom(delegateType) && delegateType != typeof(MulticastDelegate));
            var mi = delegateType.GetInvokeMethod();
            var pms = mi.GetParameters();
            Debug.Assert(pms.Length == conversion.ParameterCount);
            if (pms.Length != 1)
            {
                throw Error.IncorrectNumberOfMethodCallArguments(conversion, nameof(conversion));
            }
            if (!TypeUtils.AreEquivalent(mi.ReturnType, left.Type))
            {
                throw Error.OperandTypesDoNotMatchParameters(nodeType, conversion.ToString());
            }
            Debug.Assert(method != null);
            // The parameter type of conversion lambda must be the same as the return type of the overload method
            if (!TypeUtils.AreEquivalent(pms[0].ParameterType, method.ReturnType))
            {
                throw Error.OverloadOperatorTypeDoesNotMatchConversionType(nodeType, conversion.ToString());
            }
        }

        #endregion Arithmetic Expressions

        #region ArrayIndex Expression

        /// <summary>
        /// Creates a <see cref="BinaryExpression"/> that represents applying an array index operator to an array of rank one.
        /// </summary>
        /// <param name="array">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Left"/> property equal to.</param>
        /// <param name="index">An <see cref="Expression"/> to set the <see cref="BinaryExpression.Right"/> property equal to.</param>
        /// <returns>A <see cref="BinaryExpression"/> that has the <see cref="NodeType"/> property equal to <see cref="ExpressionType.ArrayIndex"/>
        /// and the <see cref="BinaryExpression.Left"/> and <see cref="BinaryExpression.Right"/> properties set to the specified values.</returns>
        public static BinaryExpression ArrayIndex(Expression array, Expression index)
        {
            ExpressionUtils.RequiresCanRead(array, nameof(array));
            ExpressionUtils.RequiresCanRead(index, nameof(index));
            if (index.Type != typeof(int))
            {
                throw Error.ArgumentMustBeArrayIndexType(nameof(index));
            }

            var arrayType = array.Type;
            if (!arrayType.IsArray)
            {
                throw Error.ArgumentMustBeArray(nameof(array));
            }
            if (arrayType.GetArrayRank() != 1)
            {
                throw Error.IncorrectNumberOfIndexes();
            }

            return new SimpleBinaryExpression(ExpressionType.ArrayIndex, array, index, arrayType.GetElementType());
        }

        #endregion ArrayIndex Expression
    }

    // Optimized assignment node, only holds onto children
    internal class AssignBinaryExpression : BinaryExpression
    {
        internal AssignBinaryExpression(Expression left, Expression right)
            : base(left, right)
        {
        }

        public sealed override ExpressionType NodeType => ExpressionType.Assign;

        public sealed override Type Type => Left.Type;

        internal virtual bool IsByRef => false;

        public static AssignBinaryExpression Make(Expression left, Expression right, bool byRef)
        {
            if (byRef)
            {
                return new ByRefAssignBinaryExpression(left, right);
            }

            return new AssignBinaryExpression(left, right);
        }
    }

    internal class ByRefAssignBinaryExpression : AssignBinaryExpression
    {
        internal ByRefAssignBinaryExpression(Expression left, Expression right)
            : base(left, right)
        {
        }

        internal override bool IsByRef => true;
    }

    // Coalesce with conversion
    // This is not a frequently used node, but rather we want to save every
    // other BinaryExpression from holding onto the null conversion lambda
    internal sealed class CoalesceConversionBinaryExpression : BinaryExpression
    {
        private readonly LambdaExpression _conversion;

        internal CoalesceConversionBinaryExpression(Expression left, Expression right, LambdaExpression conversion)
            : base(left, right)
        {
            _conversion = conversion;
        }

        public override ExpressionType NodeType => ExpressionType.Coalesce;

        public override Type Type => Right.Type;

        internal override LambdaExpression GetConversion() => _conversion;
    }

    // Optimized representation of simple logical expressions:
    // && || == != > < >= <=
    internal sealed class LogicalBinaryExpression : BinaryExpression
    {
        internal LogicalBinaryExpression(ExpressionType nodeType, Expression left, Expression right)
            : base(left, right)
        {
            NodeType = nodeType;
        }

        public override ExpressionType NodeType { get; }
        public override Type Type => typeof(bool);
    }

    // Class that handles binary expressions with a method
    // If needed, it can be optimized even more (often Type == method.ReturnType)
    internal class MethodBinaryExpression : SimpleBinaryExpression
    {
        private readonly MethodInfo _method;

        internal MethodBinaryExpression(ExpressionType nodeType, Expression left, Expression right, Type type, MethodInfo method)
            : base(nodeType, left, right, type)
        {
            _method = method;
        }

        internal override MethodInfo GetMethod() => _method;
    }

    // OpAssign with conversion
    // This is not a frequently used node, but rather we want to save every
    // other BinaryExpression from holding onto the null conversion lambda
    internal sealed class OpAssignMethodConversionBinaryExpression : MethodBinaryExpression
    {
        private readonly LambdaExpression _conversion;

        internal OpAssignMethodConversionBinaryExpression(ExpressionType nodeType, Expression left, Expression right, Type type, MethodInfo method, LambdaExpression conversion)
            : base(nodeType, left, right, type, method)
        {
            _conversion = conversion;
        }

        internal override LambdaExpression GetConversion() => _conversion;
    }

    // Class that handles most binary expressions
    // If needed, it can be optimized even more (often Type == left.Type)
    internal class SimpleBinaryExpression : BinaryExpression
    {
        internal SimpleBinaryExpression(ExpressionType nodeType, Expression left, Expression right, Type type)
            : base(left, right)
        {
            NodeType = nodeType;
            Type = type;
        }

        public sealed override ExpressionType NodeType { get; }

        public sealed override Type Type { get; }
    }
}

#endif