﻿using System;
using System.Collections.Generic;
using System.Reflection;
using AzureTables;
using Microsoft.WindowsAzure.StorageClient;

namespace Microsoft.WindowsAzure.Jobs
{
    // Support local execution. This does not have a trigger service, but still maintains all of the logging, prereqs, and causality.
    // Exposes some of the logging objects so that callers can monitor what happened. 
    internal class LocalExecutionContext : ICall
    {
        private readonly IConfiguration _config;
        private readonly IPrereqManager _prereq;
        private readonly IQueueFunction _queueFunction;
        private readonly IActivateFunction _activator;
        private readonly IFunctionInstanceLookup _lookup;
        private readonly IFunctionUpdatedLogger _functionUpdate;
        private readonly ICausalityLogger _causalityLogger;
        private readonly ICausalityReader _causalityReader;

        private readonly Func<MethodInfo, FunctionDefinition> _fpResolveFuncDefinition;
        private readonly Func<string, MethodInfo> _fpResolveMethod;
        private readonly Func<string, CloudBlob> _fpResolveBlobs;

        public ICausalityReader CausalityReader
        {
            get
            {
                return _causalityReader;
            }
        }

        public IFunctionInstanceLookup FunctionInstanceLookup
        {
            get
            {
                return _lookup;
            }
        }

        public IQueueFunction QueueFunction
        {
            get
            {
                return _queueFunction;
            }
        }

        // Expose to allow callers to hook in new binders. 
        public IConfiguration Configuration
        {
            get
            {
                return _config;
            }
        }

        // Need Config that binds ICall back to invoke here. 
        private static IConfiguration CreateConfig(ICall call, Type scope)
        {
            IConfiguration config = RunnerProgram.InitBinders();

            CallBinderProvider.Insert(config, call);

            if (scope != null)
            {
                RunnerProgram.ApplyHooks(scope, config);
            }

            JobHostContext.AddCustomerBinders(config, new Type[] { scope } );

            return config;
        }

        public LocalExecutionContext(string accountConnectionString, Type scope)
        {
            var account = CloudStorageAccount.Parse(accountConnectionString);

            _config = CreateConfig(this, scope);

            // These resolution functions are "open". We lazily resolve to any method on the scope type. 
            _fpResolveMethod = name => Resolve(scope, name); // string-->MethodInfo
            _fpResolveFuncDefinition = method => Resolve(account, _config, method); // MethodInfo-->FunctionDefinition

            _fpResolveBlobs = blobPath => new CloudBlobPath(blobPath).Resolve(account);

            {
                var x = new LocalFunctionLogger();
                _functionUpdate = x;
                _lookup = x;
            }

            IAzureTable prereqTable = AzureTable.NewInMemory();
            IAzureTable successorTable = AzureTable.NewInMemory();

            _prereq = new PrereqManager(prereqTable, successorTable, _lookup);

            {
                IAzureTable<TriggerReasonEntity> table = AzureTable<TriggerReasonEntity>.NewInMemory();
                var x = new CausalityLogger(table, _lookup);
                _causalityLogger = x;
                _causalityReader = x;
            }

            var qi = new QueueInterfaces
            {
                AccountInfo = new AccountInfo(), // For webdashboard. NA in local case
                Logger = _functionUpdate,
                Lookup = _lookup,
                PrereqManager = _prereq,
                CausalityLogger = _causalityLogger
            };

            var y = new LocalQueue(qi, this);
            _queueFunction = y;
            _activator = y;
        }

        private static MethodInfo Resolve(Type scope, string functionShortName)
        {
            MethodInfo method = scope.GetMethod(functionShortName, BindingFlags.Static | BindingFlags.Public);
            if (method == null)
            {
                string msg = string.Format("Can't resolve function '{0}' in type '{1}", functionShortName, scope.FullName);
                throw new InvalidOperationException(msg);
            }
            return method;
        }

