// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;

using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;
using Interlocked = System.Threading.Interlocked;

namespace Internal.IL.Stubs
{
    /// <summary>
    /// Thunk to dynamically invoke a method using reflection. The method accepts an object[] of parameters
    /// to target method, lays them out on the stack, and calls the target method. This thunk has heavy
    /// dependencies on the general dynamic invocation infrastructure in System.InvokeUtils and gets called from there
    /// at runtime. See comments in System.InvokeUtils for a more thorough explanation.
    /// </summary>
    internal partial class DynamicInvokeMethodThunk : ILStubMethod
    {
        private TypeDesc _owningType;
        private DynamicInvokeMethodSignature _targetSignature;

        private TypeDesc[] _instantiation;
        private MethodSignature _signature;

        public DynamicInvokeMethodThunk(TypeDesc owningType, DynamicInvokeMethodSignature signature)
        {
            _owningType = owningType;
            _targetSignature = signature;
        }

        public static bool SupportsThunks(TypeSystemContext context)
        {
            return context.SystemModule.GetType("System", "InvokeUtils", false) != null;
        }

        public override TypeSystemContext Context
        {
            get
            {
                return _owningType.Context;
            }
        }

        public override TypeDesc OwningType
        {
            get
            {
                return _owningType;
            }
        }

        private MetadataType InvokeUtilsType
        {
            get
            {
                return Context.SystemModule.GetKnownType("System", "InvokeUtils");
            }
        }

        private MetadataType ArgSetupStateType
        {
            get
            {
                return InvokeUtilsType.GetNestedType("ArgSetupState");
            }
        }

        public override MethodSignature Signature
        {
            get
            {
                if (_signature == null)
                {
                    _signature = new MethodSignature(
                        MethodSignatureFlags.Static,
                        Instantiation.Length,
                        Context.GetWellKnownType(WellKnownType.Object),
                        new TypeDesc[]
                        {
                            Context.GetWellKnownType(WellKnownType.Object),  // thisPtr
                            Context.GetWellKnownType(WellKnownType.IntPtr),  // methodToCall
                            ArgSetupStateType.MakeByRefType(),               // argSetupState
                            Context.GetWellKnownType(WellKnownType.Boolean), // targetIsThisCall
                        });
                }

                return _signature;
            }
        }

        public override Instantiation Instantiation
        {
            get
            {
                if (_instantiation == null)
                {
                    TypeDesc[] instantiation =
                        new TypeDesc[_targetSignature.HasReturnValue ? _targetSignature.Length + 1 : _targetSignature.Length];

                    for (int i = 0; i < _targetSignature.Length; i++)
                        instantiation[i] = new DynamicInvokeThunkGenericParameter(this, i);

                    if (_targetSignature.HasReturnValue)
                        instantiation[_targetSignature.Length] =
                            new DynamicInvokeThunkGenericParameter(this, _targetSignature.Length);

                    Interlocked.CompareExchange(ref _instantiation, instantiation, null);
                }

                return new Instantiation(_instantiation);
            }
        }

        public override string Name
        {
            get
            {
                StringBuilder sb = new StringBuilder("InvokeRet");

                switch (_targetSignature.ReturnType)
                {
                    case DynamicInvokeMethodParameterKind.None:
                        sb.Append('V');
                        break;
                    case DynamicInvokeMethodParameterKind.Pointer:
                        sb.Append('P');

                        for (int i = 0; i < _targetSignature.GetNumerOfReturnTypePointerIndirections() - 1; i++)
                            sb.Append('p');

                        break;
                    case DynamicInvokeMethodParameterKind.Value:
                        sb.Append('O');
                        break;
                    default:
                        Debug.Fail("Unreachable");
                        break;
                }

                for (int i = 0; i < _targetSignature.Length; i++)
                {
                    switch (_targetSignature[i])
                    {
                        case DynamicInvokeMethodParameterKind.Pointer:
                            sb.Append('P');

                            for (int j = 0; j < _targetSignature.GetNumberOfParameterPointerIndirections(i) - 1; j++)
                                sb.Append('p');

                            break;
                        case DynamicInvokeMethodParameterKind.Reference:
                            sb.Append("R");
                            break;
                        case DynamicInvokeMethodParameterKind.Value:
                            sb.Append("I");
                            break;
                        default:
                            Debug.Fail("Unreachable");
                            break;
                    }
                }

                return sb.ToString();
            }
        }

