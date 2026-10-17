using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BlindsDown.Tests.NoNetwork;

/// <summary>
/// A method or field as the scanner names it: <c>Namespace.Type+Nested.Name(ParamType,ParamType)</c>.
/// Parameter types use the same spelling as BannedSymbols.txt (e.g. <c>System.String</c>,
/// <c>System.Collections.Generic.IEnumerable{System.String}</c>), so rules can be compared side by side.
/// </summary>
internal sealed record ScannedMember(string DeclaringType, string Name, string? Parameters)
{
    public string FullName => Parameters is null ? $"{DeclaringType}.{Name}" : $"{DeclaringType}.{Name}({Parameters})";

    /// <summary>
    /// True when the member belongs to <paramref name="typeFullName"/> or to a type nested in it. Lambdas,
    /// closures and async state machines are compiled into nested types such as <c>SafeShell+&lt;&gt;c</c>,
    /// so code written inside a class still counts as that class.
    /// </summary>
    public bool IsInType(string typeFullName) =>
        DeclaringType == typeFullName || DeclaringType.StartsWith(typeFullName + "+", StringComparison.Ordinal);

    public override string ToString() => FullName;
}

/// <summary>A type plus the namespace of its outermost type (nested types have no namespace of their own).</summary>
internal sealed record ScannedType(string FullName, string Namespace);

/// <summary>
/// One instruction in a method body that refers to a method: call, callvirt, newobj, ldftn, ldvirtftn,
/// jmp, or ldtoken of a method (expression trees). Taking a delegate (ldftn) counts as a call, because the
/// delegate can be invoked later from anywhere.
/// </summary>
internal sealed record CallSite(
    ScannedMember Caller,
    MethodDefinitionHandle CallerHandle,
    ScannedMember Callee,
    MethodDefinitionHandle CalleeHandle,
    string Instruction,
    int ILOffset)
{
    public override string ToString() => $"{Caller} --{Instruction} @IL_{ILOffset:x4}--> {Callee}";
}

/// <summary>A P/Invoke: a method whose body lives in a native DLL (DllImport, or the inner stub that LibraryImport generates).</summary>
internal sealed record PInvokeImport(ScannedMember Method, MethodDefinitionHandle Handle, string Module, string EntryPoint)
{
    public override string ToString() => $"{Method} -> {Module}!{EntryPoint}";
}

/// <summary>A [ComImport] class (a coclass): creating one with <c>new</c> asks Windows to activate that CLSID.</summary>
internal sealed record ComImportClass(string TypeName, Guid? Clsid);

/// <summary>
/// Reads a compiled .NET assembly WITHOUT loading or running it (System.Reflection.Metadata) and lists
/// everything the no-network policy cares about: assembly, type and member references, P/Invoke imports,
/// [ComImport] classes, and every method referenced from IL (found by decoding each method body).
///
/// Why metadata instead of reflection: reflection would load the assembly into the test process and
/// resolve its dependencies, and it can't see IL call sites at all. Metadata shows exactly what the
/// compiler wrote into the file, which is what ships.
/// </summary>
internal sealed class AssemblyScanner : IDisposable
{
    private const string GeneratedCodeAttribute = "System.CodeDom.Compiler.GeneratedCodeAttribute";
    private const string LibraryImportGenerator = "Microsoft.Interop.LibraryImportGenerator";

    /// <summary>
    /// Every IL opcode by its encoded value (0xFE-prefixed ones as 0xFExx). Built from
    /// <see cref="OpCodes"/> so the operand sizes come from the runtime's own table instead of a
    /// hand-copied one that could drift.
    /// </summary>
    private static readonly FrozenDictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToFrozenDictionary(op => unchecked((ushort)op.Value));

    private readonly PEReader _pe;
    private readonly MetadataReader _md;
    private readonly Dictionary<EntityHandle, ScannedMember> _memberCache = [];
    private IReadOnlyList<CallSite>? _callSites;