        private static FunctionDefinition Resolve(CloudStorageAccount account, IConfiguration config, MethodInfo method)
        {
            LocalFunctionTable store = new LocalFunctionTable(account);
            Indexer i = new Indexer(store);

            IndexTypeContext ctx = new IndexTypeContext { Config = config };
            i.IndexMethod(store.OnApplyLocationInfo, method, ctx);

            IFunctionTable functionTable = store;
            var funcs = functionTable.ReadAll();

            if (funcs.Length == 0)
            {
                string msg = string.Format("Function '{0}' is not found. Is it missing Azure Jobs attributes?", method);
                throw new InvalidOperationException(msg);
            }

            FunctionDefinition func = funcs[0];
            return func;
        }

        private FunctionDefinition ResolveFunctionDefinition(string functionName)
        {
            var methodInfo = _fpResolveMethod(functionName);
            return ResolveFunctionDefinition(methodInfo);
        }

        private FunctionDefinition ResolveFunctionDefinition(MethodInfo methodInfo)
        {
            return _fpResolveFuncDefinition(methodInfo);
        }

        // Direct call from outside of a simple batch function. No current function guid. 
        IFunctionToken ICall.QueueCall(string functionName, object arguments, IEnumerable<IFunctionToken> prereqs)
        {
            var prereqs2 = CallUtil.Unwrap(prereqs);
            var guid = Call(functionName, arguments, prereqs2);
            return new SimpleFunctionToken(guid);
        }

        public Guid Call(MethodInfo method, object arguments = null, IEnumerable<Guid> prereqs = null)
        {
            var args2 = ObjectBinderHelpers.ConvertObjectToDict(arguments);

            var guid = Call(method, args2, prereqs);
            return guid;
        }

        public Guid Call(string functionName, object arguments = null, IEnumerable<Guid> prereqs = null)
        {
            var args2 = ObjectBinderHelpers.ConvertObjectToDict(arguments);
            MethodInfo method = _fpResolveMethod(functionName);

            var guid = Call(method, args2, prereqs);
            return guid;
        }

        // If no prereqs, can run immediately. 
        public Guid Call(MethodInfo method, IDictionary<string, string> parameters = null, IEnumerable<Guid> prereqs = null)
        {
            FunctionDefinition func = ResolveFunctionDefinition(method);
            FunctionInvokeRequest instance = Worker.GetFunctionInvocation(func, parameters, prereqs);

            Guid guidThis = CallUtil.GetParentGuid(parameters);
            return CallInner(instance, guidThis);
        }

        private Guid CallInner(FunctionInvokeRequest instance)
        {
            return CallInner(instance, Guid.Empty);
        }
        private Guid CallInner(FunctionInvokeRequest instance, Guid parentGuid)
        {
            instance.TriggerReason = new InvokeTriggerReason
            {
                Message = "Local invoke",
                ParentGuid = parentGuid
            };

            var logItem = _queueFunction.Queue(instance);
            var guid = logItem.FunctionInstance.Id;

            return guid;
        }

        public Guid CallOnBlob(string functionName, string blobPath)
        {
            CloudBlob blobInput = _fpResolveBlobs(blobPath);
            FunctionDefinition func = ResolveFunctionDefinition(functionName);
            FunctionInvokeRequest instance = Worker.GetFunctionInvocation(func, blobInput);

            return CallInner(instance);
        }

        class LocalQueue : QueueFunctionBase, IActivateFunction
        {
            private readonly LocalExecutionContext _parent;

            public LocalQueue(QueueInterfaces interfaces, LocalExecutionContext parent)
                : base(interfaces)
            {
                _parent = parent;
            }

            protected override void Work(ExecutionInstanceLogEntity logItem)
            {
                RunnerProgram runner = RunnerProgram.Create(logItem.FunctionInstance);

                // Run the function. 
                // The config is what will have the ICall binder that ultimately points back to this object. 
                try
                {
                    runner.Invoke(logItem.FunctionInstance, _parent._config);
                }
                catch (Exception e)
                {
                    logItem.ExceptionType = e.GetType().FullName;
                    logItem.ExceptionMessage = e.Message;
                }

                // Mark this function as done executing. $$$ Merge with ExecutionBase?
                logItem.EndTime = DateTime.UtcNow;
                _parent._functionUpdate.Log(logItem);

                // Now execute any successors that were queued up. 
                var guid = logItem.FunctionInstance.Id;
                _parent._prereq.OnComplete(guid, _parent._activator);
            }
        }
    }
}