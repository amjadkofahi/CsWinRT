﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Linq.Expressions;

#pragma warning disable 0169 // The field 'xxx' is never used
#pragma warning disable 0649 // Field 'xxx' is never assigned to, and will always have its default value
#pragma warning disable CA1060

namespace WinRT
{
    using System.Diagnostics;
    using WinRT.Interop;

    internal static class DelegateExtensions
    {
        public static void DynamicInvokeAbi(this System.Delegate del, object[] invoke_params)
        {
            Marshal.ThrowExceptionForHR((int)del.DynamicInvoke(invoke_params));
        }

        public static T AsDelegate<T>(this MulticastDelegate del)
        {
            return Marshal.GetDelegateForFunctionPointer<T>(
                Marshal.GetFunctionPointerForDelegate(del));
        }
    }

    internal class Platform
    {
        [DllImport("api-ms-win-core-com-l1-1-0.dll")]
        internal static extern unsafe int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsContext, ref Guid iid, IntPtr* instance);

        [DllImport("api-ms-win-core-com-l1-1-0.dll")]
        internal static extern int CoDecrementMTAUsage(IntPtr cookie);

        [DllImport("api-ms-win-core-com-l1-1-0.dll")]
        internal static extern unsafe int CoIncrementMTAUsage(IntPtr* cookie);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr moduleHandle);

        [DllImport("kernel32.dll", SetLastError = true, BestFitMapping = false)]
        internal static extern IntPtr GetProcAddress(IntPtr moduleHandle, [MarshalAs(UnmanagedType.LPStr)] string functionName);

        internal static T GetProcAddress<T>(IntPtr moduleHandle)
        {
            IntPtr functionPtr = Platform.GetProcAddress(moduleHandle, typeof(T).Name);
            if (functionPtr == IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
            return Marshal.GetDelegateForFunctionPointer<T>(functionPtr);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LoadLibraryExW([MarshalAs(UnmanagedType.LPWStr)] string fileName, IntPtr fileHandle, uint flags);

        [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
        internal static extern unsafe int RoGetActivationFactory(IntPtr runtimeClassId, ref Guid iid, IntPtr* factory);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString,
                                                  int length,
                                                  IntPtr* hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe int WindowsCreateStringReference(char* sourceString,
                                                  int length,
                                                  IntPtr* hstring_header,
                                                  IntPtr* hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe int WindowsDuplicateString(IntPtr sourceString,
                                                  IntPtr* hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);

        [DllImport("api-ms-win-core-com-l1-1-1.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern unsafe int RoGetAgileReference(uint options, ref Guid iid, IntPtr unknown, IntPtr* agileReference);
    }

    internal struct VftblPtr
    {
        public IntPtr Vftbl;
    }

    internal class DllModule
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int DllGetActivationFactory(
            IntPtr activatableClassId,
            out IntPtr activationFactory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int DllCanUnloadNow();

        readonly string _fileName;
        readonly IntPtr _moduleHandle;
        readonly DllGetActivationFactory _GetActivationFactory;
        readonly DllCanUnloadNow _CanUnloadNow; // TODO: Eventually periodically call

        static readonly string _currentModuleDirectory = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        static Dictionary<string, DllModule> _cache = new System.Collections.Generic.Dictionary<string, DllModule>();

        public static DllModule Load(string fileName)
        {
            lock (_cache)
            {
                DllModule module;
                if (!_cache.TryGetValue(fileName, out module))
                {
                    module = new DllModule(fileName);
                    _cache[fileName] = module;
                }
                return module;
            }
        }

        DllModule(string fileName)
        {
            _fileName = fileName;

            // Explicitly look for module in the same directory as this one, and
            // use altered search path to ensure any dependencies in the same directory are found.
            _moduleHandle = Platform.LoadLibraryExW(System.IO.Path.Combine(_currentModuleDirectory, fileName), IntPtr.Zero, /* LOAD_WITH_ALTERED_SEARCH_PATH */ 8);
#if !NETSTANDARD2_0 && !NETCOREAPP2_0
            if (_moduleHandle == IntPtr.Zero)
            {
                try
                {
                    // Allow runtime to find module in RID-specific relative subfolder
                    _moduleHandle = NativeLibrary.Load(fileName, Assembly.GetExecutingAssembly(), null);
                }
                catch (Exception) { }
            }
#endif
            if (_moduleHandle == IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }

            _GetActivationFactory = Platform.GetProcAddress<DllGetActivationFactory>(_moduleHandle);

            var canUnloadNow = Platform.GetProcAddress(_moduleHandle, "DllCanUnloadNow");
            if (canUnloadNow != IntPtr.Zero)
            {
                _CanUnloadNow = Marshal.GetDelegateForFunctionPointer<DllCanUnloadNow>(canUnloadNow);
            }
        }

        public unsafe (ObjectReference<IActivationFactoryVftbl> obj, int hr) GetActivationFactory(string runtimeClassId)
        {
            IntPtr instancePtr;
            var hstrRuntimeClassId = MarshalString.CreateMarshaler(runtimeClassId);
            int hr = _GetActivationFactory(MarshalString.GetAbi(hstrRuntimeClassId), out instancePtr);
            return (hr == 0 ? ObjectReference<IActivationFactoryVftbl>.Attach(ref instancePtr) : null, hr);
        }

        ~DllModule()
        {
            System.Diagnostics.Debug.Assert(_CanUnloadNow == null || _CanUnloadNow() == 0); // S_OK
            lock (_cache)
            {
                _cache.Remove(_fileName);
            }
            if ((_moduleHandle != IntPtr.Zero) && !Platform.FreeLibrary(_moduleHandle))
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
        }
    }

    internal class WeakLazy<T> where T : class, new()
    {
        WeakReference<T> _instance = new WeakReference<T>(null);
        public T Value
        {
            get
            {
                lock (_instance)
                {
                    T value;
                    if (!_instance.TryGetTarget(out value))
                    {
                        value = new T();
                        _instance.SetTarget(value);
                    }
                    return value;
                }
            }
        }
    }

    internal class WinrtModule
    {
        readonly IntPtr _mtaCookie;
        static Lazy<WinrtModule> _instance = new Lazy<WinrtModule>();
        public static WinrtModule Instance => _instance.Value;

        public unsafe WinrtModule()
        {
            IntPtr mtaCookie;
            Marshal.ThrowExceptionForHR(Platform.CoIncrementMTAUsage(&mtaCookie));
            _mtaCookie = mtaCookie;
        }

        public static unsafe (IntPtr instancePtr, int hr) GetActivationFactory(IntPtr hstrRuntimeClassId)
        {
            var module = Instance; // Ensure COM is initialized
            Guid iid = typeof(IActivationFactoryVftbl).GUID;
            IntPtr instancePtr;
            int hr = Platform.RoGetActivationFactory(hstrRuntimeClassId, ref iid, &instancePtr);
            return (hr == 0 ? instancePtr : IntPtr.Zero, hr);
        }

        public static unsafe (ObjectReference<IActivationFactoryVftbl> obj, int hr) GetActivationFactory(string runtimeClassId)
        {
            // TODO: "using var" with ref struct and remove the try/catch below
            var m = MarshalString.CreateMarshaler(runtimeClassId);
            try
            {
                IntPtr instancePtr;
                int hr;
                (instancePtr, hr) = GetActivationFactory(MarshalString.GetAbi(m));
                return (hr == 0 ? ObjectReference<IActivationFactoryVftbl>.Attach(ref instancePtr) : null, hr);
            }
            finally
            {
                m.Dispose();
            }
        }

        ~WinrtModule()
        {
            Marshal.ThrowExceptionForHR(Platform.CoDecrementMTAUsage(_mtaCookie));
        }
    }

    internal class BaseActivationFactory
    {
        private ObjectReference<IActivationFactoryVftbl> _IActivationFactory;

        public ObjectReference<IActivationFactoryVftbl> Value { get => _IActivationFactory; }

        public I AsInterface<I>() => _IActivationFactory.AsInterface<I>();

        public BaseActivationFactory(string typeNamespace, string typeFullName)
        {
            // Prefer the RoGetActivationFactory HRESULT failure over the LoadLibrary/etc. failure
            int hr;
            (_IActivationFactory, hr) = WinrtModule.GetActivationFactory(typeFullName);
            if (_IActivationFactory != null) { return; }

            var moduleName = typeNamespace;
            while (true)
            {
                try
                {
                    (_IActivationFactory, _) = DllModule.Load(moduleName + ".dll").GetActivationFactory(typeFullName);
                    if (_IActivationFactory != null) { return; }
                }
                catch (Exception) { }

                var lastSegment = moduleName.LastIndexOf(".");
                if (lastSegment <= 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
                moduleName = moduleName.Remove(lastSegment);
            }
        }

        public unsafe ObjectReference<I> _ActivateInstance<I>()
        {
            IntPtr instancePtr;
            Marshal.ThrowExceptionForHR(_IActivationFactory.Vftbl.ActivateInstance(_IActivationFactory.ThisPtr, &instancePtr));
            try
            {
                return ComWrappersSupport.GetObjectReferenceForInterface(instancePtr).As<I>();
            }
            finally
            {
                MarshalInspectable<object>.DisposeAbi(instancePtr);
            }
        }

        public ObjectReference<I> _As<I>() => _IActivationFactory.As<I>();
        public IObjectReference _As(Guid iid) => _IActivationFactory.As<WinRT.Interop.IUnknownVftbl>(iid);
    }

    internal class ActivationFactory<T> : BaseActivationFactory
    {
        public ActivationFactory() : base(typeof(T).Namespace, typeof(T).FullName) { }

        static WeakLazy<ActivationFactory<T>> _factory = new WeakLazy<ActivationFactory<T>>();
        public new static I AsInterface<I>() => _factory.Value.Value.AsInterface<I>();
        public static ObjectReference<I> As<I>() => _factory.Value._As<I>();
        public static IObjectReference As(Guid iid) => _factory.Value._As(iid);
        public static ObjectReference<I> ActivateInstance<I>() => _factory.Value._ActivateInstance<I>();
    }

    internal class ComponentActivationFactory : global::WinRT.Interop.IActivationFactory
    {
        public IntPtr ActivateInstance()
        {
            throw new NotImplementedException();
        }
    }

    internal class ActivatableComponentActivationFactory<T> : ComponentActivationFactory, global::WinRT.Interop.IActivationFactory where T : class, new()
    {
        public new IntPtr ActivateInstance()
        {
            T comp = new T();
            return MarshalInspectable<T>.FromManaged(comp);
        }
    }

#pragma warning disable CA2002

    internal unsafe abstract class EventSource<TDelegate>
        where TDelegate : class, MulticastDelegate
    {
        readonly IObjectReference _obj;
        readonly int _index;
        readonly delegate* unmanaged[Stdcall]<System.IntPtr, System.IntPtr, out WinRT.EventRegistrationToken, int> _addHandler;
        readonly delegate* unmanaged[Stdcall]<System.IntPtr, WinRT.EventRegistrationToken, int> _removeHandler;

        // Registration state, cached separately to survive EventSource garbage collection
        protected class State
        {
            public EventRegistrationToken token;
            public TDelegate del;
            public System.WeakReference<System.Delegate> eventInvoke = new System.WeakReference<System.Delegate>(null);
        }
        protected State _state;

        protected abstract IObjectReference CreateMarshaler(TDelegate del);

        protected abstract IntPtr GetAbi(IObjectReference marshaler);

        protected abstract void DisposeMarshaler(IObjectReference marshaler);

        public void Subscribe(TDelegate del)
        {
            lock (this)
            {
                bool registerHandler = _state.del is null;
                
                _state.del = (TDelegate)global::System.Delegate.Combine(_state.del, del);
                if (registerHandler)
                {
                    var eventInvoke = (TDelegate)EventInvoke;
                    var marshaler = CreateMarshaler(eventInvoke);
                    try
                    {
                        var nativeDelegate = GetAbi(marshaler);
                        ExceptionHelpers.ThrowExceptionForHR(_addHandler(_obj.ThisPtr, nativeDelegate, out _state.token));

                        Cache.AddStateCleaner(_obj.ThisPtr, eventInvoke, _index);
                    }
                    finally
                    {
                        // Dispose our managed reference to the delegate's CCW.
                        // Either the native event holds a reference now or the _addHandler call failed.
                        DisposeMarshaler(marshaler);
                    }
                }
            }
        }

        public void Unsubscribe(TDelegate del)
        {
            lock (this)
            {
                var oldEvent = _state.del;
                _state.del = (TDelegate)global::System.Delegate.Remove(_state.del, del);
                if (oldEvent is object && _state.del is null)
                {
                    _UnsubscribeFromNative();
                }
            }
        }

        protected abstract System.Delegate EventInvoke { get; }

        private class Cache
        {
            private class CacheCleaner
            {
                private IntPtr objPtr;
                private int indexToClean;

                public CacheCleaner(IntPtr objPtr, int indexToClean)
                {
                    this.indexToClean = indexToClean;
                    this.objPtr = objPtr;
                }

                ~CacheCleaner()
                {
                    Cache.Remove(objPtr, indexToClean);
                }
            }

            Cache(IWeakReference target, EventSource<TDelegate> source, int index)
            {
                this.target = target;
                SetState(source, index);
            }

            private IWeakReference target;
            private readonly ConcurrentDictionary<int, EventSource<TDelegate>.State> states = new ConcurrentDictionary<int, EventSource<TDelegate>.State>();
            private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Delegate, CacheCleaner> stateCleaner = new System.Runtime.CompilerServices.ConditionalWeakTable<Delegate, CacheCleaner>();

            private static readonly ReaderWriterLockSlim cachesLock = new ReaderWriterLockSlim();
            private static readonly ConcurrentDictionary<IntPtr, Cache> caches = new ConcurrentDictionary<IntPtr, Cache>();
            

            private Cache Update(IWeakReference target, EventSource<TDelegate> source, int index)
            {
                // If target no longer exists, destroy cache
                lock (this)
                {
                    using var resolved = this.target.Resolve(typeof(IUnknownVftbl).GUID);
                    if (resolved == null)
                    {
                        this.target = target;
                        states.Clear();
                    }
                }
                SetState(source, index);
                return this;
            }

            private void SetState(EventSource<TDelegate> source, int index)
            {
                // If cache exists, use it, else create new
                if (states.ContainsKey(index))
                {
                    source._state = states[index];
                }
                else
                {
                    source._state = new EventSource<TDelegate>.State();
                    states[index] = source._state;
                }
            }

            public static void AddStateCleaner(IntPtr objPtr, Delegate eventInvoke, int index)
            {
                if (caches.TryGetValue(objPtr, out var cache) && !cache.stateCleaner.TryGetValue(eventInvoke, out var _))
                {
                    cache.stateCleaner.Add(eventInvoke, new CacheCleaner(objPtr, index));
                }
            }

            public static void Create(IObjectReference obj, EventSource<TDelegate> source, int index)
            {
                // If event source implements weak reference support, track event registrations so that
                // unsubscribes will work across garbage collections.  Note that most static/factory classes
                // do not implement IWeakReferenceSource, so static codegen caching approach is also used.
                IWeakReference target = null;
                try
                {
#if NETSTANDARD2_0
                    var weakRefSource = (IWeakReferenceSource)typeof(IWeakReferenceSource).GetHelperType().GetConstructor(new[] { typeof(IObjectReference) }).Invoke(new object[] { obj });
#else
                    var weakRefSource = (IWeakReferenceSource)(object)new WinRT.IInspectable(obj);
#endif
                    target = weakRefSource.GetWeakReference();
                }
                catch (Exception)
                {
                    source._state = new EventSource<TDelegate>.State();
                    return;
                }

                cachesLock.EnterReadLock();
                try
                {
                    caches.AddOrUpdate(obj.ThisPtr,
                        (IntPtr ThisPtr) => new Cache(target, source, index),
                        (IntPtr ThisPtr, Cache cache) => cache.Update(target, source, index));
                }
                finally
                {
                    cachesLock.ExitReadLock();
                }
            }

            public static void Remove(IntPtr thisPtr, int index)
            {
                if (caches.TryGetValue(thisPtr, out var cache))
                {
                    cache.states.TryRemove(index, out var _);
                    // using double-checked lock idiom
                    using var resolvedTarget = cache.target.Resolve(typeof(IUnknownVftbl).GUID);
                    if (resolvedTarget == null) 
                    {
                        cachesLock.EnterWriteLock();
                        try
                        {
                            if (cache.states.IsEmpty)
                            {
                                caches.TryRemove(thisPtr, out var _);
                            }
                        }
                        finally
                        {
                            cachesLock.ExitWriteLock();
                        }
                    }
                }
            }
        }

        protected EventSource(IObjectReference obj,
            delegate* unmanaged[Stdcall]<System.IntPtr, System.IntPtr, out WinRT.EventRegistrationToken, int> addHandler,
            delegate* unmanaged[Stdcall]<System.IntPtr, WinRT.EventRegistrationToken, int> removeHandler,
            int index = 0)
        {
            Cache.Create(obj, this, index);
            _obj = obj;
            _index = index;
            _addHandler = addHandler;
            _removeHandler = removeHandler;
        }

        void _UnsubscribeFromNative()
        {
            Cache.Remove(_obj.ThisPtr, _index);
            ExceptionHelpers.ThrowExceptionForHR(_removeHandler(_obj.ThisPtr, _state.token));
            _state.token.Value = 0;
        }
    }

    internal unsafe class EventSource__EventHandler<T> : EventSource<System.EventHandler<T>>
    {

        internal EventSource__EventHandler(IObjectReference obj,
            delegate* unmanaged[Stdcall]<System.IntPtr, System.IntPtr, out WinRT.EventRegistrationToken, int> addHandler,
            delegate* unmanaged[Stdcall]<System.IntPtr, WinRT.EventRegistrationToken, int> removeHandler,
            int index) : base(obj, addHandler, removeHandler, index)
        {
        }

        protected override IObjectReference CreateMarshaler(System.EventHandler<T> del) =>
            del is null ? null : ABI.System.EventHandler<T>.CreateMarshaler(del);

        protected override void DisposeMarshaler(IObjectReference marshaler) =>
            ABI.System.EventHandler<T>.DisposeMarshaler(marshaler);

        protected override IntPtr GetAbi(IObjectReference marshaler) =>
            marshaler is null ? IntPtr.Zero : ABI.System.EventHandler<T>.GetAbi(marshaler);

        protected override System.Delegate EventInvoke
        {
            // This is synchronized from the base class
            get
            {
                if (_state.eventInvoke.TryGetTarget(out var handler) && handler != null)
                {
                    return handler;
                }
                System.EventHandler<T> newHandler = (System.Object obj, T e) =>
                {
                    var localDel = _state.del;
                    if (localDel != null)
                        localDel.Invoke(obj, e);
                };
                _state.eventInvoke.SetTarget(newHandler);
                return newHandler;
            }
        }
    }

#pragma warning restore CA2002

    // An event registration token table stores mappings from delegates to event tokens, in order to support
    // sourcing WinRT style events from managed code.
    internal sealed class EventRegistrationTokenTable<T> where T : class, global::System.Delegate
    {
        // Note this dictionary is also used as the synchronization object for this table
        private readonly Dictionary<EventRegistrationToken, T> m_tokens = new Dictionary<EventRegistrationToken, T>();

        public EventRegistrationToken AddEventHandler(T handler)
        {
            // Windows Runtime allows null handlers.  Assign those the default token (token value 0) for simplicity
            if (handler == null)
            {
                return default;
            }

            lock (m_tokens)
            {
                return AddEventHandlerNoLock(handler);
            }
        }

        private EventRegistrationToken AddEventHandlerNoLock(T handler)
        {
            Debug.Assert(handler != null);

            // Get a registration token, making sure that we haven't already used the value.  This should be quite
            // rare, but in the case it does happen, just keep trying until we find one that's unused.
            EventRegistrationToken token = GetPreferredToken(handler);
            while (m_tokens.ContainsKey(token))
            {
                token = new EventRegistrationToken { Value = token.Value + 1 };
            }
            m_tokens[token] = handler;

            return token;
        }

        // Generate a token that may be used for a particular event handler.  We will frequently be called
        // upon to look up a token value given only a delegate to start from.  Therefore, we want to make
        // an initial token value that is easily determined using only the delegate instance itself.  Although
        // in the common case this token value will be used to uniquely identify the handler, it is not
        // the only possible token that can represent the handler.
        //
        // This means that both:
        //  * if there is a handler assigned to the generated initial token value, it is not necessarily
        //    this handler.
        //  * if there is no handler assigned to the generated initial token value, the handler may still
        //    be registered under a different token
        //
        // Effectively the only reasonable thing to do with this value is either to:
        //  1. Use it as a good starting point for generating a token for handler
        //  2. Use it as a guess to quickly see if the handler was really assigned this token value
        private static EventRegistrationToken GetPreferredToken(T handler)
        {
            Debug.Assert(handler != null);

            // We want to generate a token value that has the following properties:
            //  1. is quickly obtained from the handler instance
            //  2. uses bits in the upper 32 bits of the 64 bit value, in order to avoid bugs where code
            //     may assume the value is really just 32 bits
            //  3. uses bits in the bottom 32 bits of the 64 bit value, in order to ensure that code doesn't
            //     take a dependency on them always being 0.
            //
            // The simple algorithm chosen here is to simply assign the upper 32 bits the metadata token of the
            // event handler type, and the lower 32 bits the hash code of the handler instance itself. Using the
            // metadata token for the upper 32 bits gives us at least a small chance of being able to identify a
            // totally corrupted token if we ever come across one in a minidump or other scenario.
            //
            // The hash code of a unicast delegate is not tied to the method being invoked, so in the case
            // of a unicast delegate, the hash code of the target method is used instead of the full delegate
            // hash code.
            //
            // While calculating this initial value will be somewhat more expensive than just using a counter
            // for events that have few registrations, it will also give us a shot at preventing unregistration
            // from becoming an O(N) operation.
            //
            // We should feel free to change this algorithm as other requirements / optimizations become
            // available.  This implementation is sufficiently random that code cannot simply guess the value to
            // take a dependency upon it.  (Simply applying the hash-value algorithm directly won't work in the
            // case of collisions, where we'll use a different token value).

            uint handlerHashCode;
            global::System.Delegate[] invocationList = ((global::System.Delegate)(object)handler).GetInvocationList();
            if (invocationList.Length == 1)
            {
                handlerHashCode = (uint)invocationList[0].Method.GetHashCode();
            }
            else
            {
                handlerHashCode = (uint)handler.GetHashCode();
            }

            ulong tokenValue = ((ulong)(uint)typeof(T).MetadataToken << 32) | handlerHashCode;
            return new EventRegistrationToken { Value = (long)tokenValue };
        }

        // Remove the event handler from the table and
        // Get the delegate associated with an event registration token if it exists
        // If the event registration token is not registered, returns false
        public bool RemoveEventHandler(EventRegistrationToken token, out T handler)
        {
            lock (m_tokens)
            {
                if (m_tokens.TryGetValue(token, out handler))
                {
                    RemoveEventHandlerNoLock(token);
                    return true;
                }
            }

            return false;
        }

        private void RemoveEventHandlerNoLock(EventRegistrationToken token)
        {
            if (m_tokens.TryGetValue(token, out T handler))
            {
                m_tokens.Remove(token);
            }
        }
    }
}


namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method)]
    internal class ModuleInitializerAttribute : Attribute { }
}

namespace WinRT
{
    using System.Runtime.CompilerServices;
    internal static class ProjectionInitializer
    {
#pragma warning disable 0436
        [ModuleInitializer]
#pragma warning restore 0436
        internal static void InitalizeProjection()
        {
            ComWrappersSupport.RegisterProjectionAssembly(typeof(ProjectionInitializer).Assembly);
        }
    }
}
