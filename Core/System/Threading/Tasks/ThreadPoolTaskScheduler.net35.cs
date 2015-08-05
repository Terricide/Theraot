﻿#if NET20 || NET30 || NET35

// ==++==
//
//   Copyright (c) Microsoft Corporation.  All rights reserved.
// 
// ==--==
// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
// TaskScheduler.cs
//
// <OWNER>[....]</OWNER>
//
// This file contains the primary interface and management of tasks and queues.  
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-

using System.Security;
using System.Diagnostics.Contracts;
using System.Collections.Generic;
using Theraot.Collections.ThreadSafe;

namespace System.Threading.Tasks
{
    /// <summary>
    /// An implementation of TaskScheduler that uses the ThreadPool scheduler
    /// </summary>
    internal sealed class ThreadPoolTaskScheduler : TaskScheduler
    {
        private static readonly SafeQueue<Task> _longRunningTasks = new SafeQueue<Task>();

        // static delegate for threads allocated to handle LongRunning tasks.
        private static readonly ThreadStart _longRunningThreadWork = LongRunningThreadWork;
        private static readonly WaitCallback _executeCallback = TaskExecuteCallback;

        private static void TaskExecuteCallback(object obj)
        {
            var task = obj as Task;
            if (task != null)
            {
                task.ExecuteEntry(true);
            }
        }

        private static void LongRunningThreadWork()
        {
            Task task;
            if (_longRunningTasks.TryTake(out task))
            {
                task.ExecuteEntry(false);
            }
            else
            {
                Contract.Assert(false, "TaskScheduler.LongRunningThreadWork: no task to run");
            }
        }

        /// <summary>
        /// Schedules a task to the ThreadPool.
        /// </summary>
        /// <param name="task">The task to schedule.</param>
        [SecurityCritical]
        protected internal override void QueueTask(Task task)
        {
            if ((task.CreationOptions & TaskCreationOptions.LongRunning) != 0)
            {
                _longRunningTasks.Add(task);
                // Run LongRunning tasks on their own dedicated thread.
                var thread = new Thread(_longRunningThreadWork)
                {
                    IsBackground = true // Keep this thread from blocking process shutdown
                };
                thread.Start();
            }
            else
            {
                // TODO: TaskCreationOptions.PreferFairness
                ThreadPool.QueueUserWorkItem(_executeCallback, task);
            }
        }

        /// <summary>
        /// This internal function will do this:
        ///   (1) If the task had previously been queued, attempt to pop it and return false if that fails.
        ///   (2) Propagate the return value from Task.ExecuteEntry() back to the caller.
        /// 
        /// IMPORTANT NOTE: TryExecuteTaskInline will NOT throw task exceptions itself. Any wait code path using this function needs
        /// to account for exceptions that need to be propagated, and throw themselves accordingly.
        /// </summary>
        [SecurityCritical]
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            if ((task.CreationOptions & TaskCreationOptions.LongRunning) != 0)
            {
                // LongRunning task are going to run on a dedicated Thread.
                return false;
            }
            // Propagate the return value of Task.ExecuteEntry()
            bool result;
            try
            {
                result = task.ExecuteEntry(true); // handles switching Task.Current etc.
            }
            finally
            {
                //   Only call NWIP() if task was previously queued
                if (taskWasPreviouslyQueued) NotifyWorkItemProgress();
            }
            return result;
        }

        [SecurityCritical]
        protected internal override bool TryDequeue(Task task)
        {
            throw new Theraot.Core.InternalSpecialCancelException("ThreadPool");
        }

        [SecurityCritical]
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            // TODO?
            yield break;
        }

        /// <summary>
        /// Notifies the scheduler that work is progressing (no-op).
        /// </summary>
        internal override void NotifyWorkItemProgress()
        {
            // TODO?
        }

        /// <summary>
        /// This is the only scheduler that returns false for this property, indicating that the task entry codepath is unsafe (CAS free)
        /// since we know that the underlying scheduler already takes care of atomic transitions from queued to non-queued.
        /// </summary>
        internal override bool RequiresAtomicStartTransition
        {
            get
            {
                return false;
            }
        }
    }
}

#endif