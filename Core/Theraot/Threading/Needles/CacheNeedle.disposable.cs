﻿// <auto-generated />

using System;

using Theraot.Threading;

namespace Theraot.Threading.Needles
{
    public partial class CacheNeedle<T> : IDisposable, IExtendedDisposable
    {
        [global::System.Diagnostics.DebuggerNonUserCode]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("InheritedDisposableTemplate", "1.0.0.0")]
        protected override void Dispose(bool disposeManagedResources)
        {
            if (TakeDisposalExecution())
            {
                try
                {
                    if (disposeManagedResources)
                    {
                        //Empty
                    }
                }
                finally
                {
                    try
                    {
                        this.UnmanagedDispose();
                    }
                    finally
                    {
                        _valueFactory = null;
                    }
                    base.Dispose(disposeManagedResources);
                }
            }
        }
    }
}