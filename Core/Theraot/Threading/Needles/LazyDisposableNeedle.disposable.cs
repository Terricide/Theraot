﻿// <auto-generated />

using System;

using Theraot.Core;
using Theraot.Threading;

namespace Theraot.Threading.Needles
{
    public partial class LazyDisposableNeedle<T> : IDisposable, IExtendedDisposable
    {
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        private int _status;

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        [global::System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralexceptionTypes", Justification = "Pokemon")]
        ~LazyDisposableNeedle()
        {
            try
            {
                //Empty
            }
            finally
            {
                try
                {
                    Dispose(false);
                }
                catch
                {
                    //Pokemon
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        public bool IsDisposed
        {
            [global::System.Diagnostics.DebuggerNonUserCode]
            get
            {
                return _status == -1;
            }
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        public void Dispose()
        {
            try
            {
                Dispose(true);
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        public void DisposedConditional(Action whenDisposed, Action whenNotDisposed)
        {
            if (_status == -1)
            {
                if (whenDisposed != null)
                {
                    whenDisposed.Invoke();
                }
            }
            else
            {
                if (whenNotDisposed != null)
                {
                    ThreadingHelper.SpinWait
                    (
                        () =>
                        {
                            var status = _status;
                            if (status == -1)
                            {
                                return true;
                            }
                            else
                            {
                                if (System.Threading.Interlocked.CompareExchange(ref _status, _status++, status) == status)
                                {
                                    return true;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                        }
                    );
                    if (_status == -1)
                    {
                        if (whenDisposed != null)
                        {
                            whenDisposed.Invoke();
                        }
                    }
                    else
                    {
                        try
                        {
                            whenNotDisposed.Invoke();
                        }
                        finally
                        {
                            System.Threading.Interlocked.Decrement(ref _status);
                        }
                    }
                }
            }
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        public TReturn DisposedConditional<TReturn>(Func<TReturn> whenDisposed, Func<TReturn> whenNotDisposed)
        {
            if (_status == -1)
            {
                if (whenDisposed == null)
                {
                    return default(TReturn);
                }
                else
                {
                    return whenDisposed.Invoke();
                }
            }
            else
            {
                if (whenNotDisposed == null)
                {
                    return default(TReturn);
                }
                else
                {
                    ThreadingHelper.SpinWait
                    (
                        () =>
                        {
                            var status = _status;
                            if (status != -1)
                            {
                                if (System.Threading.Interlocked.CompareExchange(ref _status, _status++, status) == status)
                                {
                                    return true;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                            else
                            {
                                return true;
                            }
                        }
                    );
                    if (_status == -1)
                    {
                        if (whenDisposed == null)
                        {
                            return default(TReturn);
                        }
                        else
                        {
                            return whenDisposed.Invoke();
                        }
                    }
                    else
                    {
                        try
                        {
                            return whenNotDisposed.Invoke();
                        }
                        finally
                        {
                            System.Threading.Interlocked.Decrement(ref _status);
                        }
                    }
                }
            }
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        protected virtual void Dispose(bool disposeManagedResources)
        {
            if (TakeDisposalExecution())
            {
                try
                {
                    if (disposeManagedResources)
                    {
                        this.OnDispose();
                    }
                }
                finally
                {
                    try
                    {
                        //Empty
                    }
                    finally
                    {
                        //Empty
                    }
                }
            }
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        protected void ProtectedCheckDisposed(string exceptionMessegeWhenDisposed)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(exceptionMessegeWhenDisposed);
            }
        }

        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        protected bool TakeDisposalExecution()
        {
            if (_status == -1)
            {
                return false;
            }
            else
            {
                ThreadingHelper.SpinWaitExchange(-1, ref _status, -1, 0);
                if (_status == -1)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        protected void ThrowDisposedexception()
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        protected TReturn ThrowDisposedexception<TReturn>()
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("DisposableTemplate", "1.0.0.0")]
        protected bool UnDispose()
        {
            if (System.Threading.Thread.VolatileRead(ref _status) == -1)
            {
                System.Threading.Thread.VolatileWrite(ref _status, 0);
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}