        public override MethodIL EmitIL()
        {
            ILEmitter emitter = new ILEmitter();
            ILCodeStream argSetupStream = emitter.NewCodeStream();
            ILCodeStream thisCallSiteSetupStream = emitter.NewCodeStream();
            ILCodeStream staticCallSiteSetupStream = emitter.NewCodeStream();

            // This function will look like
            //
            // !For each parameter to the method
            //    !if (parameter is In Parameter)
            //       localX is TypeOfParameterX&
            //       ldtoken TypeOfParameterX
            //       call DynamicInvokeParamHelperIn(RuntimeTypeHandle)
            //       stloc localX
            //    !else
            //       localX is TypeOfParameter
            //       ldtoken TypeOfParameterX
            //       call DynamicInvokeParamHelperRef(RuntimeTypeHandle)
            //       stloc localX

            // ldarg.2
            // call DynamicInvokeArgSetupComplete(ref ArgSetupState)

            // *** Thiscall instruction stream starts here ***

            // ldarg.3 // Load targetIsThisCall
            // brfalse Not_this_call

            // ldarg.0 // Load this pointer
            // !For each parameter
            //    !if (parameter is In Parameter)
            //       ldloc localX
            //       ldobj TypeOfParameterX
            //    !else
            //       ldloc localX
            // ldarg.1
            // calli ReturnType thiscall(TypeOfParameter1, ...)
            // !if ((ReturnType == void)
            //    ldnull
            // !elif (ReturnType is pointer)
            //    System.Reflection.Pointer.Box(ReturnType)
            // !else
            //    box ReturnType
            // ret

            // *** Static call instruction stream starts here ***

            // Not_this_call:
            // !For each parameter
            //    !if (parameter is In Parameter)
            //       ldloc localX
            //       ldobj TypeOfParameterX
            //    !else
            //       ldloc localX
            // ldarg.1
            // calli ReturnType (TypeOfParameter1, ...)
            // !if ((ReturnType == void)
            //    ldnull
            // !elif (ReturnType is pointer)
            //    System.Reflection.Pointer.Box(ReturnType)
            // !else
            //    box ReturnType
            // ret

            ILCodeLabel lStaticCall = emitter.NewCodeLabel();
            thisCallSiteSetupStream.EmitLdArg(3); // targetIsThisCall
            thisCallSiteSetupStream.Emit(ILOpcode.brfalse, lStaticCall);
            staticCallSiteSetupStream.EmitLabel(lStaticCall);

            thisCallSiteSetupStream.EmitLdArg(0); // thisPtr

            ILToken tokDynamicInvokeParamHelperRef =
                emitter.NewToken(InvokeUtilsType.GetKnownMethod("DynamicInvokeParamHelperRef", null));
            ILToken tokDynamicInvokeParamHelperIn =
                emitter.NewToken(InvokeUtilsType.GetKnownMethod("DynamicInvokeParamHelperIn", null));

            TypeDesc[] targetMethodSignature = new TypeDesc[_targetSignature.Length];

            for (int paramIndex = 0; paramIndex < _targetSignature.Length; paramIndex++)
            {
                TypeDesc paramType = Context.GetSignatureVariable(paramIndex, true);
                DynamicInvokeMethodParameterKind paramKind = _targetSignature[paramIndex];

                if (paramKind == DynamicInvokeMethodParameterKind.Pointer)
                    for (int i = 0; i < _targetSignature.GetNumberOfParameterPointerIndirections(paramIndex); i++)
                        paramType = paramType.MakePointerType();

                ILToken tokParamType = emitter.NewToken(paramType);
                ILLocalVariable local = emitter.NewLocal(paramType.MakeByRefType());

                thisCallSiteSetupStream.EmitLdLoc(local);
                staticCallSiteSetupStream.EmitLdLoc(local);

                argSetupStream.Emit(ILOpcode.ldtoken, tokParamType);

                if (paramKind == DynamicInvokeMethodParameterKind.Reference)
                {
                    argSetupStream.Emit(ILOpcode.call, tokDynamicInvokeParamHelperRef);

                    targetMethodSignature[paramIndex] = paramType.MakeByRefType();
                }
                else
                {
                    argSetupStream.Emit(ILOpcode.call, tokDynamicInvokeParamHelperIn);

                    thisCallSiteSetupStream.Emit(ILOpcode.ldobj, tokParamType);
                    staticCallSiteSetupStream.Emit(ILOpcode.ldobj, tokParamType);

                    targetMethodSignature[paramIndex] = paramType;
                }
                argSetupStream.EmitStLoc(local);
            }

            argSetupStream.EmitLdArg(2); // argSetupState
            argSetupStream.Emit(ILOpcode.call, emitter.NewToken(InvokeUtilsType.GetKnownMethod("DynamicInvokeArgSetupComplete", null)));

            thisCallSiteSetupStream.EmitLdArg(1); // methodToCall
            staticCallSiteSetupStream.EmitLdArg(1); // methodToCall

            DynamicInvokeMethodParameterKind returnKind = _targetSignature.ReturnType;
            TypeDesc returnType = returnKind != DynamicInvokeMethodParameterKind.None ?
                Context.GetSignatureVariable(_targetSignature.Length, true) :
                Context.GetWellKnownType(WellKnownType.Void);

            if (returnKind == DynamicInvokeMethodParameterKind.Pointer)
                for (int i = 0; i < _targetSignature.GetNumerOfReturnTypePointerIndirections(); i++)
                    returnType = returnType.MakePointerType();

            MethodSignature thisCallMethodSig = new MethodSignature(0, 0, returnType, targetMethodSignature);
            thisCallSiteSetupStream.Emit(ILOpcode.calli, emitter.NewToken(thisCallMethodSig));

            MethodSignature staticCallMethodSig = new MethodSignature(MethodSignatureFlags.Static, 0, returnType, targetMethodSignature);
            staticCallSiteSetupStream.Emit(ILOpcode.calli, emitter.NewToken(staticCallMethodSig));

            if (returnKind == DynamicInvokeMethodParameterKind.None)
            {
                thisCallSiteSetupStream.Emit(ILOpcode.ldnull);
                staticCallSiteSetupStream.Emit(ILOpcode.ldnull);
            }
            else if (returnKind == DynamicInvokeMethodParameterKind.Pointer)
            {
                thisCallSiteSetupStream.Emit(ILOpcode.ldtoken, emitter.NewToken(returnType));
                staticCallSiteSetupStream.Emit(ILOpcode.ldtoken, emitter.NewToken(returnType));
                MethodDesc getTypeFromHandleMethod =
                    Context.SystemModule.GetKnownType("System", "Type").GetKnownMethod("GetTypeFromHandle", null);
                thisCallSiteSetupStream.Emit(ILOpcode.call, emitter.NewToken(getTypeFromHandleMethod));
                staticCallSiteSetupStream.Emit(ILOpcode.call, emitter.NewToken(getTypeFromHandleMethod));

                MethodDesc pointerBoxMethod =
                    Context.SystemModule.GetKnownType("System.Reflection", "Pointer").GetKnownMethod("Box", null);
                thisCallSiteSetupStream.Emit(ILOpcode.call, emitter.NewToken(pointerBoxMethod));
                staticCallSiteSetupStream.Emit(ILOpcode.call, emitter.NewToken(pointerBoxMethod));
            }
            else
            {
                Debug.Assert(returnKind == DynamicInvokeMethodParameterKind.Value);
                ILToken tokReturnType = emitter.NewToken(returnType);
                thisCallSiteSetupStream.Emit(ILOpcode.box, tokReturnType);
                staticCallSiteSetupStream.Emit(ILOpcode.box, tokReturnType);
            }

            thisCallSiteSetupStream.Emit(ILOpcode.ret);
            staticCallSiteSetupStream.Emit(ILOpcode.ret);

            return emitter.Link(this);
        }

