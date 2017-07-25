﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

using ILCompiler.DependencyAnalysisFramework;

using Internal.Text;
using Internal.TypeSystem;
using Internal.TypeSystem.TypesDebugInfo;
using Internal.JitInterface;
using ObjectData = ILCompiler.DependencyAnalysis.ObjectNode.ObjectData;

using LLVMSharp;
using ILCompiler.CodeGen;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Object writer using https://github.com/dotnet/llilc
    /// </summary>
    internal class WebAssemblyObjectWriter : IDisposable
    {
        public static string GetBaseSymbolName(ISymbolNode symbol, NameMangler nameMangler, bool objectWriterUse = false)
        {
            if (symbol is ObjectNode)
            {
                ObjectNode objNode = (ObjectNode)symbol;
                ISymbolDefinitionNode symbolDefNode = (ISymbolDefinitionNode)symbol;
                if (symbolDefNode.Offset == 0)
                {
                    return symbol.GetMangledName(nameMangler);
                }
                else
                {
                    return symbol.GetMangledName(nameMangler) + "___REALBASE";
                }
            }
            else if (symbol is ObjectAndOffsetSymbolNode)
            {
                ObjectAndOffsetSymbolNode objAndOffset = (ObjectAndOffsetSymbolNode)symbol;
                if (objAndOffset.Target is IHasStartSymbol)
                {
                    ISymbolNode startSymbol = ((IHasStartSymbol)objAndOffset.Target).StartSymbol;
                    if (startSymbol == symbol)
                    {
                        Debug.Assert(startSymbol.Offset == 0);
                        return symbol.GetMangledName(nameMangler);
                    }
                    return GetBaseSymbolName(startSymbol, nameMangler, objectWriterUse);
                }
                return GetBaseSymbolName((ISymbolNode)objAndOffset.Target, nameMangler, objectWriterUse);
            }
            else if (symbol is EmbeddedObjectNode)
            {
                EmbeddedObjectNode embeddedNode = (EmbeddedObjectNode)symbol;
                return GetBaseSymbolName(embeddedNode.ContainingNode.StartSymbol, nameMangler, objectWriterUse);
            }
            else
            {
                ThrowHelper.ThrowInvalidProgramException();
                return null;
            }
        }

        public static LLVMValueRef GetOffsetFromBaseSymbolValue(LLVMModuleRef module, ISymbolNode symbol, NameMangler nameMangler, bool objectWriterUse = false)
        {
            return LLVM.GetNamedGlobal(module, symbol.GetMangledName(nameMangler) + "___OFFSET");
        }

        private static int GetNumericOffsetFromBaseSymbolValue(ISymbolNode symbol)
        {
            if(symbol is ObjectNode)
            {
                ISymbolDefinitionNode symbolDefNode = (ISymbolDefinitionNode)symbol;
                return symbolDefNode.Offset;
            }
            else if (symbol is ObjectAndOffsetSymbolNode)
            {
                ObjectAndOffsetSymbolNode objAndOffset = (ObjectAndOffsetSymbolNode)symbol;
                ISymbolDefinitionNode symbolDefNode = (ISymbolDefinitionNode)symbol;
                if (objAndOffset.Target is IHasStartSymbol)
                {
                    ISymbolNode startSymbol = ((IHasStartSymbol)objAndOffset.Target).StartSymbol;
                    
                    if (startSymbol == symbol)
                    {
                        Debug.Assert(symbolDefNode.Offset == 0);
                        return symbolDefNode.Offset;
                    }
                    return symbolDefNode.Offset;
                }
                int baseOffset = GetNumericOffsetFromBaseSymbolValue((ISymbolNode)objAndOffset.Target);
                return baseOffset + symbolDefNode.Offset;
            }
            else if (symbol is EmbeddedObjectNode)
            {
                EmbeddedObjectNode embeddedNode = (EmbeddedObjectNode)symbol;
                int baseOffset = GetNumericOffsetFromBaseSymbolValue(embeddedNode.ContainingNode.StartSymbol);
                return baseOffset + embeddedNode.OffsetFromBeginningOfArray;
            }
            else
            {
                ThrowHelper.ThrowInvalidProgramException();
                return 0;
            }
        }

        // this is the llvm instance.
        public LLVMModuleRef Module { get; }

        // This is used to build mangled names
        private Utf8StringBuilder _sb = new Utf8StringBuilder();

        // Track offsets in node data that prevent writing all bytes in one single blob. This includes
        // relocs, symbol definitions, debug data that must be streamed out using the existing LLVM API
        private SortedSet<int> _byteInterruptionOffsets = new SortedSet<int>();

        // Code offset to defined names
        private Dictionary<int, List<ISymbolDefinitionNode>> _offsetToDefName = new Dictionary<int, List<ISymbolDefinitionNode>>();

        // The section for the current node being processed.
        private ObjectNodeSection _currentSection;

        // The first defined symbol name of the current node being processed.
        private Utf8String _currentNodeZeroTerminatedName;

        // Nodefactory for which ObjectWriter is instantiated for.
        private NodeFactory _nodeFactory;

#if DEBUG
        static Dictionary<string, ISymbolNode> _previouslyWrittenNodeNames = new Dictionary<string, ISymbolNode>();
#endif

        public void SetSection(ObjectNodeSection section)
        {
            _currentSection = section;
            throw new NotImplementedException(); // This function isn't complete
        }

        public void FinishObjWriter()
        {
            EmitNativeMain();
            LLVM.WriteBitcodeToFile(Module, _objectFilePath);
            LLVM.DumpModule(Module);
            //throw new NotImplementedException(); // This function isn't complete
        }

        private void EmitNativeMain()
        {
            LLVMBuilderRef builder = LLVM.CreateBuilder();
            var mainSignature = LLVM.FunctionType(LLVM.Int32Type(), new LLVMTypeRef[0], false);
            var mainFunc = LLVM.AddFunction(Module, "main", mainSignature);
            var mainEntryBlock = LLVM.AppendBasicBlock(mainFunc, "entry");
            LLVM.PositionBuilderAtEnd(builder, mainEntryBlock);
            LLVMValueRef managedMain = LLVM.GetNamedFunction(Module, "Main");

            var shadowStack = LLVM.BuildMalloc(builder, LLVM.ArrayType(LLVM.Int8Type(), 1000000), String.Empty);
            var castShadowStack = LLVM.BuildPointerCast(builder, shadowStack, LLVM.PointerType(LLVM.Int8Type(), 0), String.Empty);
            LLVM.BuildCall(builder, managedMain, new LLVMValueRef[]
            {
                castShadowStack,
                LLVM.ConstPointerNull(LLVM.PointerType(LLVM.Int8Type(), 0))
            },
            String.Empty);

            LLVM.BuildRet(builder, LLVM.ConstInt(LLVM.Int32Type(), 42, LLVMMisc.False));
            LLVM.SetLinkage(mainFunc, LLVMLinkage.LLVMExternalLinkage);
        }

        public void SetCodeSectionAttribute(ObjectNodeSection section)
        {
        }

        public void EnsureCurrentSection()
        {
        }

        ArrayBuilder<byte> _currentObjectData = new ArrayBuilder<byte>();
        Dictionary<int, LLVMValueRef> _currentObjectSymbolRefs = new Dictionary<int, LLVMValueRef>();
        ObjectNode _currentObjectNode;

        public void StartObjectNode(ObjectNode node)
        {
            Debug.Assert(_currentObjectNode == null);
            _currentObjectNode = node;
            Debug.Assert(_currentObjectData.Count == 0);
        }

        public void DoneObjectNode()
        {
            int pointerSize = _nodeFactory.Target.PointerSize;
            EmitAlignment(_nodeFactory.Target.PointerSize);
            Debug.Assert(_nodeFactory.Target.PointerSize == 4);
            int countOfPointerSizedElements = _currentObjectData.Count / _nodeFactory.Target.PointerSize;
            List<LLVMValueRef> entries = new List<LLVMValueRef>();

            byte[] currentObjectData = _currentObjectData.ToArray();
            var intPtrType = LLVM.PointerType(LLVM.Int32Type(), 0);

            for (int i = 0; i < countOfPointerSizedElements; i++)
            {
                int curOffset = (i * pointerSize);
                LLVMValueRef pointedAtValue;
                if (_currentObjectSymbolRefs.TryGetValue(curOffset, out pointedAtValue))
                {
                    var ptrValue = LLVM.ConstBitCast(pointedAtValue, intPtrType);
                    entries.Add(ptrValue);
                }
                else
                {
                    int value = BitConverter.ToInt32(currentObjectData, curOffset);
                    var dataVal = LLVM.ConstInt(intPtrType, (uint)value, (LLVMBool)false);
                    entries.Add(dataVal);
                }
            }

            ISymbolNode symNode = _currentObjectNode as ISymbolNode;
            if (symNode == null)
                symNode = ((IHasStartSymbol)_currentObjectNode).StartSymbol;
            string realName = GetBaseSymbolName(symNode, _nodeFactory.NameMangler, true);

            var funcptrarray = LLVM.ConstArray(intPtrType, entries.ToArray());
            var arrayglobal = LLVM.AddGlobalInAddressSpace(Module, LLVM.ArrayType(intPtrType, 2), realName, 0);
            LLVM.SetInitializer(arrayglobal, funcptrarray);
            LLVM.SetLinkage(arrayglobal, LLVMLinkage.LLVMExternalLinkage);

            _currentObjectNode = null;
            _currentObjectSymbolRefs.Clear();
            _currentObjectData = new ArrayBuilder<byte>();
        }

        public void EmitAlignment(int byteAlignment)
        {
            while ((_currentObjectData.Count % byteAlignment) != 0)
                _currentObjectData.Add(0);
        }

        public void EmitBlob(byte[] blob)
        {
            _currentObjectData.Append(blob);
        }
        
        public void EmitIntValue(ulong value, int size)
        {
            switch (size)
            {
                case 1:
                    _currentObjectData.Append(BitConverter.GetBytes((byte)value));
                    break;
                case 2:
                    _currentObjectData.Append(BitConverter.GetBytes((ushort)value));
                    break;
                case 4:
                    _currentObjectData.Append(BitConverter.GetBytes((uint)value));
                    break;
                case 8:
                    _currentObjectData.Append(BitConverter.GetBytes(value));
                    break;
                default:
                    ThrowHelper.ThrowInvalidProgramException();
                    break;
            }
        }

        public void EmitBytes(IntPtr pArray, int length)
        {
            unsafe
            {
                byte* pBytes = (byte*)pArray;
                for (int i = 0; i < length; i++)
                    _currentObjectData.Add(pBytes[i]);
            }
        }
        
        public void EmitSymbolDef(string realSymbolName, string symbolIdentifier, int offsetFromSymbolName)
        {
            var intType = LLVM.Int32Type();
            var myGlobal = LLVM.AddGlobalInAddressSpace(Module, intType, symbolIdentifier + "___OFFSET", 0);
            LLVM.SetInitializer(myGlobal, LLVM.ConstInt(intType, (uint)offsetFromSymbolName, (LLVMBool)false));
            LLVM.SetLinkage(myGlobal, LLVMLinkage.LLVMExternalLinkage);
        }

        public int EmitSymbolRef(string realSymbolName, int offsetFromSymbolName, bool isFunction, RelocType relocType, int delta = 0)
        {
            int symbolStartOffset = _currentObjectData.Count;

            // Workaround for ObjectWriter's lack of support for IMAGE_REL_BASED_RELPTR32
            // https://github.com/dotnet/corert/issues/3278
            if (relocType == RelocType.IMAGE_REL_BASED_RELPTR32)
            {
                relocType = RelocType.IMAGE_REL_BASED_REL32;
                delta = checked(delta + sizeof(int));
            }

            EmitBlob(new byte[this._nodeFactory.Target.PointerSize]);
            if (relocType == RelocType.IMAGE_REL_BASED_REL32)
            {
                Console.WriteLine("REL BASED RELOC");
                return this._nodeFactory.Target.PointerSize;
            }

            LLVMValueRef valRef = isFunction ? LLVM.GetNamedFunction(Module, realSymbolName) : LLVM.GetNamedGlobal(Module, realSymbolName);

            if (offsetFromSymbolName != 0)
                valRef = LLVM.GetElementAsConstant(LLVM.ConstBitCast(valRef, LLVM.PointerType(LLVM.Int8Type(), 0)), (uint)offsetFromSymbolName);
            

            _currentObjectSymbolRefs.Add(symbolStartOffset, valRef);
            return _nodeFactory.Target.PointerSize;
        }

        public string GetMangledName(TypeDesc type)
        {
            return _nodeFactory.NameMangler.GetMangledTypeName(type);
        }

        public void BuildSymbolDefinitionMap(ObjectNode node, ISymbolDefinitionNode[] definedSymbols)
        {
            _offsetToDefName.Clear();
            foreach (ISymbolDefinitionNode n in definedSymbols)
            {
                if (!_offsetToDefName.ContainsKey(n.Offset))
                {
                    _offsetToDefName[n.Offset] = new List<ISymbolDefinitionNode>();
                }

                _offsetToDefName[n.Offset].Add(n);
                _byteInterruptionOffsets.Add(n.Offset);
            }

            var symbolNode = node as ISymbolDefinitionNode;
            if (symbolNode != null)
            {
                _sb.Clear();
                AppendExternCPrefix(_sb);
                symbolNode.AppendMangledName(_nodeFactory.NameMangler, _sb);
                _currentNodeZeroTerminatedName = _sb.Append('\0').ToUtf8String();
            }
            else
            {
                _currentNodeZeroTerminatedName = default(Utf8String);
            }
        }

        private void AppendExternCPrefix(Utf8StringBuilder sb)
        {
        }

        // Returns size of the emitted symbol reference
        public int EmitSymbolReference(ISymbolNode target, int delta, RelocType relocType)
        {
            string realSymbolName = GetBaseSymbolName(target, _nodeFactory.NameMangler, true);
            int offsetFromBase = GetNumericOffsetFromBaseSymbolValue(target);
            return EmitSymbolRef(realSymbolName, offsetFromBase, target is CppMethodCodeNode, relocType, delta);
        }

        public void EmitBlobWithRelocs(byte[] blob, Relocation[] relocs)
        {
            int nextRelocOffset = -1;
            int nextRelocIndex = -1;
            if (relocs.Length > 0)
            {
                nextRelocOffset = relocs[0].Offset;
                nextRelocIndex = 0;
            }

            int i = 0;
            while (i < blob.Length)
            {
                if (i == nextRelocOffset)
                {
                    Relocation reloc = relocs[nextRelocIndex];

                    long delta;
                    unsafe
                    {
                        fixed (void* location = &blob[i])
                        {
                            delta = Relocation.ReadValue(reloc.RelocType, location);
                        }
                    }
                    int size = EmitSymbolReference(reloc.Target, (int)delta, reloc.RelocType);

                    // Update nextRelocIndex/Offset
                    if (++nextRelocIndex < relocs.Length)
                    {
                        nextRelocOffset = relocs[nextRelocIndex].Offset;
                    }
                    i += size;
                }
                else
                {
                    EmitIntValue(blob[i], 1);
                    i++;
                }
            }
        }

        public void EmitSymbolDefinition(int currentOffset)
        {
            List<ISymbolDefinitionNode> nodes;
            if (_offsetToDefName.TryGetValue(currentOffset, out nodes))
            {
                foreach (var name in nodes)
                {
                    _sb.Clear();
                    AppendExternCPrefix(_sb);
                    name.AppendMangledName(_nodeFactory.NameMangler, _sb);

                    string baseSymbolNode = GetBaseSymbolName(name, _nodeFactory.NameMangler, true);
                    string symbolId = name.ToString();
                    int offsetFromBase = GetNumericOffsetFromBaseSymbolValue(name);
                    Debug.Assert(offsetFromBase == currentOffset);
                    
                    EmitSymbolDef(baseSymbolNode, symbolId, offsetFromBase);
                    /*
                    string alternateName = _nodeFactory.GetSymbolAlternateName(name);
                    if (alternateName != null)
                    {
                        _sb.Clear();
                        //AppendExternCPrefix(_sb);
                        _sb.Append(alternateName);

                        EmitSymbolDef(_sb);
                    }*/
                }
            }
        }

        //System.IO.FileStream _file;
        string _objectFilePath;

        public WebAssemblyObjectWriter(string objectFilePath, NodeFactory factory, WebAssemblyCodegenCompilation compilation)
        {
            _nodeFactory = factory;
            _objectFilePath = objectFilePath;
            Module = compilation.Module;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public virtual void Dispose(bool bDisposing)
        {
            FinishObjWriter();
            //if (_file != null)
            //{
            //    // Finalize object emission.
            //    FinishObjWriter();
            //    _file.Flush();
            //    _file.Dispose();
            //    _file = null;
            //}

            _nodeFactory = null;

            if (bDisposing)
            {
                GC.SuppressFinalize(this);
            }
        }

        ~WebAssemblyObjectWriter()
        {
            Dispose(false);
        }

        private bool ShouldShareSymbol(ObjectNode node)
        {
            if (_nodeFactory.CompilationModuleGroup.IsSingleFileCompilation)
                return false;

            if (!(node is ISymbolNode))
                return false;

            // These intentionally clash with one another, but are merged with linker directives so should not be Comdat folded
            if (node is ModulesSectionNode)
                return false;

            return true;
        }

        private ObjectNodeSection GetSharedSection(ObjectNodeSection section, string key)
        {
            string standardSectionPrefix = "";
            if (section.IsStandardSection)
                standardSectionPrefix = ".";

            return new ObjectNodeSection(standardSectionPrefix + section.Name, section.Type, key);
        }

        public void ResetByteRunInterruptionOffsets(Relocation[] relocs)
        {
            _byteInterruptionOffsets.Clear();

            for (int i = 0; i < relocs.Length; ++i)
            {
                _byteInterruptionOffsets.Add(relocs[i].Offset);
            }
        }

        public static void EmitObject(string objectFilePath, IEnumerable<DependencyNode> nodes, NodeFactory factory, WebAssemblyCodegenCompilation compilation, IObjectDumper dumper)
        {
            WebAssemblyObjectWriter objectWriter = new WebAssemblyObjectWriter(objectFilePath, factory, compilation);
            bool succeeded = false;

            try
            {
                //ObjectNodeSection managedCodeSection = null;

                var listOfOffsets = new List<int>();
                foreach (DependencyNode depNode in nodes)
                {
                    ObjectNode node = depNode as ObjectNode;
                    if (node == null)
                        continue;

                    if (node.ShouldSkipEmittingObjectNode(factory))
                        continue;
                    objectWriter.StartObjectNode(node);
                    ObjectData nodeContents = node.GetData(factory);

                    if (dumper != null)
                        dumper.DumpObjectNode(factory.NameMangler, node, nodeContents);

#if DEBUG
                    foreach (ISymbolNode definedSymbol in nodeContents.DefinedSymbols)
                    {
                        try
                        {
                            _previouslyWrittenNodeNames.Add(definedSymbol.GetMangledName(factory.NameMangler), definedSymbol);
                        }
                        catch (ArgumentException)
                        {
                            ISymbolNode alreadyWrittenSymbol = _previouslyWrittenNodeNames[definedSymbol.GetMangledName(factory.NameMangler)];
                            Debug.Assert(false, "Duplicate node name emitted to file",
                            $"Symbol {definedSymbol.GetMangledName(factory.NameMangler)} has already been written to the output object file {objectFilePath} with symbol {alreadyWrittenSymbol}");
                        }
                    }
#endif

                    ObjectNodeSection section = node.Section;
                    if (objectWriter.ShouldShareSymbol(node))
                    {
                        section = objectWriter.GetSharedSection(section, ((ISymbolNode)node).GetMangledName(factory.NameMangler));
                    }

                    // Ensure section and alignment for the node.
                    objectWriter.SetSection(section);
                    objectWriter.EmitAlignment(nodeContents.Alignment);

                    objectWriter.ResetByteRunInterruptionOffsets(nodeContents.Relocs);

                    // Build symbol definition map.
                    objectWriter.BuildSymbolDefinitionMap(node, nodeContents.DefinedSymbols);

                    Relocation[] relocs = nodeContents.Relocs;
                    int nextRelocOffset = -1;
                    int nextRelocIndex = -1;
                    if (relocs.Length > 0)
                    {
                        nextRelocOffset = relocs[0].Offset;
                        nextRelocIndex = 0;
                    }

                    int i = 0;

                    listOfOffsets.Clear();
                    listOfOffsets.AddRange(objectWriter._byteInterruptionOffsets);

                    int offsetIndex = 0;
                    while (i < nodeContents.Data.Length)
                    {
                        // Emit symbol definitions if necessary
                        objectWriter.EmitSymbolDefinition(i);

                        if (i == nextRelocOffset)
                        {
                            Relocation reloc = relocs[nextRelocIndex];

                            long delta;
                            unsafe
                            {
                                fixed (void* location = &nodeContents.Data[i])
                                {
                                    delta = Relocation.ReadValue(reloc.RelocType, location);
                                }
                            }
                            int size = objectWriter.EmitSymbolReference(reloc.Target, (int)delta, reloc.RelocType);

                            /*
                             WebAssembly has no thumb 
                            // Emit a copy of original Thumb2 instruction that came from RyuJIT
                            if (reloc.RelocType == RelocType.IMAGE_REL_BASED_THUMB_MOV32 ||
                                reloc.RelocType == RelocType.IMAGE_REL_BASED_THUMB_BRANCH24)
                            {
                                unsafe
                                {
                                    fixed (void* location = &nodeContents.Data[i])
                                    {
                                        objectWriter.EmitBytes((IntPtr)location, size);
                                    }
                                }
                            }*/

                            // Update nextRelocIndex/Offset
                            if (++nextRelocIndex < relocs.Length)
                            {
                                nextRelocOffset = relocs[nextRelocIndex].Offset;
                            }
                            else
                            {
                                // This is the last reloc. Set the next reloc offset to -1 in case the last reloc has a zero size, 
                                // which means the reloc does not have vacant bytes corresponding to in the data buffer. E.g, 
                                // IMAGE_REL_THUMB_BRANCH24 is a kind of 24-bit reloc whose bits scatte over the instruction that 
                                // references it. We do not vacate extra bytes in the data buffer for this kind of reloc.
                                nextRelocOffset = -1;
                            }
                            i += size;
                        }
                        else
                        {
                            while (offsetIndex < listOfOffsets.Count && listOfOffsets[offsetIndex] <= i)
                            {
                                offsetIndex++;
                            }

                            int nextOffset = offsetIndex == listOfOffsets.Count ? nodeContents.Data.Length : listOfOffsets[offsetIndex];

                            unsafe
                            {
                                // Todo: Use Span<T> instead once it's available to us in this repo
                                fixed (byte* pContents = &nodeContents.Data[i])
                                {
                                    objectWriter.EmitBytes((IntPtr)(pContents), nextOffset - i);
                                    i += nextOffset - i;
                                }
                            }

                        }
                    }
                    Debug.Assert(i == nodeContents.Data.Length);

                    // It is possible to have a symbol just after all of the data.
                    objectWriter.EmitSymbolDefinition(nodeContents.Data.Length);
                    objectWriter.DoneObjectNode();
                }

                succeeded = true;
            }
            finally
            {
                objectWriter.Dispose();

                if (!succeeded)
                {
                    // If there was an exception while generating the OBJ file, make sure we don't leave the unfinished
                    // object file around.
                    try
                    {
                        File.Delete(objectFilePath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