    public AssemblyScanner(string path)
    {
        Path = path;
        _pe = new PEReader(File.OpenRead(path));
        try
        {
            if (!_pe.HasMetadata)
            {
                throw new InvalidDataException($"{path} is not a .NET assembly (no metadata).");
            }

            _md = _pe.GetMetadataReader();
        }
        catch
        {
            // The reader owns the open file; a constructor that throws is never disposed by its caller.
            _pe.Dispose();
            throw;
        }
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public MetadataReader Metadata => _md;

    public IEnumerable<string> AssemblyReferenceNames() =>
        _md.AssemblyReferences.Select(h => _md.GetString(_md.GetAssemblyReference(h).Name));

    /// <summary>Every ModuleRef in the file. P/Invokes name their DLL through these.</summary>
    public IEnumerable<string> ModuleReferenceNames()
    {
        for (int row = 1; row <= _md.GetTableRowCount(TableIndex.ModuleRef); row++)
        {
            yield return _md.GetString(_md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name);
        }
    }

    public IEnumerable<ScannedType> TypeReferences() =>
        _md.TypeReferences.Select(h => new ScannedType(TypeName(h), OutermostNamespace(h)));

    /// <summary>Types defined in this assembly (a type defined in a banned namespace is as bad as one referenced).</summary>
    public IEnumerable<ScannedType> TypeDefinitions() =>
        _md.TypeDefinitions.Select(h => new ScannedType(TypeName(h), OutermostNamespace(h)));

    /// <summary>Every method and field this assembly uses from somewhere else (the MemberRef table).</summary>
    public IEnumerable<ScannedMember> MemberReferences() => _md.MemberReferences.Select(h => Describe(h));

    public IEnumerable<PInvokeImport> PInvokes()
    {
        foreach (MethodDefinitionHandle handle in _md.MethodDefinitions)
        {
            MethodDefinition method = _md.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
            {
                continue;
            }

            MethodImport import = method.GetImport();
            string module = import.Module.IsNil ? string.Empty : _md.GetString(_md.GetModuleReference(import.Module).Name);
            string entryPoint = import.Name.IsNil ? _md.GetString(method.Name) : _md.GetString(import.Name);
            yield return new PInvokeImport(Describe(handle), handle, module, entryPoint);
        }
    }

    /// <summary>[ComImport] classes. [ComImport] interfaces are not listed: an interface only describes a
    /// vtable and can't create anything.</summary>
    public IEnumerable<ComImportClass> ComImportClasses()
    {
        foreach (TypeDefinitionHandle handle in _md.TypeDefinitions)
        {
            TypeDefinition type = _md.GetTypeDefinition(handle);
            if ((type.Attributes & TypeAttributes.Import) == 0 || (type.Attributes & TypeAttributes.Interface) != 0)
            {
                continue;
            }

            Guid? clsid = null;
            foreach (CustomAttributeHandle attributeHandle in type.GetCustomAttributes())
            {
                CustomAttribute attribute = _md.GetCustomAttribute(attributeHandle);
                if (AttributeTypeName(attribute) == "System.Runtime.InteropServices.GuidAttribute"
                    && TryReadFirstStringArgument(attribute, out string? text)
                    && Guid.TryParse(text, out Guid parsed))
                {
                    clsid = parsed;
                }
            }

            yield return new ComImportClass(TypeName(handle), clsid);
        }
    }

    /// <summary>
    /// True for the outer method that the LibraryImport source generator writes around a P/Invoke (it
    /// marshals arguments and then calls the real, inner P/Invoke stub). Callers of this wrapper are
    /// the real callers of the native function. The generator only writes static methods; requiring that
    /// stops a hand-written instance method wearing the same attribute from hiding its callers, which
    /// would reach it through virtual or interface calls that name a different method.
    /// </summary>
    public bool IsLibraryImportWrapper(MethodDefinitionHandle handle)
    {
        MethodDefinition method = _md.GetMethodDefinition(handle);
        if ((method.Attributes & MethodAttributes.Static) == 0)
        {
            return false;
        }

        foreach (CustomAttributeHandle attributeHandle in method.GetCustomAttributes())
        {
            CustomAttribute attribute = _md.GetCustomAttribute(attributeHandle);
            if (AttributeTypeName(attribute) == GeneratedCodeAttribute
                && TryReadFirstStringArgument(attribute, out string? tool)
                && tool == LibraryImportGenerator)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every method reference in every method body, found by decoding the IL instruction stream.</summary>
    public IReadOnlyList<CallSite> CallSites() => _callSites ??= DecodeAllCallSites();

    public ScannedMember Describe(MethodDefinitionHandle handle) => Describe((EntityHandle)handle);

    public void Dispose() => _pe.Dispose();

    private List<CallSite> DecodeAllCallSites()
    {
        var sites = new List<CallSite>();
        foreach (MethodDefinitionHandle callerHandle in _md.MethodDefinitions)
        {
            MethodDefinition caller = _md.GetMethodDefinition(callerHandle);

            // RVA 0: abstract, extern, P/Invoke and runtime-implemented methods have no IL.
            if (caller.RelativeVirtualAddress == 0)
            {
                continue;
            }

            MethodBodyBlock body = _pe.GetMethodBody(caller.RelativeVirtualAddress);
            DecodeBody(callerHandle, body.GetILReader(), sites);
        }

        return sites;
    }

    private void DecodeBody(MethodDefinitionHandle callerHandle, BlobReader il, List<CallSite> sites)
    {
        while (il.RemainingBytes > 0)
        {
            int offset = il.Offset;
            ushort value = il.ReadByte();
            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il.ReadByte());
            }

            if (!OpCodesByValue.TryGetValue(value, out OpCode opCode))
            {
                throw new InvalidDataException(
                    $"{FileName}: unknown IL opcode 0x{value:x} at IL_{offset:x4} in {Describe(callerHandle)}. The scanner must understand every instruction.");
            }

            switch (opCode.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    EntityHandle target = MetadataTokens.EntityHandle(il.ReadInt32());
                    AddCallSite(callerHandle, target, opCode, offset, sites);
                    break;
                case OperandType.InlineSwitch:
                    int targets = il.ReadInt32();
                    il.Offset += 4 * targets;
                    break;
                default:
                    il.Offset += OperandSize(opCode.OperandType);
                    break;
            }
        }
    }

    private void AddCallSite(MethodDefinitionHandle callerHandle, EntityHandle target, OpCode opCode, int offset, List<CallSite> sites)
    {
        if (target.Kind == HandleKind.MethodSpecification)
        {
            // A generic method instantiation (Foo<int>): what matters is the method it instantiates.
            target = _md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
        }

        bool isMethod = target.Kind switch
        {
            HandleKind.MethodDefinition => true,
            HandleKind.MemberReference => _md.GetMemberReference((MemberReferenceHandle)target).GetKind() == MemberReferenceKind.Method,
            _ => false, // ldtoken of a type or field
        };
        if (!isMethod)
        {
            return;
        }

        MethodDefinitionHandle calleeHandle = target.Kind == HandleKind.MethodDefinition ? (MethodDefinitionHandle)target : default;
        sites.Add(new CallSite(Describe(callerHandle), callerHandle, Describe(target), calleeHandle, opCode.Name ?? "?", offset));
    }

    /// <summary>Operand sizes from ECMA-335 partition III. InlineSwitch is variable and handled by the caller.</summary>
    private static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => throw new InvalidDataException($"Unexpected IL operand type {type}."),
    };

