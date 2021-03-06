﻿#if LESSTHAN_NET35
extern alias nunitlinq;
#endif

//
// ExpressionTest_Call.cs
//
// Author:
//   Federico Di Gregorio <fog@initd.org>
//   Jb Evain (jbevain@novell.com)
//
// (C) 2008 Novell, Inc. (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using Theraot;

namespace MonoTests.System.Linq.Expressions
{
    [TestFixture]
    public partial class ExpressionTestCall
    {
        private static bool _fOutCalled;

        public static void AcceptsIEnumerable(IEnumerable<object> o)
        {
            No.Op(o);
        }

        public static void AGenericMethod<TX, TY>(string foo, int bar)
        {
            // Used via Reflection
            No.Op(foo);
            No.Op(bar);
            No.Op(typeof(TX));
            No.Op(typeof(TY));
        }

        public static string DoAnotherThing(ref int a, string s)
        {
            return s + a;
        }

        public static int DoSomethingWith(ref int a)
        {
            return a + 4;
        }

        public static int FooOut(out int x)
        {
            _fOutCalled = true;
            return x = 0;
        }

        public static int FooOut2(out int x)
        {
            x = 2;
            return 3;
        }

        public static void FooRef(ref string s)
        {
            No.Op(s);
        }

        public static object Identity(object o)
        {
            return o;
        }

        public static int OneStaticMethod()
        {
            return 42;
        }

        public static int Thing()
        {
            return 42;
        }

        [Test]
        public void Arg1Null()
        {
            Assert.Throws<ArgumentNullException>(() => Expression.Call((Type)null, "TestMethod", null, Expression.Constant(1)));
        }

        [Test]
        public void Arg2Null()
        {
            Assert.Throws<ArgumentNullException>(() => Expression.Call(typeof(MemberClass), null, null, Expression.Constant(1)));
        }

        [Test]
        public void Arg4WrongType()
        {
            Assert.Throws<InvalidOperationException>(() => Expression.Call(typeof(MemberClass), nameof(StaticMethod), null, Expression.Constant(true)));
        }

        [Test]
        [Category("NotDotNet")]
        public void ArgInstanceNullForNonStaticMethod() // Passing on .NET 2.0, .3.0, .4.0 and .4.5 Failing on .NET 3.5
        {
            Assert.Throws<ArgumentException>(() => Expression.Call(null, typeof(object).GetMethod("ToString")));
        }

        [Test]
        public void ArgMethodNull()
        {
            Assert.Throws<ArgumentNullException>(() => Expression.Call(Expression.Constant(new object()), null));
        }

        [Test]
        public void CallAsQueryable() // #537768
        {
            var constant = Expression.Constant
            (
                new List<string>(),
                typeof(IEnumerable<string>)
            );

            var call = Expression.Call
            (
                typeof(Queryable),
                "AsQueryable",
                new[] { typeof(string) },
                constant
            );

            Assert.IsNotNull(call);
            Assert.AreEqual(1, call.Arguments.Count);
            Assert.AreEqual(constant, call.Arguments[0]);

            var method = call.Method;

            Assert.AreEqual("AsQueryable", method.Name);
            Assert.IsTrue(method.IsGenericMethod);
            Assert.AreEqual(typeof(string), method.GetGenericArguments()[0]);
        }