        private partial class DynamicInvokeThunkGenericParameter : GenericParameterDesc
        {
            private DynamicInvokeMethodThunk _owningMethod;

            public DynamicInvokeThunkGenericParameter(DynamicInvokeMethodThunk owningMethod, int index)
            {
                _owningMethod = owningMethod;
                Index = index;
            }

            public override TypeSystemContext Context
            {
                get
                {
                    return _owningMethod.Context;
                }
            }

            public override int Index
            {
                get;
            }

            public override GenericParameterKind Kind
            {
                get
                {
                    return GenericParameterKind.Method;
                }
            }
        }
    }

    internal enum DynamicInvokeMethodParameterKind
    {
        None,
        Value,
        Reference,
        Pointer,
    }

    /// <summary>
    /// Wraps a <see cref="MethodSignature"/> to reduce it's fidelity.
    /// </summary>
    internal struct DynamicInvokeMethodSignature : IEquatable<DynamicInvokeMethodSignature>
    {
        private MethodSignature _signature;

        public bool HasReturnValue
        {
            get
            {
                return !_signature.ReturnType.IsVoid;
            }
        }

        public int Length
        {
            get
            {
                return _signature.Length;
            }
        }

        public DynamicInvokeMethodParameterKind this[int index]
        {
            get
            {
                TypeDesc type = _signature[index];

                if (type.IsByRef)
                    return DynamicInvokeMethodParameterKind.Reference;
                else if (type.IsPointer)
                    return DynamicInvokeMethodParameterKind.Pointer;
                else
                    return DynamicInvokeMethodParameterKind.Value;
            }
        }