    private ScannedMember Describe(EntityHandle handle)
    {
        if (_memberCache.TryGetValue(handle, out ScannedMember? cached))
        {
            return cached;
        }

        ScannedMember member;
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                MethodDefinition definition = _md.GetMethodDefinition((MethodDefinitionHandle)handle);
                MethodSignature<string> definitionSignature = definition.DecodeSignature(SignatureTypeNames.Instance, null);
                member = new ScannedMember(TypeName(definition.GetDeclaringType()), _md.GetString(definition.Name), string.Join(",", definitionSignature.ParameterTypes));
                break;
            case HandleKind.MemberReference:
                MemberReference reference = _md.GetMemberReference((MemberReferenceHandle)handle);
                string? parameters = reference.GetKind() == MemberReferenceKind.Method
                    ? string.Join(",", reference.DecodeMethodSignature(SignatureTypeNames.Instance, null).ParameterTypes)
                    : null;
                member = new ScannedMember(ParentName(reference.Parent), _md.GetString(reference.Name), parameters);
                break;
            default:
                member = new ScannedMember($"<{handle.Kind}>", "?", null);
                break;
        }

        _memberCache[handle] = member;
        return member;
    }

    private string ParentName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification => TypeName(parent),

        // A vararg call site's MemberRef points at the MethodDef it calls.
        HandleKind.MethodDefinition => TypeName(_md.GetMethodDefinition((MethodDefinitionHandle)parent).GetDeclaringType()),

        // A global function in another module.
        HandleKind.ModuleReference => $"<module {_md.GetString(_md.GetModuleReference((ModuleReferenceHandle)parent).Name)}>",
        _ => $"<{parent.Kind}>",
    };

    private string TypeName(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => SignatureTypeNames.FormatDefinition(_md, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => SignatureTypeNames.FormatReference(_md, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => _md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(SignatureTypeNames.Instance, null),
        _ => $"<{handle.Kind}>",
    };

    private string OutermostNamespace(TypeReferenceHandle handle)
    {
        TypeReference reference = _md.GetTypeReference(handle);
        while (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            reference = _md.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope);
        }

        return _md.GetString(reference.Namespace);
    }

    private string OutermostNamespace(TypeDefinitionHandle handle)
    {
        TypeDefinition definition = _md.GetTypeDefinition(handle);
        while (!definition.GetDeclaringType().IsNil)
        {
            definition = _md.GetTypeDefinition(definition.GetDeclaringType());
        }

        return _md.GetString(definition.Namespace);
    }

    private string AttributeTypeName(CustomAttribute attribute) => attribute.Constructor.Kind switch
    {
        HandleKind.MethodDefinition => TypeName(_md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
        HandleKind.MemberReference => ParentName(_md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
        _ => string.Empty,
    };

    /// <summary>Reads the first constructor argument of an attribute whose constructor starts with a string
    /// (GuidAttribute, GeneratedCodeAttribute). Blob layout: prolog 0x0001, then a SerString.</summary>
    private bool TryReadFirstStringArgument(CustomAttribute attribute, out string? value)
    {
        value = null;
        BlobReader blob = _md.GetBlobReader(attribute.Value);
        if (blob.Length < 2 || blob.ReadUInt16() != 1)
        {
            return false;
        }

        value = blob.ReadSerializedString();
        return value is not null;
    }

    /// <summary>Turns signature blobs into readable type names. Stateless.</summary>
    private sealed class SignatureTypeNames : ISignatureTypeProvider<string, object?>
    {
        public static readonly SignatureTypeNames Instance = new();

        public static string FormatDefinition(MetadataReader md, TypeDefinitionHandle handle)
        {
            TypeDefinition type = md.GetTypeDefinition(handle);
            string name = md.GetString(type.Name);
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return FormatDefinition(md, declaring) + "+" + name;
            }

            string ns = md.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public static string FormatReference(MetadataReader md, TypeReferenceHandle handle)
        {
            TypeReference type = md.GetTypeReference(handle);
            string name = md.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return FormatReference(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
            }

            string ns = md.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            FormatDefinition(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            FormatReference(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "@";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            // Doc-comment-ID style, like BannedSymbols.txt: IEnumerable`1 + {System.String} -> IEnumerable{System.String}.
            // Only a trailing arity is dropped: in Dictionary`2+Enumerator the tick belongs to the outer type.
            int tick = genericType.LastIndexOf('`');
            bool trailingArity = tick > 0 && tick < genericType.Length - 1 && !genericType.AsSpan(tick + 1).ContainsAnyExceptInRange('0', '9');
            string name = trailingArity ? genericType[..tick] : genericType;
            return name + "{" + string.Join(",", typeArguments) + "}";
        }

        public string GetGenericTypeParameter(object? genericContext, int index) => "`" + index;

        public string GetGenericMethodParameter(object? genericContext, int index) => "``" + index;

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            "method " + signature.ReturnType + "(" + string.Join(",", signature.ParameterTypes) + ")";
    }
}