        [Test]
        public void CallIQueryableMethodWithNewArrayBoundExpression() // #2304
        {
            Expression.Call
            (
                GetType().GetMethod(nameof(AcceptsIEnumerable), BindingFlags.Public | BindingFlags.Static),
                Expression.NewArrayBounds(typeof(object), Expression.Constant(0))
            );
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void CallMethodOnStruct()
        {
            var param = Expression.Parameter(typeof(AStruct), "s");
            var compiled = Expression.Lambda<Func<AStruct, string>>
            (
                Expression.Call(param, typeof(AStruct).GetMethod(nameof(AStruct.GetValue))), param
            ).Compile();

            var s = new AStruct("foo");
            Assert.AreEqual("foo", compiled(s));
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void CallNullableGetValueOrDefault() // #568989
        {
            var value = Expression.Parameter(typeof(int?), "value");
            var defaultParameter = Expression.Parameter(typeof(int), "default");

            var compiled = Expression.Lambda<Func<int?, int, int>>
            (
                Expression.Call
                (
                    value,
                    "GetValueOrDefault",
                    ArrayEx.Empty<Type>(),
                    defaultParameter
                ),
                value,
                defaultParameter
            ).Compile();

            Assert.AreEqual(2, compiled(null, 2));
            Assert.AreEqual(4, compiled(4, 2));
        }

        [Test]
        public void CallQueryableSelect() // #536637
        {
            var parameter = Expression.Parameter(typeof(string), "s");
            var stringLength = Expression.Property(parameter, typeof(string).GetProperty("Length"));
            var lambda = Expression.Lambda(stringLength, parameter);

            var strings = new[] { "1", "22", "333" };

            var call = Expression.Call
            (
                typeof(Queryable),
                "Select",
                new[] { typeof(string), typeof(int) },
                Expression.Constant(strings.AsQueryable()),
                lambda
            );

            Assert.IsNotNull(call);

            var method = call.Method;

            Assert.AreEqual("Select", method.Name);
            Assert.IsTrue(method.IsGenericMethod);
            Assert.AreEqual(typeof(string), method.GetGenericArguments()[0]);
            Assert.AreEqual(typeof(int), method.GetGenericArguments()[1]);
        }

        [Test]
        public void CallQueryableWhere()
        {
            var queryable = new[] { 1, 2, 3 }.AsQueryable();

            var parameter = Expression.Parameter(typeof(int), "i");
            var lambda = Expression.Lambda<Func<int, bool>>
            (
                Expression.LessThan(parameter, Expression.Constant(2)),
                parameter
            );

            var selector = Expression.Quote(lambda);

            var call = Expression.Call
            (
                typeof(Queryable),
                "Where",
                new[] { typeof(int) },
                queryable.Expression,
                selector
            );

            Assert.IsNotNull(call);
            Assert.IsNotNull(call.Method);
        }

        [Test]
        [Category("NotDotNet")] // http://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=339351
        public void CallStaticMethodOnNonSenseInstanceExpression()
        {
            Assert.Throws<ArgumentException>
            (
                () => Expression.Call
                (
                    Expression.Constant("la la la"),
                    GetType().GetMethod(nameof(OneStaticMethod))
                )
            );
        }

        [Test]
        [Category("NotDotNet")] // http://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=339351
        public void CallStaticMethodWithInstanceArgument()
        {
            Assert.Throws<ArgumentException>
            (
                () => Expression.Call
                (
                    Expression.Parameter(GetType(), "t"),
                    GetType().GetMethod(nameof(Identity)),
                    Expression.Constant(null)
                )
            );
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void CallStaticMethodWithRefParameter()
        {
            var p = Expression.Parameter(typeof(int), "i");

            var compiled = Expression.Lambda<Func<int, int>>
            (
                Expression.Call(GetType().GetMethod(nameof(DoSomethingWith)), p), p
            ).Compile();

            Assert.AreEqual(42, compiled(38));
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void CallStaticMethodWithRefParameterAndOtherParameter()
        {
            var i = Expression.Parameter(typeof(int), "i");
            var s = Expression.Parameter(typeof(string), "s");

            var compiled = Expression.Lambda<Func<int, string, string>>
            (
                Expression.Call(GetType().GetMethod(nameof(DoAnotherThing)), i, s), i, s
            ).Compile();

            Assert.AreEqual("foo42", compiled(42, "foo"));
        }

        [Test]
        public void CallStringIsNullOrEmpty()
        {
            var call = Expression.Call(null, typeof(string).GetMethod("IsNullOrEmpty"), Expression.Constant(""));
            Assert.AreEqual("IsNullOrEmpty(\"\")", call.ToString());
        }

        [Test]
        public void CallToString()
        {
            var call = Expression.Call(Expression.Constant(new object()), typeof(object).GetMethod("ToString"));
            Assert.AreEqual("value(System.Object).ToString()", call.ToString());
        }

        [Test]
        public void CallToStringOnEnum() // #625367
        {
            var compiled = Expression.Lambda<Func<string>>
            (
                Expression.Call
                (
                    Expression.Constant(TypeCode.Boolean, typeof(TypeCode)),
                    typeof(object).GetMethod("ToString")
                )
            ).Compile();

            Assert.AreEqual("Boolean", compiled());
        }

        [Test]
        public void CheckTypeArgsIsNotUsedForParameterLookup()
        {
            Assert.Throws<InvalidOperationException>(() => Expression.Call(GetType(), "AMethod", new[] { typeof(string), typeof(int) }, "foo".ToConstant(), 2.ToConstant()));
        }

        [Test]
        public void CheckTypeArgsIsUsedForGenericArguments()
        {
            var m = Expression.Call(GetType(), nameof(AGenericMethod), new[] { typeof(string), typeof(int) }, "foo".ToConstant(), 2.ToConstant());
            Assert.IsNotNull(m.Method);
            Assert.AreEqual($"Void {nameof(AGenericMethod)}[String,Int32](System.String, Int32)", m.Method.ToString());
        }

        [Test]
        public void CompileSimpleInstanceCall()
        {
            var p = Expression.Parameter(typeof(string), "p");
            var lambda = Expression.Lambda<Func<string, string>>
            (
                Expression.Call
                (
                    p, typeof(string).GetMethod("ToString", ArrayEx.Empty<Type>())
                ),
                p
            );

            var compiled = lambda.Compile();

            Assert.AreEqual("foo", compiled("foo"));
            Assert.AreEqual("bar", compiled("bar"));
        }

        [Test]
        public void CompileSimpleStaticCall()
        {
            const string value = "Str";

            var p = Expression.Parameter(typeof(object), "o");
            var lambda = Expression.Lambda<Func<object, object>>(Expression.Call(GetType().GetMethod(nameof(Identity)), p), p);

            var compiled = lambda.Compile();

            Assert.AreEqual(2, compiled(2));
            Assert.AreEqual(value, compiled(value));
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void Connect282702()
        {
            var compiled = Expression.Lambda<Func<Func<int>>>
            (
                Expression.Convert
                (
                    Expression.Call
                    (
                        typeof(Delegate).GetMethod("CreateDelegate", new[] { typeof(Type), typeof(object), typeof(MethodInfo) }),
                        Expression.Constant(typeof(Func<int>), typeof(Type)),
                        Expression.Constant(null, typeof(object)),
                        Expression.Constant(GetType().GetMethod(nameof(Thing)))
                    ),
                    typeof(Func<int>)
                )
            ).Compile();

            Assert.AreEqual(42, compiled().Invoke());
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void Connect282729()
        {
            // test from https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=282729

            var p = Expression.Parameter(typeof(int), "p");
            var compiled = Expression.Lambda<Func<int, int>>
            (
                Expression.Call
                (
                    GetType().GetMethod(nameof(FooOut)),
                    Expression.ArrayIndex
                    (
                        Expression.NewArrayBounds
                        (
                            typeof(int),
                            1.ToConstant()
                        ),
                        0.ToConstant()
                    )
                ),
                p
            ).Compile();

            Assert.AreEqual(0, compiled(0));
            Assert.IsTrue(_fOutCalled);
        }

        [Test]
        [Category("NotWorking")]
        [Category("NotWorkingInterpreter")]
        public void Connect290278()
        {
            // test from https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=290278

            var p = Expression.Parameter(typeof(int[,]), "p");
            var compiled = Expression.Lambda<Func<int[,], int>>
            (
                Expression.Call
                (
                    GetType().GetMethod(nameof(FooOut2)),
                    Expression.ArrayIndex(p, 0.ToConstant(), 0.ToConstant())
                ),
                p
            ).Compile();

            int[,] data = { { 1 } };

            Assert.AreEqual(3, compiled(data));
            Assert.AreEqual(2, data[0, 0]);
        }

        [Test]
        [Category("NotWorkingInterpreter")]
        public void Connect297597()
        {
            // test from https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=297597

            var strings = new string[1];

            var compiled = Expression.Lambda<Action>
            (
                Expression.Call
                (
                    GetType().GetMethod(nameof(FooRef)),
                    Expression.ArrayIndex
                    (
                        Expression.Constant(strings), 0.ToConstant()
                    )
                )
            ).Compile();

            compiled();
        }

        [Test]
        public void InstanceMethod()
        {
            Assert.Throws<InvalidOperationException>(() => Expression.Call(typeof(MemberClass), "TestMethod", null, Expression.Constant(1)));
        }

        [Test]
        public void InstanceTypeDoesNotMatchMethodDeclaringType()
        {
#if MOBILE
            // ensure that String.Intern won't be removed by the linker
            string s = String.Intern (String.Empty);
#endif
            Assert.Throws<ArgumentException>(() => Expression.Call(Expression.Constant(1), typeof(string).GetMethod("Intern")));
        }

        [Test]
        public void MethodArgumentCountDoesNotMatchParameterLength()
        {
            Assert.Throws<ArgumentException>(() => Expression.Call(Expression.Constant(new object()), typeof(object).GetMethod("ToString"), Expression.Constant(new object())));
        }

        [Test]
        public void MethodArgumentDoesNotMatchParameterType()
        {
            Assert.Throws<ArgumentException>(() => Expression.Call(Expression.New(typeof(Foo)), typeof(Foo).GetMethod(nameof(Foo.Bar)), Expression.Constant(42)));
        }

        [Test]
        public void MethodHasNullArgument()
        {
            Assert.Throws<ArgumentNullException>(() => Expression.Call(Expression.New(typeof(Foo)), typeof(Foo).GetMethod(nameof(Foo.Bar)), null as Expression));
        }

        [Test]
        public void StaticGenericMethod()
        {
            Expression.Call(typeof(MemberClass), nameof(MemberClass.StaticGenericMethod), new[] { typeof(int) }, Expression.Constant(1));
        }

        [Test]
        public void StaticMethod()
        {
            Expression.Call(typeof(MemberClass), nameof(MemberClass.StaticMethod), null, Expression.Constant(1));
        }

        private struct AStruct
        {
            private readonly string _value;

            public AStruct(string value)
            {
                _value = value;
            }

            // Used via reflection
            public string GetValue()
            {
                return _value;
            }
        }

        public class Foo
        {
            public void Bar(string s)
            {
                No.Op(s);
            }
        }
    }

    public partial class ExpressionTestCall
    {
#if TARGETS_NET || GREATERTHAN_NETCOREAPP11 || GREATERTHAN_NETSTANDARD16

        [Test]
        [Category("NotDotNet")] // https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=319190
        public void Connect319190()
        {
            var compiled = Expression.Lambda<Func<bool>>
            (
                Expression.TypeIs
                (
                    Expression.New(typeof(TypedReference)),
                    typeof(object)
                )
            ).Compile();

            Assert.IsTrue(compiled());
        }

#endif
    }
}