        public static int GetNumberOfIndirections(TypeDesc type)
        {
            int result = 0;
            while (type.IsPointer)
            {
                result++;
                type = ((PointerType)type).ParameterType;
            }

            return result;
        }

        public int GetNumberOfParameterPointerIndirections(int paramIndex)
        {
            return GetNumberOfIndirections(_signature[paramIndex]);
        }

        public int GetNumerOfReturnTypePointerIndirections()
        {
            return GetNumberOfIndirections(_signature.ReturnType);
        }

        public DynamicInvokeMethodParameterKind ReturnType
        {
            get
            {
                Debug.Assert(!_signature.ReturnType.IsByRef);

                TypeDesc type = _signature.ReturnType;
                if (type.IsPointer)
                    return DynamicInvokeMethodParameterKind.Pointer;
                else if (type.IsVoid)
                    return DynamicInvokeMethodParameterKind.None;
                else
                    return DynamicInvokeMethodParameterKind.Value;
            }
        }

        public DynamicInvokeMethodSignature(MethodSignature concreteSignature)
        {
            // ByRef returns should have been filtered out elsewhere. We don't handle them
            // because reflection can't invoke such methods.
            Debug.Assert(!concreteSignature.ReturnType.IsByRef);
            _signature = concreteSignature;
        }

        public override bool Equals(object obj)
        {
            return obj is DynamicInvokeMethodSignature && Equals((DynamicInvokeMethodSignature)obj);
        }

        public override int GetHashCode()
        {
            int hashCode = (int)this.ReturnType * 0x5498341 + 0x832424;

            for (int i = 0; i < Length; i++)
            {
                int value = (int)this[i] * 0x5498341 + 0x832424;
                hashCode = hashCode * 31 + value;
            }

            return hashCode;
        }

        public bool Equals(DynamicInvokeMethodSignature other)
        {
            DynamicInvokeMethodParameterKind thisReturnKind = ReturnType;
            if (thisReturnKind != other.ReturnType)
                return false;

            if (thisReturnKind == DynamicInvokeMethodParameterKind.Pointer &&
                GetNumerOfReturnTypePointerIndirections() != other.GetNumerOfReturnTypePointerIndirections())
                return false;
            
            if (Length != other.Length)
                return false;

            for (int i = 0; i < Length; i++)
            {
                DynamicInvokeMethodParameterKind thisParamKind = this[i];
                if (thisParamKind != other[i])
                    return false;

                if (thisParamKind == DynamicInvokeMethodParameterKind.Pointer &&
                    GetNumberOfParameterPointerIndirections(i) != other.GetNumberOfParameterPointerIndirections(i))
                    return false;
            }

            return true;
        }
    }
}
